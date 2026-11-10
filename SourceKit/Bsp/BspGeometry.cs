using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SourceKit.Geometry;

namespace SourceKit
{
    /// <summary>
    /// The world's drawn surfaces as triangles: every face of the world model, cut into a fan, and every
    /// displacement built from its grid of offsets. Each triangle knows the face it came from, and whether
    /// it can be seen through (translucent, or a tool face that is never drawn), so a ray can pass it.
    /// </summary>
    public sealed class BspGeometry
    {
        private const int LumpTexinfo = 6, LumpFaces = 7, LumpVertexes = 3, LumpEdges = 12, LumpSurfedges = 13, LumpModels = 14;
        private const int LumpDispInfo = 26, LumpDispVerts = 33;
        private const int SurfSky2D = 0x2, SurfSky = 0x4, SurfTrans = 0x10, SurfNoDraw = 0x80, SurfHint = 0x100, SurfSkip = 0x200;

        /// <summary>Triangle corners, three per triangle.</summary>
        public Vec3[] Corners { get; }

        /// <summary>The face each triangle belongs to.</summary>
        public int[] FaceOf { get; }

        /// <summary>Whether each triangle stops a ray: false for translucent and undrawn surfaces.</summary>
        public bool[] Blocks { get; }

        /// <summary>Whether each triangle is sky: the map's ceiling, drawn as the skybox.</summary>
        public bool[] Sky { get; private init; } = [];

        /// <summary>Whether each triangle is part of a displacement.</summary>
        public bool[] Displacement { get; private init; } = [];

        public int FaceCount { get; }

        public int TriangleCount => FaceOf.Length;

        private BspGeometry(Vec3[] corners, int[] faceOf, bool[] blocks, int faceCount)
        {
            Corners = corners;
            FaceOf = faceOf;
            Blocks = blocks;
            FaceCount = faceCount;
        }

        public static BspGeometry Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            if (r.ReadInt32() != 0x50534256)
                throw new InvalidDataException($"{path} is not a Source BSP.");
            r.ReadInt32();
            var offsets = new int[64];
            var lengths = new int[64];
            for (int i = 0; i < 64; i++)
            {
                offsets[i] = r.ReadInt32();
                lengths[i] = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt32();
            }
            byte[] Lump(int index) => lengths[index] <= 0 ? [] : LzmaLump.Read(r, offsets[index], lengths[index]);

            var texinfo = Lump(LumpTexinfo);
            var faces = Lump(LumpFaces);
            var vertexes = Lump(LumpVertexes);
            var edges = Lump(LumpEdges);
            var surfedges = Lump(LumpSurfedges);
            var models = Lump(LumpModels);
            var dispInfo = Lump(LumpDispInfo);
            var dispVerts = Lump(LumpDispVerts);

            Vec3 Vertex(int i) => new(BitConverter.ToSingle(vertexes, i * 12), BitConverter.ToSingle(vertexes, i * 12 + 4), BitConverter.ToSingle(vertexes, i * 12 + 8));

            // Only the world model's faces: brush entities move, or switch off, and no leaf lists them.
            int faceCount = faces.Length / 56;
            int worldFirst = 0, worldCount = faceCount;
            if (models.Length >= 48)
            {
                worldFirst = BitConverter.ToInt32(models, 40);
                worldCount = BitConverter.ToInt32(models, 44);
            }

