using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SourceKit.Vmf
{
    /// <summary>
    /// A VMF as a tree of blocks and key-value pairs that writes back exactly as it was read.
    ///
    /// Every line keeps its original text, terminator included, and is written out unchanged unless
    /// something edits it. An unedited document therefore saves byte for byte, whatever editor wrote
    /// it - Hammer, Hammer++, a decompiler or a hand edit - and a tool that changes three keys produces
    /// a file whose diff is three lines. Parsing to values and printing them back would instead
    /// renormalise every float and every piece of whitespace in a 30 MB map.
    /// </summary>
    public sealed class VmfDocument
    {
        /// <summary>The top level: versioninfo, visgroups, world, every entity, cameras, cordons.</summary>
        public VmfNode Root { get; }

        /// <summary>The line ending most of the file uses, given to lines that are added.</summary>
        public string NewLine { get; }

        /// <summary>Whether the file started with a UTF-8 byte order mark, which saving keeps.</summary>
        public bool HasByteOrderMark { get; }

        private VmfDocument(VmfNode root, string newLine, bool bom)
        {
            Root = root;
            NewLine = newLine;
            HasByteOrderMark = bom;
        }

        public static VmfDocument Load(string path) => Parse(File.ReadAllBytes(path));

        public static VmfDocument Parse(byte[] bytes)
        {
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            // Latin-1 maps every byte to one char and back, so text the map holds in any other
            // encoding - a Windows-1252 entity name, say - survives a round trip untouched.
            string text = Encoding.Latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            return Parse(text, bom);
        }

        public static VmfDocument Parse(string text, bool byteOrderMark = false)
        {
            var root = new VmfNode("", null, headerLine: null, openLine: null);
            var stack = new Stack<VmfNode>();
            stack.Push(root);

            int crlf = 0, lf = 0;
            string? pendingName = null;
            string? pendingNameLine = null;
            int lineNumber = 0;

            foreach (string line in SplitKeepingTerminators(text))
            {
                lineNumber++;
                if (line.EndsWith("\r\n", StringComparison.Ordinal)) crlf++;
                else if (line.EndsWith('\n')) lf++;

                string content = line.TrimEnd('\r', '\n');
                string trimmed = content.Trim();
                var current = stack.Peek();

                if (pendingName != null)
                {
                    if (trimmed != "{")
                        throw new InvalidDataException($"Line {lineNumber}: expected '{{' after '{pendingName}', found '{trimmed}'.");
                    var node = new VmfNode(pendingName, current, pendingNameLine, line);
                    current.AddParsed(node);
                    stack.Push(node);
                    pendingName = null;
                    pendingNameLine = null;
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    current.AddParsed(new VmfTrivia(line));
                }
                else if (trimmed == "}")
                {
                    if (stack.Count == 1)
                        throw new InvalidDataException($"Line {lineNumber}: '}}' with no block open.");
                    current.SetCloseLine(line);
                    stack.Pop();
                }
                else if (trimmed[0] == '"')
                {
                    if (!TryParseKeyValue(trimmed, out string key, out string value))
                        throw new InvalidDataException($"Line {lineNumber}: cannot read a key-value pair from '{trimmed}'.");
                    current.AddParsed(new VmfKeyValue(key, value, line));
                }
                else if (trimmed.EndsWith('{') && trimmed.Length > 1)
                {
                    // "solid {" on one line. Hammer never writes this, but other tools do.
                    string name = trimmed[..^1].Trim();
                    var node = new VmfNode(name, current, line, openLine: "");
                    current.AddParsed(node);
                    stack.Push(node);
                }
                else
                {
                    pendingName = trimmed;
                    pendingNameLine = line;
                }
            }

            if (pendingName != null)
                throw new InvalidDataException($"The file ends after '{pendingName}' with no block following it.");
            if (stack.Count != 1)
                throw new InvalidDataException($"The file ends inside '{stack.Peek().Name}': a closing brace is missing.");

            return new VmfDocument(root, crlf >= lf ? "\r\n" : "\n", byteOrderMark);
        }

        /// <summary>The single world block.</summary>
        public VmfNode World => Root.Child("world") ?? throw new InvalidDataException("The map has no world block.");

        /// <summary>Every top-level entity, in file order.</summary>
        public IEnumerable<VmfNode> Entities => Root.Children("entity");

        /// <summary>The largest "id" anywhere in the map, so new blocks can be given ids that collide with nothing.</summary>
        public long MaxId()
        {
            long max = 0;
            foreach (var node in Root.Descendants())
                if (long.TryParse(node.Get("id"), out long id) && id > max)
                    max = id;
            return max;
        }

        public void Save(string path)
        {
            string temp = path + ".tmp";
            using (var stream = File.Create(temp))
                Write(stream);
            File.Move(temp, path, overwrite: true);
        }

        public void Write(Stream stream)
        {
            if (HasByteOrderMark)
                stream.Write([0xEF, 0xBB, 0xBF]);
            var bytes = Encoding.Latin1.GetBytes(ToString());
            stream.Write(bytes);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            Root.WriteChildren(builder, this, depth: 0);
            return builder.ToString();
        }

        internal static bool TryParseKeyValue(string trimmed, out string key, out string value)
        {
            key = value = "";
            int keyEnd = trimmed.IndexOf('"', 1);
            if (keyEnd < 0)
                return false;
            key = trimmed[1..keyEnd];

            int valueStart = trimmed.IndexOf('"', keyEnd + 1);
            if (valueStart < 0)
                return false;
            // The value runs to the last quote on the line: Hammer does not escape quotes, so a value
            // that contains one would otherwise be cut short.
            int valueEnd = trimmed.LastIndexOf('"');
            if (valueEnd <= valueStart)
                return false;
            value = trimmed[(valueStart + 1)..valueEnd];
            return true;
        }

        private static IEnumerable<string> SplitKeepingTerminators(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    yield return text[start..(i + 1)];
                    start = i + 1;
                }
            }
            if (start < text.Length)
                yield return text[start..];
        }
    }

    /// <summary>Something inside a block: a key-value pair, a child block, or a blank line.</summary>
    public abstract class VmfItem
    {
        public VmfNode? Parent { get; internal set; }
    }

    /// <summary>A blank line, kept so the file writes back as it was.</summary>
    public sealed class VmfTrivia(string raw) : VmfItem
    {
        internal string Raw { get; } = raw;
    }

    public sealed class VmfKeyValue : VmfItem
    {
        private string value;

        /// <summary>The line as read, or null once the value has been changed or for a pair that was added.</summary>
        internal string? Raw { get; private set; }

        public string Key { get; }

        public string Value
        {
            get => value;
            set
            {
                if (value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                    throw new ArgumentException("A VMF value cannot contain a quote or a line break.", nameof(value));
                if (value == this.value)
                    return;
                this.value = value;
                Raw = null;
            }
        }

        internal VmfKeyValue(string key, string value, string? raw)
        {
            Key = key;
            this.value = value;
            Raw = raw;
        }

        public VmfKeyValue(string key, string value) : this(key, "", null)
        {
            if (key.Contains('"'))
                throw new ArgumentException("A VMF key cannot contain a quote.", nameof(key));
            Value = value;
            Raw = null;
        }

        public bool IsModified => Raw == null;

        public override string ToString() => $"\"{Key}\" \"{Value}\"";
    }

    /// <summary>A named block: world, entity, solid, side, editor, connections, dispinfo...</summary>
    public sealed class VmfNode : VmfItem
    {
        private readonly List<VmfItem> items = [];
        private string? headerLine;
        private string? openLine;
        private string? closeLine;

        public string Name { get; }

        public IReadOnlyList<VmfItem> Items => items;

        internal VmfNode(string name, VmfNode? parent, string? headerLine, string? openLine)
        {
            Name = name;
            Parent = parent;
            this.headerLine = headerLine;
            this.openLine = openLine;
        }

        /// <summary>A new, empty block, to be added with <see cref="Add"/>.</summary>
        public VmfNode(string name) : this(name, null, null, null)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('"') || name.Contains('{') || name.Contains('}'))
                throw new ArgumentException($"'{name}' is not a block name.", nameof(name));
        }

        public bool IsAdded => headerLine == null && Parent != null;

        public IEnumerable<VmfKeyValue> KeyValues => items.OfType<VmfKeyValue>();

        public IEnumerable<VmfNode> Nodes => items.OfType<VmfNode>();

        public IEnumerable<VmfNode> Children(string name) =>
            Nodes.Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

        public VmfNode? Child(string name) => Children(name).FirstOrDefault();

        /// <summary>The first value for <paramref name="key"/> in this block (not its children), or null.</summary>
        public string? Get(string key) =>
            KeyValues.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        public VmfKeyValue? GetPair(string key) =>
            KeyValues.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Changes the first value for <paramref name="key"/>, or adds the pair after the last one.</summary>
        public void Set(string key, string value)
        {
            var pair = GetPair(key);
            if (pair != null)
            {
                pair.Value = value;
                return;
            }

            var added = new VmfKeyValue(key, value) { Parent = this };
            // After the last pair and before the first child block, where Hammer writes keys.
            int index = items.FindLastIndex(i => i is VmfKeyValue) + 1;
            items.Insert(index, added);
        }

        /// <summary>Adds a block at the end of this one.</summary>
        public void Add(VmfNode node)
        {
            if (node.Parent != null)
                throw new InvalidOperationException($"'{node.Name}' already belongs to a block.");
            node.Parent = this;
            items.Add(node);
        }

        public bool Remove(VmfItem item)
        {
            if (!items.Remove(item))
                return false;
            item.Parent = null;
            return true;
        }

        public IEnumerable<VmfNode> Descendants()
        {
            foreach (var node in Nodes)
            {
                yield return node;
                foreach (var inner in node.Descendants())
                    yield return inner;
            }
        }

        public string? Classname => Get("classname");

        public long? Id => long.TryParse(Get("id"), out long id) ? id : null;

        internal void AddParsed(VmfItem item)
        {
            item.Parent = this;
            items.Add(item);
        }

        internal void SetCloseLine(string line) => closeLine = line;

        internal void WriteChildren(StringBuilder builder, VmfDocument document, int depth)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case VmfTrivia trivia:
                        builder.Append(trivia.Raw);
                        break;
                    case VmfKeyValue pair:
                        if (pair.Raw != null)
                            builder.Append(pair.Raw);
                        else
                            builder.Append(IndentFor(depth)).Append('"').Append(pair.Key).Append("\" \"")
                                .Append(pair.Value).Append('"').Append(document.NewLine);
                        break;
                    case VmfNode node:
                        node.Write(builder, document, depth);
                        break;
                }
            }
        }

        private void Write(StringBuilder builder, VmfDocument document, int depth)
        {
            string indent = IndentFor(depth);
            builder.Append(headerLine ?? indent + Name + document.NewLine);
            builder.Append(openLine ?? indent + "{" + document.NewLine);
            WriteChildren(builder, document, depth + 1);
            builder.Append(closeLine ?? indent + "}" + document.NewLine);
        }

        /// <summary>
        /// The indentation for an added line at <paramref name="depth"/>: copied from a line already at
        /// that depth in this block when there is one, so additions match the file around them.
        /// </summary>
        private string IndentFor(int depth)
        {
            foreach (var pair in KeyValues)
                if (pair.Raw != null)
                    return pair.Raw[..(pair.Raw.Length - pair.Raw.TrimStart().Length)];
            return new string('\t', depth);
        }

        public override string ToString() => Id is { } id ? $"{Name} {id}" : Name;
    }
}
