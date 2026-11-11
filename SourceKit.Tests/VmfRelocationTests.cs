using System;
using System.IO;
using System.Linq;
using SourceKit.Vmf;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// Saving a map somewhere else without losing its instances: VBSP finds a func_instance's file
    /// relative to the VMF naming it, so a copy elsewhere has to name it by a path that still reaches it.
    /// </summary>
    public class VmfRelocationTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"sk_reloc_{Guid.NewGuid():N}");

        public VmfRelocationTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private string Touch(string relative)
        {
            string full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "world\n{\n}\n");
            return full;
        }

        private static VmfDocument MapWithInstances(params string[] files) => VmfDocument.Parse(
            "world\n{\n\t\"id\" \"1\"\n\t\"classname\" \"worldspawn\"\n}\n" +
            string.Concat(files.Select((f, i) =>
                $"entity\n{{\n\t\"id\" \"{10 + i}\"\n\t\"classname\" \"func_instance\"\n\t\"file\" \"{f}\"\n}}\n")) +
            "entity\n{\n\t\"id\" \"99\"\n\t\"classname\" \"info_target\"\n\t\"file\" \"instances/door.vmf\"\n}\n");

        private static string[] Files(VmfDocument map) =>
            map.Entities.Where(e => e.Classname == "func_instance").Select(e => e.Get("file")!).ToArray();

        [Fact]
        public void AnInstanceBesideTheMapIsRenamedFromTheNewFolder()
        {
            Touch(@"src\maps\instances\door.vmf");
            string loadedFrom = Path.Combine(root, @"src\maps\city.vmf");
            var map = MapWithInstances("instances/door.vmf");

            int changed = VmfRelocation.RetargetInstances(map, loadedFrom, Path.Combine(root, @"work\compile\city.vmf"));

            Assert.Equal(1, changed);
            // Forward slashes: VBSP would read a backslash as the start of an escape.
            Assert.Equal(["../../src/maps/instances/door.vmf"], Files(map));
            // Only func_instance: another entity's "file" key is not a path VBSP resolves.
            Assert.Equal("instances/door.vmf", map.Entities.Single(e => e.Classname == "info_target").Get("file"));
        }

        /// <summary>The second place VBSP looks: under the nearest folder called "maps" above the map.</summary>
        [Fact]
        public void AnInstanceUnderTheMapsFolderIsFoundThere()
        {
            Touch(@"game\maps\instances\lamp.vmf");
            string loadedFrom = Path.Combine(root, @"game\maps\district\city.vmf");
            var map = MapWithInstances("instances/lamp.vmf");

            Assert.Equal(Path.Combine(root, @"game\maps\instances\lamp.vmf"), VmfRelocation.ResolveInstance(loadedFrom, "instances/lamp.vmf"));

            int changed = VmfRelocation.RetargetInstances(map, loadedFrom, Path.Combine(root, @"out\city.vmf"));

            Assert.Equal(1, changed);
            Assert.Equal(["../game/maps/instances/lamp.vmf"], Files(map));
        }

        /// <summary>
        /// An instance that cannot be found from where the map was is left as written: the copy then
        /// fails the same way the original would, rather than pointing somewhere new.
        /// </summary>
        [Fact]
        public void AnInstanceThatCannotBeFoundIsLeftAlone()
        {
            var map = MapWithInstances("instances/missing.vmf", "");
            int changed = VmfRelocation.RetargetInstances(map, Path.Combine(root, @"src\city.vmf"), Path.Combine(root, @"out\city.vmf"));

            Assert.Equal(0, changed);
            Assert.Equal(["instances/missing.vmf", ""], Files(map));
        }

        [Fact]
        public void SavingInTheSameFolderChangesNothing()
        {
            Touch(@"src\instances\door.vmf");
            string loadedFrom = Path.Combine(root, @"src\city.vmf");
            var map = MapWithInstances("instances/door.vmf");
            string before = map.ToString();

            Assert.Equal(0, VmfRelocation.RetargetInstances(map, loadedFrom, Path.Combine(root, @"src\city_copy.vmf")));
            Assert.Equal(before, map.ToString());
        }

        /// <summary>A file named without its extension, or with backslashes, is found as VBSP finds it.</summary>
        [Fact]
        public void ANameWithoutExtensionOrWithBackslashesResolves()
        {
            string door = Touch(@"src\instances\door.vmf");
            string loadedFrom = Path.Combine(root, @"src\city.vmf");

            Assert.Equal(door, VmfRelocation.ResolveInstance(loadedFrom, "instances/door"));
            Assert.Equal(door, VmfRelocation.ResolveInstance(loadedFrom, @"instances\door.vmf"));
        }

        [Fact]
        public void ACopyOnAnotherDriveIsRefused()
        {
            Touch(@"src\instances\door.vmf");
            string loadedFrom = Path.Combine(root, @"src\city.vmf");
            char drive = char.ToUpperInvariant(Path.GetPathRoot(root)![0]) == 'Q' ? 'R' : 'Q';
            var map = MapWithInstances("instances/door.vmf");

            Assert.Throws<IOException>(() => VmfRelocation.RetargetInstances(map, loadedFrom, $@"{drive}:\elsewhere\city.vmf"));
        }
    }
}
