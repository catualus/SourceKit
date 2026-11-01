using System;
using System.Collections.Generic;

namespace SourceKit.Geometry
{
    /// <summary>
    /// A bounding volume hierarchy over triangles, for casting many rays: which triangle a ray meets first.
    ///
    /// Each node's triangles are split where the surface area heuristic says a ray will test the fewest
    /// boxes and triangles - tried at sixteen places along each axis - and a ray visits the nearer child
    /// first, so a hit close by rules out most of what lies behind it. The build is the same on every run.
    /// Triangles are stored in tree order, as floats, so a leaf's triangles sit together in memory.
    /// Triangles marked as not blocking are left out: a ray passes through them.
    /// </summary>
    public sealed class TriangleBvh
    {
        private const int MaxLeaf = 8;
        private const int Bins = 16;

        // Per triangle, in tree order: a corner and the two edges from it (Möller-Trumbore), and its index.
        private readonly float[] v0x, v0y, v0z, e1x, e1y, e1z, e2x, e2y, e2z;
        private readonly int[] original;

        // Per node: bounds; for a leaf, its first triangle and how many; for an inner node, count 0, its
        // first child (the second follows it) and the axis it was split on.
        private readonly float[] minX, minY, minZ, maxX, maxY, maxZ;
        private readonly int[] start, count;
        private readonly byte[] axisOf;
        private int nodeCount;

        public int TriangleCount => original.Length;

