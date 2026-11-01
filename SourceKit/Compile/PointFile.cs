using System;
using System.Collections.Generic;
using System.IO;
using SourceKit.Geometry;

namespace SourceKit.Compile
{
    /// <summary>
    /// VBSP's leak pointfile (<c>.lin</c>): the path from an entity to the void through the hole that
    /// lets them meet, one "x y z" point per line. VBSP writes it for an ordinary leak and for an
    /// areaportal that does not seal its two areas, and deletes it at the start of every compile.
    /// </summary>
    public sealed class PointFile
    {
        public IReadOnlyList<Vec3> Points { get; }

        private PointFile(IReadOnlyList<Vec3> points)
        {
            Points = points;
        }

        public static PointFile Load(string path)
        {
            using var reader = new StreamReader(path);
            return Read(reader);
        }

        public static PointFile Read(TextReader reader)
        {
            var points = new List<Vec3>();
            string? line;
            int number = 0;
            while ((line = reader.ReadLine()) != null)
            {
                number++;
                if (line.Trim().Length == 0)
                    continue;
                if (!Vec3.TryParse(line, out var point))
                    throw new InvalidDataException($"Line {number} of the pointfile is not a point: '{line}'.");
                points.Add(point);
            }
            return new PointFile(points);
        }

        /// <summary>The leak path as segments between consecutive points.</summary>
        public IEnumerable<(Vec3 From, Vec3 To)> Segments()
        {
            for (int i = 0; i + 1 < Points.Count; i++)
                yield return (Points[i], Points[i + 1]);
        }

        /// <summary>How close the leak path comes to <paramref name="point"/>.</summary>
        public double DistanceTo(Vec3 point)
        {
            double best = double.MaxValue;
            if (Points.Count == 1)
                return (Points[0] - point).Length;
            foreach (var (a, b) in Segments())
            {
                var ab = b - a;
                double lengthSquared = ab.Dot(ab);
                double t = lengthSquared == 0 ? 0 : Math.Clamp((point - a).Dot(ab) / lengthSquared, 0, 1);
                best = Math.Min(best, (a + ab * t - point).Length);
            }
            return best;
        }
    }
}
