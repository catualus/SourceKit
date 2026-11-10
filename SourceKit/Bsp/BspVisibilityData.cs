using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;

namespace SourceKit
{
    /// <summary>A visibility leaf: the cluster it belongs to, its area, its bounds and the faces in it.</summary>
    public readonly record struct BspLeaf(int Contents, int Cluster, int Area, Vec3 Mins, Vec3 Maxs, int FirstLeafFace, int LeafFaceCount);

    /// <summary>A drawn face: how many triangles it costs, and the leaves' area it sits in.</summary>
    public readonly record struct BspFace(int Triangles, bool IsDisplacement, int TexInfo);

    /// <summary>A static prop: its model, the run of leaves it sits in, where it stands, and how far away it is gone (0: never).</summary>
    public readonly record struct BspStaticProp(int Model, int FirstLeaf, int LeafCount, Vec3 Origin = default, float FadeMax = 0);

    /// <summary>
    /// An areaportal entity as compiled: its portal number, what kind it is, the door that closes it
    /// (its target), whether it starts open, and for a func_areaportalwindow the distance it is shut at.
    /// </summary>
    public sealed record BspAreaPortalEntity(int Number, string Classname, string? Targetname, string? Target, bool StartOpen, double FadeDist);

    /// <summary>
    /// What a compiled map's visibility is made of: the potentially visible set per cluster, which
    /// leaves make up each cluster, which faces each leaf draws, and the areas and areaportals.
    ///
    /// Read-only, and only these lumps - visibility (4), faces (7), texinfo (6), leaves (10), leaf
    /// faces (16), displacement info (26), areas (20), areaportals (21) and the entities (0). The PVS is
    /// decoded in full up front: rows are run-length encoded, and every question asked of it (how many
    /// clusters a cluster sees, how many triangles that adds up to) touches every row.
    /// </summary>
    public sealed class BspVisibilityData
    {
        private const int LumpEntities = 0;
        private const int LumpVisibility = 4;
        private const int LumpTexinfo = 6;
        private const int LumpFaces = 7;
        private const int LumpLeafs = 10;
        private const int LumpLeafFaces = 16;
        private const int LumpAreas = 20;
        private const int LumpAreaPortals = 21;
        private const int LumpClipPortalVerts = 41;
        private const int LumpDispInfo = 26;
        private const int LumpGame = 35;

        private const int SurfSky2D = 0x2, SurfSky = 0x4, SurfNoDraw = 0x80, SurfHint = 0x100, SurfSkip = 0x200;

        public int ClusterCount { get; }
        public IReadOnlyList<BspLeaf> Leaves { get; }
        public IReadOnlyList<BspFace> Faces { get; }
        public IReadOnlyList<ushort> LeafFaces { get; }

        /// <summary>Area entries in the BSP, including the reserved area 0.</summary>
        public int AreaCount { get; }

        /// <summary>Areaportal entries in the BSP: each portal is written once from each side.</summary>
        public int AreaPortalCount { get; }

        /// <summary>The largest "portalnumber" any func_areaportal or func_areaportalwindow was given.</summary>
        public int HighestPortalNumber { get; }

        /// <summary>How many func_areaportal and func_areaportalwindow entities the map has.</summary>
        public int AreaPortalEntityCount { get; }

        /// <summary>
        /// Every areaportal VBSP placed, by its portal number, with the two areas it joins. A portal that
        /// VBSP could not place between two areas is not here.
        /// </summary>
        public IReadOnlyDictionary<int, (int AreaA, int AreaB)> AreaPortalLinks { get; private init; } = new Dictionary<int, (int, int)>();

        /// <summary>
        /// The outline VBSP clipped each placed portal to, by portal number, as the engine uses it to clip
        /// what is drawn beyond the portal.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<Vec3>> AreaPortalPolygons { get; private init; } = new Dictionary<int, IReadOnlyList<Vec3>>();

        /// <summary>The static props' model names, which <see cref="StaticProps"/> index.</summary>
        public IReadOnlyList<string> StaticPropModels { get; private init; } = [];

        /// <summary>Every static prop, with the run of <see cref="StaticPropLeaves"/> it sits in.</summary>
        public IReadOnlyList<BspStaticProp> StaticProps { get; private init; } = [];