        /// <param name="corners">Three corners per triangle.</param>
        /// <param name="blocks">Which triangles stop a ray; null for all of them.</param>
        public TriangleBvh(IReadOnlyList<Vec3> corners, IReadOnlyList<bool>? blocks = null)
        {
            var kept = new List<int>();
            for (int t = 0; t < corners.Count / 3; t++)
                if (blocks == null || blocks[t])
                    kept.Add(t);
            int n = kept.Count;

            var centre = new double[n * 3];
            var lo = new double[n * 3];
            var hi = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                var (a, b, c) = (corners[kept[i] * 3], corners[kept[i] * 3 + 1], corners[kept[i] * 3 + 2]);
                var mn = Vec3.Min(a, Vec3.Min(b, c));
                var mx = Vec3.Max(a, Vec3.Max(b, c));
                (lo[i * 3], lo[i * 3 + 1], lo[i * 3 + 2]) = (mn.X, mn.Y, mn.Z);
                (hi[i * 3], hi[i * 3 + 1], hi[i * 3 + 2]) = (mx.X, mx.Y, mx.Z);
                (centre[i * 3], centre[i * 3 + 1], centre[i * 3 + 2]) = ((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
            }

            // Every inner node has two children and every leaf at least one triangle: under 2n nodes.
            int capacity = 2 * n + 1;
            minX = new float[capacity]; minY = new float[capacity]; minZ = new float[capacity];
            maxX = new float[capacity]; maxY = new float[capacity]; maxZ = new float[capacity];
            start = new int[capacity]; count = new int[capacity]; axisOf = new byte[capacity];

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            nodeCount = 1;
            if (n > 0)
                Build(0, order, 0, n, centre, lo, hi);

            v0x = new float[n]; v0y = new float[n]; v0z = new float[n];
            e1x = new float[n]; e1y = new float[n]; e1z = new float[n];
            e2x = new float[n]; e2y = new float[n]; e2z = new float[n];
            original = new int[n];
            for (int i = 0; i < n; i++)
            {
                int t = kept[order[i]];
                var (a, b, c) = (corners[t * 3], corners[t * 3 + 1], corners[t * 3 + 2]);
                (v0x[i], v0y[i], v0z[i]) = ((float)a.X, (float)a.Y, (float)a.Z);
                (e1x[i], e1y[i], e1z[i]) = ((float)(b.X - a.X), (float)(b.Y - a.Y), (float)(b.Z - a.Z));
                (e2x[i], e2y[i], e2z[i]) = ((float)(c.X - a.X), (float)(c.Y - a.Y), (float)(c.Z - a.Z));
                original[i] = t;
            }
        }

        private static double Area(double ex, double ey, double ez) =>
            ex < 0 ? 0 : ex * ey + ey * ez + ez * ex;

        private void Build(int node, int[] order, int from, int to, double[] centre, double[] lo, double[] hi)
        {
            Span<double> bmin = [double.MaxValue, double.MaxValue, double.MaxValue];
            Span<double> bmax = [double.MinValue, double.MinValue, double.MinValue];
            Span<double> cmin = [double.MaxValue, double.MaxValue, double.MaxValue];
            Span<double> cmax = [double.MinValue, double.MinValue, double.MinValue];
            for (int i = from; i < to; i++)
            {
                int t = order[i] * 3;
                for (int k = 0; k < 3; k++)
                {
                    bmin[k] = Math.Min(bmin[k], lo[t + k]); bmax[k] = Math.Max(bmax[k], hi[t + k]);
                    cmin[k] = Math.Min(cmin[k], centre[t + k]); cmax[k] = Math.Max(cmax[k], centre[t + k]);
                }
            }
            // A hair wider, so float rounding never lets a ray slip between a box and its triangle.
            minX[node] = (float)bmin[0] - 0.01f; minY[node] = (float)bmin[1] - 0.01f; minZ[node] = (float)bmin[2] - 0.01f;
            maxX[node] = (float)bmax[0] + 0.01f; maxY[node] = (float)bmax[1] + 0.01f; maxZ[node] = (float)bmax[2] + 0.01f;

            int n = to - from;
            if (n <= 2)
            {
                MakeLeaf(node, from, n);
                return;
            }

            // Binned SAH: for each axis, sort the triangles' centres into bins, then cost every boundary
            // between bins as (area left x count left + area right x count right).
            double bestCost = double.MaxValue;
            int bestAxis = -1, bestBin = -1;
            Span<int> binCount = stackalloc int[Bins];
            Span<double> binLo = stackalloc double[Bins * 3];
            Span<double> binHi = stackalloc double[Bins * 3];
            Span<double> rightArea = stackalloc double[Bins];
            Span<int> rightCount = stackalloc int[Bins];
            for (int axis = 0; axis < 3; axis++)
            {
                double extent = cmax[axis] - cmin[axis];
                if (extent <= 1e-9)
                    continue;
                binCount.Clear();
                binLo.Fill(double.MaxValue);
                binHi.Fill(double.MinValue);
                for (int i = from; i < to; i++)
                {
                    int t = order[i] * 3;
                    int b = Math.Min(Bins - 1, (int)((centre[t + axis] - cmin[axis]) / extent * Bins));
                    binCount[b]++;
                    for (int k = 0; k < 3; k++)
                    {
                        binLo[b * 3 + k] = Math.Min(binLo[b * 3 + k], lo[t + k]);
                        binHi[b * 3 + k] = Math.Max(binHi[b * 3 + k], hi[t + k]);
                    }
                }
                // Sweep from the right, then from the left.
                double rx0 = double.MaxValue, ry0 = double.MaxValue, rz0 = double.MaxValue, rx1 = double.MinValue, ry1 = double.MinValue, rz1 = double.MinValue;
                int rc = 0;
                for (int b = Bins - 1; b > 0; b--)
                {
                    if (binCount[b] > 0)
                    {
                        rx0 = Math.Min(rx0, binLo[b * 3]); ry0 = Math.Min(ry0, binLo[b * 3 + 1]); rz0 = Math.Min(rz0, binLo[b * 3 + 2]);
                        rx1 = Math.Max(rx1, binHi[b * 3]); ry1 = Math.Max(ry1, binHi[b * 3 + 1]); rz1 = Math.Max(rz1, binHi[b * 3 + 2]);
                    }
                    rc += binCount[b];
                    rightCount[b] = rc;
                    rightArea[b] = rc == 0 ? 0 : Area(rx1 - rx0, ry1 - ry0, rz1 - rz0);
                }
                double lx0 = double.MaxValue, ly0 = double.MaxValue, lz0 = double.MaxValue, lx1 = double.MinValue, ly1 = double.MinValue, lz1 = double.MinValue;
                int lc = 0;
                for (int b = 0; b < Bins - 1; b++)
                {
                    if (binCount[b] > 0)
                    {
                        lx0 = Math.Min(lx0, binLo[b * 3]); ly0 = Math.Min(ly0, binLo[b * 3 + 1]); lz0 = Math.Min(lz0, binLo[b * 3 + 2]);
                        lx1 = Math.Max(lx1, binHi[b * 3]); ly1 = Math.Max(ly1, binHi[b * 3 + 1]); lz1 = Math.Max(lz1, binHi[b * 3 + 2]);
                    }
                    lc += binCount[b];
                    if (lc == 0 || rightCount[b + 1] == 0)
                        continue;
                    double cost = Area(lx1 - lx0, ly1 - ly0, lz1 - lz0) * lc + rightArea[b + 1] * rightCount[b + 1];
                    if (cost < bestCost)
                        (bestCost, bestAxis, bestBin) = (cost, axis, b);
                }
            }

            double leafCost = Area(bmax[0] - bmin[0], bmax[1] - bmin[1], bmax[2] - bmin[2]) * n;
            if (bestAxis < 0 || (n <= MaxLeaf && bestCost >= leafCost))
            {
                if (n <= MaxLeaf || bestAxis < 0)
                {
                    // Nothing splits them (every centre the same) or splitting does not pay.
                    if (n <= MaxLeaf)
                    {
                        MakeLeaf(node, from, n);
                        return;
                    }
                    bestAxis = 0;
                    bestBin = -1;
                }
            }

            int mid;
            if (bestBin >= 0)
            {
                // Partition in place, keeping each side in its original order so the build repeats exactly.
                double extent = cmax[bestAxis] - cmin[bestAxis];
                var left = new List<int>(n);
                var right = new List<int>(n);
                for (int i = from; i < to; i++)
                {
                    int t = order[i];
                    int b = Math.Min(Bins - 1, (int)((centre[t * 3 + bestAxis] - cmin[bestAxis]) / extent * Bins));
                    (b <= bestBin ? left : right).Add(t);
                }
                left.CopyTo(order, from);
                right.CopyTo(order, from + left.Count);
                mid = from + left.Count;
            }
            else
                mid = (from + to) / 2;

            int child = nodeCount;
            nodeCount += 2;
            start[node] = child;
            count[node] = 0;
            axisOf[node] = (byte)bestAxis;
            Build(child, order, from, mid, centre, lo, hi);
            Build(child + 1, order, mid, to, centre, lo, hi);
        }

        private void MakeLeaf(int node, int from, int n)
        {
            start[node] = from;
            count[node] = n;
        }

        /// <summary>
        /// The tree laid out for a traversal with no stack, as a GPU runs it: nodes in depth-first order,
        /// each with where to go when its box is missed (the node after its subtree, or -1 at the end).
        /// A node's first child is always the next node. Leaves carry their first triangle and count;
        /// inner nodes a count of 0. Triangles are as stored: corner, then the two edges.
        /// </summary>
        public (float[] Bounds, int[] Miss, int[] First, int[] Count, float[] Triangles, int[] Original) Threaded()
        {
            var preorder = new List<int>(nodeCount);
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                preorder.Add(node);
                if (count[node] == 0 && original.Length > 0)
                {
                    stack.Push(start[node] + 1);
                    stack.Push(start[node]);
                }
            }
            var position = new Dictionary<int, int>(preorder.Count);
            for (int i = 0; i < preorder.Count; i++)
                position[preorder[i]] = i;

            int n = preorder.Count;
            var bounds = new float[n * 6];
            var miss = new int[n];
            var first = new int[n];
            var leafCount = new int[n];
            // The node after a subtree: walk the preorder, tracking subtree sizes.
            var size = new int[n];
            for (int i = n - 1; i >= 0; i--)
            {
                int node = preorder[i];
                size[i] = count[node] == 0 && original.Length > 0
                    ? 1 + size[position[start[node]]] + size[position[start[node] + 1]]
                    : 1;
            }
            for (int i = 0; i < n; i++)
            {
                int node = preorder[i];
                (bounds[i * 6], bounds[i * 6 + 1], bounds[i * 6 + 2]) = (minX[node], minY[node], minZ[node]);
                (bounds[i * 6 + 3], bounds[i * 6 + 4], bounds[i * 6 + 5]) = (maxX[node], maxY[node], maxZ[node]);
                miss[i] = i + size[i] < n ? i + size[i] : -1;
                first[i] = count[node] == 0 ? 0 : start[node];
                leafCount[i] = count[node];
            }
            var triangles = new float[original.Length * 9];
            for (int t = 0; t < original.Length; t++)
            {
                triangles[t * 9] = v0x[t]; triangles[t * 9 + 1] = v0y[t]; triangles[t * 9 + 2] = v0z[t];
                triangles[t * 9 + 3] = e1x[t]; triangles[t * 9 + 4] = e1y[t]; triangles[t * 9 + 5] = e1z[t];
                triangles[t * 9 + 6] = e2x[t]; triangles[t * 9 + 7] = e2y[t]; triangles[t * 9 + 8] = e2z[t];
            }
            return (bounds, miss, first, leafCount, triangles, (int[])original.Clone());
        }

