using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// The VPK directory and where it says each file's bytes are: a numbered archive, the directory file
    /// itself, or the directory's preload block alone. Stock content resolves through these on any
    /// install that has not unpacked it, and nothing on a development machine exercised them.
    /// </summary>
    public class VpkArchiveTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"sk_vpk_{Guid.NewGuid():N}");

        public VpkArchiveTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private const ushort Inline = 0x7fff;

        /// <summary>One file in the directory: where its body is, and the bytes kept in the directory itself.</summary>
        private sealed record Entry(string Extension, string Folder, string Name, ushort Archive, uint Offset, byte[] Body, byte[] Preload);

        /// <summary>
        /// Writes <c>pak_dir.vpk</c> holding <paramref name="entries"/>, and a <c>pak_NNN.vpk</c> for
        /// every numbered archive they name, each body at the offset its entry gives. Inline bodies are
        /// written after the tree, at their offsets from its end.
        /// </summary>
        private string Write(int version, params Entry[] entries)
        {
            var tree = new MemoryStream();
            var t = new BinaryWriter(tree);
            void Str(string s) { t.Write(Encoding.ASCII.GetBytes(s)); t.Write((byte)0); }

            foreach (var byExtension in entries.GroupBy(e => e.Extension))
            {
                Str(byExtension.Key);
                foreach (var byFolder in byExtension.GroupBy(e => e.Folder))
                {
                    Str(byFolder.Key);
                    foreach (var e in byFolder)
                    {
                        Str(e.Name);
                        t.Write(0u);                           // CRC
                        t.Write((ushort)e.Preload.Length);
                        t.Write(e.Archive);
                        t.Write(e.Offset);
                        t.Write((uint)e.Body.Length);
                        t.Write((ushort)0xffff);
                        t.Write(e.Preload);
                    }
                    Str("");
                }
                Str("");
            }
            Str("");

            var inline = new byte[entries.Where(e => e.Archive == Inline).Select(e => (int)e.Offset + e.Body.Length).DefaultIfEmpty(0).Max()];
            foreach (var e in entries.Where(e => e.Archive == Inline))
                e.Body.CopyTo(inline, (int)e.Offset);

            using (var dir = new BinaryWriter(File.Create(Path.Combine(root, "pak_dir.vpk"))))
            {
                dir.Write(0x55aa1234u);
                dir.Write((uint)version);
                dir.Write((uint)tree.Length);
                if (version == 2)
                {
                    dir.Write((uint)inline.Length);  // file data section
                    dir.Write(0u); dir.Write(0u); dir.Write(0u);
                }
                dir.Write(tree.ToArray());
                dir.Write(inline);
            }

            foreach (var archive in entries.Where(e => e.Archive != Inline && e.Body.Length > 0).GroupBy(e => e.Archive))
            {
                var bytes = new byte[archive.Max(e => (int)e.Offset + e.Body.Length)];
                foreach (var e in archive)
                    e.Body.CopyTo(bytes, (int)e.Offset);
                File.WriteAllBytes(Path.Combine(root, $"pak_{archive.Key:000}.vpk"), bytes);
            }

            return Path.Combine(root, "pak_dir.vpk");
        }

        private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

        private static string Read(VpkArchive vpk, string path) =>
            vpk.TryRead(path, out var bytes) ? Encoding.ASCII.GetString(bytes) : "<missing>";

        private static Entry[] Typical() =>
        [
            new("mdl", "models/props", "chair", 0, 0, B("chair body"), []),
            new("phy", "models/props", "chair", 0, 10, B("chair hull"), []),
            new("mdl", "models/props", "table", 12, 4, B("table body"), []),
            new("phy", "models/props", "table", 12, 0, B("tab"), B("pre:")),
            new("vmt", "materials/dev", "dev_measure", Inline, 0, B("inline material"), []),
            new("vmt", "materials/dev", "dev_other", Inline, 15, B("second inline"), B("head-")),
            new("txt", "scripts", "tiny", Inline, 0, [], B("preload only")),
            new("txt", " ", "readme", 1, 0, B("in the root"), []),
        ];

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void EveryKindOfEntryReadsBack(int version)
        {
            using var vpk = VpkArchive.TryOpen(Write(version, Typical()));
            Assert.NotNull(vpk);
            Assert.Equal(8, vpk.FileCount);

            // In _000, and in _012: a two- or three-digit index is still three digits in the name.
            Assert.Equal("chair body", Read(vpk, "models/props/chair.mdl"));
            Assert.Equal("chair hull", Read(vpk, "models/props/chair.phy"));
            Assert.Equal("table body", Read(vpk, "models/props/table.mdl"));
            // The preload bytes come first, then the body from the archive.
            Assert.Equal("pre:tab", Read(vpk, "models/props/table.phy"));
            // 0x7fff: the body is in the directory file, after the tree, at the offset from the tree's
            // end - which on version 2 is past four more header fields.
            Assert.Equal("inline material", Read(vpk, "materials/dev/dev_measure.vmt"));
            Assert.Equal("head-second inline", Read(vpk, "materials/dev/dev_other.vmt"));
            // No body at all: the whole file is the preload block.
            Assert.Equal("preload only", Read(vpk, "scripts/tiny.txt"));
            // A file in the root of the archive is listed under the folder " ".
            Assert.Equal("in the root", Read(vpk, "readme.txt"));
        }

        [Fact]
        public void LookupsIgnoreCaseAndSlashDirection()
        {
            using var vpk = VpkArchive.TryOpen(Write(2, Typical()))!;
            Assert.Equal("chair body", Read(vpk, "\\Models\\Props\\CHAIR.mdl"));
            Assert.Equal("chair body", Read(vpk, "/models/props/chair.mdl"));
        }

        [Fact]
        public void AMissingArchiveOrFileIsAMissNotAnError()
        {
            using var vpk = VpkArchive.TryOpen(Write(2, Typical()))!;
            File.Delete(Path.Combine(root, "pak_012.vpk"));

            Assert.False(vpk.TryRead("models/props/table.mdl", out _));
            Assert.False(vpk.TryRead("models/props/nothing.mdl", out _));
            Assert.Equal("chair body", Read(vpk, "models/props/chair.mdl"));
        }

        /// <summary>A body the directory places past the end of its archive is refused, not read short.</summary>
        [Fact]
        public void ABodyPastTheEndOfItsArchiveIsRefused()
        {
            using var vpk = VpkArchive.TryOpen(Write(2, Typical()))!;
            File.WriteAllBytes(Path.Combine(root, "pak_000.vpk"), B("chair bo"));

            Assert.False(vpk.TryRead("models/props/chair.mdl", out _));
        }

        [Theory]
        [InlineData(0x55aa1234u, 3u)]   // a version that is not read
        [InlineData(0x12345678u, 2u)]   // not a VPK
        public void SomethingThatIsNotAReadableVpkIsRefused(uint signature, uint version)
        {
            string path = Path.Combine(root, "bad_dir.vpk");
            File.WriteAllBytes(path, [.. BitConverter.GetBytes(signature), .. BitConverter.GetBytes(version), .. new byte[24]]);
            Assert.Null(VpkArchive.TryOpen(path));
        }
    }
}
