using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;

namespace SourceKit
{
    /// <summary>
    /// A compiled map as space: which leaf a point is in, whether it is inside something solid, and
    /// what the collision brushes are.
    ///
    /// Solidity is answered from the brushes, not the leaves. A leaf is only marked solid when
    /// structural brushes fill it; func_detail never makes a leaf solid, because detail brushes are
    /// filtered into the finished tree and only listed on the leaves they cross. So a point inside a
    /// detail pillar is in an empty leaf, and only testing the leaf's brushes says otherwise.
    /// </summary>
    public sealed class BspSpace
    {
        public const int ContentsSolid = 0x1;
        public const int ContentsDetail = 0x8000000;

        private const int LumpPlanes = 1, LumpNodes = 5, LumpLeafs = 10, LumpModels = 14, LumpLeafBrushes = 17,
            LumpBrushes = 18, LumpBrushSides = 19;

        private readonly Plane3[] planes;
        private readonly (int Plane, int Front, int Back)[] nodes;
        private readonly (int Contents, int Cluster, int FirstBrush, int BrushCount, int Area)[] leaves;
        private readonly ushort[] leafBrushes;
        private readonly (int FirstSide, int SideCount, int Contents)[] brushes;
        private readonly (int Plane, bool Bevel)[] sides;

        /// <summary>The world's bounds, from model 0.</summary>
        public Vec3 Mins { get; }
        public Vec3 Maxs { get; }

        private BspSpace(Plane3[] planes, (int, int, int)[] nodes, (int, int, int, int, int)[] leaves, ushort[] leafBrushes,
            (int, int, int)[] brushes, (int, bool)[] sides, Vec3 mins, Vec3 maxs)
        {
            this.planes = planes;
            this.nodes = nodes;
            this.leaves = leaves;
            this.leafBrushes = leafBrushes;
            this.brushes = brushes;
            this.sides = sides;
            Mins = mins;
            Maxs = maxs;
        }

        public int BrushCount => brushes.Length;

        public static BspSpace Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            if (r.ReadInt32() != 0x50534256)
                throw new InvalidDataException($"{path} is not a Source BSP.");
            r.ReadInt32();

            var offsets = new int[64];
            var lengths = new int[64];
            var versions = new int[64];
            for (int i = 0; i < 64; i++)
            {
                offsets[i] = r.ReadInt32();
                lengths[i] = r.ReadInt32();
                versions[i] = r.ReadInt32();
                r.ReadInt32();
            }
            byte[] Lump(int index) => lengths[index] <= 0 ? [] : LzmaLump.Read(r, offsets[index], lengths[index]);

            var planeBytes = Lump(LumpPlanes);
            var planes = new Plane3[planeBytes.Length / 20];
            for (int i = 0; i < planes.Length; i++)
            {
                int at = i * 20;
                planes[i] = new Plane3(new Vec3(BitConverter.ToSingle(planeBytes, at), BitConverter.ToSingle(planeBytes, at + 4),
                    BitConverter.ToSingle(planeBytes, at + 8)), BitConverter.ToSingle(planeBytes, at + 12));
            }

            var nodeBytes = Lump(LumpNodes);
            var nodes = new (int, int, int)[nodeBytes.Length / 32];
            for (int i = 0; i < nodes.Length; i++)
            {
                int at = i * 32;
                nodes[i] = (BitConverter.ToInt32(nodeBytes, at), BitConverter.ToInt32(nodeBytes, at + 4), BitConverter.ToInt32(nodeBytes, at + 8));
            }

            var leafBytes = Lump(LumpLeafs);
            int leafSize = versions[LumpLeafs] == 0 ? 56 : 32;
            var leaves = new (int, int, int, int, int)[leafBytes.Length / leafSize];
            for (int i = 0; i < leaves.Length; i++)
            {
                int at = i * leafSize;
                leaves[i] = (BitConverter.ToInt32(leafBytes, at), BitConverter.ToInt16(leafBytes, at + 4),
                    BitConverter.ToUInt16(leafBytes, at + 24), BitConverter.ToUInt16(leafBytes, at + 26),
                    BitConverter.ToUInt16(leafBytes, at + 6) & 0x1FF);
            }

            var lbBytes = Lump(LumpLeafBrushes);
            var leafBrushes = new ushort[lbBytes.Length / 2];
            for (int i = 0; i < leafBrushes.Length; i++)
                leafBrushes[i] = BitConverter.ToUInt16(lbBytes, i * 2);

            var brushBytes = Lump(LumpBrushes);
            var brushes = new (int, int, int)[brushBytes.Length / 12];
            for (int i = 0; i < brushes.Length; i++)
            {
                int at = i * 12;
                brushes[i] = (BitConverter.ToInt32(brushBytes, at), BitConverter.ToInt32(brushBytes, at + 4), BitConverter.ToInt32(brushBytes, at + 8));
            }

            var sideBytes = Lump(LumpBrushSides);
            var sides = new (int, bool)[sideBytes.Length / 8];
            for (int i = 0; i < sides.Length; i++)
            {
                int at = i * 8;
                sides[i] = (BitConverter.ToUInt16(sideBytes, at), BitConverter.ToInt16(sideBytes, at + 6) != 0);
            }