            var corners = new List<Vec3>();
            var faceOf = new List<int>();
            var blocks = new List<bool>();
            var sky = new List<bool>();
            var displacement = new List<bool>();
            var polygon = new List<Vec3>();
            for (int f = worldFirst; f < Math.Min(faceCount, worldFirst + worldCount); f++)
            {
                int at = f * 56;
                int firstEdge = BitConverter.ToInt32(faces, at + 4);
                int edgeCount = BitConverter.ToInt16(faces, at + 8);
                int tex = BitConverter.ToInt16(faces, at + 10);
                int disp = BitConverter.ToInt16(faces, at + 12);
                int flags = tex >= 0 && tex * 72 + 68 <= texinfo.Length ? BitConverter.ToInt32(texinfo, tex * 72 + 64) : 0;
                if ((flags & (SurfHint | SurfSkip)) != 0)
                    continue;
                bool block = (flags & (SurfTrans | SurfNoDraw | SurfSky2D)) == 0;

                polygon.Clear();
                for (int e = 0; e < edgeCount; e++)
                {
                    int surfedge = BitConverter.ToInt32(surfedges, (firstEdge + e) * 4);
                    int edge = Math.Abs(surfedge);
                    int v = BitConverter.ToUInt16(edges, edge * 4 + (surfedge >= 0 ? 0 : 2));
                    polygon.Add(Vertex(v));
                }
                if (polygon.Count < 3)
                    continue;

                bool isSky = (flags & SurfSky) != 0;
                int before = faceOf.Count;
                if (disp >= 0 && (disp + 1) * 176 <= dispInfo.Length && polygon.Count == 4)
                {
                    AddDisplacement(polygon, dispInfo, disp * 176, dispVerts, f, block, corners, faceOf, blocks);
                    for (int k = before; k < faceOf.Count; k++)
                    {
                        sky.Add(isSky);
                        displacement.Add(true);
                    }
                    continue;
                }
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    corners.Add(polygon[0]);
                    corners.Add(polygon[k]);
                    corners.Add(polygon[k + 1]);
                    faceOf.Add(f);
                    blocks.Add(block);
                    sky.Add(isSky);
                    displacement.Add(false);
                }
            }
            return new BspGeometry(corners.ToArray(), faceOf.ToArray(), blocks.ToArray(), faceCount)
                { Sky = sky.ToArray(), Displacement = displacement.ToArray() };
        }

        /// <summary>
        /// A displacement's surface: its base quad turned so the first corner is the start position, a grid
        /// of 2^power + 1 points a side laid across it, each pushed out by its offset vector times distance,
        /// and each cell of the grid cut into two triangles on alternating diagonals.
        /// </summary>
        internal static void AddDisplacement(List<Vec3> quad, byte[] info, int at, byte[] verts, int face, bool block,
            List<Vec3> corners, List<int> faceOf, List<bool> blocks)
        {
            var start = new Vec3(BitConverter.ToSingle(info, at), BitConverter.ToSingle(info, at + 4), BitConverter.ToSingle(info, at + 8));
            int firstVert = BitConverter.ToInt32(info, at + 12);
            int power = Math.Clamp(BitConverter.ToInt32(info, at + 20), 1, 4);
            int n = (1 << power) + 1;
            if ((firstVert + n * n) * 20 > verts.Length)
                return;

            int first = 0;
            double nearest = double.MaxValue;
            for (int i = 0; i < 4; i++)
                if ((quad[i] - start).Length is var d && d < nearest)
                    (nearest, first) = (d, i);
            var c = new Vec3[4];
            for (int i = 0; i < 4; i++)
                c[i] = quad[(first + i) % 4];

            var grid = new Vec3[n * n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)(n - 1);
                var a = c[0] + (c[1] - c[0]) * t;
                var b = c[3] + (c[2] - c[3]) * t;
                for (int j = 0; j < n; j++)
                {
                    int v = (firstVert + i * n + j) * 20;
                    var offset = new Vec3(BitConverter.ToSingle(verts, v), BitConverter.ToSingle(verts, v + 4), BitConverter.ToSingle(verts, v + 8));
                    float distance = BitConverter.ToSingle(verts, v + 12);
                    grid[i * n + j] = a + (b - a) * (j / (double)(n - 1)) + offset * distance;
                }
            }
            for (int i = 0; i + 1 < n; i++)
                for (int j = 0; j + 1 < n; j++)
                {
                    var p00 = grid[i * n + j];
                    var p01 = grid[i * n + j + 1];
                    var p10 = grid[(i + 1) * n + j];
                    var p11 = grid[(i + 1) * n + j + 1];
                    // Source splits the cells in a checkerboard, on the parity of the cell's first
                    // vertex index - which, the grid being an odd number of points wide, is the parity
                    // of i + j. Splitting every cell on the same diagonal puts half of all cells on the
                    // wrong surface, by tens of units on coarse terrain. The winding is the same either
                    // way, so faces keep facing the way the BSP's own triangles do.
                    if (((i + j) & 1) == 0)
                    {
                        corners.Add(p00); corners.Add(p10); corners.Add(p11);
                        corners.Add(p00); corners.Add(p11); corners.Add(p01);
                    }
                    else
                    {
                        corners.Add(p00); corners.Add(p10); corners.Add(p01);
                        corners.Add(p10); corners.Add(p11); corners.Add(p01);
                    }
                    faceOf.Add(face); faceOf.Add(face);
                    blocks.Add(block); blocks.Add(block);
                }
        }
    }
}
