using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Geometry;
using Xunit;

namespace SourceKit.Tests
{
    public class BspSpaceTests
    {
        /// <summary>
        /// A world split by the plane x = 0 into two empty leaves, the x &gt; 0 one holding a solid
        /// 10-unit cube at 10..20, listed on that leaf the way VBSP lists detail brushes.
        /// </summary>
        private static string WriteSplitWorld(int cubeContents = 1)
        {
            byte[] F(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
            byte[] I(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
            byte[] Plane(float x, float y, float z, float d) => [.. F(x, y, z, d), .. I(0)];

            // Plane 0 splits the world; planes 1-6 bound the cube.
            var planes = new List<byte>(Plane(1, 0, 0, 0));
            planes.AddRange(Plane(1, 0, 0, 20)); planes.AddRange(Plane(-1, 0, 0, -10));
            planes.AddRange(Plane(0, 1, 0, 20)); planes.AddRange(Plane(0, -1, 0, -10));
            planes.AddRange(Plane(0, 0, 1, 20)); planes.AddRange(Plane(0, 0, -1, -10));

            var node = new byte[32];
            I(0, -2, -3).CopyTo(node, 0); // plane 0; front is leaf 1, back is leaf 2

            byte[] Leaf(int contents, short cluster, ushort firstBrush, ushort brushes)
            {
                var leaf = new byte[32];
                BitConverter.GetBytes(contents).CopyTo(leaf, 0);
                BitConverter.GetBytes(cluster).CopyTo(leaf, 4);
                BitConverter.GetBytes(firstBrush).CopyTo(leaf, 24);
                BitConverter.GetBytes(brushes).CopyTo(leaf, 26);
                return leaf;
            }
            byte[] leaves = [.. Leaf(1, -1, 0, 0), .. Leaf(0, 0, 0, 1), .. Leaf(0, 1, 0, 0)];

            byte[] sides = Enumerable.Range(1, 6).SelectMany(p => new byte[] { (byte)p, 0, 0, 0, 0xFF, 0xFF, 0, 0 }).ToArray();
            byte[] model = [.. F(-64, -64, -64, 64, 64, 64), .. new byte[24]];

            var lumps = new Dictionary<int, byte[]>
            {
                [1] = planes.ToArray(), [5] = node, [10] = leaves, [14] = model,
                [17] = BitConverter.GetBytes((ushort)0), [18] = I(0, 6, cubeContents), [19] = sides,
            };

            string path = Path.Combine(Path.GetTempPath(), "sourcekit-space-" + Guid.NewGuid().ToString("N") + ".bsp");
            using var w = new BinaryWriter(File.Create(path));
            w.Write(0x50534256); w.Write(20);
            int offset = 8 + 64 * 16 + 4;
            var order = new List<byte[]>();
            for (int i = 0; i < 64; i++)
            {
                var bytes = lumps.GetValueOrDefault(i, []);
                w.Write(bytes.Length == 0 ? 0 : offset); w.Write(bytes.Length); w.Write(i == 10 ? 1 : 0); w.Write(0);
                offset += bytes.Length;
                order.Add(bytes);
            }
            w.Write(1);
            foreach (var bytes in order) w.Write(bytes);
            return path;
        }

        [Fact]
        public void PointsFindTheirLeafAndCluster()
        {
            string path = WriteSplitWorld();
            try
            {
                var space = BspSpace.Load(path);
                Assert.Equal(1, space.LeafAt(new Vec3(5, 0, 0)));
                Assert.Equal(2, space.LeafAt(new Vec3(-5, 0, 0)));
                Assert.Equal(1, space.ClusterAt(new Vec3(-5, 0, 0)));
                Assert.Equal(new Vec3(-64, -64, -64), space.Mins);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void APointInsideABrushOnAnEmptyLeafIsSolid()
        {
            string path = WriteSplitWorld();
            try
            {
                var space = BspSpace.Load(path);
                Assert.True(space.IsSolid(new Vec3(15, 15, 15)));
                Assert.False(space.IsSolid(new Vec3(25, 15, 15)));
                Assert.False(space.IsSolid(new Vec3(-15, 15, 15)));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void CollisionIgnoresWhetherABrushIsDetail()
        {
            string world = WriteSplitWorld(BspSpace.ContentsSolid);
            string detail = WriteSplitWorld(BspSpace.ContentsSolid | BspSpace.ContentsDetail);
            try
            {
                var a = BspSpace.Load(world).CollisionShapes(-1);
                var b = BspSpace.Load(detail).CollisionShapes(-1);
                Assert.Single(a);
                Assert.Equal(1000, a[0].Volume, 3);
                Assert.Equal(0, BspSpace.CollisionDifferences(a, b));

                // A brush half a unit bigger is the same within tolerance; ten units bigger is not.
                var nudged = new BspSpace.BrushShape(a[0].Contents, a[0].Mins, a[0].Maxs + new Vec3(0.5, 0, 0), a[0].Volume * 1.005);
                var moved = new BspSpace.BrushShape(a[0].Contents, a[0].Mins, a[0].Maxs + new Vec3(10, 0, 0), a[0].Volume * 2);
                Assert.Equal(0, BspSpace.CollisionDifferences(a, [nudged]));
                Assert.Equal(2, BspSpace.CollisionDifferences(a, [moved]));
                Assert.Equal(1, BspSpace.CollisionDifferences(a, []));
            }
            finally { File.Delete(world); File.Delete(detail); }
        }

        [Fact]
        public void ANodePointingOutsideItsLumpsIsRefused()
        {
            string path = WriteSplitWorld();
            try
            {
                var bytes = File.ReadAllBytes(path);
                // Node 0's front child is the int after its plane number; point it at leaf 99.
                int nodeOffset = BitConverter.ToInt32(bytes, 8 + 5 * 16);
                BitConverter.GetBytes(-100).CopyTo(bytes, nodeOffset + 4);
                File.WriteAllBytes(path, bytes);
                Assert.Throws<InvalidDataException>(() => BspSpace.Load(path));
            }
            finally { File.Delete(path); }
        }
    }
}