            var modelBytes = Lump(LumpModels);
            if (modelBytes.Length < 48 || nodes.Length == 0)
                throw new InvalidDataException($"{path} has no world model.");
            var mins = new Vec3(BitConverter.ToSingle(modelBytes, 0), BitConverter.ToSingle(modelBytes, 4), BitConverter.ToSingle(modelBytes, 8));
            var maxs = new Vec3(BitConverter.ToSingle(modelBytes, 12), BitConverter.ToSingle(modelBytes, 16), BitConverter.ToSingle(modelBytes, 20));

            foreach (var (plane, front, back) in nodes)
                if (plane < 0 || plane >= planes.Length || front >= nodes.Length || back >= nodes.Length
                    || -1 - front >= leaves.Length || -1 - back >= leaves.Length)
                    throw new InvalidDataException($"{path} has a node pointing outside the node, leaf or plane lumps.");
            foreach (var (_, _, first, count, _) in leaves)
                if (first + count > leafBrushes.Length)
                    throw new InvalidDataException($"{path} has a leaf whose brush list runs past the end of its lump.");
            foreach (ushort b in leafBrushes)
                if (b >= brushes.Length)
                    throw new InvalidDataException($"{path} lists a brush its brush lump does not have.");
            foreach (var (first, count, _) in brushes)
                if (first < 0 || first + count > sides.Length)
                    throw new InvalidDataException($"{path} has a brush whose sides run past the end of their lump.");

            return new BspSpace(planes, nodes, leaves, leafBrushes, brushes, sides, mins, maxs) { source = path };
        }

        private string? source;
        private Triangle[]? displacements;

        /// <summary>A displacement triangle.</summary>
        public readonly record struct Triangle(Vec3 A, Vec3 B, Vec3 C);

        /// <summary>
        /// The world's displacement surfaces. VBSP makes none of them solid in the tree: the space under
        /// a terrain displacement is open there, joined to the air above it, and only the displacement's
        /// own collision keeps a player on top. Read from the BSP the first time they are asked for.
        /// </summary>
        public IReadOnlyList<Triangle> Displacements => displacements ??= LoadDisplacements();

        private Triangle[] LoadDisplacements()
        {
            if (source == null)
                return [];
            var geometry = BspGeometry.Load(source);
            var list = new List<Triangle>();
            for (int i = 0; i < geometry.TriangleCount; i++)
                if (geometry.Displacement[i])
                    list.Add(new Triangle(geometry.Corners[i * 3], geometry.Corners[i * 3 + 1], geometry.Corners[i * 3 + 2]));
            return list.ToArray();
        }

        /// <summary>The leaf <paramref name="point"/> is in, walking the world's tree from its head node.</summary>
        public int LeafAt(Vec3 point)
        {
            int node = 0;
            while (node >= 0)
            {
                var (plane, front, back) = nodes[node];
                node = planes[plane].DistanceTo(point) >= 0 ? front : back;
            }
            return -1 - node;
        }

        public int ClusterAt(Vec3 point) => leaves[LeafAt(point)].Cluster;

        /// <summary>The area <paramref name="point"/> is in; areaportals divide the map into areas.</summary>
        public int AreaAt(Vec3 point) => leaves[LeafAt(point)].Area;

        /// <summary>
        /// Whether <paramref name="point"/> is inside structural (world) brushes. Detail never counts:
        /// it does not fill leaves, and it cannot seal a leak or frame an areaportal.
        /// </summary>
        public bool IsStructuralSolid(Vec3 point) => (leaves[LeafAt(point)].Contents & ContentsSolid) != 0;

