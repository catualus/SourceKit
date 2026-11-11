using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Tests.Legacy;
using SourceKit.Vmf;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// The span-trimming parser and the in-order diff, held to exactly what the versions before them
    /// produced (copied into <see cref="Legacy"/>): the same tree from the same text, and the same
    /// changes - paths, order and the pairing of repeated ids - from the same two maps.
    /// </summary>
    public class VmfEquivalenceTests
    {
        public static TheoryData<string> RealMaps => new()
        {
            @"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_meowy.vmf",
            @"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_tits_bzgg.vmf",
        };

        // ---- Parsing ----

        private static void AssertSameTree(VmfNode expected, VmfNode actual, string path = "")
        {
            Assert.Equal(expected.Name, actual.Name);
            Assert.True(expected.Items.Count == actual.Items.Count, $"{path}/{expected.Name}: {expected.Items.Count} items, now {actual.Items.Count}");
            for (int i = 0; i < expected.Items.Count; i++)
            {
                switch (expected.Items[i])
                {
                    case VmfTrivia trivia:
                        Assert.Equal(trivia.Raw, Assert.IsType<VmfTrivia>(actual.Items[i]).Raw);
                        break;
                    case VmfKeyValue pair:
                        var other = Assert.IsType<VmfKeyValue>(actual.Items[i]);
                        Assert.Equal((pair.Key, pair.Value, pair.Raw), (other.Key, other.Value, other.Raw));
                        break;
                    case VmfNode node:
                        AssertSameTree(node, Assert.IsType<VmfNode>(actual.Items[i]), $"{path}/{expected.Name}");
                        break;
                }
            }
        }

        /// <summary>Parses <paramref name="text"/> both ways: the same tree and line ending, or the same refusal.</summary>
        private static void AssertParsesAsBefore(string text)
        {
            (VmfNode Root, string NewLine) legacy;
            try
            {
                legacy = LegacyVmfParser.Parse(text);
            }
            catch (InvalidDataException expected)
            {
                var error = Assert.Throws<InvalidDataException>(() => VmfDocument.Parse(text));
                Assert.Equal(expected.Message, error.Message);
                Assert.Throws<InvalidDataException>(() => VmfDocument.Parse(Encoding.Latin1.GetBytes(text)));
                return;
            }

            var document = VmfDocument.Parse(text);
            AssertSameTree(legacy.Root, document.Root);
            Assert.Equal(legacy.NewLine, document.NewLine);
            Assert.Equal(text, document.ToString());

            // From bytes, decoded a line at a time, the same again.
            var fromBytes = VmfDocument.Parse(Encoding.Latin1.GetBytes(text));
            AssertSameTree(legacy.Root, fromBytes.Root);
            Assert.Equal(text, fromBytes.ToString());
        }

        public static TheoryData<string> AwkwardText => new()
        {
            VmfTests.CubeText,
            VmfTests.CubeText.Replace("\r\n", "\n"),
            "world\n{\n  \"id\"   \"1\"\n\n  solid {\n    \"id\" \"2\"\n  }\n}",
            // Mixed endings, a stray carriage return, and white space .NET trims that is not ASCII:
            // a no-break space (Latin-1 0xA0) and a next-line (0x85).
            "world\r\n{\n\t\"id\" \"1\"\r\r\n\u00a0\"skyname\" \"x\"\u0085\r\n \t \r\n}\r\n",
            "world{\n\"a\" \"b\"c\"\n\"\" \"\"\n\"key with spaces\"\t\t\"value with spaces\"\n}\n",
            "\u00a0world\u00a0\r\n\u00a0{\u00a0\r\n}",
            "entity\n{\n\tconnections\n\t{\n\t\t\"OnTrigger\" \"a,b,,0,-1\"\n\t\t\"OnTrigger\" \"c,d,,0,-1\"\n\t}\n}\n",
            "",
            "\r\n\r\n",
            // Refused, with the same message.
            "world\r\n{\r\n",
            "world\r\n}\r\n",
            "}\r\n",
            "world\r\n\"id\" \"1\"\r\n",
            "world\r\n{\r\n\t\"id\r\n}\r\n",
            "world\r\n{\r\n\t\"\r\n}\r\n",
            "world\r\n{\r\n\t\"id\" \"\r\n}\r\n",
            "world\r\n  {  x\r\n}\r\n",
            "world",
        };

        [Theory]
        [MemberData(nameof(AwkwardText))]
        public void AwkwardTextParsesAsBefore(string text) => AssertParsesAsBefore(text);

        /// <summary>
        /// Random lines in random white space: block names, braces, pairs good and bad, blank lines, every
        /// line ending. Most documents are well-formed; the rest must fail at the same line, the same way.
        /// </summary>
        [Fact]
        public void RandomTextParsesAsBefore()
        {
            string[] space = ["", "", " ", "\t", "\t\t", "  \t", "\u00a0", "\u0085", "\u000b"];
            string[] ends = ["\r\n", "\r\n", "\n", "\n", "\r\r\n", "\n\r\n"];
            var rng = new Random(1234);
            for (int round = 0; round < 600; round++)
            {
                var text = new StringBuilder();
                int depth = 0;
                bool sloppy = round % 4 == 0;
                int lines = rng.Next(1, 60);
                for (int i = 0; i < lines; i++)
                {
                    string line;
                    int pick = rng.Next(sloppy ? 12 : 9);
                    if (pick < 2 && depth < 5)
                    {
                        string name = new[] { "world", "solid", "side", "Editor", "dispinfo", "x y" }[rng.Next(6)];
                        line = rng.Next(3) == 0
                            ? Pad(name + Pad("{"))
                            : Pad(name) + End() + Pad("{");
                        depth++;
                    }
                    else if (pick < 4 && depth > 0)
                    {
                        line = Pad("}");
                        depth--;
                    }
                    else if (pick < 8 && depth > 0)
                    {
                        string key = new[] { "id", "ID", "plane", "material", "a b", "", "q\"" }[rng.Next(7)];
                        string value = new[] { "1", "05", "(0 0 0) (1 0 0) (1 1 0)", "", "a\"b", " spaced " }[rng.Next(6)];
                        line = Pad($"\"{key}\"{space[rng.Next(space.Length)]} \"{value}\"");
                    }
                    else if (pick < 9)
                        line = space[rng.Next(space.Length)];
                    else
                        line = Pad(new[] { "\"unclosed", "{", "}", "solid", "\"k\" \"v", "// comment" }[rng.Next(6)]);
                    text.Append(line).Append(End());
                }
                if (!sloppy)
                    for (; depth > 0; depth--)
                        text.Append(Pad("}")).Append(End());
                if (rng.Next(5) == 0 && text.Length > 0)
                    text.Length -= 1;

                AssertParsesAsBefore(text.ToString());
            }

            string Pad(string s) => space[rng.Next(space.Length)] + s + space[rng.Next(space.Length)];
            string End() => ends[rng.Next(ends.Length)];
        }

        [Theory]
        [MemberData(nameof(RealMaps))]
        public void ARealMapParsesAsBefore(string path)
        {
            if (!File.Exists(path))
                return;

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var legacy = LegacyVmfParser.Parse(Encoding.Latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)));
            var document = VmfDocument.Parse(bytes);

            AssertSameTree(legacy.Root, document.Root);
            Assert.Equal(legacy.NewLine, document.NewLine);

            using var stream = new MemoryStream();
            document.Write(stream);
            Assert.True(bytes.AsSpan().SequenceEqual(stream.ToArray()), "the map did not write back byte for byte");
        }

        // ---- Diffing ----

        /// <summary>
        /// A map built to trip the diff: ids repeated within a block and across blocks, ids written two
        /// ways ("5" and "05"), blocks without ids, block names differing only in case, keys repeated and
        /// keys differing only in case.
        /// </summary>
        private static string AwkwardMap(Random rng)
        {
            var text = new StringBuilder("versioninfo\n{\n\t\"editorversion\" \"400\"\n}\nvisgroups\n{\n}\n");
            text.Append("world\n{\n\t\"id\" \"1\"\n\t\"classname\" \"worldspawn\"\n");
            int solids = rng.Next(3, 14);
            for (int s = 0; s < solids; s++)
            {
                string name = rng.Next(8) == 0 ? "Solid" : "solid";
                text.Append($"\t{name}\n\t{{\n");
                if (rng.Next(6) != 0)
                    text.Append($"\t\t\"id\" \"{Id(rng)}\"\n");
                for (int side = rng.Next(0, 4); side > 0; side--)
                    text.Append($"\t\tside\n\t\t{{\n\t\t\t\"id\" \"{Id(rng)}\"\n\t\t\t\"material\" \"{Value(rng)}\"\n\t\t}}\n");
                if (rng.Next(3) == 0)
                    text.Append($"\t\teditor\n\t\t{{\n\t\t\t\"color\" \"{Value(rng)}\"\n\t\t\t\"visgroupid\" \"{rng.Next(3)}\"\n\t\t\t\"visgroupid\" \"{rng.Next(3)}\"\n\t\t}}\n");
                text.Append("\t}\n");
            }
            text.Append("}\n");
            for (int e = rng.Next(1, 6); e > 0; e--)
            {
                text.Append($"entity\n{{\n\t\"id\" \"{Id(rng)}\"\n\t\"classname\" \"{Value(rng)}\"\n");
                if (rng.Next(3) == 0)
                    text.Append($"\t\"Targetname\" \"{Value(rng)}\"\n\t\"targetname\" \"{Value(rng)}\"\n");
                if (rng.Next(2) == 0)
                    text.Append($"\tconnections\n\t{{\n\t\t\"OnTrigger\" \"{Value(rng)}\"\n\t\t\"OnTrigger\" \"{Value(rng)}\"\n\t\t\"OnUser1\" \"{Value(rng)}\"\n\t}}\n");
                text.Append("}\n");
            }
            return text.ToString();
        }

        private static string Id(Random rng) => rng.Next(10) switch { 0 => "05", 1 => "x", _ => rng.Next(1, 9).ToString() };

        private static string Value(Random rng) => new[] { "a", "b", "c", "TOOLS/TOOLSNODRAW", "", "0 0 0" }[rng.Next(6)];

        /// <summary>One random edit, through the public API, the kind a pass makes or worse.</summary>
        private static void Mutate(VmfDocument map, Random rng, IReadOnlyList<VmfNode>? nodes = null)
        {
            nodes ??= map.Root.Descendants().ToList();
            if (nodes.Count == 0)
                return;
            var node = nodes[rng.Next(nodes.Count)];
            var pairs = node.KeyValues.ToList();
            switch (rng.Next(9))
            {
                case 0 when pairs.Count > 0:
                    pairs[rng.Next(pairs.Count)].Value = Value(rng);
                    break;
                case 1:
                    node.Set(new[] { "material", "note", "id", "Classname" }[rng.Next(4)], Value(rng));
                    break;
                case 2:
                    node.Append(pairs.Count > 0 && rng.Next(2) == 0 ? pairs[rng.Next(pairs.Count)].Key : "visgroupid", Value(rng));
                    break;
                case 3 when pairs.Count > 0:
                    node.Remove(pairs[rng.Next(pairs.Count)]);
                    break;
                case 4 when node.Parent != null:
                    node.Parent.Remove(node);
                    break;
                case 5:
                {
                    var added = new VmfNode(new[] { "solid", "side", "editor", "Solid", "group" }[rng.Next(5)]);
                    if (rng.Next(4) != 0)
                        added.Set("id", Id(rng));
                    var parent = rng.Next(2) == 0 ? node : node.Parent ?? map.Root;
                    if (rng.Next(2) == 0 && parent.Nodes.FirstOrDefault() is { } first)
                        parent.InsertBefore(first, added);
                    else
                        parent.Add(added);
                    break;
                }
                case 6:
                    node.Set("id", Id(rng));
                    break;
                case 7 when node.Parent is { } parent:
                    // Moved to the end of its block: the same blocks, in another order.
                    parent.Remove(node);
                    parent.Add(node);
                    break;
                default:
                    if (pairs.Count > 0)
                        pairs[rng.Next(pairs.Count)].Value = pairs[rng.Next(pairs.Count)].Value;
                    break;
            }
        }

        private static void AssertDiffsAsBefore(VmfDocument before, VmfDocument after)
        {
            var expected = LegacyVmfDiff.Compare(before, after);
            var actual = VmfDiff.Compare(before, after);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void RandomEditsToAwkwardMapsDiffAsBefore()
        {
            var rng = new Random(4321);
            int withChanges = 0;
            for (int round = 0; round < 800; round++)
            {
                var before = VmfDocument.Parse(AwkwardMap(rng));
                var after = round % 2 == 0 ? before.Clone() : VmfDocument.Parse(before.ToString());
                for (int edits = rng.Next(0, 6); edits > 0; edits--)
                    Mutate(after, rng);
                // Now and then the other side has moved too.
                if (round % 5 == 0)
                    Mutate(before, rng);

                AssertDiffsAsBefore(before, after);
                AssertDiffsAsBefore(after, before);
                if (VmfDiff.Compare(before, after).Count > 0)
                    withChanges++;
            }
            // The edits did make differences to find, most of the time.
            Assert.True(withChanges > 400, $"only {withChanges} of 800 rounds had a difference");
        }

        /// <summary>
        /// Two blocks with the same id, one removed: the pairing of the repeats that are left is the
        /// pairing the grouped diff gave, which is what keeps an untouched brush from reading as replaced.
        /// </summary>
        [Fact]
        public void RemovingOneOfARepeatedIdDiffsAsBefore()
        {
            const string map = "world\n{\n\t\"id\" \"1\"\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"first\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"second\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"05\"\n\t\t\"note\" \"third\"\n\t}\n" +
                "}\n";
            for (int removed = 0; removed < 3; removed++)
            {
                var before = VmfDocument.Parse(map);
                var after = before.Clone();
                after.World.Remove(after.World.Children("solid").ElementAt(removed));
                AssertDiffsAsBefore(before, after);
                AssertDiffsAsBefore(after, before);
            }
        }

        [Theory]
        [MemberData(nameof(RealMaps))]
        public void RandomEditsToARealMapDiffAsBefore(string path)
        {
            if (!File.Exists(path))
                return;

            var before = VmfDocument.Load(path);
            AssertDiffsAsBefore(before, VmfDocument.Load(path));

            var rng = new Random(99);
            for (int round = 0; round < 3; round++)
            {
                var after = before.Clone();
                var nodes = after.Root.Descendants().ToList();
                for (int edits = 0; edits < 300; edits++)
                    Mutate(after, rng, nodes);
                AssertDiffsAsBefore(before, after);
            }
        }
    }
}
