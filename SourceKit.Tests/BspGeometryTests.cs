using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SourceKit.Geometry;
using Xunit;

namespace SourceKit.Tests
{
    public class BspGeometryTests
    {
        /// <summary>
        /// A displacement's cells are cut on alternating diagonals, the way Source tessellates them: on
        /// the parity of the cell's first vertex index, which on a grid an odd number of points wide is
        /// the parity of row plus column. Cutting every cell the same way puts half the cells on the
        /// wrong surface wherever a cell is not flat.
        /// </summary>
        [Fact]
        public void DisplacementCellsAreCutInACheckerboard()
        {
            // Power 2: five points a side, sixteen cells, each corner of a cell at a different height so
            // no cell is flat and the diagonal decides the surface.
            const int power = 2, n = (1 << power) + 1;
            var quad = new List<Vec3> { new(0, 0, 0), new(128, 0, 0), new(128, 128, 0), new(0, 128, 0) };

            var info = new byte[176];
            BitConverter.GetBytes(0f).CopyTo(info, 0);              // start position: the first corner
            BitConverter.GetBytes(0f).CopyTo(info, 4);
            BitConverter.GetBytes(0f).CopyTo(info, 8);
            BitConverter.GetBytes(0).CopyTo(info, 12);              // first vertex
            BitConverter.GetBytes(power).CopyTo(info, 20);

            static float Height(int i, int j) => (i * 7 + j * 13) % 5 * 4;

            var verts = new byte[n * n * 20];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    int at = (i * n + j) * 20;
                    BitConverter.GetBytes(0f).CopyTo(verts, at);
                    BitConverter.GetBytes(0f).CopyTo(verts, at + 4);
                    BitConverter.GetBytes(1f).CopyTo(verts, at + 8);         // straight up...
                    BitConverter.GetBytes(Height(i, j)).CopyTo(verts, at + 12);   // ...by this much
                }

            var corners = new List<Vec3>();
            BspGeometry.AddDisplacement(quad, info, 0, verts, 7, true, corners, new List<int>(), new List<bool>());

            Assert.Equal((n - 1) * (n - 1) * 2 * 3, corners.Count);

            Vec3 Point(int i, int j) => new(i * 128.0 / (n - 1), j * 128.0 / (n - 1), Height(i, j));

            for (int i = 0; i + 1 < n; i++)
                for (int j = 0; j + 1 < n; j++)
                {
                    int first = ((i * (n - 1)) + j) * 6;
                    var a = corners.Skip(first).Take(3).ToArray();
                    var b = corners.Skip(first + 3).Take(3).ToArray();

                    // The two triangles of a cell share exactly its diagonal.
                    var shared = a.Intersect(b).ToHashSet();
                    var expected = ((i + j) & 1) == 0
                        ? new[] { Point(i, j), Point(i + 1, j + 1) }
                        : new[] { Point(i, j + 1), Point(i + 1, j) };

                    Assert.True(shared.SetEquals(expected), $"cell ({i}, {j}) is cut on the wrong diagonal");

                    // And both keep the winding of the rest of the world: facing up, out of the quad.
                    foreach (var t in new[] { a, b })
                        Assert.True((t[1] - t[0]).Cross(t[2] - t[0]).Z > 0, $"cell ({i}, {j}) faces down");
                }
        }

        /// <summary>
        /// The same surface read from a whole map: a face that names a displacement is replaced by the
        /// displacement's grid, found through the dispinfo and dispvert lumps, and cut in the checkerboard.
        /// </summary>
        [Fact]
        public void ADisplacementFaceInAMapBecomesItsGrid()
        {
            const int power = 2, n = (1 << power) + 1;
            static byte[] F(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
            static byte[] I(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
            static float Height(int i, int j) => (i * 7 + j * 13) % 5 * 4;

            var face = new byte[56];
            BitConverter.GetBytes(0).CopyTo(face, 4);           // first surfedge
            BitConverter.GetBytes((short)4).CopyTo(face, 8);    // four edges
            BitConverter.GetBytes((short)0).CopyTo(face, 10);   // texinfo 0
            BitConverter.GetBytes((short)0).CopyTo(face, 12);   // displacement 0

            var dispInfo = new byte[176];
            BitConverter.GetBytes(power).CopyTo(dispInfo, 20);  // start (0 0 0), first vertex 0

            var model = new byte[48];
            BitConverter.GetBytes(1).CopyTo(model, 44);         // the world's one face

            var lumps = new Dictionary<int, byte[]>
            {
                [3] = F(0, 0, 0, 128, 0, 0, 128, 128, 0, 0, 128, 0),
                [6] = new byte[72],
                [7] = face,
                [12] = [.. new byte[4], .. new ushort[] { 0, 1, 1, 2, 2, 3, 3, 0 }.SelectMany(BitConverter.GetBytes)],
                [13] = I(1, 2, 3, 4),
                [14] = model,
                [26] = dispInfo,
                [33] = Enumerable.Range(0, n * n).SelectMany(v => F(0, 0, 1, Height(v / n, v % n), 0)).ToArray(),
            };

            string path = Path.Combine(Path.GetTempPath(), "sourcekit-disp-" + Guid.NewGuid().ToString("N") + ".bsp");
            File.WriteAllBytes(path, BspHeaderTests.Bsp(lumps));
            try
            {
                var geometry = BspGeometry.Load(path);

                Assert.Equal((n - 1) * (n - 1) * 2, geometry.TriangleCount);
                Assert.All(geometry.Displacement, d => Assert.True(d));
                Assert.All(geometry.FaceOf, f => Assert.Equal(0, f));

                Vec3 Point(int i, int j) => new(i * 128.0 / (n - 1), j * 128.0 / (n - 1), Height(i, j));
                HashSet<Vec3> Diagonal(int i, int j)
                {
                    int first = (i * (n - 1) + j) * 6;
                    var corners = geometry.Corners.Skip(first).Take(6).ToArray();
                    return corners.Take(3).Intersect(corners.Skip(3)).ToHashSet();
                }

                // Cell (0, 0) is cut corner to opposite corner from its first vertex, and its neighbour
                // (0, 1) the other way. Neither is flat, so the cut decides where the surface is.
                Assert.True(Diagonal(0, 0).SetEquals([Point(0, 0), Point(1, 1)]));
                Assert.True(Diagonal(0, 1).SetEquals([Point(0, 2), Point(1, 1)]));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