        public IReadOnlyList<ushort> StaticPropLeaves { get; private init; } = [];

        /// <summary>Every areaportal entity in the compiled map, numbered or not.</summary>
        public IReadOnlyList<BspAreaPortalEntity> AreaPortalEntities { get; private init; } = [];

        /// <summary>The portal number VBSP gave each named areaportal entity.</summary>
        public IReadOnlyDictionary<string, int> PortalNumbersByName { get; private init; } = new Dictionary<string, int>();

        private readonly byte[][] pvs;

        private BspVisibilityData(int clusters, byte[][] pvs, BspLeaf[] leaves, BspFace[] faces, ushort[] leafFaces,
            int areas, int areaPortals, int highestPortal, int portalEntities)
        {
            ClusterCount = clusters;
            this.pvs = pvs;
            Leaves = leaves;
            Faces = faces;
            LeafFaces = leafFaces;
            AreaCount = areas;
            AreaPortalCount = areaPortals;
            HighestPortalNumber = highestPortal;
            AreaPortalEntityCount = portalEntities;
        }

        public bool HasVisibility => ClusterCount > 0;

        /// <summary>Whether <paramref name="from"/> can potentially see <paramref name="to"/>.</summary>
        public bool CanSee(int from, int to) => (pvs[from][to >> 3] & (1 << (to & 7))) != 0;

        /// <summary>The clusters <paramref name="cluster"/> can potentially see, itself included.</summary>
        public IEnumerable<int> VisibleFrom(int cluster)
        {
            var row = pvs[cluster];
            for (int i = 0; i < ClusterCount; i++)
                if ((row[i >> 3] & (1 << (i & 7))) != 0)
                    yield return i;
        }

        public int VisibleCount(int cluster)
        {
            int count = 0;
            foreach (byte b in pvs[cluster])
                count += System.Numerics.BitOperations.PopCount(b);
            return count;
        }

        public static BspVisibilityData Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.ASCII);

            if (r.ReadInt32() != 0x50534256)
                throw new InvalidDataException($"{path} is not a Source BSP.");
            r.ReadInt32(); // version

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

            var texFlags = ReadTexinfoFlags(Lump(LumpTexinfo));
            var dispPowers = ReadDispPowers(Lump(LumpDispInfo));
            var faces = ReadFaces(Lump(LumpFaces), texFlags, dispPowers);
            var leaves = ReadLeaves(Lump(LumpLeafs), versions[LumpLeafs]);
            var leafFaces = ReadUShorts(Lump(LumpLeafFaces));
            var (clusters, rows) = ReadPvs(Lump(LumpVisibility));

            foreach (var leaf in leaves)
            {
                if (leaf.Cluster >= clusters && clusters > 0)
                    throw new InvalidDataException($"A leaf names cluster {leaf.Cluster}, but the map has {clusters}.");
                if (leaf.FirstLeafFace + leaf.LeafFaceCount > leafFaces.Length)
                    throw new InvalidDataException("A leaf's face list runs past the end of the leaf face lump.");
            }
            foreach (ushort face in leafFaces)
                if (face >= faces.Length)
                    throw new InvalidDataException($"A leaf lists face {face}, but the map has {faces.Length}.");

            string entities = Encoding.Latin1.GetString(Lump(LumpEntities));
            var (propModels, props, propLeaves) = ReadStaticProps(r, offsets[LumpGame], lengths[LumpGame]);
            var portalNumbers = PortalNumbers(entities);

            // Counted from the lumps as read, not from the lump table: on a compressed map the table
            // gives the size on disk, which undercounts by the compression ratio (20 areas for 70 on
            // the shipped rp_downtown_meowy).
            var areas = Lump(LumpAreas);
            var areaPortals = Lump(LumpAreaPortals);