        /// <summary>
        /// Whether <paramref name="point"/> is in the void outside the map. VBSP fills everything a leak
        /// test cannot reach from an entity with solid, so the outside is solid leaves with no brush in
        /// them - unlike the inside of a wall, whose leaf lists the brush that fills it.
        /// </summary>
        public bool IsOutside(Vec3 point)
        {
            var leaf = leaves[LeafAt(point)];
            if ((leaf.Contents & ContentsSolid) == 0)
                return false;
            for (int i = 0; i < leaf.BrushCount; i++)
            {
                var brush = brushes[leafBrushes[leaf.FirstBrush + i]];
                bool inside = true;
                for (int s = 0; s < brush.SideCount && inside; s++)
                    inside = planes[sides[brush.FirstSide + s].Plane].DistanceTo(point) <= 0;
                if (inside)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Whether <paramref name="point"/> is inside a brush with any of <paramref name="contentsMask"/>,
        /// structural or detail.
        /// </summary>
        public bool IsSolid(Vec3 point, int contentsMask = ContentsSolid)
        {
            var leaf = leaves[LeafAt(point)];
            if ((leaf.Contents & contentsMask) != 0)
                return true;

            for (int i = 0; i < leaf.BrushCount; i++)
            {
                var brush = brushes[leafBrushes[leaf.FirstBrush + i]];
                if ((brush.Contents & contentsMask) == 0)
                    continue;
                bool inside = true;
                for (int s = 0; s < brush.SideCount && inside; s++)
                    inside = planes[sides[brush.FirstSide + s].Plane].DistanceTo(point) <= 0;
                if (inside)
                    return true;
            }
            return false;
        }

        /// <summary>One collision brush, by what it occupies: contents (without the detail bit), bounds and volume.</summary>
        public readonly record struct BrushShape(int Contents, Vec3 Mins, Vec3 Maxs, double Volume);

        /// <summary>
        /// Every brush with any of <paramref name="contentsMask"/>, as the space it fills.
        ///
        /// Shapes rather than plane lists, because VBSP++ does not always add the same extra collision
        /// planes (bevels) to a brush from one compile to the next: a bevel touches the brush without
        /// cutting it, so it changes the list of planes but not the shape, and comparing planes reports
        /// a change where there is none.
        /// </summary>
        public List<BrushShape> CollisionShapes(int contentsMask = ContentsSolid)
        {
            var result = new List<BrushShape>(brushes.Length);
            foreach (var (first, count, contents) in brushes)
            {
                if ((contents & contentsMask) == 0)
                    continue;
                var brushPlanes = new Plane3[count];
                for (int s = 0; s < count; s++)
                    brushPlanes[s] = planes[sides[first + s].Plane];

                var mins = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                var maxs = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                var centre = Vec3.Zero;
                var faces = new List<(Plane3 Plane, Winding Winding)>();
                for (int i = 0; i < count; i++)
                {
                    Winding? w = Winding.ForPlane(brushPlanes[i]);
                    for (int j = 0; j < count && w != null; j++)
                        if (j != i && !(brushPlanes[i] == brushPlanes[j]))
                            w = w.ClipBehind(brushPlanes[j]);
                    if (w == null)
                        continue;
                    faces.Add((brushPlanes[i], w));
                    foreach (var p in w.Points)
                    {
                        mins = Vec3.Min(mins, p);
                        maxs = Vec3.Max(maxs, p);
                    }
                }
                if (faces.Count == 0)
                    continue;
                centre = (mins + maxs) / 2;
                double volume = faces.Sum(f => f.Winding.Area * -f.Plane.DistanceTo(centre) / 3);
                result.Add(new BrushShape(contents & ~ContentsDetail, mins, maxs, volume));
            }
            return result;
        }

        /// <summary>
        /// How many brushes in either compile have no counterpart in the other: same contents and bounds
        /// within <paramref name="tolerance"/> units on every side. Zero means no brush was lost, added,
        /// moved or given other contents. Volume is not compared: on a brush a unit or two thick it is
        /// computed from faces so thin that rounding swings it by a third.
        /// </summary>
        public static int CollisionDifferences(IReadOnlyList<BrushShape> a, IReadOnlyList<BrushShape> b, double tolerance = 1.0)
        {
            // Bucket b by contents and rounded centre, so each brush of a looks only at brushes near it.
            static (int, long, long, long) Key(BrushShape s, int dx = 0, int dy = 0, int dz = 0)
            {
                var c = (s.Mins + s.Maxs) / 2;
                return (s.Contents, (long)Math.Floor(c.X / 8) + dx, (long)Math.Floor(c.Y / 8) + dy, (long)Math.Floor(c.Z / 8) + dz);
            }

            var buckets = new Dictionary<(int, long, long, long), List<int>>();
            for (int i = 0; i < b.Count; i++)
            {
                var key = Key(b[i]);
                if (!buckets.TryGetValue(key, out var list))
                    buckets[key] = list = [];
                list.Add(i);
            }

            // Each brush takes the closest unused counterpart, not the first: a map has brushes a fraction
            // of a unit apart, and taking the first can leave its neighbour with nothing.
            var used = new bool[b.Count];
            int unmatched = 0;
            foreach (var s in a)
            {
                int found = -1;
                double best = double.MaxValue;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!buckets.TryGetValue(Key(s, dx, dy, dz), out var list))
                        continue;
                    foreach (int i in list)
                    {
                        if (used[i] || !Same(s, b[i], tolerance))
                            continue;
                        double distance = Gap(s, b[i]);
                        if (distance < best) { best = distance; found = i; }
                    }
                }
                if (found < 0)
                    unmatched++;
                else
                    used[found] = true;
            }
            return unmatched + used.Count(u => !u);
        }

        private static double Gap(BrushShape x, BrushShape y) =>
            (x.Mins - y.Mins).Length + (x.Maxs - y.Maxs).Length;

        private static bool Same(BrushShape x, BrushShape y, double tolerance) =>
            x.Contents == y.Contents
            && Math.Abs(x.Mins.X - y.Mins.X) <= tolerance && Math.Abs(x.Mins.Y - y.Mins.Y) <= tolerance && Math.Abs(x.Mins.Z - y.Mins.Z) <= tolerance
            && Math.Abs(x.Maxs.X - y.Maxs.X) <= tolerance && Math.Abs(x.Maxs.Y - y.Maxs.Y) <= tolerance && Math.Abs(x.Maxs.Z - y.Maxs.Z) <= tolerance;
    }
}
