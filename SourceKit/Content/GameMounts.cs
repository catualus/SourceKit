using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SourceKit
{
    /// <summary>
    /// The other games a Garry's Mod install mounts, as directories to search.
    ///
    /// Garry's Mod does not mount Counter-Strike: Source, Team Fortress 2 or the Half-Life 2 episodes
    /// through its gameinfo - its own gameinfo says so - but through two files in <c>garrysmod/cfg</c>:
    ///
    /// - <c>mount.cfg</c>, a <c>"mountcfg"</c> block of name and folder pairs a person writes by hand,
    ///   usually to point at a dedicated server's copy of a game;
    /// - <c>mountdepots.txt</c>, a <c>"gamedepotsystem"</c> block of game folder names set to 1 or 0,
    ///   which is what the in-game mount menu writes. The folder lives in whichever Steam library the
    ///   game is installed in, found through Steam's <c>libraryfolders.vdf</c> and each game's
    ///   <c>appmanifest_&lt;appid&gt;.acf</c>.
    ///
    /// None of those games' folders sit beside a GMod install, so without this every prop a map takes
    /// from one of them resolves nowhere: no collision for Meshwright, no occluder for Viswright, and
    /// nothing but the missing list to say so.
    ///
    /// Best-effort like the rest of the content lookup: a file that is missing or will not parse adds
    /// nothing, and only directories that exist are returned.
    /// </summary>
    internal static class GameMounts
    {
        /// <summary>
        /// The Steam apps each mountable game folder is installed by, most likely first. The Half-Life 2
        /// episodes and Lost Coast ship inside Half-Life 2's own install since its 2023 update, and
        /// under their own app ids on installs older than that.
        /// </summary>
        private static readonly Dictionary<string, int[]> Depots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hl2"] = [220],
            ["cstrike"] = [240],
            ["hl1"] = [280],
            ["dod"] = [300],
            ["hl2mp"] = [320],
            ["lostcoast"] = [220, 340],
            ["hl1mp"] = [360],
            ["episodic"] = [220, 380],
            ["portal"] = [400],
            ["ep2"] = [220, 420],
            ["tf"] = [440],
            ["left4dead"] = [500],
            ["left4dead2"] = [550],
            ["portal2"] = [620],
            ["swarm"] = [630],
        };

        /// <summary>
        /// Every game directory <paramref name="modDirectory"/> mounts: <c>mount.cfg</c>'s first, in the
        /// order written, then the enabled depots in the order <c>mountdepots.txt</c> lists them.
        /// </summary>
        internal static IReadOnlyList<string> Read(string modDirectory)
        {
            var found = new List<string>();

            if (GameFiles.Canonical(modDirectory) is not { } mod)
                return found;

            void Add(string path)
            {
                if (GameFiles.Canonical(path) is { } full && Directory.Exists(full) &&
                    !found.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(full);
                }
            }

            string cfg = Path.Combine(mod, "cfg");

            foreach (var entry in Block(Path.Combine(cfg, "mount.cfg"), "mountcfg"))
            {
                if (entry.Value is not { Length: > 0 } value)
                    continue;

                try { Add(Path.IsPathRooted(value) ? value : Path.Combine(mod, value)); }
                catch (ArgumentException) { }
            }

            var depots = new List<string>();

            foreach (var entry in Block(Path.Combine(cfg, "mountdepots.txt"), "gamedepotsystem"))
            {
                if (entry.Value == "1" && Depots.ContainsKey(entry.Key))
                    depots.Add(entry.Key);
            }

            if (depots.Count == 0)
                return found;

            var libraries = SteamLibraries(mod);

            foreach (string depot in depots)
            {
                foreach (int app in Depots[depot])
                {
                    if (InstallDirectory(libraries, app) is not { } install)
                        continue;

                    string folder = Path.Combine(install, depot);

                    if (Directory.Exists(folder))
                    {
                        Add(folder);
                        break;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// The <c>steamapps</c> folder of every Steam library: the one the game itself is installed in
        /// (<c>&lt;library&gt;/steamapps/common/&lt;game&gt;/&lt;mod&gt;</c>), then every library its
        /// <c>libraryfolders.vdf</c> lists. Only the main Steam folder carries that file, so for a game
        /// in a secondary library Steam's own location is taken from the registry instead.
        /// </summary>
        private static List<string> SteamLibraries(string mod)
        {
            var libraries = new List<string>();

            void Add(string path)
            {
                if (GameFiles.Canonical(path) is { } full && Directory.Exists(full) &&
                    !libraries.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    libraries.Add(full);
                }
            }

            if (Directory.GetParent(mod)?.Parent is { } common &&
                common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                common.Parent is { } steamapps)
            {
                Add(steamapps.FullName);
            }

            string? list = null;

            foreach (string library in libraries)
            {
                string candidate = Path.Combine(library, "libraryfolders.vdf");
                if (File.Exists(candidate)) { list = candidate; break; }
            }

            if (list is null && SteamFromRegistry() is { } steam)
                list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");

            if (list is not null)
            {
                foreach (var library in Block(list, "libraryfolders"))
                {
                    foreach (var field in library.Children)
                    {
                        if (field.Key.Equals("path", StringComparison.OrdinalIgnoreCase) && field.Value is { Length: > 0 } path)
                        {
                            try { Add(Path.Combine(Unescape(path), "steamapps")); }
                            catch (ArgumentException) { }
                        }
                    }
                }
            }

            return libraries;
        }

        /// <summary>
        /// Where <paramref name="app"/> is installed, from the first library holding its manifest:
        /// <c>&lt;library&gt;/common/&lt;installdir&gt;</c>.
        /// </summary>
        private static string? InstallDirectory(List<string> libraries, int app)
        {
            foreach (string library in libraries)
            {
                string manifest = Path.Combine(library, $"appmanifest_{app}.acf");

                foreach (var field in Block(manifest, "AppState"))
                {
                    if (field.Key.Equals("installdir", StringComparison.OrdinalIgnoreCase) && field.Value is { Length: > 0 } name)
                    {
                        try { return Path.Combine(library, "common", Unescape(name)); }
                        catch (ArgumentException) { return null; }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Steam writes its own files with KeyValues escapes, so a path arrives as
        /// <c>C:\\Program Files (x86)\\Steam</c>. The hand-written mount.cfg is read without escapes,
        /// like the engine reads it.
        /// </summary>
        private static string Unescape(string value) => value.Replace(@"\\", @"\");

        private static string? SteamFromRegistry()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("SteamPath") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        /// <summary>One KeyValues entry: a key with either a value or a block of children.</summary>
        internal sealed record Entry(string Key, string? Value, List<Entry> Children);

        /// <summary>The entries inside the top-level block named <paramref name="name"/> in a file, or none.</summary>
        private static List<Entry> Block(string path, string name)
        {
            try
            {
                if (!File.Exists(path))
                    return [];

                foreach (var entry in Parse(File.ReadAllText(path)))
                {
                    if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return entry.Children;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return [];
        }

        /// <summary>
        /// Reads KeyValues text into a tree. Lenient, since these are files people edit by hand: an
        /// unbalanced brace ends the block it is in rather than the whole read.
        /// </summary>
        internal static List<Entry> Parse(string text)
        {
            var tokens = GameInfo.Tokenise(text);
            int at = 0;
            return Parse(tokens, ref at);
        }

        private static List<Entry> Parse(List<string> tokens, ref int at)
        {
            var entries = new List<Entry>();

            while (at < tokens.Count)
            {
                string key = tokens[at++];

                if (key == "}")
                    break;

                if (key == "{" || at >= tokens.Count)
                    continue;

                if (tokens[at] == "{")
                {
                    at++;
                    entries.Add(new Entry(key, null, Parse(tokens, ref at)));
                }
                else if (tokens[at] != "}")
                {
                    entries.Add(new Entry(key, tokens[at++], []));
                }
            }

            return entries;
        }
    }
}
