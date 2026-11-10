using System;
using System.Collections.Generic;
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
    }
}
