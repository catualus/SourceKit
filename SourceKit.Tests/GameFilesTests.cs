using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// Where a model gets read from, and in what order.
    ///
    /// This is tested because it is the part with no visible failure mode. If the priority is wrong, a
    /// map that ships a modified prop silently collides against the stock one - the build succeeds, the
    /// mesh looks reasonable, and the areas are subtly in the wrong place. Nothing downstream can tell.
    ///
    /// The pakfile path in particular had no coverage from real use: every model on both development
    /// maps resolves from loose files, so the zip reader inside a .bsp was written, wired up and never
    /// executed. A synthetic map is the only way to exercise it without shipping a fixture.
    /// </summary>
    public class GameFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_files_{Guid.NewGuid():N}");

        /// <summary>The mod directory, whose <c>maps</c> subdirectory holds the .bsp.</summary>
        private string Mod => Path.Combine(root, "garrysmod");

        public GameFilesTests() => Directory.CreateDirectory(Path.Combine(Mod, "maps"));

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        /// <summary>
        /// Writes a .bsp carrying nothing but a pakfile lump, holding the given entries.
        /// </summary>
        private string WriteBsp(string name, params (string Path, string Body)[] packed)
            => WriteBspAt(Path.Combine(Mod, "maps", name), lzma: false, packed);

        /// <summary>
        /// Writes a .bsp at any path. With <paramref name="lzma"/>, every entry is stored the way
        /// <c>bspzip -repack</c> stores it in a shipped map: zip method 14.
        /// </summary>
        private static string WriteBspAt(string path, bool lzma, params (string Path, string Body)[] packed)
        {
            const int LumpPakfile = 40;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            byte[] zip;

            using (var zipStream = new MemoryStream())
            {
                if (lzma)
                {
                    using var writer = new ZipWriter(zipStream, new ZipWriterOptions(CompressionType.LZMA) { LeaveStreamOpen = true });

                    foreach (var (entryPath, body) in packed)
                        writer.Write(entryPath, new MemoryStream(Encoding.UTF8.GetBytes(body)), (DateTime?)null);
                }
                else
                {
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);

                    foreach (var (entryPath, body) in packed)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
                        writer.Write(body);
                    }
                }

                zip = zipStream.ToArray();
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(0x50534256);            // 'VBSP'
            w.Write(20);

            long directory = ms.Position;
            for (int i = 0; i < BspFile.HeaderLumps * 4; i++) w.Write(0);
            w.Write(0);                     // map revision

            int pakAt = (int)ms.Position;

            if (packed.Length > 0) w.Write(zip);

            ms.Seek(directory + LumpPakfile * 16, SeekOrigin.Begin);
            w.Write(pakAt);
            w.Write(packed.Length > 0 ? zip.Length : 0);

            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        private void WriteLoose(string relativePath, string body) => WriteFile(Mod, relativePath, body);

        private static void WriteFile(string directory, string relativePath, string body)
        {
            string full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        /// <summary>A gameinfo shaped like Garry's Mod's: the mod itself, then the install's sourceengine folder.</summary>
        private const string GameInfoText = """
            "GameInfo"
            {
                FileSystem
                {
                    SearchPaths
                    {
                        game+mod            garrysmod/garrysmod.vpk
                        mod+mod_write+default_write_path        |gameinfo_path|.
                        game                |all_source_engine_paths|sourceengine
                    }
                }
            }
            """;

        private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        [Fact]
        public void ReadsAModelPackedInsideTheMap()
        {
            string bsp = WriteBsp("packed.bsp", ("models/custom/thing.phy", "from the pakfile"));

            using var files = GameFiles.Open(bsp);

            Assert.Equal(1, files.PakfileEntries);
            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("from the pakfile", Text(bytes));
            Assert.Equal(1, files.ReadsFromPakfile);
        }

        [Fact]
        public void ThePakfileWinsOverALooseFileOfTheSameName()
        {
            // The case the priority order exists for. A mapper packs a modified prop precisely because
            // the installed one is not what they want; reading the installed one instead is silent and
            // wrong.
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("override.bsp", ("models/custom/thing.phy", "packed"));

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("packed", Text(bytes));
        }

        [Fact]
        public void FallsBackToALooseFileWhenTheMapPacksNothing()
        {
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("plain.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("installed", Text(bytes));
            Assert.Equal(1, files.ReadsFromDisk);
        }

        [Fact]
        public void BackslashesAndLeadingSlashesResolveTheSameWay()
        {
            // Model paths come out of the prop lump in Windows form and out of a VPK in Unix form, and
            // the two have to name the same file or half a map's props go missing on one of them.
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("slashes.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead(@"models\custom\thing.phy", out _));
            Assert.True(files.TryRead("/models/custom/thing.phy", out _));
        }

        [Fact]
        public void AMissingFileIsReportedRatherThanThrown()
        {
            string bsp = WriteBsp("empty.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.False(files.TryRead("models/nothing/here.phy", out var bytes));
            Assert.Empty(bytes);
            Assert.Contains("models/nothing/here.phy", files.Missing);
        }

        [Fact]
        public void AMapWithACorruptPakfileStillResolvesLooseContent()
        {
            // Third-party maps are the norm and some are damaged. A pakfile that will not open must cost
            // the packed content, not the whole lookup - the build has to keep going.
            string bsp = Path.Combine(Mod, "maps", "corrupt.bsp");

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x50534256);
                w.Write(20);

                long directory = ms.Position;
                for (int i = 0; i < BspFile.HeaderLumps * 4; i++) w.Write(0);
                w.Write(0);

                int at = (int)ms.Position;
                w.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });     // not a zip

                ms.Seek(directory + 40 * 16, SeekOrigin.Begin);
                w.Write(at);
                w.Write(8);

                File.WriteAllBytes(bsp, ms.ToArray());
            }

            WriteLoose("models/custom/thing.phy", "installed");

            using var files = GameFiles.Open(bsp);

            Assert.Equal(0, files.PakfileEntries);
            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("installed", Text(bytes));
        }

        /// <summary>
        /// A map outside any game directory resolves nothing, and says nothing about it.
        ///
        /// This is the failure the -content flag exists for, and it is worth pinning down because it
        /// looks like success: the run completes, reports its numbers, and writes a mesh with no prop
        /// collision anywhere in it. The only visible symptom is areas floating where props stand,
        /// which reads as a generator bug rather than a missing content path.
        /// </summary>
        [Fact]
        public void AMapOutsideAGameDirectoryFindsNothingUntilARootIsNamed()
        {
            // A .bsp on its own, nowhere near the mod directory holding the model it names.
            string loose = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(Path.Combine(loose, "maps"));

            string bsp = Path.Combine(loose, "maps", "orphan.bsp");
            File.WriteAllBytes(bsp, File.ReadAllBytes(WriteBsp("donor.bsp")));

            WriteLoose("models/custom/thing.phy", "installed");

            using (var blind = GameFiles.Open(bsp))
            {
                Assert.False(blind.TryRead("models/custom/thing.phy", out _));
                Assert.Contains("models/custom/thing.phy", blind.Missing);
            }

            // Named explicitly, the same lookup resolves.
            using (var told = GameFiles.Open(bsp, [Mod]))
            {
                Assert.True(told.TryRead("models/custom/thing.phy", out var found));
                Assert.Equal("installed", Text(found));
            }
        }

        /// <summary>
        /// A model name is a string from somebody else's map, and it does not get to leave the root.
        ///
        /// Both of these escape <see cref="Path.Combine"/>: a relative walk goes up out of the content
        /// directory, and on Windows a rooted second argument makes Combine discard the first entirely
        /// and hand back the absolute path. Neither amounts to much on its own - the bytes are parsed as
        /// a collision hull and discarded if they are not one - but reading arbitrary files the user
        /// happens to have access to is not something a nav generator should do at the say-so of a
        /// downloaded map.
        /// </summary>
        [Fact]
        public void AModelNameCannotReadOutsideTheContentRoot()
        {
            string secret = Path.Combine(root, "secret.txt");
            File.WriteAllText(secret, "private");

            // Inside the mod directory, a normal lookup still works.
            WriteLoose("models/ok.phy", "fine");

            string bsp = WriteBsp("map.bsp");
            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/ok.phy", out var ok));
            Assert.Equal("fine", Text(ok));

            // Walking up out of the root.
            Assert.False(files.TryRead("../secret.txt", out _));
            Assert.False(files.TryRead("models/../../secret.txt", out _));

            // A rooted path, which Path.Combine would otherwise return verbatim.
            Assert.False(files.TryRead(secret, out _));
        }

        /// <summary>
        /// The process-wide root reaches call sites that were never given one.
        ///
        /// <see cref="GameFiles.AdditionalRoots"/> is static precisely so that a caller cannot lose it:
        /// a BSP is opened from a dozen places, and one that forgets to pass a root along produces a
        /// silently prop-less mesh rather than an error. This asserts the guarantee that makes the
        /// static worth having.
        /// </summary>
        [Fact]
        public void TheProcessWideRootIsHonouredWithoutBeingPassed()
        {
            string loose = Path.Combine(root, "elsewhere2");
            Directory.CreateDirectory(Path.Combine(loose, "maps"));

            string bsp = Path.Combine(loose, "maps", "orphan.bsp");
            File.WriteAllBytes(bsp, File.ReadAllBytes(WriteBsp("donor2.bsp")));

            WriteLoose("models/custom/thing.phy", "installed");

            var previous = GameFiles.AdditionalRoots;
            try
            {
                GameFiles.AdditionalRoots = [Mod];

                // No extraRoots argument, which is the whole point.
                using var files = GameFiles.Open(bsp);

                Assert.True(files.TryRead("models/custom/thing.phy", out var found));
                Assert.Equal("installed", Text(found));
            }
            finally
            {
                GameFiles.AdditionalRoots = previous;
            }
        }

        /// <summary>
        /// A shipped map's pakfile, after <c>bspzip -repack</c>: every entry LZMA-compressed (zip method
        /// 14), which the framework's own zip reader refuses. On the real rp_downtown_meowy.bsp that was
        /// 3,534 of 3,540 entries, every packed model among them, and the refusal was swallowed - so a
        /// packed override of a stock prop silently resolved to the stock copy instead.
        /// </summary>
        [Fact]
        public void ReadsAnLzmaEntryFromARepackedMap()
        {
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBspAt(Path.Combine(Mod, "maps", "repacked.bsp"), lzma: true,
                ("models/custom/thing.phy", "packed, and compressed the way a shipped map is"));

            // The fixture is what it claims to be: the first local header's method field is 14.
            var raw = File.ReadAllBytes(bsp);
            int zipAt = 8 + BspFile.HeaderLumps * 16 + 4;
            Assert.Equal(0x04034b50, BitConverter.ToInt32(raw, zipAt));
            Assert.Equal(14, BitConverter.ToUInt16(raw, zipAt + 8));

            using var files = GameFiles.Open(bsp);

            Assert.Equal(1, files.PakfileEntries);
            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("packed, and compressed the way a shipped map is", Text(bytes));
            Assert.Equal(1, files.ReadsFromPakfile);
        }

        /// <summary>
        /// Maps Garry's Mod keeps outside <c>garrysmod/maps</c>: a legacy addon's own <c>maps</c> folder,
        /// and what a server sent to the client. Two levels up from either is a folder with no gameinfo,
        /// VPKs or addons, and taking it as the mod lost nearly every stock and workshop prop.
        /// </summary>
        [Theory]
        [InlineData("addons/rp_city")]
        [InlineData("download")]
        public void AMapOutsideTheModsOwnMapsFolderStillSeesTheWholeGame(string folder)
        {
            File.WriteAllText(Path.Combine(Mod, GameInfo.FileName), GameInfoText);

            WriteLoose("models/mod/thing.phy", "from the mod");
            WriteFile(Path.Combine(root, "sourceengine"), "models/stock/thing.phy", "from sourceengine");

            // The map's own folder - the addon, or download - holds its own content, and it wins over
            // the mod's copy the way a mounted addon does.
            string own = Path.Combine(Mod, folder.Replace('/', Path.DirectorySeparatorChar));
            WriteFile(own, "models/own/thing.phy", "from the map's folder");
            WriteFile(own, "models/mod/shadowed.phy", "from the map's folder");
            WriteLoose("models/mod/shadowed.phy", "from the mod");

            string bsp = WriteBspAt(Path.Combine(own, "maps", "rp_city.bsp"), lzma: false);

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/mod/thing.phy", out var mod));
            Assert.Equal("from the mod", Text(mod));
            Assert.True(files.TryRead("models/stock/thing.phy", out var stock));
            Assert.Equal("from sourceengine", Text(stock));
            Assert.True(files.TryRead("models/own/thing.phy", out var ownBytes));
            Assert.Equal("from the map's folder", Text(ownBytes));
            Assert.True(files.TryRead("models/mod/shadowed.phy", out var shadowed));
            Assert.Equal("from the map's folder", Text(shadowed));
        }

        /// <summary>
        /// A named content root typed with a trailing separator. The gameinfo under it must still
        /// resolve |all_source_engine_paths| to the install, not to the mod folder itself, and the root
        /// must not be counted twice beside the same folder spelt without one.
        /// </summary>
        [Fact]
        public void ANamedRootWithATrailingSeparatorMountsTheSameContent()
        {
            File.WriteAllText(Path.Combine(Mod, GameInfo.FileName), GameInfoText);
            WriteFile(Path.Combine(root, "sourceengine"), "models/stock/thing.phy", "from sourceengine");

            // Deep enough that the guess at sibling base-game folders cannot find sourceengine either.
            string bsp = WriteBspAt(Path.Combine(root, "elsewhere", "build", "maps", "orphan.bsp"), lzma: false);

            using var plain = GameFiles.Open(bsp, [Mod]);
            using var slashed = GameFiles.Open(bsp, [Mod + Path.DirectorySeparatorChar]);
            using var both = GameFiles.Open(bsp, [Mod, Mod + Path.DirectorySeparatorChar, Mod + "/"]);

            Assert.True(slashed.TryRead("models/stock/thing.phy", out var found));
            Assert.Equal("from sourceengine", Text(found));

            Assert.Equal(plain.RootCount, slashed.RootCount);
            Assert.Equal(plain.RootCount, both.RootCount);
        }

        /// <summary>
        /// Each archive is queued once, however many ways it is reached: a VPK the gameinfo names that
        /// also sits in a root, and a .gma under a named root that is also the mod. Duplicates meant the
        /// largest HL2 directories were parsed twice on every first miss.
        /// </summary>
        [Fact]
        public void AnArchiveReachedTwiceIsQueuedOnce()
        {
            File.WriteAllText(Path.Combine(Mod, GameInfo.FileName), GameInfoText);
            File.WriteAllBytes(Path.Combine(Mod, "garrysmod_dir.vpk"), []);
            Directory.CreateDirectory(Path.Combine(Mod, "addons"));
            File.WriteAllBytes(Path.Combine(Mod, "addons", "workshop.gma"), []);

            string bsp = WriteBsp("dupes.bsp");

            using var files = GameFiles.Open(bsp, [Mod, Mod + Path.DirectorySeparatorChar]);

            Assert.Equal(1, files.VpkCount);
            Assert.Equal(1, files.GmaCount);
        }

        /// <summary>
        /// Games Garry's Mod mounts on top of its own content, through <c>cfg/mount.cfg</c> (folders named
        /// by hand) and <c>cfg/mountdepots.txt</c> (the in-game mount menu, found through Steam's library
        /// list and app manifests). A GMod install has none of those games beside it, so they were never
        /// searched and every prop a map took from one went missing.
        /// </summary>
        [Fact]
        public void GamesMountedByGarrysModAreSearchedAfterItsOwnContent()
        {
            // Steam's main library, holding Garry's Mod and a Counter-Strike: Source left unmounted.
            string steam = Path.Combine(root, "Steam");
            string steamapps = Path.Combine(steam, "steamapps");
            string gmod = Path.Combine(steamapps, "common", "GarrysMod", "garrysmod");

            // A second library, holding Team Fortress 2.
            string library = Path.Combine(root, "Library Two");

            WriteFile(steamapps, "libraryfolders.vdf", $$"""
                "libraryfolders"
                {
                    "0"
                    {
                        "path"      "{{steam.Replace(@"\", @"\\")}}"
                        "apps"      { "4000" "1" "240" "1" }
                    }
                    "1"
                    {
                        "path"      "{{library.Replace(@"\", @"\\")}}"
                        "apps"      { "440" "1" }
                    }
                }
                """);

            WriteFile(steamapps, "appmanifest_240.acf", "\"AppState\"\n{\n\t\"appid\" \"240\"\n\t\"installdir\" \"Counter-Strike Source\"\n}\n");
            WriteFile(Path.Combine(steamapps, "common", "Counter-Strike Source", "cstrike"), "models/css/unmounted.phy", "unmounted");

            WriteFile(Path.Combine(library, "steamapps"), "appmanifest_440.acf", "\"AppState\"\n{\n\t\"appid\" \"440\"\n\t\"installdir\" \"Team Fortress 2\"\n}\n");
            string tf = Path.Combine(library, "steamapps", "common", "Team Fortress 2", "tf");
            WriteFile(tf, "models/tf/thing.phy", "from tf2");
            WriteFile(tf, "models/shared.phy", "from tf2");
            File.WriteAllBytes(Path.Combine(tf, "tf2_misc_dir.vpk"), []);

            // A game named by hand in mount.cfg, the way a dedicated server's copy is mounted.
            string server = Path.Combine(root, "server", "dod");
            WriteFile(server, "models/dod/thing.phy", "from mount.cfg");

            WriteFile(gmod, GameInfo.FileName, GameInfoText);
            WriteFile(gmod, "models/shared.phy", "from garrysmod");
            WriteFile(gmod, "cfg/mount.cfg", $$"""
                // DO NOT add a slash to the end of the filename
                "mountcfg"
                {
                //	"cstrike"	"C:\steamcmd\steamapps\common\Counter-Strike Source Dedicated Server\cstrike"
                	"dod"		"{{server}}"
                }
                """);
            WriteFile(gmod, "cfg/mountdepots.txt", """
                "gamedepotsystem"
                {
                	"hl2"		"1"
                	"cstrike"		"0"
                	"tf"		"1"
                }
                """);

            string bsp = WriteBspAt(Path.Combine(gmod, "maps", "gm_mounted.bsp"), lzma: false);

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/tf/thing.phy", out var tfBytes));
            Assert.Equal("from tf2", Text(tfBytes));
            Assert.True(files.TryRead("models/dod/thing.phy", out var dod));
            Assert.Equal("from mount.cfg", Text(dod));

            // Garry's Mod's own copy wins over a mounted game's.
            Assert.True(files.TryRead("models/shared.phy", out var shared));
            Assert.Equal("from garrysmod", Text(shared));

            // A game switched off in the mount menu stays unmounted.
            Assert.False(files.TryRead("models/css/unmounted.phy", out _));

            // The mounted game's own VPKs are queued too; that is where nearly all of its content is.
            Assert.Equal(1, files.VpkCount);
        }
    }
}
