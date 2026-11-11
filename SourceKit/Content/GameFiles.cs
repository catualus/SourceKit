using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace SourceKit
{
    /// <summary>
    /// Finds a game file the way the engine would, so a model named by the map can be opened.
    ///
    /// A static prop names its model as a path relative to the mod directory, and that path can resolve
    /// to several different places. In descending priority:
    ///
    /// 1. The map's own embedded pakfile. A mapper who ships custom models packs them into the .bsp, and
    ///    those override everything - they exist precisely because the content is not installed.
    /// 2. Loose files: the addon or download folder the map itself sits in, the mod directory, any addon
    ///    folder beneath it, the base game directories a mod inherits from, and last the other games
    ///    Garry's Mod mounts through <c>cfg/mount.cfg</c> and <c>cfg/mountdepots.txt</c>.
    /// 3. Garry's Mod addon archives (.gma).
    /// 4. VPKs, where stock game content lives, in the same order as the directories above.
    ///
    /// The order matters and is not arbitrary. Getting it backwards means a map that ships a modified
    /// version of a stock prop silently collides against the stock one.
    ///
    /// **This is best-effort by design.** A missing model is reported and skipped, never fatal. These tools
    /// run on a build machine that may have nothing installed but the map, and a result built without
    /// some props is worth more than none at all - so long as it says so, which is what
    /// <see cref="Missing"/> is for.
    /// </summary>
    public sealed class GameFiles : IDisposable
    {
        private const int LumpPakfile = 40;

        private readonly List<string> roots = [];

        /// <summary>
        /// Paths to the VPKs that exist, and the archives themselves once anything has needed one.
        ///
        /// Opening a VPK means parsing its whole directory - three nested runs of null-terminated
        /// strings covering every file in the archive - and a Garry's Mod install has nine of them.
        /// That measured 133ms, which was the single largest part of loading a map's props, and on any
        /// install where the content is unpacked not one of those directories is ever consulted: all
        /// 169 of rp_downtown_meowy's models resolved from loose files.
        ///
        /// So the paths are collected eagerly, which is a directory listing, and the directories are
        /// parsed only when a lookup has already missed the map's pakfile and every loose root. A
        /// machine that really does need them pays exactly what it paid before, on first use.
        /// </summary>
        private readonly List<string> vpkPaths = [];

        /// <summary>Workshop addon archives, discovered eagerly and indexed on demand.</summary>
        private readonly List<string> gmaPaths = [];

        /// <summary>
        /// Every archive already queued, by full path. The same VPK is named by the gameinfo and found
        /// again in the root it sits in, and the same .gma turns up under a named root that is also the
        /// mod; without this each would be parsed and searched twice.
        /// </summary>
        private readonly HashSet<string> queued = new(StringComparer.OrdinalIgnoreCase);

        private List<GmaArchive>? gmas;

        private List<VpkArchive>? vpks;

        /// <summary>
        /// The map's pakfile, read straight out of the .bsp through a window onto the lump.
        ///
        /// SharpCompress rather than <c>System.IO.Compression</c>, because a map that has been through
        /// <c>bspzip -repack</c> stores nearly every entry with zip method 14 (LZMA), which the framework's
        /// reader refuses: on the shipped rp_downtown_meowy.bsp that was 3,534 of 3,540 entries, every
        /// packed model among them. The window keeps the lump on disk instead of in memory, which on the
        /// largest map measured was 163 MB.
        /// </summary>
        private IArchive? pak;
        private Stream? pakStream;
        private readonly Dictionary<string, IArchiveEntry> pakEntries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths asked for and not found anywhere, so the gap can be reported rather than guessed at.</summary>
        public IReadOnlyCollection<string> Missing => missing.Keys.ToArray();

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> missing =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Guards the three archive readers, which are stateful and are not safe to share.
        ///
        /// Callers load models in parallel, and most of that work - parsing a hull out of the bytes -
        /// genuinely is independent. Fetching the bytes is not always: a zip archive reads its entries
        /// through one shared stream, and a <see cref="VpkArchive"/> seeks and reads shared file handles,
        /// so two threads reading different files from one archive would interleave each other's seeks
        /// and return spliced garbage.
        ///
        /// Deliberately narrow. Loose files are read outside it, because <c>File.ReadAllBytes</c> is safe
        /// concurrently and loose content is the common case - on a normal Garry's Mod install with
        /// addons unpacked, nearly every model resolves before this lock is ever taken. Parsing always
        /// happens outside it, which is where the time goes.
        /// </summary>
        private readonly object archives = new();

        /// <summary>Milliseconds spent parsing VPK directories and reading the map's pakfile.</summary>
        public long VpkMs, PakMs, GmaMs;

        public int RootCount => roots.Count;
        public int VpkCount => vpkPaths.Count;
        public int GmaCount => gmaPaths.Count;
        public int GmasOpened => gmas?.Count ?? 0;
        public int VpkOpened => vpks?.Count ?? 0;
        public int PakfileEntries => pakEntries.Count;

        /// <summary>
        /// Where reads were served from. Worth counting because two of the three sources are behind a
        /// lock and one is not, so the split decides whether loading models in parallel is worth
        /// anything at all on a given map.
        /// </summary>
        public int ReadsFromPakfile;
        public int ReadsFromDisk;
        public int ReadsFromVpk;
        public int ReadsFromGma;

        /// <summary>
        /// Directories to search on top of the ones inferred from the map's own path, for every lookup
        /// in the process.
        ///
        /// **Process-wide because the alternative silently loses content.** Where the map is loaded from
        /// is decided in a dozen places - every command that opens a BSP, plus readers that load lumps in
        /// parallel - and a caller that forgets to pass a root along does not
        /// get an error. It gets a mesh with no prop collision, floating wherever the missing props are,
        /// and nothing anywhere says so. Setting it once means no path can miss it.
        ///
        /// The same shape as <see cref="SourceKitConcurrency.MaxThreads"/>: configuration a person supplies once for a
        /// whole run, not state a pass reasons about. The <paramref name="extraRoots"/> parameter stays
        /// for callers that want to be explicit; both are used.
        /// </summary>
        public static IReadOnlyList<string> AdditionalRoots { get; set; } = [];

        /// <summary>
        /// Opens the content a map can see, inferring the install layout from the map's own path.
        ///
        /// The mod directory is the nearest folder at or above the map that holds a
        /// <c>gameinfo.txt</c>, and the rest of the install is found from there. For the ordinary
        /// <c>&lt;mod&gt;/maps/x.bsp</c> that is two levels up, but Garry's Mod also keeps maps in
        /// <c>addons/&lt;addon&gt;/maps</c> and <c>download/maps</c>, where two levels up is a folder with no
        /// gameinfo, no VPKs and no addons of its own. When no gameinfo is found at all, the folder
        /// above the map's is taken as the mod, as before. When the map sits in a <c>maps</c> folder
        /// that is not the mod's own, the folder above it - the addon, or <c>download</c> - is searched
        /// first, ahead of the mod, because that is where the map's own content was put.
        ///
        /// That inference is the whole reason no configuration is needed for the ordinary case, and it
        /// is also its limit: a BSP sitting outside any install - a build server's output directory, a
        /// scratch copy - finds nothing, and produces a mesh with no prop collision at all.
        /// <paramref name="extraRoots"/> and <see cref="AdditionalRoots"/> are how that case is answered.
        /// </summary>
        public static GameFiles Open(string bspPath, IEnumerable<string>? extraRoots = null)
        {
            var files = new GameFiles();

            var folder = Directory.GetParent(Path.GetFullPath(bspPath));
            var mod = FindMod(folder);

            // The addon or download folder the map was installed into. Searched before the mod, the
            // same as the engine does with a mounted addon over the base game.
            if (folder is not null && folder.Name.Equals("maps", StringComparison.OrdinalIgnoreCase) &&
                folder.Parent is { } own && !SamePath(own.FullName, mod?.FullName))
            {
                files.AddRoot(own.FullName);
            }

            if (mod is not null)
            {
                files.AddRoot(mod.FullName);

                // Addons are searched as roots in their own right. In Garry's Mod an installed addon is
                // a directory with the same shape as the mod - models/, materials/ - and a map built
                // against one names its props exactly as if they were installed normally.
                string addons = Path.Combine(mod.FullName, "addons");

                if (Directory.Exists(addons))
                {
                    foreach (var dir in Directory.EnumerateDirectories(addons))
                        files.AddRoot(dir);
                }

                // What the mod itself says it mounts, which is the authoritative answer and the one
                // the engine uses.
                files.AddSearchPaths(GameInfo.Read(mod.FullName));

                // The old guess, kept behind it rather than replaced by it. Naming the base games a
                // mod is likely to inherit from is right for the common cases and costs nothing when
                // gameinfo already covered them - AddRoot ignores duplicates - while still answering
                // for a mod whose gameinfo is missing, unreadable, or shaped in a way the reader above
                // declines to guess at. Enumerated rather than scanned: a mod folder's siblings include
                // plenty that are not content, and walking all of them turns a lookup into a directory
                // scan of the whole install.
                if (mod.Parent is { } install)
                {
                    foreach (var name in new[] { "sourceengine", "platform", "hl2", "cstrike", "episodic", "ep2", "tf", "portal" })
                    {
                        string path = Path.Combine(install.FullName, name);
                        if (Directory.Exists(path)) files.AddRoot(path);
                    }
                }
            }

            // Both, and after the inferred ones: a root someone named explicitly is a supplement to the
            // install layout, not a replacement for it. AddRoot already ignores duplicates, so naming a
            // directory the inference had found anyway costs nothing.
            foreach (var root in Named(extraRoots))
            {
                files.AddRoot(root);

                // A directory someone names with -content is nearly always a mod directory - it is the
                // thing you point at when the map is not inside the install. Reading its gameinfo is
                // what turns that one flag from "also look here" into "mount this game the way the
                // engine would", which is the difference between finding the mod's own models and
                // finding the base game's as well.
                files.AddSearchPaths(GameInfo.Read(root));
            }

            // Other games Garry's Mod mounts - Counter-Strike: Source, Team Fortress 2 and the rest -
            // last, after all of its own content, which is where the engine puts them too. A GMod
            // install has none of their folders beside it, so the sibling guess above never finds them.
            if (mod is not null)
                files.AddMountedGames(mod.FullName);

            foreach (var root in Named(extraRoots))
                files.AddMountedGames(root);

            foreach (var root in files.roots.ToArray())
                files.AddVpksIn(root);

            // Subscribed content, which for Garry's Mod is where most third-party props live. The cache
            // directory is the game's own download location; loose .gma files beside the addons are the
            // other place they turn up.
            if (mod is not null)
            {
                files.AddGmasIn(Path.Combine(mod.FullName, "cache", "workshop"));
                files.AddGmasIn(Path.Combine(mod.FullName, "addons"));

                // Steam keeps its own copy of subscribed items, outside the game directory entirely:
                // steamapps/workshop/content/4000, a sibling of the "common" folder the install lives in.
                if (mod.Parent?.Parent?.Parent is { } steamapps)
                    files.AddGmasIn(Path.Combine(steamapps.FullName, "workshop", "content", "4000"));
            }

            // A named root gets the same treatment as the mod directory, addon archives included. It is
            // typically a copy of one - content staged onto a build server keeps the shape it had - and
            // finding its loose files while ignoring its .gma files would resolve a map's props on the
            // developer's machine and not on the server, which is the whole case this exists for.
            foreach (var root in Named(extraRoots))
            {
                files.AddGmasIn(root);
                files.AddGmasIn(Path.Combine(root, "addons"));
                files.AddGmasIn(Path.Combine(root, "cache", "workshop"));
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            files.OpenPakfile(bspPath);
            files.PakMs = clock.ElapsedMilliseconds;
            return files;
        }

        /// <summary>
        /// The mod directory a map belongs to: the nearest folder at or above <paramref name="folder"/>
        /// holding a gameinfo, or, when there is none, the folder above <paramref name="folder"/>.
        /// </summary>
        private static DirectoryInfo? FindMod(DirectoryInfo? folder)
        {
            try
            {
                for (var at = folder; at is not null; at = at.Parent)
                {
                    if (File.Exists(Path.Combine(at.FullName, GameInfo.FileName)))
                        return at;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return folder?.Parent;
        }

        /// <summary>
        /// A directory in one canonical form: absolute, and without a trailing separator. A path typed
        /// as <c>...\garrysmod\</c> and one inferred as <c>...\garrysmod</c> are the same root, and
        /// treating them as two both searches it twice and breaks anything that walks up from it.
        /// Null for a path that cannot be a directory at all.
        /// </summary>
        internal static string? Canonical(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static bool SamePath(string a, string? b) =>
            b is not null && string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves a model path against a content root, refusing anything that escapes it.
        ///
        /// **The path is not ours.** It is the model name a static prop carries in the BSP's prop lump,
        /// and a BSP is a file from somebody else - a map downloaded from the workshop, a map handed to
        /// a build server by whoever submitted it. <see cref="Path.Combine"/> takes such a string at its
        /// word in two ways that both leave the root behind: <c>../../..</c> walks up out of it, and on
        /// Windows a rooted second argument (<c>C:/Users/…</c>) makes Combine discard the first entirely
        /// and return the absolute path unchanged.
        ///
        /// Neither turns into much on its own - the bytes are parsed as a collision hull and thrown away
        /// if they are not one - but "read an arbitrary file the user can read" is not a thing a nav
        /// generator should do, and the diagnostics do print what they found and where. Resolving fully
        /// and checking containment costs one call per lookup on a path that is about to hit the disk
        /// anyway.
        /// </summary>
        private static bool TryResolveUnder(string root, string relativePath, out string full)
        {
            full = string.Empty;

            try
            {
                string candidate = Path.GetFullPath(
                    Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                string prefix = Path.GetFullPath(root);

                if (!prefix.EndsWith(Path.DirectorySeparatorChar))
                    prefix += Path.DirectorySeparatorChar;

                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                full = candidate;
                return true;
            }
            catch (ArgumentException) { return false; }      // illegal characters in the name
            catch (PathTooLongException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        /// <summary>Every explicitly named root: the call's own, plus the process-wide ones.</summary>
        private static IEnumerable<string> Named(IEnumerable<string>? extraRoots)
            => (extraRoots ?? []).Concat(AdditionalRoots);

        private void AddRoot(string path)
        {
            if (Canonical(path) is { } root && Directory.Exists(root) &&
                !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        /// <summary>
        /// Adds the game directories a Garry's Mod install mounts on top of its own content. Each
        /// becomes an ordinary root, so its loose files are searched and its VPKs queued like any other.
        /// </summary>
        private void AddMountedGames(string modDirectory)
        {
            foreach (var game in GameMounts.Read(modDirectory))
                AddRoot(game);
        }

        /// <summary>
        /// Collects addon archives from a directory, and from one level of subdirectories beneath it.
        ///
        /// Subscribed content lands in two shapes and both have to be looked at. The game downloads
        /// straight into <c>garrysmod/cache/workshop</c> as loose .gma files, while Steam's own
        /// workshop tree gives every item its own numbered folder with the archive inside. Scanning
        /// only the top level finds 15 of this machine's addons and misses 55.
        /// </summary>
        private void AddGmasIn(string directory)
        {
            try
            {
                if (Canonical(directory) is not { } dir || !Directory.Exists(dir)) return;

                foreach (var gma in Directory.EnumerateFiles(dir, "*.gma", SearchOption.TopDirectoryOnly))
                    Queue(gmaPaths, gma);

                foreach (var child in Directory.EnumerateDirectories(dir))
                    foreach (var gma in Directory.EnumerateFiles(child, "*.gma", SearchOption.TopDirectoryOnly))
                        Queue(gmaPaths, gma);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Adds an archive to its list unless it is already queued, under any spelling of its path.</summary>
        private void Queue(List<string> list, string path)
        {
            if (Canonical(path) is { } full && queued.Add(full))
                list.Add(full);
        }

        /// <summary>
        /// Adds what a gameinfo declared: directories become content roots, VPKs are queued for
        /// indexing on first use like any other.
        /// </summary>
        private void AddSearchPaths(IReadOnlyList<GameInfo.Mount> mounts)
        {
            foreach (var mount in mounts)
            {
                if (mount.IsArchive)
                    AddVpk(mount.Path);
                else
                    AddRoot(mount.Path);
            }
        }

        /// <summary>
        /// Queues one named VPK.
        ///
        /// A gameinfo names the logical archive - <c>hl2_misc.vpk</c> - where a multi-part set is stored
        /// as <c>hl2_misc_dir.vpk</c> plus numbered body files. The engine resolves the one to the
        /// other, so this does too; naming the file that does not exist would silently mount nothing.
        /// </summary>
        private void AddVpk(string path)
        {
            string directoryForm = path[..^4] + "_dir.vpk";

            string? actual =
                File.Exists(directoryForm) ? directoryForm :
                File.Exists(path) ? path : null;

            if (actual is not null)
                Queue(vpkPaths, actual);
        }

        private void AddVpksIn(string root)
        {
            try
            {
                // Only the directory archives. The numbered ones beside them hold bodies and are opened
                // through the directory that indexes them; treating one as an archive in its own right
                // finds no directory and wastes a file handle.
                foreach (var vpk in Directory.EnumerateFiles(root, "*_dir.vpk", SearchOption.TopDirectoryOnly))
                    Queue(vpkPaths, vpk);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void OpenPakfile(string bspPath)
        {
            Stream? file = null;

            try
            {
                // Shared for writing and deleting too: this handle stays open for as long as the lookup
                // does, and must not stop a later step from rewriting or replacing the map.
                file = new FileStream(bspPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new BinaryReader(file, System.Text.Encoding.ASCII, leaveOpen: true);

                // A file that is not a BSP we read throws here, and is treated like any other pakfile
                // that will not open: the map's packed content is lost, the rest of the lookup is not.
                var header = BspHeader.Read(r, bspPath);
                int offset = header[LumpPakfile].Offset, length = header[LumpPakfile].Length;

                if (length <= 0 || offset < 0 || offset >= file.Length) return;

                // The engine never compresses the pakfile lump itself, but a lump that does carry the
                // tag is decompressed into memory rather than misread as a zip.
                r.BaseStream.Seek(offset, SeekOrigin.Begin);
                bool tagged = length >= 4 && r.ReadUInt32() == 0x414D5A4C;     // "LZMA"

                Stream lump = tagged
                    ? new MemoryStream(LzmaLump.Read(r, offset, length), writable: false)
                    : new Window(file, offset, Math.Min(length, file.Length - offset));

                var archive = ZipArchive.OpenArchive(lump, new ReaderOptions { LeaveStreamOpen = true });

                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory && entry.Key is { } key)
                        pakEntries[key.Replace('\\', '/')] = entry;
                }

                pak = archive;
                pakStream = lump;

                if (tagged)
                    file.Dispose();

                file = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A pakfile that will not open costs the packed content, not the lookup. Which exception
                // a damaged zip produces depends on where it is damaged, so all of them are taken here.
                pakEntries.Clear();
                pak?.Dispose();
                pak = null;
                pakStream = null;
            }
            finally
            {
                file?.Dispose();
            }
        }

        public bool TryRead(string relativePath, out byte[] bytes)
        {
            string path = relativePath.Replace('\\', '/').TrimStart('/');
            bytes = [];

            if (pakEntries.TryGetValue(path, out var entry))
            {
                try
                {
                    lock (archives)
                    {
                        using var s = entry.OpenEntryStream();
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }

                    System.Threading.Interlocked.Increment(ref ReadsFromPakfile);
                    return true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A damaged entry, or a compression method nothing here reads: fall through to the
                    // installed copy, which is the best answer left.
                    bytes = [];
                }
            }

            foreach (var root in roots)
            {
                if (!TryResolveUnder(root, path, out string full))
                    continue;

                if (File.Exists(full))
                {
                    try { bytes = File.ReadAllBytes(full); System.Threading.Interlocked.Increment(ref ReadsFromDisk); return true; }
                    catch (IOException) { }
                }
            }

            lock (archives)
            {
                // Addons before the base game, matching how the engine mounts them: a workshop addon
                // that replaces a stock prop is meant to win.
                foreach (var gma in OpenGmas())
                    if (gma.TryRead(path, out bytes)) { System.Threading.Interlocked.Increment(ref ReadsFromGma); return true; }

                foreach (var vpk in OpenVpks())
                    if (vpk.TryRead(path, out bytes)) { System.Threading.Interlocked.Increment(ref ReadsFromVpk); return true; }
            }

            missing[path] = 0;
            return false;
        }

        /// <summary>
        /// The addon archives, indexed on first use.
        ///
        /// Lazily and in parallel for the same reasons as the VPKs, but it matters much more here: a
        /// subscribed Garry's Mod install has hundreds of these - 652 on the machine this was written on
        /// - and indexing one means walking its entire file list, because a .gma stores no offsets and a
        /// body's position is the sum of every size before it.
        /// </summary>
        private List<GmaArchive> OpenGmas()
        {
            if (gmas is not null) return gmas;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = new GmaArchive?[gmaPaths.Count];

            System.Threading.Tasks.Parallel.For(0, gmaPaths.Count, SourceKitConcurrency.Options,
                i => found[i] = GmaArchive.TryOpen(gmaPaths[i]));

            var opened = new List<GmaArchive>();

            foreach (var gma in found)
                if (gma is not null) opened.Add(gma);

            GmaMs = clock.ElapsedMilliseconds;
            return gmas = opened;
        }

        /// <summary>
        /// The VPK archives, parsing their directories on first use. Callers hold
        /// <see cref="archives"/>, which is what makes the one-time build safe to do lazily.
        /// </summary>
        private List<VpkArchive> OpenVpks()
        {
            if (vpks is not null) return vpks;

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Nine independent files, each needing its whole directory parsed. Doing them one at a time
            // was 133ms; they share nothing, so they are parsed together and collected in path order
            // afterwards to keep lookup priority stable.
            //
            // Being lazy alone was not enough here, and it is worth recording why. A map that names ten
            // models nobody has still forces every VPK open, because "not in any VPK" is not a
            // conclusion you can reach without looking - so the laziness only pays on a map where every
            // model resolves earlier, and this one does not.
            var found = new VpkArchive?[vpkPaths.Count];

            System.Threading.Tasks.Parallel.For(0, vpkPaths.Count, SourceKitConcurrency.Options,
                i => found[i] = VpkArchive.TryOpen(vpkPaths[i]));

            var opened = new List<VpkArchive>(vpkPaths.Count);

            foreach (var vpk in found)
                if (vpk is not null) opened.Add(vpk);

            VpkMs = clock.ElapsedMilliseconds;
            return vpks = opened;
        }

        public void Dispose()
        {
            if (vpks is not null)
            {
                foreach (var vpk in vpks) vpk.Dispose();
                vpks = null;
            }

            if (gmas is not null)
            {
                foreach (var gma in gmas) gma.Dispose();
                gmas = null;
            }

            pak?.Dispose();
            pak = null;

            pakStream?.Dispose();
            pakStream = null;
        }

        /// <summary>
        /// A read-only view of one stretch of a stream, so the pakfile can be read as a zip in place.
        /// Disposing it disposes the stream underneath, which only it holds.
        /// </summary>
        private sealed class Window(Stream inner, long start, long length) : Stream
        {
            private long position;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;

            public override long Position
            {
                get => position;
                set => position = Math.Clamp(value, 0, length);
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                long left = length - position;
                if (left <= 0 || buffer.Length == 0) return 0;

                if (buffer.Length > left)
                    buffer = buffer[..(int)left];

                inner.Seek(start + position, SeekOrigin.Begin);
                int read = inner.Read(buffer);
                position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                _ => length + offset,
            };

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
