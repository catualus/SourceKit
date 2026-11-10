using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SourceKit.Tests
{
    public class BspVisibilityTests
    {
        /// <summary>
        /// Writes a BSP holding only the lumps BspVisibilityData reads: three clusters in a row, each
        /// seeing its neighbours, one face shared by the first two.
        /// </summary>
        /// <param name="extra">More lumps by index; the game lump is given its own offset in the file, which its headers need.</param>
        internal static void WriteTinyBsp(string path, byte[]? visibility = null, string entities = "",
            Dictionary<int, byte[]>? extra = null, Func<int, byte[]>? gameLump = null)
        {
            var lumps = new Dictionary<int, byte[]>
            {
                [0] = System.Text.Encoding.ASCII.GetBytes(entities + "\0"),
                [4] = visibility ?? Visibility([0b011], [0b111], [0b110]),
                [6] = new byte[72],                               // one texinfo, no flags
                [7] = Concat(Face(4), Face(3), Face(6)),          // 2, 1 and 4 triangles
                [10] = Concat(Leaf(1, -1, 0, 0), Leaf(0, 0, 0, 1), Leaf(0, 1, 1, 2), Leaf(0, 2, 3, 1)),
                [16] = Concat(U16(0), U16(0), U16(1), U16(2), U16(2)),
            };
            foreach (var (index, bytes) in extra ?? [])
                lumps[index] = bytes;
            if (gameLump != null)
            {
                int before = 8 + 64 * 16 + 4 + Enumerable.Range(0, 35).Sum(i => lumps.GetValueOrDefault(i, []).Length);
                lumps[35] = gameLump(before);
            }

            using var stream = File.Create(path);
            using var w = new BinaryWriter(stream);
            w.Write(0x50534256);
            w.Write(20);
            int offset = 8 + 64 * 16 + 4;
            var order = new List<byte[]>();
            for (int i = 0; i < 64; i++)
            {
                var bytes = lumps.GetValueOrDefault(i, []);
                w.Write(bytes.Length == 0 ? 0 : offset);
                w.Write(bytes.Length);
                w.Write(i == 10 ? 1 : 0); // leaves in version 1, without ambient cubes
                w.Write(0);
                offset += bytes.Length;
                order.Add(bytes);
            }
            w.Write(1); // map revision
            foreach (var bytes in order)
                w.Write(bytes);
        }

        private static byte[] Visibility(params byte[][] rows)
        {
            var header = new List<byte>(BitConverter.GetBytes(rows.Length));
            int at = 4 + rows.Length * 8;
            var body = new List<byte>();
            foreach (var row in rows)
            {
                header.AddRange(BitConverter.GetBytes(at + body.Count));
                header.AddRange(BitConverter.GetBytes(0));
                body.AddRange(row);
            }
            return [.. header, .. body];
        }

        private static byte[] Face(int edges)
        {
            var face = new byte[56];
            BitConverter.GetBytes((short)edges).CopyTo(face, 8);
            BitConverter.GetBytes((short)0).CopyTo(face, 10);   // texinfo
            BitConverter.GetBytes((short)-1).CopyTo(face, 12);  // no displacement
            return face;
        }

        private static byte[] Leaf(int contents, int cluster, int firstFace, int faceCount)
        {
            var leaf = new byte[32];
            BitConverter.GetBytes(contents).CopyTo(leaf, 0);
            BitConverter.GetBytes((short)cluster).CopyTo(leaf, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(leaf, 6);   // area 1
            BitConverter.GetBytes((short)(cluster * 100)).CopyTo(leaf, 8);
            BitConverter.GetBytes((short)(cluster * 100 + 64)).CopyTo(leaf, 14);
            BitConverter.GetBytes((ushort)firstFace).CopyTo(leaf, 20);
            BitConverter.GetBytes((ushort)faceCount).CopyTo(leaf, 22);
            return leaf;
        }

        private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static string Temp() => Path.Combine(Path.GetTempPath(), "sourcekit-bsp-" + Guid.NewGuid().ToString("N") + ".bsp");

        [Fact]
        public void ThePvsLeavesAndFacesAreRead()
        {
            string path = Temp();
            try
            {
                WriteTinyBsp(path, entities: "{\n\"classname\" \"func_areaportal\"\n\"portalnumber\" \"3\"\n}\n{\n\"classname\" \"func_areaportalwindow\"\n\"portalnumber\" \"7\"\n}\n");
                var bsp = BspVisibilityData.Load(path);

                Assert.Equal(3, bsp.ClusterCount);
                Assert.Equal([0, 1], bsp.VisibleFrom(0));
                Assert.Equal([0, 1, 2], bsp.VisibleFrom(1));
                Assert.False(bsp.CanSee(0, 2));
                Assert.Equal(3, bsp.VisibleCount(1));
                Assert.Equal([2, 1, 4], bsp.Faces.Select(f => f.Triangles));
                Assert.Equal(-1, bsp.Leaves[0].Cluster);
                Assert.Equal(1, bsp.Leaves[1].Area);
                Assert.Equal(2, bsp.AreaPortalEntityCount);
                Assert.Equal(7, bsp.HighestPortalNumber);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AreaportalOutlinesEntitiesAndStaticPropsAreRead()
        {
            string path = Temp();
            try
            {
                // Two areas joined by portal 1, a square outline listed from each side.
                var areas = Concat(I32(0), I32(0), I32(1), I32(0), I32(1), I32(1));
                var portals = Concat(U16(1), U16(2), U16(0), U16(4), I32(0), U16(1), U16(1), U16(0), U16(4), I32(0));
                var verts = Concat(V(0, 0, 0), V(0, 64, 0), V(0, 64, 96), V(0, 0, 96));
                byte[] Game(int at)
                {
                    // One game lump, the static props: a model, a leaf list, and one prop in leaves 2 and 3.
                    var name = new byte[128];
                    System.Text.Encoding.ASCII.GetBytes("models/props_c17/chair.mdl").CopyTo(name, 0);
                    var prop = new byte[60];
                    BitConverter.GetBytes((ushort)0).CopyTo(prop, 24);
                    BitConverter.GetBytes((ushort)1).CopyTo(prop, 26);
                    BitConverter.GetBytes((ushort)2).CopyTo(prop, 28);
                    var sprp = Concat(I32(1), name, I32(3), U16(9), U16(2), U16(3), I32(1), prop);
                    return Concat(I32(1), I32(0x73707270), U16(0), U16(5), I32(at + 4 + 16), I32(sprp.Length), sprp);
                }
                WriteTinyBsp(path, entities: "{\n\"classname\" \"func_areaportalwindow\"\n\"targetname\" \"shop\"\n\"target\" \"shop_shut\"\n" +
                    "\"FadeDist\" \"1200\"\n\"portalnumber\" \"1\"\n}\n{\n\"classname\" \"func_areaportal\"\n\"target\" \"door\"\n\"StartOpen\" \"0\"\n\"portalnumber\" \"2\"\n}\n",
                    extra: new() { [20] = areas, [21] = portals, [41] = verts }, gameLump: Game);
                var bsp = BspVisibilityData.Load(path);

                var (a, b) = bsp.AreaPortalLinks[1];
                Assert.Equal([1, 2], new[] { a, b }.Order());
                Assert.Equal([new Geometry.Vec3(0, 0, 0), new Geometry.Vec3(0, 64, 0), new Geometry.Vec3(0, 64, 96), new Geometry.Vec3(0, 0, 96)],
                    bsp.AreaPortalPolygons[1]);
                var window = bsp.AreaPortalEntities.Single(e => e.Number == 1);
                Assert.Equal(("func_areaportalwindow", "shop_shut", 1200.0), (window.Classname, window.Target, window.FadeDist));
                var door = bsp.AreaPortalEntities.Single(e => e.Number == 2);
                Assert.False(door.StartOpen);
                Assert.Equal(double.PositiveInfinity, door.FadeDist);

                Assert.Equal(["models/props_c17/chair.mdl"], bsp.StaticPropModels);
                var placed = Assert.Single(bsp.StaticProps);
                Assert.Equal([2, 3], bsp.StaticPropLeaves.Skip(placed.FirstLeaf).Take(placed.LeafCount).Select(l => (int)l));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static byte[] I32(int value) => BitConverter.GetBytes(value);
        private static byte[] V(float x, float y, float z) => Concat(BitConverter.GetBytes(x), BitConverter.GetBytes(y), BitConverter.GetBytes(z));

        /// <summary>
        /// A lump compressed the way VBSP's optional LZMA compression stores it: "LZMA", the
        /// decompressed and compressed sizes, the five property bytes, then the LZMA stream.
        /// </summary>
        internal static byte[] Compress(byte[] data)
        {
            using var packed = new MemoryStream();
            byte[] properties;
            using (var encoder = SharpCompress.Compressors.LZMA.LzmaStream.Create(
                SharpCompress.Compressors.LZMA.LzmaEncoderProperties.Default, false, packed))
            {
                encoder.Write(data, 0, data.Length);
                properties = encoder.Properties;
            }
            var stream = packed.ToArray();
            return Concat("LZMA"u8.ToArray(), I32(data.Length), I32(stream.Length), properties, stream);
        }

        [Fact]
        public void ACompressedLumpReadsBackAsItWasWritten()
        {
            var data = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 % 251)).ToArray();
            var lump = Compress(data);
            using var reader = new BinaryReader(new MemoryStream(Concat(new byte[5], lump)));

            Assert.Equal(data, LzmaLump.Read(reader, 5, lump.Length));
        }

        /// <summary>
        /// A map compiled with LZMA-compressed lumps, as a shipped map is. The static props sit in a
        /// game lump compressed on its own (flag 1), which used to be skipped, and the area counts came
        /// from the on-disk sizes in the lump table, which undercount or overcount by the compression.
        /// </summary>
        [Fact]
        public void ACompressedMapHasItsStaticPropsAndTrueAreaCounts()
        {
            string path = Temp();
            try
            {
                // Three areas joined by portal 1 as in the test above, padded out with empty ones so the
                // compressed lumps are clearly smaller than the real ones: 20 areas, 22 portal entries.
                var areas = Concat(I32(0), I32(0), I32(1), I32(0), I32(1), I32(1), new byte[17 * 8]);
                var portals = Concat(U16(1), U16(2), U16(0), U16(4), I32(0), U16(1), U16(1), U16(0), U16(4), I32(0), new byte[20 * 12]);
                var verts = Concat(V(0, 0, 0), V(0, 64, 0), V(0, 64, 96), V(0, 0, 96));
                var compressedAreas = Compress(areas);
                var compressedPortals = Compress(portals);
                Assert.NotEqual(areas.Length / 8, compressedAreas.Length / 8);
                Assert.NotEqual(portals.Length / 12, compressedPortals.Length / 12);

                byte[] Game(int at)
                {
                    var name = new byte[128];
                    System.Text.Encoding.ASCII.GetBytes("models/props_c17/chair.mdl").CopyTo(name, 0);
                    var prop = new byte[60];
                    BitConverter.GetBytes((ushort)0).CopyTo(prop, 24);
                    BitConverter.GetBytes((ushort)1).CopyTo(prop, 26);
                    BitConverter.GetBytes((ushort)2).CopyTo(prop, 28);
                    var sprp = Concat(I32(1), name, I32(3), U16(9), U16(2), U16(3), I32(1), prop);
                    // Flag 1, compressed; the directory gives the decompressed size, the tag the rest.
                    return Concat(I32(1), I32(0x73707270), U16(1), U16(5), I32(at + 4 + 16), I32(sprp.Length), Compress(sprp));
                }
                WriteTinyBsp(path, entities: "{\n\"classname\" \"func_areaportal\"\n\"portalnumber\" \"1\"\n}\n",
                    extra: new() { [20] = compressedAreas, [21] = compressedPortals, [41] = verts }, gameLump: Game);

                var bsp = BspVisibilityData.Load(path);

                Assert.Equal(20, bsp.AreaCount);
                Assert.Equal(22, bsp.AreaPortalCount);
                var (a, b) = bsp.AreaPortalLinks[1];
                Assert.Equal([1, 2], new[] { a, b }.Order());
                Assert.Equal(["models/props_c17/chair.mdl"], bsp.StaticPropModels);
                var placed = Assert.Single(bsp.StaticProps);
                Assert.Equal([2, 3], bsp.StaticPropLeaves.Skip(placed.FirstLeaf).Take(placed.LeafCount).Select(l => (int)l));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ARunOfZeroesExpandsToEmptyBytes()
        {
            string path = Temp();
            try
            {
                // 20 clusters, three bytes a row. Each row: one byte, then a run of two zero bytes.
                var rows = Enumerable.Range(0, 20).Select(_ => new byte[] { 0b1, 0, 2 }).ToArray();
                WriteTinyBsp(path, Visibility(rows));

                var bsp = BspVisibilityData.Load(path);

                Assert.Equal(20, bsp.ClusterCount);
                Assert.Equal([0], bsp.VisibleFrom(5));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void APvsThatClaimsMoreThanItHoldsIsRefused()
        {
            string path = Temp();
            try
            {
                WriteTinyBsp(path, BitConverter.GetBytes(1000));
                Assert.Throws<InvalidDataException>(() => BspVisibilityData.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod\maps\gm_construct.bsp")]
        public void ARealMapReads(string path)
        {
            if (!File.Exists(path))
                return;

            var bsp = BspVisibilityData.Load(path);

            Assert.True(bsp.HasVisibility);
            Assert.All(Enumerable.Range(0, bsp.ClusterCount), c => Assert.True(bsp.CanSee(c, c)));
        }
    }
}
