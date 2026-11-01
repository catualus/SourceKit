using System;
using System.Globalization;

namespace SourceKit.Geometry
{
    /// <summary>
    /// A point or direction in Hammer units, in double precision.
    ///
    /// Double rather than <see cref="System.Numerics.Vector3"/>: brush faces are built by clipping a
    /// plane against every other plane of the brush, and the planes come from integer-ish points up
    /// to ±16384 apart. Single precision loses whole units of accuracy at that scale, and a face that
    /// should touch its neighbour exactly ends up a fraction away from it.
    /// </summary>
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static readonly Vec3 Zero = new(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;
        public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));

        public Vec3 Normalized()
        {
            double length = Length;
            return length == 0 ? Zero : this / length;
        }

        public double this[int axis] => axis switch
        {
            0 => X,
            1 => Y,
            2 => Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

        public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        /// <summary>Parses "x y z", the form VMF origins and portal points use.</summary>
        public static bool TryParse(string text, out Vec3 value)
        {
            value = Zero;
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                return false;
            value = new Vec3(x, y, z);
            return true;
        }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{X:0.###} {Y:0.###} {Z:0.###}");
    }

    /// <summary>
    /// A plane as VBSP stores it: points <c>p</c> with <c>Normal·p == Distance</c>. For a brush side,
    /// the normal points out of the brush, so the brush is the set of points behind all of its planes.
    /// </summary>
    public readonly record struct Plane3(Vec3 Normal, double Distance)
    {
        /// <summary>
        /// The plane through three points the way VBSP's PlaneFromPoints builds a brush side's plane:
        /// the normal is (p0 - p1) × (p2 - p1), normalised. Null when the points are collinear.
        /// </summary>
        public static Plane3? FromPoints(Vec3 p0, Vec3 p1, Vec3 p2)
        {
            var normal = (p0 - p1).Cross(p2 - p1);
            if (normal.Length < 1e-9)
                return null;
            normal = normal.Normalized();
            return new Plane3(normal, normal.Dot(p0));
        }

        /// <summary>Positive in front of the plane (outside a brush), negative behind it.</summary>
        public double DistanceTo(Vec3 point) => Normal.Dot(point) - Distance;

        public Plane3 Flipped => new(-Normal, -Distance);

        /// <summary>True when the normal lies along one axis, which is what VBSP calls an axial plane.</summary>
        public bool IsAxial =>
            (Math.Abs(Normal.X) > 1 - 1e-9) || (Math.Abs(Normal.Y) > 1 - 1e-9) || (Math.Abs(Normal.Z) > 1 - 1e-9);
    }
}
