using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// The one header reader every BSP reader goes through: what it accepts, and that every reader
    /// refuses what it refuses, by name, rather than reading a map of another layout as garbage.
    /// </summary>
    public class BspHeaderTests
    {
        /// <summary>
        /// A BSP holding <paramref name="lumps"/>, back to back after the header. With
        /// <paramref name="versionFirst"/> the lump table is written as Left 4 Dead 2 writes it: version,
        /// offset, length, fourCC.
        /// </summary>
        internal static byte[] Bsp(Dictionary<int, byte[]> lumps, int version = 20, bool versionFirst = false)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x50534256);
            w.Write(version);
            int offset = 8 + 64 * 16 + 4;
            var order = new List<byte[]>();
            for (int i = 0; i < 64; i++)
            {
                var bytes = lumps.GetValueOrDefault(i, []);
                int at = bytes.Length == 0 ? 0 : offset, lumpVersion = i == 10 ? 1 : 0;
                if (versionFirst)
                {
                    w.Write(lumpVersion); w.Write(at); w.Write(bytes.Length);
                }
                else
                {
                    w.Write(at); w.Write(bytes.Length); w.Write(lumpVersion);
                }
                w.Write(0);
                offset += bytes.Length;
                order.Add(bytes);
            }
            w.Write(1); // map revision
            foreach (var bytes in order)
                w.Write(bytes);
            return ms.ToArray();
        }

        /// <summary>A few lumps with something in them: an entity, a plane, a texinfo, a brush model.</summary>
        private static Dictionary<int, byte[]> SomeLumps() => new()
        {
            [0] = "{\n\"classname\" \"worldspawn\"\n}\n\0"u8.ToArray(),
            [1] = new byte[20],
            [6] = new byte[72],
            [14] = new byte[48],
        };

        public static TheoryData<string> Readers => new() { "BspFile", "BspSpace", "BspVisibilityData", "BspGeometry" };

        public static TheoryData<string, int> ReadersAndBadVersions()
        {
            var data = new TheoryData<string, int>();
            foreach (string reader in new[] { "BspFile", "BspSpace", "BspVisibilityData", "BspGeometry" })
                foreach (int version in new[] { 17, 18, 22, 29 })
                    data.Add(reader, version);
            return data;
        }

        private static void Load(string reader, string path)
        {
            switch (reader)
            {
                case "BspFile": BspFile.Load(path); break;
                case "BspSpace": BspSpace.Load(path); break;
                case "BspVisibilityData": BspVisibilityData.Load(path); break;
                case "BspGeometry": BspGeometry.Load(path); break;
                default: throw new ArgumentException(reader);
            }
        }

        private static string Temp(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "sourcekit-header-" + Guid.NewGuid().ToString("N") + ".bsp");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Theory]
        [MemberData(nameof(ReadersAndBadVersions))]
        public void AVersionOutsideNineteenToTwentyOneIsRefusedByEveryReader(string reader, int version)
        {
            string path = Temp(Bsp(SomeLumps(), version));
            try
            {
                var error = Assert.Throws<InvalidDataException>(() => Load(reader, path));
                Assert.Contains("unsupported BSP version/layout", error.Message);
                Assert.Contains($"version {version}", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Left 4 Dead 2 is version 21, as CS:GO is, but writes each lump entry version first. Read the
        /// usual way, every lump starts inside the header; the readers used to clamp that into data.
        /// </summary>
        [Theory]
        [MemberData(nameof(Readers))]
        public void TheVersionFirstLumpOrderIsRefusedByEveryReader(string reader)
        {
            string path = Temp(Bsp(SomeLumps(), version: 21, versionFirst: true));
            try
            {
                var error = Assert.Throws<InvalidDataException>(() => Load(reader, path));
                Assert.Contains("unsupported BSP version/layout", error.Message);
                Assert.Contains("version-first", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [MemberData(nameof(Readers))]
        public void SomethingThatIsNotABspIsRefusedByEveryReader(string reader)
        {
            var notVbsp = Bsp(SomeLumps());
            "IBSP"u8.CopyTo(notVbsp);
            string wrongIdent = Temp(notVbsp);
            string tooShort = Temp([.. "VBSP"u8, 20, 0, 0, 0]);
            try
            {
                Assert.Contains("not a Source BSP", Assert.Throws<InvalidDataException>(() => Load(reader, wrongIdent)).Message);
                Assert.Contains("too short", Assert.Throws<InvalidDataException>(() => Load(reader, tooShort)).Message);
            }
            finally
            {
                File.Delete(wrongIdent);
                File.Delete(tooShort);
            }
        }

        [Theory]
        [InlineData(19)]
        [InlineData(20)]
        [InlineData(21)]
        public void EveryVersionReadIsAccepted(int version)
        {
            string path = Temp(Bsp(SomeLumps(), version));
            try
            {
                var bsp = BspFile.Load(path);
                Assert.Equal(version, bsp.Version);
                Assert.Equal(1, bsp.MapRevision);
                Assert.Single(bsp.Planes);
                Assert.Contains("worldspawn", bsp.EntityLump);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// One damaged entry in an ordinary map - a lump with data that starts inside the header - does
        /// not make it look version-first: read that way, the rest of the table does not fit the file.
        /// The map reads as it always did.
        /// </summary>
        [Fact]
        public void AnOrdinaryMapWithOneDamagedEntryIsStillRead()
        {
            var bytes = Bsp(SomeLumps(), version: 21);
            BitConverter.GetBytes(8).CopyTo(bytes, 8 + 30 * 16);       // lump 30 at offset 8...
            BitConverter.GetBytes(100).CopyTo(bytes, 8 + 30 * 16 + 4); // ...for 100 bytes
            string path = Temp(bytes);
            try
            {
                var bsp = BspFile.Load(path);
                Assert.Single(bsp.Planes);
                Assert.Contains("worldspawn", bsp.EntityLump);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// <see cref="GameFiles"/> is best-effort: a map whose header is refused costs its packed
        /// content, as a damaged pakfile does, and nothing else.
        /// </summary>
        [Fact]
        public void TheContentLookupSkipsThePakfileOfARefusedMap()
        {
            byte[] zip;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                using (var writer = new StreamWriter(archive.CreateEntry("models/a.phy").Open()))
                    writer.Write("packed");
                zip = ms.ToArray();
            }

            string usual = Temp(Bsp(new() { [40] = zip }, version: 21));
            string versionFirst = Temp(Bsp(new() { [40] = zip }, version: 21, versionFirst: true));
            try
            {
                using (var files = GameFiles.Open(usual))
                    Assert.Equal(1, files.PakfileEntries);
                using (var files = GameFiles.Open(versionFirst))
                    Assert.Equal(0, files.PakfileEntries);
            }
            finally
            {
                File.Delete(usual);
                File.Delete(versionFirst);
            }
        }

        /// <summary>A lump compressed on its own is read through the same header as the rest.</summary>
        [Fact]
        public void ACompressedLumpReadsThroughTheHeader()
        {
            var planes = Enumerable.Range(0, 40).SelectMany(i => new[] { 0f, 0f, 1f, i * 8f }.SelectMany(BitConverter.GetBytes).Concat(BitConverter.GetBytes(2))).ToArray();
            var lumps = SomeLumps();
            lumps[1] = BspVisibilityTests.Compress(planes);
            Assert.NotEqual(planes.Length, lumps[1].Length);
            string path = Temp(Bsp(lumps));
            try
            {
                var bsp = BspFile.Load(path);
                Assert.Equal(40, bsp.Planes.Length);
                Assert.Equal(39 * 8f, bsp.Planes[^1].Distance);
                Assert.Equal(2, bsp.Planes[^1].Type);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
