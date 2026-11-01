using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SourceKit.Geometry;

namespace SourceKit.Vmf
{
    /// <summary>One side of a brush: its plane, the polygon it covers, and the block it came from.</summary>
    public sealed class VmfFace
    {
        public VmfNode Side { get; }
        public Plane3 Plane { get; }

        /// <summary>The visible polygon, or null when the side's plane does not reach the brush surface.</summary>
        public Winding? Winding { get; }

        public string Material => Side.Get("material") ?? "";
        public bool HasDisplacement => Side.Child("dispinfo") != null;

        internal VmfFace(VmfNode side, Plane3 plane, Winding? winding)
        {
            Side = side;
            Plane = plane;
            Winding = winding;
        }
    }

    /// <summary>
    /// A brush built into geometry from its sides' plane points.
    ///
    /// A VMF stores a brush only as planes - three points per side - and its vertices, edges and face
    /// polygons have to be worked out the way VBSP does: start each side as a huge square on its plane
    /// and cut it down by every other side. Hammer also writes a "vertices_plus" list for some sides,
    /// but not every editor does and it is not what VBSP reads, so it is not used here.
    /// </summary>
    public sealed class VmfBrush
    {
        public VmfNode Solid { get; }
        public IReadOnlyList<VmfFace> Faces { get; }
        public Vec3 Mins { get; }
        public Vec3 Maxs { get; }

        /// <summary>Faces whose side had three collinear points or a plane that leaves no polygon.</summary>
        public int DegenerateSides { get; }

        private VmfBrush(VmfNode solid, IReadOnlyList<VmfFace> faces, Vec3 mins, Vec3 maxs, int degenerate)
        {
            Solid = solid;
            Faces = faces;
            Mins = mins;
            Maxs = maxs;
            DegenerateSides = degenerate;
        }

        public Vec3 Size => Maxs - Mins;

        /// <summary>Whether every side lies along an axis, which makes the brush a box.</summary>
        public bool IsAxialBox => Faces.Count == 6 && Faces.All(f => f.Plane.IsAxial);

        public bool HasDisplacement => Faces.Any(f => f.HasDisplacement);

        public double Volume
        {
            get
            {
                // Sum of the pyramids from the centre to each face.
                var centre = (Mins + Maxs) / 2;
                double volume = 0;
                foreach (var face in Faces)
                    if (face.Winding != null)
                        volume += face.Winding.Area * -face.Plane.DistanceTo(centre) / 3;
                return volume;
            }
        }

        /// <summary>
        /// Builds a brush from a "solid" block, or returns null when its sides do not enclose a volume -
        /// a brush VBSP would also reject.
        /// </summary>
        public static VmfBrush? Build(VmfNode solid)
        {
            var sides = solid.Children("side").ToList();
            var planes = new List<(VmfNode Side, Plane3 Plane)>(sides.Count);
            int degenerate = 0;
            foreach (var side in sides)
            {
                if (TryReadPlane(side, out var plane))
                    planes.Add((side, plane));
                else
                    degenerate++;
            }

            if (planes.Count < 4)
                return null;

            var faces = new List<VmfFace>(planes.Count);
            var mins = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var maxs = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            int polygons = 0;

            for (int i = 0; i < planes.Count; i++)
            {
                Winding? winding = Winding.ForPlane(planes[i].Plane);
                for (int j = 0; j < planes.Count && winding != null; j++)
                {
                    if (j == i)
                        continue;
                    // A duplicate of this side's plane would cut the face away entirely.
                    if (IsSamePlane(planes[i].Plane, planes[j].Plane))
                        continue;
                    winding = winding.ClipBehind(planes[j].Plane);
                }

                if (winding == null)
                    degenerate++;
                else
                {
                    polygons++;
                    foreach (var p in winding.Points)
                    {
                        mins = Vec3.Min(mins, p);
                        maxs = Vec3.Max(maxs, p);
                    }
                }

                faces.Add(new VmfFace(planes[i].Side, planes[i].Plane, winding));
            }

            if (polygons < 4 || mins.X >= maxs.X || mins.Y >= maxs.Y || mins.Z >= maxs.Z)
                return null;
            if (Math.Abs(mins.X) >= Winding.MaxExtent / 2 || Math.Abs(maxs.X) >= Winding.MaxExtent / 2
                || Math.Abs(mins.Y) >= Winding.MaxExtent / 2 || Math.Abs(maxs.Y) >= Winding.MaxExtent / 2
                || Math.Abs(mins.Z) >= Winding.MaxExtent / 2 || Math.Abs(maxs.Z) >= Winding.MaxExtent / 2)
                return null; // open: some direction is not bounded by any side

            return new VmfBrush(solid, faces, mins, maxs, degenerate);
        }

        /// <summary>Reads a side's "plane" value, "(x y z) (x y z) (x y z)".</summary>
        public static bool TryReadPlane(VmfNode side, out Plane3 plane)
        {
            plane = default;
            string? text = side.Get("plane");
            if (text == null)
                return false;

            var points = new Vec3[3];
            int found = 0, at = 0;
            while (found < 3)
            {
                int open = text.IndexOf('(', at);
                int close = open < 0 ? -1 : text.IndexOf(')', open);
                if (close < 0)
                    return false;
                if (!Vec3.TryParse(text[(open + 1)..close], out points[found]))
                    return false;
                found++;
                at = close + 1;
            }

            var built = Plane3.FromPoints(points[0], points[1], points[2]);
            if (built == null)
                return false;
            plane = built.Value;
            return true;
        }

        private static bool IsSamePlane(Plane3 a, Plane3 b) =>
            (a.Normal - b.Normal).Length < 1e-6 && Math.Abs(a.Distance - b.Distance) < 0.01;

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"solid {Solid.Id} ({Mins}) - ({Maxs})");
    }
}
