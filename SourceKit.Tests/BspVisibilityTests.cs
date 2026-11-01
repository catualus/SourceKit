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
        internal static void WriteTinyBsp(string path, byte[]? visibility = null, string entities = "")
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
