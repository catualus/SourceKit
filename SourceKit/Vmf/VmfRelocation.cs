using System;
using System.IO;

namespace SourceKit.Vmf
{
    /// <summary>
    /// What changes when a VMF is saved somewhere else.
    ///
    /// Only one thing does: VBSP looks for a func_instance's file relative to the VMF that names it,
    /// so a copy in another folder has to name its instances by a path that reaches the same files
    /// from there. Everything else in a map - materials, models, sounds - is found through the game's
    /// search paths and does not care where the VMF is.
    /// </summary>
    public static class VmfRelocation
    {
        /// <summary>
        /// Rewrites each func_instance's file in <paramref name="document"/>, read from
        /// <paramref name="loadedFrom"/>, so that it reaches the same file from
        /// <paramref name="newLocation"/>. Returns how many were rewritten. An instance VBSP could not
        /// have found from the original location is left as written, so it fails the same way.
        /// </summary>
        /// <exception cref="IOException">An instance is on a different drive from the new location.</exception>
        public static int RetargetInstances(VmfDocument document, string loadedFrom, string newLocation)
        {
            int changed = 0;
            string newFolder = Path.GetDirectoryName(Path.GetFullPath(newLocation))!;

            foreach (var entity in document.Entities)
            {
                if (!string.Equals(entity.Classname, "func_instance", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? file = entity.Get("file");
                if (string.IsNullOrWhiteSpace(file))
                    continue;

                string? found = ResolveInstance(loadedFrom, file);
                if (found is null)
                    continue;

                // VBSP joins the instance path onto the map's folder, so an absolute path does not
                // work, and a relative one cannot cross drives.
                if (!string.Equals(Path.GetPathRoot(found), Path.GetPathRoot(newFolder), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"The instance {file} is on a different drive from {newFolder}, so a copy of the map there cannot reach it.");

                string relative = Path.GetRelativePath(newFolder, found);
                if (relative != file)
                {
                    entity.Set("file", relative);
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>
        /// Finds an instance file the way VBSP's DeterminePath does: beside the map that names it, then
        /// under the nearest folder called "maps" above it. The third place VBSP looks, gameinfo's
        /// InstancePath, goes through the game's file system and is the same wherever the map is.
        /// </summary>
        public static string? ResolveInstance(string mapFile, string instanceFile)
        {
            string relative = Path.ChangeExtension(instanceFile.Replace('/', '\\'), ".vmf");
            string folder = Path.GetDirectoryName(Path.GetFullPath(mapFile))!;

            string beside = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(beside))
                return beside;

            string withSlash = folder.TrimEnd('\\') + "\\";
            int maps = withSlash.IndexOf("\\maps\\", StringComparison.OrdinalIgnoreCase);
            if (maps >= 0)
            {
                string underMaps = Path.GetFullPath(Path.Combine(withSlash[..(maps + "\\maps\\".Length)], relative));
                if (File.Exists(underMaps))
                    return underMaps;
            }

            return null;
        }
    }
}
