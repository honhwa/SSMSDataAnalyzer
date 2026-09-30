using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// The §5 mapping file: <c>database[@server] = path to the .sqlproj</c>, one per line,
    /// hand-editable. '#' starts a whole-line comment; blank lines are ignored; a line is split
    /// on the first '=' and then the key on the last '@'; keys compare OrdinalIgnoreCase. Lines
    /// this class does not touch are kept byte-for-byte by <see cref="Serialize"/>, in their
    /// original order -- only the line a <see cref="With"/> call actually changes is rewritten.
    /// Never throws: anything it cannot parse becomes a <see cref="Problems"/> entry and an
    /// inert line, not an exception.
    /// </summary>
    public sealed class SourceControlMap
    {
        private readonly List<Line> _lines;
        private readonly List<string> _problems;

        private SourceControlMap(List<Line> lines, List<string> problems)
        {
            _lines = lines;
            _problems = problems;
        }

        /// <summary>"line 4: no '='", one per line that could not be parsed as a mapping.</summary>
        public IReadOnlyList<string> Problems => _problems;

        public static SourceControlMap Parse(string text)
        {
            var lines = new List<Line>();
            var problems = new List<string>();

            if (text == null) return new SourceControlMap(lines, problems);

            string[] rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int idx = 0; idx < rawLines.Length; idx++)
            {
                string raw = rawLines[idx];
                int lineNo = idx + 1;
                string trimmed = raw.Trim();

                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    lines.Add(Line.Inert(raw));
                    continue;
                }

                int eq = trimmed.IndexOf('=');
                if (eq < 0)
                {
                    problems.Add("line " + lineNo + ": no '='");
                    lines.Add(Line.Inert(raw));
                    continue;
                }

                string keyPart = trimmed.Substring(0, eq).Trim();
                string valuePart = trimmed.Substring(eq + 1).Trim();

                if (keyPart.Length == 0)
                {
                    problems.Add("line " + lineNo + ": no database");
                    lines.Add(Line.Inert(raw));
                    continue;
                }
                if (valuePart.Length == 0)
                {
                    problems.Add("line " + lineNo + ": no path");
                    lines.Add(Line.Inert(raw));
                    continue;
                }

                int at = keyPart.LastIndexOf('@');
                string database = at >= 0 ? keyPart.Substring(0, at).Trim() : keyPart;
                string server = at >= 0 ? keyPart.Substring(at + 1).Trim() : null;
                if (server != null && server.Length == 0) server = null;

                if (database.Length == 0)
                {
                    problems.Add("line " + lineNo + ": no database");
                    lines.Add(Line.Inert(raw));
                    continue;
                }

                lines.Add(Line.Mapping(raw, database, server, valuePart));
            }

            return new SourceControlMap(lines, problems);
        }

        /// <summary>null = not mapped. A server-specific line wins over a database-only one; the
        /// later of several matching lines wins over an earlier one (last write, in file order).</summary>
        public string FindProject(string server, string database)
        {
            string dbOnly = null;
            string serverSpecific = null;

            foreach (Line line in _lines)
            {
                if (!line.IsMapping) continue;
                if (!string.Equals(line.Database, database, StringComparison.OrdinalIgnoreCase)) continue;

                if (line.Server == null)
                    dbOnly = line.Path;
                else if (string.Equals(line.Server, server, StringComparison.OrdinalIgnoreCase))
                    serverSpecific = line.Path;
            }

            return serverSpecific ?? dbOnly;
        }

        /// <summary>server null = any. Replaces the matching line in place (same key: database and
        /// server both, OrdinalIgnoreCase) or appends a new one; every other line is untouched.</summary>
        public SourceControlMap With(string database, string server, string projectPath)
        {
            var newLines = new List<Line>(_lines);
            int matchIndex = -1;

            for (int i = 0; i < newLines.Count; i++)
            {
                Line l = newLines[i];
                if (!l.IsMapping) continue;
                if (!string.Equals(l.Database, database, StringComparison.OrdinalIgnoreCase)) continue;

                bool serverMatches = l.Server == null
                    ? server == null
                    : server != null && string.Equals(l.Server, server, StringComparison.OrdinalIgnoreCase);

                if (serverMatches) { matchIndex = i; break; }
            }

            string raw = FormatLine(database, server, projectPath);
            Line newLine = Line.Mapping(raw, database, server, projectPath);

            if (matchIndex >= 0)
                newLines[matchIndex] = newLine;
            else
                newLines.Add(newLine);

            return new SourceControlMap(newLines, _problems);
        }

        public string Serialize() => string.Join("\n", _lines.Select(l => l.Raw));

        private static string FormatLine(string database, string server, string path) =>
            server == null ? database + " = " + path : database + "@" + server + " = " + path;

        private sealed class Line
        {
            public string Raw;
            public bool IsMapping;
            public string Database;
            public string Server;
            public string Path;

            public static Line Inert(string raw) => new Line { Raw = raw, IsMapping = false };

            public static Line Mapping(string raw, string database, string server, string path) =>
                new Line { Raw = raw, IsMapping = true, Database = database, Server = server, Path = path };
        }
    }
}
