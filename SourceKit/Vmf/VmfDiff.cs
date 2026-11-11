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
    ///
    /// Two maps being compared are nearly always one map and an edited copy of it, so nearly every
    /// block lines up with the block at the same place on the other side and is unchanged. That case is
    /// checked first, in order and without allocating: blocks whose keys and children match one for one
    /// are skipped outright, and keys that match one for one are compared by position. Only where that
    /// fails - a block added or removed, a key repeated - are the blocks grouped and keyed, and the
    /// grouping is the one that decides the paths and the pairing of repeated ids, so the result is the
    /// same as if every block had been grouped.
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

            // Every child the same as the one at the same place on the other side: nothing below here
            // can differ, and grouping would pair each child with that same one.
            if (SameChildren(a, b))
                return;

            var left = Keyed(a);
            var right = Keyed(b);

            foreach (var (key, node) in left)
            {
                if (right.TryGetValue(key, out var other))
                {
                    if (!Same(node, other))
                        CompareNodes(node, other, Join(path, key), changes);
                }
                else
                    changes.Add(new VmfChange(VmfChangeKind.Removed, Join(path, key), null, null));
            }

            foreach (var (key, _) in right)
                if (!left.ContainsKey(key))
                    changes.Add(new VmfChange(VmfChangeKind.Added, Join(path, key), null, null));
        }

        /// <summary>
        /// Whether nothing in or under the two blocks differs: the same keys with the same values in the
        /// same order, and children that are the same, block for block. When this holds, grouping finds
        /// no change either: the children have the same names and ids in the same order, so they get the
        /// same keys on both sides and pair one for one.
        /// </summary>
        private static bool Same(VmfNode a, VmfNode b)
        {
            List<VmfItem> left = a.ItemList, right = b.ItemList;
            int i = 0, j = 0;
            while (true)
            {
                var x = NextPair(left, ref i);
                var y = NextPair(right, ref j);
                if (x == null || y == null)
                {
                    if (x != y)
                        return false;
                    break;
                }
                if (!string.Equals(x.Key, y.Key, StringComparison.Ordinal) || !string.Equals(x.Value, y.Value, StringComparison.Ordinal))
                    return false;
            }
            return SameChildren(a, b);
        }

        /// <summary>
        /// Whether the child blocks match one for one: the same name, and <see cref="Same"/>. Same keys
        /// with the same values means the same id, so the same key when grouped.
        /// </summary>
        private static bool SameChildren(VmfNode a, VmfNode b)
        {
            List<VmfItem> left = a.ItemList, right = b.ItemList;
            int i = 0, j = 0;
            while (true)
            {
                var x = NextNode(left, ref i);
                var y = NextNode(right, ref j);
                if (x == null || y == null)
                    return x == y;
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal) || !Same(x, y))
                    return false;
            }
        }

        private static void CompareKeys(VmfNode a, VmfNode b, string path, List<VmfChange> changes)
        {
            // In order first. The same keys in the same order, none repeated, pair by position exactly as
            // grouping pairs them, and an unchanged block - nearly all of them - costs no allocation.
            List<VmfItem> left = a.ItemList, right = b.ItemList;
            int i = 0, j = 0, count = 0;
            bool sameValues = true;
            while (true)
            {
                var x = NextPair(left, ref i);
                var y = NextPair(right, ref j);
                if (x == null || y == null)
                {
                    if (x != y)
                    {
                        CompareKeysGrouped(a, b, path, changes);
                        return;
                    }
                    break;
                }
                if (!string.Equals(x.Key, y.Key, StringComparison.Ordinal))
                {
                    CompareKeysGrouped(a, b, path, changes);
                    return;
                }
                sameValues &= string.Equals(x.Value, y.Value, StringComparison.Ordinal);
                count++;
            }

            if (sameValues)
                return;

            // A repeated key is numbered by occurrence and reported grouped by key, not in file order.
            if (HasRepeatedKey(left, count))
            {
                CompareKeysGrouped(a, b, path, changes);
                return;
            }

            i = j = 0;
            while (NextPair(left, ref i) is { } x && NextPair(right, ref j) is { } y)
                if (!string.Equals(x.Value, y.Value, StringComparison.Ordinal))
                    changes.Add(new VmfChange(VmfChangeKind.ValueChanged, Join(path, x.Key), x.Value, y.Value));
        }

        /// <summary>
        /// Whether any key appears twice among a block's <paramref name="count"/> pairs, compared as
        /// grouping compares them. A block with many pairs is taken to have one, which costs only the
        /// fast path.
        /// </summary>
        private static bool HasRepeatedKey(List<VmfItem> items, int count)
        {
            if (count > 64)
                return true;
            int i = 0;
            while (NextPair(items, ref i) is { } x)
            {
                int j = i;
                while (NextPair(items, ref j) is { } y)
                    if (string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        private static void CompareKeysGrouped(VmfNode a, VmfNode b, string path, List<VmfChange> changes)
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

        /// <summary>The next key-value pair at or after <paramref name="at"/>, moving past it; null at the end.</summary>
        private static VmfKeyValue? NextPair(List<VmfItem> items, ref int at)
        {
            while (at < items.Count)
                if (items[at++] is VmfKeyValue pair)
                    return pair;
            return null;
        }

        /// <summary>The next child block at or after <paramref name="at"/>, moving past it; null at the end.</summary>
        private static VmfNode? NextNode(List<VmfItem> items, ref int at)
        {
            while (at < items.Count)
                if (items[at++] is VmfNode node)
                    return node;
            return null;
        }

        private static string Join(string path, string part) => path.Length == 0 ? part : path + "/" + part;
    }
}
