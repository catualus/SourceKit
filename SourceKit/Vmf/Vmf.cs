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
            // encoding - a Windows-1252 entity name, say - survives a round trip untouched. Decoded a
            // line at a time: the same text, without first holding the whole map as one string.
            return Parse(SplitKeepingTerminators(bytes, bom ? 3 : 0), bom);
        }

        public static VmfDocument Parse(string text, bool byteOrderMark = false) =>
            Parse(SplitKeepingTerminators(text), byteOrderMark);

        /// <summary>
        /// The parser proper. Each line is kept as it was read, and only that line, its key and its value
        /// are allocated: the trimming that decides what a line is happens on spans, and block names and
        /// keys - a few dozen distinct strings repeated a million times in a large map - are shared.
        /// </summary>
        private static VmfDocument Parse(IEnumerable<string> lines, bool byteOrderMark)
        {
            var root = new VmfNode("", null, headerLine: null, openLine: null);
            var stack = new Stack<VmfNode>();
            stack.Push(root);

            var names = new HashSet<string>(StringComparer.Ordinal);
            var interned = names.GetAlternateLookup<ReadOnlySpan<char>>();
            string Intern(ReadOnlySpan<char> text)
            {
                if (!interned.TryGetValue(text, out string? existing))
                {
                    existing = text.ToString();
                    names.Add(existing);
                }
                return existing;
            }

            int crlf = 0, lf = 0;
            string? pendingName = null;
            string? pendingNameLine = null;
            int lineNumber = 0;

            foreach (string line in lines)
            {
                lineNumber++;
                if (line.Length >= 2 && line[^1] == '\n' && line[^2] == '\r') crlf++;
                else if (line.Length >= 1 && line[^1] == '\n') lf++;

                // Line breaks are white space, so this is the line without its terminator, trimmed.
                ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                var current = stack.Peek();

                if (pendingName != null)
                {
                    if (trimmed is not "{")
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
                else if (trimmed is "}")
                {
                    if (stack.Count == 1)
                        throw new InvalidDataException($"Line {lineNumber}: '}}' with no block open.");
                    current.SetCloseLine(line);
                    stack.Pop();
                }
                else if (trimmed[0] == '"')
                {
                    if (!TryParseKeyValue(trimmed, out var key, out var value))
                        throw new InvalidDataException($"Line {lineNumber}: cannot read a key-value pair from '{trimmed}'.");
                    current.AddParsed(new VmfKeyValue(Intern(trimmed[key]), trimmed[value].ToString(), line));
                }
                else if (trimmed.EndsWith('{') && trimmed.Length > 1)
                {
                    // "solid {" on one line. Hammer never writes this, but other tools do.
                    string name = Intern(trimmed[..^1].Trim());
                    var node = new VmfNode(name, current, line, openLine: "");
                    current.AddParsed(node);
                    stack.Push(node);
                }
                else
                {
                    pendingName = Intern(trimmed);
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

        /// <summary>
        /// The largest "id" anywhere in the map, so new blocks can be given ids that collide with nothing.
        /// This walks every block each time; to number many new blocks, use <see cref="NextIds"/>.
        /// </summary>
        public long MaxId()
        {
            long max = 0;
            foreach (var node in Root.Descendants())
                if (long.TryParse(node.Get("id"), out long id) && id > max)
                    max = id;
            return max;
        }

        /// <summary>
        /// Reserves <paramref name="count"/> consecutive ids and returns the first: the range is
        /// <c>first</c> to <c>first + count - 1</c>. Each is above every id in the map and every id an
        /// earlier call reserved, so a brush and its six sides cost one call, not a walk of the whole map
        /// per brush as <see cref="MaxId"/> does.
        ///
        /// The map is walked once, on the first call. After that the document keeps count, and still
        /// notices ids it did not hand out: a block added with ids already in it, or an "id" set on a
        /// block in the map, moves the next range above them. An id given up - its block removed, or
        /// reserved and never used - is not handed out again.
        ///
        /// Not safe to call from two threads at once, like any other edit to a document.
        /// </summary>
        public long NextIds(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            return Root.ReserveIds(count, MaxId);
        }

        /// <summary>
        /// A deep copy, made without writing the map out and reading it back. The copy writes out exactly
        /// as this document does, edits to either leave the other alone, and ids this document reserved
        /// with <see cref="NextIds"/> stay reserved in the copy. Unchanged lines are shared rather than
        /// copied: they are immutable strings, and most of a map's memory.
        ///
        /// Unlike a copy made by parsing <see cref="ToString"/>, an edited pair or added block is still
        /// marked as one in the copy (<see cref="VmfKeyValue.IsModified"/>, <see cref="VmfNode.IsAdded"/>).
        /// </summary>
        public VmfDocument Clone() => new(Root.CloneTree(null), NewLine, HasByteOrderMark);

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

        /// <summary>
        /// Where the key and the value are in a trimmed line that starts with a quote, found without
        /// allocating either.
        /// </summary>
        internal static bool TryParseKeyValue(ReadOnlySpan<char> trimmed, out Range key, out Range value)
        {
            key = value = default;
            int keyEnd = trimmed[1..].IndexOf('"') + 1;
            if (keyEnd < 1)
                return false;
            key = 1..keyEnd;

            int valueStart = trimmed[(keyEnd + 1)..].IndexOf('"');
            if (valueStart < 0)
                return false;
            valueStart += keyEnd + 1;
            // The value runs to the last quote on the line: Hammer does not escape quotes, so a value
            // that contains one would otherwise be cut short.
            int valueEnd = trimmed.LastIndexOf('"');
            if (valueEnd <= valueStart)
                return false;
            value = (valueStart + 1)..valueEnd;
            return true;
        }

        private static IEnumerable<string> SplitKeepingTerminators(string text)
        {
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                int next = end < 0 ? text.Length : end + 1;
                yield return text[start..next];
                start = next;
            }
        }

        /// <summary>The lines <see cref="SplitKeepingTerminators(string)"/> gives, decoded from Latin-1 one at a time.</summary>
        private static IEnumerable<string> SplitKeepingTerminators(byte[] bytes, int start)
        {
            while (start < bytes.Length)
            {
                int end = Array.IndexOf(bytes, (byte)'\n', start);
                int next = end < 0 ? bytes.Length : end + 1;
                yield return Encoding.Latin1.GetString(bytes, start, next - start);
                start = next;
            }
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
                if (Parent != null && VmfNode.IsIdKey(Key))
                    Parent.NoteId(value);
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

        internal VmfKeyValue CloneFor(VmfNode parent) => new(Key, value, Raw) { Parent = parent };

        public override string ToString() => $"\"{Key}\" \"{Value}\"";
    }

    /// <summary>A named block: world, entity, solid, side, editor, connections, dispinfo...</summary>
    public sealed class VmfNode : VmfItem
    {
        private readonly List<VmfItem> items = [];
        private string? headerLine;
        private string? openLine;
        private string? closeLine;

        /// <summary>
        /// On a document's root, once <see cref="VmfDocument.NextIds"/> has run: every id at or below this
        /// is in the map or has been handed out. Null until then, and on every other block.
        /// </summary>
        private long? idCeiling;

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
            if (IsIdKey(key))
                NoteId(value);
        }

        /// <summary>
        /// Adds a pair after the last one even when <paramref name="key"/> is already there: Hammer repeats
        /// some keys, such as <c>visgroupid</c> for an object in more than one visgroup.
        /// </summary>
        public void Append(string key, string value)
        {
            var added = new VmfKeyValue(key, value) { Parent = this };
            items.Insert(items.FindLastIndex(i => i is VmfKeyValue) + 1, added);
            if (IsIdKey(key))
                NoteId(value);
        }

        /// <summary>Adds a block just before <paramref name="before"/>, one of this block's own.</summary>
        public void InsertBefore(VmfNode before, VmfNode node)
        {
            if (node.Parent != null)
                throw new InvalidOperationException($"'{node.Name}' already belongs to a block.");
            int index = items.IndexOf(before);
            if (index < 0)
                throw new ArgumentException($"'{before.Name}' is not in this block.", nameof(before));
            node.Parent = this;
            items.Insert(index, node);
            NoteIdsOf(node);
        }

        /// <summary>Adds a block at the end of this one.</summary>
        public void Add(VmfNode node)
        {
            if (node.Parent != null)
                throw new InvalidOperationException($"'{node.Name}' already belongs to a block.");
            node.Parent = this;
            items.Add(node);
            NoteIdsOf(node);
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

        internal static bool IsIdKey(string key) => string.Equals(key, "id", StringComparison.OrdinalIgnoreCase);

        /// <summary>The top of the tree this block is in: a document's root, or the top of a detached block.</summary>
        private VmfNode Top()
        {
            var top = this;
            while (top.Parent != null)
                top = top.Parent;
            return top;
        }

        /// <summary>Reserves <paramref name="count"/> ids on this root, finding the largest with <paramref name="scan"/> the first time.</summary>
        internal long ReserveIds(int count, Func<long> scan)
        {
            long ceiling = idCeiling ??= scan();
            idCeiling = checked(ceiling + count);
            return ceiling + 1;
        }

        /// <summary>An id has been written into this block: the tree's next reserved range must start above it.</summary>
        internal void NoteId(string value)
        {
            var top = Top();
            if (top.idCeiling is { } ceiling && long.TryParse(value, out long id) && id > ceiling)
                top.idCeiling = id;
        }

        /// <summary><paramref name="node"/> has joined this tree, carrying whatever ids it already had.</summary>
        private void NoteIdsOf(VmfNode node)
        {
            var top = Top();
            if (top.idCeiling is not { } ceiling)
                return;
            long max = ceiling;
            if (long.TryParse(node.Get("id"), out long own) && own > max)
                max = own;
            foreach (var inner in node.Descendants())
                if (long.TryParse(inner.Get("id"), out long id) && id > max)
                    max = id;
            top.idCeiling = max;
        }

        /// <summary>A copy of this block and everything in it, sharing the immutable line strings.</summary>
        internal VmfNode CloneTree(VmfNode? parent)
        {
            var copy = new VmfNode(Name, parent, headerLine, openLine) { closeLine = closeLine, idCeiling = idCeiling };
            copy.items.Capacity = items.Count;
            foreach (var item in items)
            {
                copy.items.Add(item switch
                {
                    VmfNode node => node.CloneTree(copy),
                    VmfKeyValue pair => pair.CloneFor(copy),
                    VmfTrivia trivia => new VmfTrivia(trivia.Raw) { Parent = copy },
                    _ => throw new InvalidOperationException($"Unknown VMF item {item.GetType().Name}."),
                });
            }
            return copy;
        }

        /// <summary>The list itself, for readers in this assembly that walk every block and must not allocate to do it.</summary>
        internal List<VmfItem> ItemList => items;

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
