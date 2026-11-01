using System;
using System.Collections.Generic;

namespace SourceKit.Geometry
{
    /// <summary>
    /// A convex polygon, its points in order around it.
    ///
    /// The name is the compilers' own: VBSP and VVIS call a face or portal polygon a winding, and the
    /// operations here are the ones they use - start from a huge square on a plane, then cut it down
    /// by other planes.
    /// </summary>
    public sealed class Winding
    {
        /// <summary>Half the size of the square a plane starts as: well past the ±16384 map bounds.</summary>
        public const double MaxExtent = 65536;

        public IReadOnlyList<Vec3> Points { get; }

        public Winding(IReadOnlyList<Vec3> points)
        {
            Points = points;
        }

        /// <summary>A square on <paramref name="plane"/>, larger than any map, facing along its normal.</summary>
        public static Winding ForPlane(Plane3 plane)
        {
            var normal = plane.Normal;
            // The axis the normal leans on least gives a vector that is safely not parallel to it.
            double ax = Math.Abs(normal.X), ay = Math.Abs(normal.Y), az = Math.Abs(normal.Z);
            Vec3 up = az >= ax && az >= ay ? new Vec3(1, 0, 0) : new Vec3(0, 0, 1);

            var right = up.Cross(normal).Normalized() * MaxExtent;
            up = normal.Cross(right).Normalized() * MaxExtent;
            var origin = normal * plane.Distance;

            // Counter-clockwise seen from in front, so the winding faces the way the plane does.
            return new Winding([origin - right - up, origin + right - up, origin + right + up, origin - right + up]);
        }

        /// <summary>
        /// The part of this winding behind <paramref name="plane"/> (where <see cref="Plane3.DistanceTo"/>
        /// is at most <paramref name="epsilon"/>), or null when nothing is left.
        /// </summary>
        public Winding? ClipBehind(Plane3 plane, double epsilon = 0.01)
        {
            int count = Points.Count;
            var distances = new double[count];
            bool anyFront = false, anyBack = false;
            for (int i = 0; i < count; i++)
            {
                distances[i] = plane.DistanceTo(Points[i]);
                if (distances[i] > epsilon) anyFront = true;
                else if (distances[i] < -epsilon) anyBack = true;
            }

            if (!anyFront)
                return this;
            if (!anyBack)
                return null;

            var result = new List<Vec3>(count + 2);
            for (int i = 0; i < count; i++)
            {
                var a = Points[i];
                var b = Points[(i + 1) % count];
                double da = distances[i], db = distances[(i + 1) % count];

                if (da <= epsilon)
                    result.Add(a);

                bool crosses = (da > epsilon && db < -epsilon) || (da < -epsilon && db > epsilon);
                if (crosses)
                {
                    double t = da / (da - db);
                    var point = a + (b - a) * t;
                    // Snap the cut onto the plane's own axis value when it is axial, so faces that
                    // should meet on a grid line do so exactly rather than a rounding error apart.
                    result.Add(SnapToAxialPlane(point, plane));
                }
            }

            return result.Count >= 3 ? new Winding(result) : null;
        }

        public double Area
        {
            get
            {
                var total = Vec3.Zero;
                for (int i = 1; i + 1 < Points.Count; i++)
                    total += (Points[i] - Points[0]).Cross(Points[i + 1] - Points[0]);
                return total.Length / 2;
            }
        }

        public Vec3 Centre
        {
            get
            {
                var sum = Vec3.Zero;
                foreach (var p in Points)
                    sum += p;
                return sum / Points.Count;
            }
        }

        private static Vec3 SnapToAxialPlane(Vec3 point, Plane3 plane)
        {
            var n = plane.Normal;
            if (Math.Abs(n.X) > 1 - 1e-9) return point with { X = plane.Distance * Math.Sign(n.X) };
            if (Math.Abs(n.Y) > 1 - 1e-9) return point with { Y = plane.Distance * Math.Sign(n.Y) };
            if (Math.Abs(n.Z) > 1 - 1e-9) return point with { Z = plane.Distance * Math.Sign(n.Z) };
            return point;
        }
    }
}