        /// <summary>
        /// The triangle (by its index in the corners given) a ray from <paramref name="origin"/> along
        /// <paramref name="direction"/> meets first within <paramref name="maxDistance"/>, or -1.
        /// </summary>
        public int Trace(Vec3 origin, Vec3 direction, double maxDistance = double.MaxValue)
        {
            if (original.Length == 0)
                return -1;
            float ox = (float)origin.X, oy = (float)origin.Y, oz = (float)origin.Z;
            float dx = (float)direction.X, dy = (float)direction.Y, dz = (float)direction.Z;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            Span<bool> negative = [dx < 0, dy < 0, dz < 0];
            float best = (float)Math.Min(maxDistance, float.MaxValue);
            int hit = -1;

            Span<int> stack = stackalloc int[128];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                float t1 = (minX[node] - ox) * ix, t2 = (maxX[node] - ox) * ix;
                float tmin = MathF.Min(t1, t2), tmax = MathF.Max(t1, t2);
                t1 = (minY[node] - oy) * iy; t2 = (maxY[node] - oy) * iy;
                tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
                t1 = (minZ[node] - oz) * iz; t2 = (maxZ[node] - oz) * iz;
                tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
                if (tmax < MathF.Max(tmin, 0) || tmin > best)
                    continue;

                if (count[node] == 0)
                {
                    if (top + 2 > stack.Length) continue;
                    // The far child goes on the stack first, so the near one is tried first.
                    int first = start[node];
                    if (negative[axisOf[node]])
                    {
                        stack[top++] = first;
                        stack[top++] = first + 1;
                    }
                    else
                    {
                        stack[top++] = first + 1;
                        stack[top++] = first;
                    }
                    continue;
                }

                for (int i = start[node], end = start[node] + count[node]; i < end; i++)
                {
                    // Möller-Trumbore.
                    float px = dy * e2z[i] - dz * e2y[i], py = dz * e2x[i] - dx * e2z[i], pz = dx * e2y[i] - dy * e2x[i];
                    float det = e1x[i] * px + e1y[i] * py + e1z[i] * pz;
                    if (MathF.Abs(det) < 1e-9f) continue;
                    float inv = 1f / det;
                    float sx = ox - v0x[i], sy = oy - v0y[i], sz = oz - v0z[i];
                    float u = (sx * px + sy * py + sz * pz) * inv;
                    if (u < 0 || u > 1) continue;
                    float qx = sy * e1z[i] - sz * e1y[i], qy = sz * e1x[i] - sx * e1z[i], qz = sx * e1y[i] - sy * e1x[i];
                    float v = (dx * qx + dy * qy + dz * qz) * inv;
                    if (v < 0 || u + v > 1) continue;
                    float t = (e2x[i] * qx + e2y[i] * qy + e2z[i] * qz) * inv;
                    if (t > 0.01f && t < best)
                    {
                        best = t;
                        hit = original[i];
                    }
                }
            }
            return hit;
        }
    }
}
