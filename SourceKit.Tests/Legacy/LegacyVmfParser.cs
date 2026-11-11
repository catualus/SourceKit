using System;
using System.Collections.Generic;
using System.IO;
using SourceKit.Vmf;

namespace SourceKit.Tests.Legacy
{
    // VmfDocument's parser as it was at 8c8f289, before it trimmed on spans: copied verbatim apart from
    // returning the tree and line ending rather than a document, whose constructor is private. The
    // equivalence tests hold the new parser to exactly the tree this one builds.
    internal static class LegacyVmfParser
    {
        public static (VmfNode Root, string NewLine) Parse(string text)
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

            return (root, crlf >= lf ? "\r\n" : "\n");
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
}
