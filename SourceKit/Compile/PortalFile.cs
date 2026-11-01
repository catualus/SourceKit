using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SourceKit.Geometry;

namespace SourceKit.Compile
{
    /// <summary>A portal between two visibility clusters, as VBSP wrote it for VVIS.</summary>
    public sealed record Portal(int Index, int ClusterA, int ClusterB, Winding Winding);

    /// <summary>
    /// VBSP's portal file (<c>.prt</c>, format "PRT1"): the openings between the map's visibility
    /// clusters, which is everything VVIS works from.
    ///
    /// The layout is a header line, the cluster count, the portal count, then one portal per line:
    /// its point count, the two clusters it joins, and its points as "(x y z)". Detail brushes,
    /// func_detail and displacements are already gone by this stage, so the file is exactly the
    /// structural space a player's view can flow through.
    /// </summary>
    public sealed class PortalFile
    {
        public int ClusterCount { get; }
        public IReadOnlyList<Portal> Portals { get; }

        private PortalFile(int clusterCount, IReadOnlyList<Portal> portals)
        {
            ClusterCount = clusterCount;
            Portals = portals;
        }

        public static PortalFile Load(string path)
        {
            using var reader = new StreamReader(path);
            return Read(reader);
        }

        public static PortalFile Read(TextReader reader)
        {
            string? header = reader.ReadLine()?.Trim();
            if (header != "PRT1")
                throw new InvalidDataException($"Not a portal file: expected 'PRT1', found '{header}'.");

            int clusters = ReadCount(reader, "cluster");
            int count = ReadCount(reader, "portal");
            var portals = new List<Portal>(count);

            for (int i = 0; i < count; i++)
            {
                string line = reader.ReadLine()
                    ?? throw new InvalidDataException($"The file ends after {i} of its {count} portals.");
                portals.Add(ParsePortal(i, line, clusters));
            }

            return new PortalFile(clusters, portals);
        }

        private static Portal ParsePortal(int index, string line, int clusters)
        {
            int firstParen = line.IndexOf('(');
            if (firstParen < 0)
                throw new InvalidDataException($"Portal {index} has no points: '{line}'.");

            var head = line[..firstParen].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length != 3
                || !int.TryParse(head[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pointCount)
                || !int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)
                || !int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                throw new InvalidDataException($"Portal {index} does not start with a point count and two clusters: '{line}'.");

            if (a < 0 || b < 0 || a >= clusters || b >= clusters)
                throw new InvalidDataException($"Portal {index} joins clusters {a} and {b}, but the file has {clusters}.");
            if (pointCount < 3 || pointCount > 1024)
                throw new InvalidDataException($"Portal {index} claims {pointCount} points.");

            var points = new List<Vec3>(pointCount);
            int at = firstParen;
            while (points.Count < pointCount)
            {
                int open = line.IndexOf('(', at);
                int close = open < 0 ? -1 : line.IndexOf(')', open);
                if (close < 0 || !Vec3.TryParse(line[(open + 1)..close], out var point))
                    throw new InvalidDataException($"Portal {index} has {points.Count} readable points of {pointCount}.");
                points.Add(point);
                at = close + 1;
            }

            return new Portal(index, a, b, new Winding(points));
        }

        private static int ReadCount(TextReader reader, string what)
        {
            string? line = reader.ReadLine();
            if (!int.TryParse(line?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0 || count > 1 << 24)
                throw new InvalidDataException($"Expected the {what} count, found '{line}'.");
            return count;
        }
    }
}
