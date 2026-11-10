using System;
using System.Collections.Generic;
using System.Linq;

namespace SourceKit.Vmf
{
    public enum VmfChangeKind { Added, Removed, ValueChanged }

    /// <summary>
    /// One difference between two maps. <see cref="Path"/> names where it is, block by block, using ids
    /// where the block has one: <c>world/solid#41/side#200/material</c>. A block without an id is named by
    /// its position among blocks of its name, <c>versioninfo[0]</c>; a block repeating an id its parent
    /// already used gets <c>~1</c>, <c>~2</c> and so on after it, numbered per id in file order.
    /// </summary>
    public sealed record VmfChange(VmfChangeKind Kind, string Path, string? Before, string? After)
    {
        public override string ToString() => Kind switch
        {
            VmfChangeKind.ValueChanged => $"{Path}: \"{Before}\" -> \"{After}\"",
            VmfChangeKind.Added => $"{Path}: added{(After != null ? $" \"{After}\"" : "")}",
            _ => $"{Path}: removed{(Before != null ? $" \"{Before}\"" : "")}",
        };
    }

    /// <summary>
    /// What changed between two versions of a map, in terms of the map rather than its text.
    ///
    /// A tool that edits a map promises to change only certain things - a material here, a classname
    /// there, some blocks added. Checking that promise against a text diff is unreliable, since the
    /// same edit can move lines around; this compares the trees, pairing blocks by their id where
    /// they have one and by position among same-named siblings where they do not.
    /// </summary>
    public static class VmfDiff
    {
        public static IReadOnlyList<VmfChange> Compare(VmfDocument before, VmfDocument after)
        {
            var changes = new List<VmfChange>();
            CompareNodes(before.Root, after.Root, "", changes);
            return changes;
        }

        private static void CompareNodes(VmfNode a, VmfNode b, string path, List<VmfChange> changes)
        {
            CompareKeys(a, b, path, changes);

            var left = Keyed(a);
            var right = Keyed(b);

            foreach (var (key, node) in left)
            {
                string childPath = Join(path, key);
                if (right.TryGetValue(key, out var other))
                    CompareNodes(node, other, childPath, changes);
                else
                    changes.Add(new VmfChange(VmfChangeKind.Removed, childPath, null, null));
            }

            foreach (var (key, _) in right)
                if (!left.ContainsKey(key))
                    changes.Add(new VmfChange(VmfChangeKind.Added, Join(path, key), null, null));
        }

        private static void CompareKeys(VmfNode a, VmfNode b, string path, List<VmfChange> changes)
        {
            // Keys can repeat (an entity's outputs, a displacement's rows), so pair them by occurrence.
            var left = a.KeyValues.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList(), StringComparer.OrdinalIgnoreCase);
            var right = b.KeyValues.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var (key, values) in left)
            {
                right.TryGetValue(key, out var others);
                others ??= [];
                for (int i = 0; i < Math.Max(values.Count, others.Count); i++)
                {
                    string keyPath = Join(path, values.Count > 1 || others.Count > 1 ? $"{key}[{i}]" : key);
                    string? x = i < values.Count ? values[i] : null;
                    string? y = i < others.Count ? others[i] : null;
                    if (x == y)
                        continue;
                    var kind = x == null ? VmfChangeKind.Added : y == null ? VmfChangeKind.Removed : VmfChangeKind.ValueChanged;
                    changes.Add(new VmfChange(kind, keyPath, x, y));
                }
            }

            foreach (var (key, values) in right)
            {
                if (left.ContainsKey(key))
                    continue;
                for (int i = 0; i < values.Count; i++)
                    changes.Add(new VmfChange(VmfChangeKind.Added, Join(path, values.Count > 1 ? $"{key}[{i}]" : key), null, values[i]));
            }
        }

        /// <summary>
        /// Each child block under a key that identifies it: its id, or its position among blocks of its
        /// name.
        ///
        /// Ids are meant to be unique but real maps repeat them. A block whose key is already taken gets
        /// <c>~1</c> for the first repeat, <c>~2</c> for the next, counted per key - <c>solid#5</c>,
        /// <c>solid#5~1</c> - so same-id blocks pair up in the order they appear. Counting them any other
        /// way, such as by how many siblings came before, makes the key of an untouched block change
        /// when some other block earlier in the list is removed, and the diff then reports it as removed
        /// and added again.
        /// </summary>
        private static Dictionary<string, VmfNode> Keyed(VmfNode node)
        {
            var result = new Dictionary<string, VmfNode>(StringComparer.OrdinalIgnoreCase);
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var repeats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in node.Nodes)
            {
                string key;
                if (child.Id is { } id)
                    key = $"{child.Name}#{id}";
                else
                {
                    positions.TryGetValue(child.Name, out int position);
                    positions[child.Name] = position + 1;
                    key = $"{child.Name}[{position}]";
                }

                // Two blocks with the same id would collide; keep both, numbering the repeats.
                if (!result.TryAdd(key, child))
                {
                    repeats.TryGetValue(key, out int repeat);
                    repeats[key] = ++repeat;
                    result[$"{key}~{repeat}"] = child;
                }
            }
            return result;
        }

        private static string Join(string path, string part) => path.Length == 0 ? part : path + "/" + part;
    }
}
