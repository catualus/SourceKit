using System;
using System.Collections.Generic;
using SourceKit.Geometry;
using Xunit;

namespace SourceKit.Tests
{
    public class TriangleBvhTests
    {
        private static List<Vec3> RandomTriangles(int count, int seed)
        {
            var random = new Random(seed);
            var corners = new List<Vec3>();
            for (int i = 0; i < count; i++)
            {
                var centre = new Vec3(random.NextDouble() * 2000 - 1000, random.NextDouble() * 2000 - 1000, random.NextDouble() * 2000 - 1000);
                for (int k = 0; k < 3; k++)
                    corners.Add(centre + new Vec3(random.NextDouble() * 120 - 60, random.NextDouble() * 120 - 60, random.NextDouble() * 120 - 60));
            }
            return corners;
        }

        private static int BruteForce(List<Vec3> corners, bool[]? blocks, Vec3 o, Vec3 d)
        {
            int best = -1;
            double nearest = double.MaxValue;
            for (int t = 0; t < corners.Count / 3; t++)
            {
                if (blocks != null && !blocks[t]) continue;
                var (a, b, c) = (corners[t * 3], corners[t * 3 + 1], corners[t * 3 + 2]);
                var e1 = b - a;
                var e2 = c - a;
                var p = d.Cross(e2);
                double det = e1.Dot(p);
                if (Math.Abs(det) < 1e-9) continue;
                var s = o - a;
                double u = s.Dot(p) / det;
                if (u < 0 || u > 1) continue;
                var q = s.Cross(e1);
                double v = d.Dot(q) / det;
                if (v < 0 || u + v > 1) continue;
                double dist = e2.Dot(q) / det;
                if (dist > 0.01 && dist < nearest) (nearest, best) = (dist, t);
            }
            return best;
        }

        [Fact]
        public void TheTreeFindsWhatTestingEveryTriangleFinds()
        {
            var corners = RandomTriangles(3000, 7);
            var blocks = new bool[3000];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = i % 7 != 0;
            var bvh = new TriangleBvh(corners, blocks);
            var random = new Random(11);
            int agree = 0, rays = 2000;
            for (int i = 0; i < rays; i++)
            {
                var o = new Vec3(random.NextDouble() * 2400 - 1200, random.NextDouble() * 2400 - 1200, random.NextDouble() * 2400 - 1200);
                var d = new Vec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5).Normalized();
                if (bvh.Trace(o, d) == BruteForce(corners, blocks, o, d)) agree++;
            }
            // Floats against doubles can split a ray that grazes an edge; anything more is a bug.
            Assert.True(agree >= rays * 0.998, $"{agree} of {rays} rays agree");
        }

        [Fact]
        public void TheStacklessLayoutVisitsTheSameTriangles()
        {
            var corners = RandomTriangles(2000, 3);
            var bvh = new TriangleBvh(corners);
            var (bounds, miss, first, count, tris, original) = bvh.Threaded();
            var random = new Random(5);
            for (int i = 0; i < 500; i++)
            {
                var o = new Vec3(random.NextDouble() * 2400 - 1200, random.NextDouble() * 2400 - 1200, random.NextDouble() * 2400 - 1200);
                var d = new Vec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5).Normalized();
                Assert.Equal(bvh.Trace(o, d), Stackless(bounds, miss, first, count, tris, original, o, d));
            }
        }

        /// <summary>The GPU's walk of the tree, in C#: box missed, jump past the subtree; hit, go to the next node.</summary>
        private static int Stackless(float[] bounds, int[] miss, int[] first, int[] count, float[] tris, int[] original, Vec3 origin, Vec3 direction)
        {
            float ox = (float)origin.X, oy = (float)origin.Y, oz = (float)origin.Z;
            float dx = (float)direction.X, dy = (float)direction.Y, dz = (float)direction.Z;
            float ix = 1 / dx, iy = 1 / dy, iz = 1 / dz, best = float.MaxValue;
            int hit = -1, node = 0;
            while (node >= 0)
            {
                int b = node * 6;
                float t1 = (bounds[b] - ox) * ix, t2 = (bounds[b + 3] - ox) * ix;
                float tmin = MathF.Min(t1, t2), tmax = MathF.Max(t1, t2);
                t1 = (bounds[b + 1] - oy) * iy; t2 = (bounds[b + 4] - oy) * iy;
                tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
                t1 = (bounds[b + 2] - oz) * iz; t2 = (bounds[b + 5] - oz) * iz;
                tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
                if (tmax < MathF.Max(tmin, 0) || tmin > best) { node = miss[node]; continue; }
                if (count[node] == 0) { node++; continue; }
                for (int i = first[node]; i < first[node] + count[node]; i++)
                {
                    int k = i * 9;
                    float px = dy * tris[k + 8] - dz * tris[k + 7], py = dz * tris[k + 6] - dx * tris[k + 8], pz = dx * tris[k + 7] - dy * tris[k + 6];
                    float det = tris[k + 3] * px + tris[k + 4] * py + tris[k + 5] * pz;
                    if (MathF.Abs(det) < 1e-9f) continue;
                    float inv = 1 / det, sx = ox - tris[k], sy = oy - tris[k + 1], sz = oz - tris[k + 2];
                    float u = (sx * px + sy * py + sz * pz) * inv;
                    if (u < 0 || u > 1) continue;
                    float qx = sy * tris[k + 5] - sz * tris[k + 4], qy = sz * tris[k + 3] - sx * tris[k + 5], qz = sx * tris[k + 4] - sy * tris[k + 3];
                    float v = (dx * qx + dy * qy + dz * qz) * inv;
                    if (v < 0 || u + v > 1) continue;
                    float t = (tris[k + 6] * qx + tris[k + 7] * qy + tris[k + 8] * qz) * inv;
                    if (t > 0.01f && t < best) { best = t; hit = original[i]; }
                }
                node = miss[node];
            }
            return hit;
        }

        [Fact]
        public void TheSameTrianglesBuildTheSameTree()
        {
            var corners = RandomTriangles(1500, 9);
            var a = new TriangleBvh(corners).Threaded();
            var b = new TriangleBvh(corners).Threaded();
            Assert.Equal(a.Bounds, b.Bounds);
            Assert.Equal(a.Miss, b.Miss);
            Assert.Equal(a.Original, b.Original);
        }
    }
}