            return new BspVisibilityData(clusters, rows, leaves, faces, leafFaces,
                areas.Length / 8, areaPortals.Length / 12,
                portalNumbers.Count == 0 ? 0 : portalNumbers.Max(), portalNumbers.Count)
            {
                AreaPortalLinks = ReadAreaPortalLinks(areas, areaPortals),
                AreaPortalPolygons = ReadAreaPortalPolygons(areaPortals, Lump(LumpClipPortalVerts)),
                AreaPortalEntities = ReadAreaPortalEntities(entities),
                StaticPropModels = propModels,
                StaticProps = props,
                StaticPropLeaves = propLeaves,
                PortalNumbersByName = PortalNumbersByTargetname(entities),
            };
        }

        private static int[] ReadTexinfoFlags(byte[] bytes)
        {
            // texinfo_t: two 2x4 float vectors (64 bytes), then flags and texdata.
            const int size = 72;
            var flags = new int[bytes.Length / size];
            for (int i = 0; i < flags.Length; i++)
                flags[i] = BitConverter.ToInt32(bytes, i * size + 64);
            return flags;
        }

        private static int[] ReadDispPowers(byte[] bytes)
        {
            // ddispinfo_t is 176 bytes; power follows the start position and two vertex/triangle starts.
            const int size = 176;
            var powers = new int[bytes.Length / size];
            for (int i = 0; i < powers.Length; i++)
                powers[i] = BitConverter.ToInt32(bytes, i * size + 20);
            return powers;
        }

        private static BspFace[] ReadFaces(byte[] bytes, int[] texFlags, int[] dispPowers)
        {
            const int size = 56;
            var faces = new BspFace[bytes.Length / size];
            for (int i = 0; i < faces.Length; i++)
            {
                int at = i * size;
                int edges = BitConverter.ToInt16(bytes, at + 8);
                int texinfo = BitConverter.ToInt16(bytes, at + 10);
                int dispinfo = BitConverter.ToInt16(bytes, at + 12);

                int flags = texinfo >= 0 && texinfo < texFlags.Length ? texFlags[texinfo] : 0;
                bool drawn = (flags & (SurfSky2D | SurfSky | SurfNoDraw | SurfHint | SurfSkip)) == 0;

                int triangles;
                bool isDisplacement = dispinfo >= 0 && dispinfo < dispPowers.Length;
                if (!drawn)
                    triangles = 0;
                else if (isDisplacement)
                {
                    int side = 1 << Math.Clamp(dispPowers[dispinfo], 2, 4);
                    triangles = 2 * side * side;
                }
                else
                    triangles = Math.Max(0, edges - 2);

                faces[i] = new BspFace(triangles, isDisplacement, texinfo);
            }
            return faces;
        }

        private static BspLeaf[] ReadLeaves(byte[] bytes, int version)
        {
            // Version 0 leaves carry a 24-byte ambient light cube after the 32 bytes every version has.
            int size = version == 0 ? 56 : 32;
            var leaves = new BspLeaf[bytes.Length / size];
            for (int i = 0; i < leaves.Length; i++)
            {
                int at = i * size;
                int contents = BitConverter.ToInt32(bytes, at);
                int cluster = BitConverter.ToInt16(bytes, at + 4);
                int areaFlags = BitConverter.ToUInt16(bytes, at + 6);
                var mins = new Vec3(BitConverter.ToInt16(bytes, at + 8), BitConverter.ToInt16(bytes, at + 10), BitConverter.ToInt16(bytes, at + 12));
                var maxs = new Vec3(BitConverter.ToInt16(bytes, at + 14), BitConverter.ToInt16(bytes, at + 16), BitConverter.ToInt16(bytes, at + 18));
                int firstFace = BitConverter.ToUInt16(bytes, at + 20);
                int faceCount = BitConverter.ToUInt16(bytes, at + 22);
                leaves[i] = new BspLeaf(contents, cluster, areaFlags & 0x1FF, mins, maxs, firstFace, faceCount);
            }
            return leaves;
        }

        private static ushort[] ReadUShorts(byte[] bytes)
        {
            var values = new ushort[bytes.Length / 2];
            for (int i = 0; i < values.Length; i++)
                values[i] = BitConverter.ToUInt16(bytes, i * 2);
            return values;
        }

        /// <summary>
        /// Decodes every cluster's PVS row. A row is run-length encoded: a non-zero byte stands for itself,
        /// and a zero byte is followed by how many zero bytes it stands for.
        /// </summary>
        private static (int Clusters, byte[][] Rows) ReadPvs(byte[] bytes)
        {
            if (bytes.Length < 4)
                return (0, []);

            int clusters = BitConverter.ToInt32(bytes, 0);
            if (clusters < 0 || 4 + clusters * 8L > bytes.Length)
                throw new InvalidDataException($"The visibility lump claims {clusters} clusters, more than it holds.");

            int rowBytes = (clusters + 7) / 8;
            var rows = new byte[clusters][];
            for (int c = 0; c < clusters; c++)
            {
                int offset = BitConverter.ToInt32(bytes, 4 + c * 8);
                if (offset < 0 || offset >= bytes.Length)
                    throw new InvalidDataException($"Cluster {c}'s visibility row starts outside the lump.");

                var row = new byte[rowBytes];
                int at = offset, outAt = 0;
                while (outAt < rowBytes)
                {
                    if (at >= bytes.Length)
                        throw new InvalidDataException($"Cluster {c}'s visibility row runs past the end of the lump.");
                    byte b = bytes[at++];
                    if (b != 0)
                    {
                        row[outAt++] = b;
                        continue;
                    }
                    if (at >= bytes.Length)
                        throw new InvalidDataException($"Cluster {c}'s visibility row ends inside a run.");
                    outAt += bytes[at++];
                }
                rows[c] = row;
            }
            return (clusters, rows);
        }

        /// <summary>
        /// Each area lists its portals (dareas_t: count and first index, 8 bytes) in the areaportal lump
        /// (dareaportal_t, 12 bytes: portal key, the other area, clip vertex range, plane). A portal is
        /// listed once from each side, which is what gives both of its areas.
        /// </summary>
        private static Dictionary<int, (int, int)> ReadAreaPortalLinks(byte[] areas, byte[] portals)
        {
            var links = new Dictionary<int, (int, int)>();
            int portalCount = portals.Length / 12;
            for (int area = 0; area < areas.Length / 8; area++)
            {
                int count = BitConverter.ToInt32(areas, area * 8), first = BitConverter.ToInt32(areas, area * 8 + 4);
                if (count < 0 || first < 0 || first + count > portalCount)
                    throw new InvalidDataException($"Area {area}'s portal list runs past the end of the areaportal lump.");
                for (int i = first; i < first + count; i++)
                {
                    int key = BitConverter.ToUInt16(portals, i * 12);
                    int other = BitConverter.ToUInt16(portals, i * 12 + 2);
                    links[key] = (area, other);
                }
            }
            return links;
        }

        /// <summary>Each portal's clip outline: dareaportal_t names a run of clip portal vertices (12-byte vectors).</summary>
        private static Dictionary<int, IReadOnlyList<Vec3>> ReadAreaPortalPolygons(byte[] portals, byte[] verts)
        {
            var polygons = new Dictionary<int, IReadOnlyList<Vec3>>();
            int vertCount = verts.Length / 12;
            for (int i = 0; i < portals.Length / 12; i++)
            {
                int key = BitConverter.ToUInt16(portals, i * 12);
                int first = BitConverter.ToUInt16(portals, i * 12 + 4), count = BitConverter.ToUInt16(portals, i * 12 + 6);
                if (polygons.ContainsKey(key) || count < 3 || first + count > vertCount)
                    continue;
                var points = new Vec3[count];
                for (int v = 0; v < count; v++)
                {
                    int at = (first + v) * 12;
                    points[v] = new Vec3(BitConverter.ToSingle(verts, at), BitConverter.ToSingle(verts, at + 4), BitConverter.ToSingle(verts, at + 8));
                }
                polygons[key] = points;
            }
            return polygons;
        }

        /// <summary>
        /// The static prop game lump ('sprp'): model names of 128 bytes, the leaf list, then the props.
        /// Every version keeps the first leaf and leaf count at the same place in a prop, after its
        /// origin, angles and type; the size of a prop is what the lump has left over each.
        ///
        /// A compressed map (flag 1) compresses each game lump on its own, LZMA-tagged like any other
        /// lump; its directory entry's size is the decompressed size, and the tag carries the rest.
        /// </summary>
        private static (string[] Models, BspStaticProp[] Props, ushort[] Leaves) ReadStaticProps(BinaryReader r, int offset, int length)
        {
            if (length < 4)
                return ([], [], []);
            r.BaseStream.Position = offset;
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                ushort flags = r.ReadUInt16();
                r.ReadUInt16(); // version
                int at = r.ReadInt32(), size = r.ReadInt32();
                if (id != 0x73707270 || size < 12)
                    continue;
                byte[] bytes;
                if ((flags & 1) != 0)
                {
                    bytes = LzmaLump.Read(r, at, size);
                    size = bytes.Length;
                    if (size < 12)
                        return ([], [], []);
                }
                else
                {
                    bytes = new byte[size];
                    r.BaseStream.Position = at;
                    r.BaseStream.ReadExactly(bytes);
                }
                int p = 0;
                int names = BitConverter.ToInt32(bytes, p); p += 4;
                if (names < 0 || p + names * 128L > size)
                    return ([], [], []);
                var models = new string[names];
                for (int n = 0; n < names; n++, p += 128)
                {
                    int end = Array.IndexOf(bytes, (byte)0, p, 128);
                    models[n] = Encoding.ASCII.GetString(bytes, p, (end < 0 ? p + 128 : end) - p);
                }
                int leafCount = BitConverter.ToInt32(bytes, p); p += 4;
                if (leafCount < 0 || p + leafCount * 2L > size)
                    return ([], [], []);
                var leaves = new ushort[leafCount];
                for (int n = 0; n < leafCount; n++, p += 2)
                    leaves[n] = BitConverter.ToUInt16(bytes, p);
                int propCount = BitConverter.ToInt32(bytes, p); p += 4;
                if (propCount <= 0)
                    return (models, [], leaves);
                int stride = (size - p) / propCount;
                if (stride < 30)
                    return (models, [], leaves);
                var props = new BspStaticProp[propCount];
                for (int n = 0; n < propCount; n++)
                {
                    int e = p + n * stride;
                    var origin = new Vec3(BitConverter.ToSingle(bytes, e), BitConverter.ToSingle(bytes, e + 4), BitConverter.ToSingle(bytes, e + 8));
                    // fade distances follow the skin, from version 4 on: every version a GMod or TF2 map uses
                    float fadeMax = stride >= 44 ? BitConverter.ToSingle(bytes, e + 40) : 0;
                    props[n] = new BspStaticProp(BitConverter.ToUInt16(bytes, e + 24), BitConverter.ToUInt16(bytes, e + 26), BitConverter.ToUInt16(bytes, e + 28),
                        origin, fadeMax > 0 ? fadeMax : 0);
                }
                return (models, props, leaves);
            }
            return ([], [], []);
        }

        private static List<BspAreaPortalEntity> ReadAreaPortalEntities(string entities)
        {
            var result = new List<BspAreaPortalEntity>();
            foreach (string block in entities.Split('}'))
            {
                string? classname = KeyIn(block, "classname");
                if (classname is not ("func_areaportal" or "func_areaportalwindow") || KeyIn(block, "portalnumber") is not { } number
                    || !int.TryParse(number, out int n))
                    continue;
                double fade = double.TryParse(KeyIn(block, "FadeDist"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : double.PositiveInfinity;
                result.Add(new BspAreaPortalEntity(n, classname, KeyIn(block, "targetname"), KeyIn(block, "target"),
                    KeyIn(block, "StartOpen") is not "0", classname == "func_areaportalwindow" ? fade : double.PositiveInfinity));
            }
            return result;
        }

        private static Dictionary<string, int> PortalNumbersByTargetname(string entities)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string block in entities.Split('}'))
            {
                string? name = KeyIn(block, "targetname"), number = KeyIn(block, "portalnumber");
                if (name != null && number != null && int.TryParse(number, out int n))
                    result[name] = n;
            }
            return result;
        }

        private static string? KeyIn(string block, string key)
        {
            string quoted = "\"" + key + "\"";
            int at = block.IndexOf(quoted, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            int open = block.IndexOf('"', at + quoted.Length);
            int close = open < 0 ? -1 : block.IndexOf('"', open + 1);
            return close > open ? block[(open + 1)..close] : null;
        }

        private static List<int> PortalNumbers(string entities)
        {
            var numbers = new List<int>();
            const string key = "\"portalnumber\"";
            int at = 0;
            while ((at = entities.IndexOf(key, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                at += key.Length;
                int open = entities.IndexOf('"', at);
                int close = open < 0 ? -1 : entities.IndexOf('"', open + 1);
                if (close > open && int.TryParse(entities.AsSpan(open + 1, close - open - 1), out int n))
                    numbers.Add(n);
            }
            return numbers;
        }
    }
}
