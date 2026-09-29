using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// Reads the column list of one CREATE TABLE batch (§13b.5). Deliberately strict: anything it
    /// does not recognise with confidence makes the whole result null -- a wrong column list is
    /// worse than no comparison. Built on <see cref="TsqlLexer"/>; no I/O, never throws.
    /// </summary>
    public static class TableDefinitionParser
    {
        /// <summary>Columns of the CREATE TABLE in <paramref name="batchText"/>, in order; null =
        /// not a CREATE TABLE we can read with confidence.</summary>
        public static IReadOnlyList<TableColumn> TryParseColumns(string batchText) =>
            TryParseColumns(batchText, out _);

        /// <summary>As above; when null is returned, <paramref name="unrecognised"/> is the first
        /// keyword (or token kind) that stopped the parse -- no names, for aggregate diagnostics.</summary>
        internal static IReadOnlyList<TableColumn> TryParseColumns(string batchText, out string unrecognised)
        {
            unrecognised = null;
            try
            {
                return Parse(batchText, out unrecognised);
            }
            catch (Exception)
            {
                unrecognised = "(exception)";
                return null;
            }
        }

        private struct Tok
        {
            public TsqlTokenKind Kind;
            public string Text;

            public bool IsWord(string w) =>
                Kind == TsqlTokenKind.Identifier && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);

            public bool IsOther(char c) => Kind == TsqlTokenKind.Other && Text.Length == 1 && Text[0] == c;

            public bool IsName =>
                Kind == TsqlTokenKind.Identifier || Kind == TsqlTokenKind.BracketIdentifier
                || Kind == TsqlTokenKind.QuotedIdentifier;
        }

        private static readonly HashSet<string> WithLength = new HashSet<string>
            { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" };

        private static readonly HashSet<string> WithScaleOnly = new HashSet<string>
            { "datetime2", "time", "datetimeoffset" };

        private static readonly HashSet<string> SystemTypes = new HashSet<string>
        {
            "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney",
            "float", "real", "datetime", "smalldatetime", "date", "time", "datetime2", "datetimeoffset",
            "char", "varchar", "text", "nchar", "nvarchar", "ntext", "binary", "varbinary", "image",
            "uniqueidentifier", "xml", "sql_variant", "timestamp", "hierarchyid", "geography", "geometry", "sysname"
        };

        // Words that end a DEFAULT expression written without parentheses.
        private static readonly HashSet<string> ColumnKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOT", "NULL", "CONSTRAINT", "PRIMARY", "UNIQUE", "COLLATE", "IDENTITY", "SPARSE", "ROWGUIDCOL",
            "FILESTREAM", "MASKED", "GENERATED", "HIDDEN", "CHECK", "REFERENCES", "FOREIGN", "CLUSTERED", "NONCLUSTERED"
        };

        private static IReadOnlyList<TableColumn> Parse(string text, out string bad)
        {
            bad = null;
            if (text != null) text = text.TrimStart((char)0xFEFF);
            if (string.IsNullOrWhiteSpace(text)) { bad = "(empty)"; return null; }

            List<TsqlToken> sig = DdlScanning.Significant(text);
            var toks = new List<Tok>(sig.Count);
            foreach (TsqlToken t in sig)
                toks.Add(new Tok { Kind = t.Kind, Text = DdlScanning.TokenText(text, t) });

            int i = 0;
            if (!(i < toks.Count && toks[i].IsWord("CREATE"))) { bad = "(not CREATE)"; return null; }
            i++;
            if (!(i < toks.Count && toks[i].IsWord("TABLE"))) { bad = "(not CREATE TABLE)"; return null; }
            i++;

            List<string> parts = DdlScanning.ReadNameParts(text, sig, ref i);
            if (parts.Count == 0) { bad = "(no table name)"; return null; }

            if (i >= toks.Count || !toks[i].IsOther('(')) { bad = KeywordOf(toks, i); return null; }
            int open = i;
            int close = MatchingParen(toks, open);
            if (close < 0) { bad = "(unbalanced)"; return null; }

            if (!TailIsPlain(toks, close + 1, out bad)) return null;

            // Split the body at depth-0 commas.
            var items = new List<List<Tok>>();
            var cur = new List<Tok>();
            int depth = 0;
            for (int k = open + 1; k < close; k++)
            {
                Tok t = toks[k];
                if (t.IsOther('(')) depth++;
                else if (t.IsOther(')')) depth--;
                if (depth == 0 && t.IsOther(',')) { items.Add(cur); cur = new List<Tok>(); continue; }
                cur.Add(t);
            }
            items.Add(cur);
            // A trailing comma before the closing parenthesis (SSDT tolerates it) is not an item.
            if (items.Count > 1 && cur.Count == 0) items.RemoveAt(items.Count - 1);

            var columns = new List<TableColumn>();
            var unspecified = new List<int>();
            var pkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (List<Tok> item in items)
            {
                if (item.Count == 0) { bad = "(empty item)"; return null; }

                if (IsTableLevel(item))
                {
                    if (!CollectPrimaryKeyColumns(item, pkColumns, out bad)) return null;
                    continue;
                }

                TableColumn col = ParseColumn(item, out bool nullabilityUnspecified, out bad);
                if (col == null) return null;
                if (nullabilityUnspecified) unspecified.Add(columns.Count);
                columns.Add(col);
            }

            if (columns.Count == 0) { bad = "(no columns)"; return null; }

            // A table-level PRIMARY KEY makes its columns NOT NULL when nothing else was said.
            foreach (int idx in unspecified)
            {
                TableColumn c = columns[idx];
                if (pkColumns.Contains(c.Name))
                    columns[idx] = new TableColumn(c.Name, c.TypeName, c.Length, c.Precision, c.Scale, false, c.IsIdentity, false);
            }
            return columns;
        }

        private static string KeywordOf(List<Tok> toks, int i) =>
            i < toks.Count
                ? (toks[i].Kind == TsqlTokenKind.Identifier ? toks[i].Text.ToUpperInvariant() : "(" + toks[i].Kind + ")")
                : "(end)";

        private static int MatchingParen(List<Tok> toks, int open)
        {
            int depth = 0;
            for (int k = open; k < toks.Count; k++)
            {
                if (toks[k].IsOther('(')) depth++;
                else if (toks[k].IsOther(')')) { depth--; if (depth == 0) return k; }
            }
            return -1;
        }

        /// <summary>After the closing parenthesis: ;  WITH (..)  ON x  TEXTIMAGE_ON x  FILESTREAM_ON x.
        /// AS NODE / AS EDGE / AS FILETABLE and the like are not plain tables.</summary>
        private static bool TailIsPlain(List<Tok> toks, int i, out string bad)
        {
            bad = null;
            while (i < toks.Count)
            {
                Tok t = toks[i];
                if (t.IsOther(';')) { i++; continue; }
                if (t.IsWord("WITH") && i + 1 < toks.Count && toks[i + 1].IsOther('('))
                {
                    int c = MatchingParen(toks, i + 1);
                    if (c < 0) { bad = "(unbalanced)"; return false; }
                    i = c + 1;
                    continue;
                }
                if ((t.IsWord("ON") || t.IsWord("TEXTIMAGE_ON") || t.IsWord("FILESTREAM_ON")) && i + 1 < toks.Count && toks[i + 1].IsName)
                {
                    i += 2;
                    if (i < toks.Count && toks[i].IsOther('('))
                    {
                        int c = MatchingParen(toks, i);
                        if (c < 0) { bad = "(unbalanced)"; return false; }
                        i = c + 1;
                    }
                    continue;
                }
                bad = KeywordOf(toks, i);
                return false;
            }
            return true;
        }

        private static bool IsTableLevel(List<Tok> item)
        {
            Tok f = item[0];
            if (f.Kind != TsqlTokenKind.Identifier) return false;
            if (f.IsWord("CONSTRAINT") || f.IsWord("PRIMARY") || f.IsWord("UNIQUE") || f.IsWord("FOREIGN")
                || f.IsWord("CHECK") || f.IsWord("INDEX")) return true;
            return f.IsWord("PERIOD") && item.Count > 1 && item[1].IsWord("FOR");
        }

        /// <summary>For a table-level PRIMARY KEY (optionally under CONSTRAINT x), records the key
        /// columns. Other table-level items are skipped. False = the PK column list is unreadable.</summary>
        private static bool CollectPrimaryKeyColumns(List<Tok> item, HashSet<string> pk, out string bad)
        {
            bad = null;
            int i = 0;
            if (item[i].IsWord("CONSTRAINT")) i += 2;
            if (!(i + 1 < item.Count && item[i].IsWord("PRIMARY") && item[i + 1].IsWord("KEY"))) return true;
            i += 2;
            while (i < item.Count && (item[i].IsWord("CLUSTERED") || item[i].IsWord("NONCLUSTERED"))) i++;
            if (i >= item.Count || !item[i].IsOther('(')) { bad = "(primary key)"; return false; }
            int depth = 0;
            for (int k = i; k < item.Count; k++)
            {
                Tok t = item[k];
                if (t.IsOther('(')) { depth++; continue; }
                if (t.IsOther(')')) { depth--; if (depth == 0) break; continue; }
                if (depth == 1 && t.IsName && !t.IsWord("ASC") && !t.IsWord("DESC"))
                    pk.Add(Unbracket(t));
            }
            return true;
        }

        private static string Unbracket(Tok t)
        {
            string s = t.Text;
            if (t.Kind == TsqlTokenKind.BracketIdentifier)
                return s.Substring(1, s.Length > 1 && s.EndsWith("]", StringComparison.Ordinal) ? s.Length - 2 : s.Length - 1).Replace("]]", "]");
            if (t.Kind == TsqlTokenKind.QuotedIdentifier)
                return s.Substring(1, s.Length > 1 && s.EndsWith("\"", StringComparison.Ordinal) ? s.Length - 2 : s.Length - 1).Replace("\"\"", "\"");
            return s;
        }

        private static TableColumn ParseColumn(List<Tok> item, out bool nullabilityUnspecified, out string bad)
        {
            nullabilityUnspecified = false;
            bad = null;
            if (!item[0].IsName) { bad = KeywordOf(item, 0); return null; }
            string name = Unbracket(item[0]);
            int i = 1;
            if (i >= item.Count) { bad = "(no type)"; return null; }

            // Computed: [X] AS (expr) [PERSISTED] ... -- the expression is not compared.
            if (item[i].IsWord("AS"))
                return new TableColumn(name, null, null, null, null, false, false, true);

            // Type: one part, or schema.type (the schema is dropped, as the catalog does).
            if (!item[i].IsName) { bad = KeywordOf(item, i); return null; }
            string type = Unbracket(item[i]);
            i++;
            while (i + 1 < item.Count && item[i].Kind == TsqlTokenKind.Dot && item[i + 1].IsName)
            {
                type = Unbracket(item[i + 1]);
                i += 2;
            }
            string lower = type.ToLowerInvariant();

            if (lower == "double" && i < item.Count && item[i].IsWord("PRECISION")) { lower = "float"; i++; }
            else if (lower == "integer") lower = "int";
            else if (lower == "dec") lower = "decimal";
            else if (lower == "rowversion") lower = "timestamp";

            // Arguments: (n) (MAX) (p, s).
            var args = new List<int>();   // -1 = MAX
            bool hasArgs = false;
            if (i < item.Count && item[i].IsOther('('))
            {
                hasArgs = true;
                int close = FindClose(item, i);
                if (close < 0) { bad = "(unbalanced)"; return null; }

                if (lower != "xml")   // an xml schema collection is not compared
                {
                    bool expectValue = true;
                    for (int k = i + 1; k < close; k++)
                    {
                        Tok t = item[k];
                        if (expectValue && t.Kind == TsqlTokenKind.Number && int.TryParse(t.Text, out int v)) { args.Add(v); expectValue = false; }
                        else if (expectValue && t.IsWord("MAX")) { args.Add(-1); expectValue = false; }
                        else if (!expectValue && t.IsOther(',')) expectValue = true;
                        else { bad = "(type argument)"; return null; }
                    }
                    if (expectValue) { bad = "(type argument)"; return null; }
                }
                i = close + 1;
            }

            int? length = null, precision = null, scale = null;
            if (!SystemTypes.Contains(lower))
            {
                // A user-defined type: compared by name only.
                if (hasArgs) { bad = "(user type arguments)"; return null; }
            }
            else if (WithLength.Contains(lower))
            {
                if (args.Count > 1) { bad = "(type argument)"; return null; }
                length = args.Count == 1 ? args[0] : 1;
                if (length.Value < -1 || length.Value == 0) { bad = "(type argument)"; return null; }
            }
            else if (lower == "decimal" || lower == "numeric")
            {
                if (args.Count > 2) { bad = "(type argument)"; return null; }
                precision = args.Count >= 1 ? args[0] : 18;
                scale = args.Count == 2 ? args[1] : 0;
            }
            else if (WithScaleOnly.Contains(lower))
            {
                if (args.Count > 1) { bad = "(type argument)"; return null; }
                scale = args.Count == 1 ? args[0] : 7;
            }
            else if (lower == "float")
            {
                if (args.Count > 1) { bad = "(type argument)"; return null; }
                // float(1..24) is stored -- and reported by the catalog -- as real.
                if (args.Count == 1 && args[0] >= 1 && args[0] <= 24) lower = "real";
                else if (args.Count == 1 && (args[0] < 1 || args[0] > 53)) { bad = "(type argument)"; return null; }
            }
            else if (lower != "xml" && hasArgs) { bad = "(type argument)"; return null; }

            // Column attributes.
            bool? nullable = null;
            bool identity = false, inlinePk = false;
            while (i < item.Count)
            {
                Tok t = item[i];
                if (t.Kind != TsqlTokenKind.Identifier && !t.IsOther('(')) { bad = KeywordOf(item, i); return null; }

                if (t.IsWord("NOT") && i + 1 < item.Count && item[i + 1].IsWord("NULL")) { nullable = false; i += 2; }
                else if (t.IsWord("NOT") && i + 2 < item.Count && item[i + 1].IsWord("FOR") && item[i + 2].IsWord("REPLICATION")) i += 3;
                else if (t.IsWord("NULL")) { nullable = true; i++; }
                else if (t.IsWord("IDENTITY"))
                {
                    identity = true;
                    i++;
                    if (i < item.Count && item[i].IsOther('('))
                    {
                        int c = FindClose(item, i);
                        if (c < 0) { bad = "(unbalanced)"; return null; }
                        i = c + 1;
                    }
                }
                else if (t.IsWord("CONSTRAINT") && i + 1 < item.Count && item[i + 1].IsName) i += 2;
                else if (t.IsWord("DEFAULT"))
                {
                    i++;
                    if (i < item.Count && item[i].IsWord("NULL")) { i++; continue; }
                    int depth = 0;
                    while (i < item.Count)
                    {
                        if (item[i].IsOther('(')) depth++;
                        else if (item[i].IsOther(')')) depth--;
                        else if (depth == 0 && item[i].Kind == TsqlTokenKind.Identifier && ColumnKeywords.Contains(item[i].Text))
                            break;
                        i++;
                    }
                }
                else if (t.IsWord("PRIMARY") && i + 1 < item.Count && item[i + 1].IsWord("KEY")) { inlinePk = true; i += 2; }
                else if (t.IsWord("UNIQUE") || t.IsWord("CLUSTERED") || t.IsWord("NONCLUSTERED") || t.IsWord("ASC") || t.IsWord("DESC")) i++;
                else if (t.IsOther('(') || (t.IsWord("FOREIGN") && i + 2 < item.Count && item[i + 1].IsWord("KEY") && item[i + 2].IsOther('(')))
                {
                    // A constraint's own column list, when SSMS wrote the constraint on the column.
                    int c = FindClose(item, t.IsOther('(') ? i : i + 2);
                    if (c < 0) { bad = "(unbalanced)"; return null; }
                    i = c + 1;
                }
                else if (t.IsWord("REFERENCES") && i + 1 < item.Count && item[i + 1].IsName)
                {
                    i += 2;
                    while (i + 1 < item.Count && item[i].Kind == TsqlTokenKind.Dot && item[i + 1].IsName) i += 2;
                }
                else if (t.IsWord("ON") && i + 1 < item.Count && (item[i + 1].IsWord("DELETE") || item[i + 1].IsWord("UPDATE")))
                {
                    i += 2;
                    if (i < item.Count && item[i].IsWord("CASCADE")) i++;
                    else if (i + 1 < item.Count && (item[i].IsWord("NO") || item[i].IsWord("SET"))) i += 2;
                    else { bad = "ON DELETE/UPDATE"; return null; }
                }
                else if (t.IsWord("ON") && i + 1 < item.Count && item[i + 1].IsName) i += 2;   // storage: ON [PRIMARY]
                else if ((t.IsWord("CHECK") || t.IsWord("WITH")) && i + 1 < item.Count && item[i + 1].IsOther('('))
                {
                    int c = FindClose(item, i + 1);
                    if (c < 0) { bad = "(unbalanced)"; return null; }
                    i = c + 1;
                }
                else if (t.IsWord("COLLATE") && i + 1 < item.Count && item[i + 1].IsName) i += 2;
                else if (t.IsWord("SPARSE") || t.IsWord("ROWGUIDCOL") || t.IsWord("FILESTREAM") || t.IsWord("HIDDEN")) i++;
                else if (t.IsWord("MASKED") && i + 2 < item.Count && item[i + 1].IsWord("WITH") && item[i + 2].IsOther('('))
                {
                    int c = FindClose(item, i + 2);
                    if (c < 0) { bad = "(unbalanced)"; return null; }
                    i = c + 1;
                }
                else if (t.IsWord("GENERATED") && i + 4 < item.Count && item[i + 1].IsWord("ALWAYS") && item[i + 2].IsWord("AS")
                         && (item[i + 3].IsWord("ROW") || item[i + 3].IsWord("TRANSACTION_ID") || item[i + 3].IsWord("SEQUENCE_NUMBER"))
                         && (item[i + 4].IsWord("START") || item[i + 4].IsWord("END")))
                    i += 5;
                else { bad = t.Text.ToUpperInvariant(); return null; }
            }

            bool notNull;
            if (nullable.HasValue) notNull = !nullable.Value;
            else if (identity || inlinePk || lower == "timestamp") notNull = true;
            else { notNull = false; nullabilityUnspecified = true; }

            return new TableColumn(name, lower, length, precision, scale, !notNull, identity, false);
        }

        private static int FindClose(List<Tok> toks, int open) => MatchingParen(toks, open);
    }
}
