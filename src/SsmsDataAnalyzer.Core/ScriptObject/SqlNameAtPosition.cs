using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    public enum SqlNameLookupStatus
    {
        Found,
        NothingAtPosition,
        InsideComment,
        InsideString,
        Variable,
        TemporaryObject,
        Keyword,
        TooManyParts
    }

    public sealed class SqlNameLookupResult
    {
        internal SqlNameLookupResult(SqlNameLookupStatus status, SqlMultipartName name, int start, int length)
        {
            Status = status;
            Name = name;
            Start = start;
            Length = length;
        }

        public SqlNameLookupStatus Status { get; }
        public bool Found => Status == SqlNameLookupStatus.Found;

        /// <summary>The whole multipart name around the position (null unless Found).</summary>
        public SqlMultipartName Name { get; }

        /// <summary>Span of the whole name (or of the rejected token) in the input text.</summary>
        public int Start { get; }
        public int Length { get; }

        /// <summary>Why there is nothing to script, phrased for a status bar; null when Found.</summary>
        public string Message
        {
            get
            {
                switch (Status)
                {
                    case SqlNameLookupStatus.Found: return null;
                    case SqlNameLookupStatus.InsideComment: return "the caret is inside a comment.";
                    case SqlNameLookupStatus.InsideString: return "the caret is inside a string literal.";
                    case SqlNameLookupStatus.Variable: return "that is a variable, not a database object.";
                    case SqlNameLookupStatus.TemporaryObject: return "temporary objects (#name) cannot be scripted.";
                    case SqlNameLookupStatus.Keyword: return "that is a T-SQL keyword, not an object name.";
                    case SqlNameLookupStatus.TooManyParts: return "that name has more than four parts.";
                    default: return "no object name at the caret.";
                }
            }
        }
    }

    /// <summary>
    /// Finds the multipart object name at a text position — the caret for F12, the clicked
    /// character for Ctrl+click. Pure text logic, no database access.
    ///
    /// The WHOLE dotted name is returned wherever inside it the position is (on the schema, on
    /// a dot, inside brackets), because the object is what gets scripted, not the part.
    /// A position exactly at the end of a name (caret right after the last character) counts as
    /// on the name, matching how the editor places the caret after double-click/typing.
    /// </summary>
    public static class SqlNameAtPosition
    {
        public static SqlNameLookupResult Find(string text, int position)
        {
            if (string.IsNullOrEmpty(text) || position < 0 || position > text.Length)
                return Nothing();

            List<TsqlToken> tokens = TsqlLexer.Tokenize(text);
            int index = TokenIndexAt(tokens, position);
            if (index < 0) return Nothing();

            TsqlToken anchor = tokens[index];
            switch (anchor.Kind)
            {
                case TsqlTokenKind.LineComment:
                case TsqlTokenKind.BlockComment:
                    return Reject(SqlNameLookupStatus.InsideComment, anchor);
                case TsqlTokenKind.String:
                    return Reject(SqlNameLookupStatus.InsideString, anchor);
                case TsqlTokenKind.Variable:
                    return Reject(SqlNameLookupStatus.Variable, anchor);
                case TsqlTokenKind.Dot:
                case TsqlTokenKind.Identifier:
                case TsqlTokenKind.BracketIdentifier:
                case TsqlTokenKind.QuotedIdentifier:
                    break;
                default:
                    return Nothing();
            }

            // Walk left and right over name (dot name)* allowing whitespace only next to dots.
            int first = index;
            int cursor = index;
            while (true)
            {
                int prev = PreviousSignificant(tokens, cursor);
                if (prev < 0) break;
                bool cursorIsDot = tokens[cursor].Kind == TsqlTokenKind.Dot;
                bool prevIsDot = tokens[prev].Kind == TsqlTokenKind.Dot;
                if (!cursorIsDot && !prevIsDot) break;               // name name: not one chain
                if (!prevIsDot && !tokens[prev].IsNamePart) break;
                first = prev;
                cursor = prev;
            }

            int last = index;
            cursor = index;
            while (true)
            {
                int next = NextSignificant(tokens, cursor);
                if (next < 0) break;
                bool cursorIsDot = tokens[cursor].Kind == TsqlTokenKind.Dot;
                bool nextIsDot = tokens[next].Kind == TsqlTokenKind.Dot;
                if (!cursorIsDot && !nextIsDot) break;
                if (!nextIsDot && !tokens[next].IsNamePart) break;
                last = next;
                cursor = next;
            }

            // Split into parts on dots; empty parts are legal in the middle (db..name).
            var parts = new List<TsqlToken?>();
            TsqlToken? current = null;
            for (int i = first; i <= last; i++)
            {
                TsqlToken t = tokens[i];
                if (t.Kind == TsqlTokenKind.Whitespace) continue;
                if (t.Kind == TsqlTokenKind.Dot)
                {
                    parts.Add(current);
                    current = null;
                }
                else
                {
                    current = t;
                }
            }
            parts.Add(current);

            // A leading dot (".name") or a trailing one ("dbo." while typing) is not a part.
            while (parts.Count > 0 && parts[0] == null) parts.RemoveAt(0);
            while (parts.Count > 0 && parts[parts.Count - 1] == null) parts.RemoveAt(parts.Count - 1);
            if (parts.Count == 0) return Nothing();

            int spanStart = tokens[first].Start;
            int spanLength = tokens[last].End - spanStart;

            if (parts.Count > 4)
                return new SqlNameLookupResult(SqlNameLookupStatus.TooManyParts, null, spanStart, spanLength);

            TsqlToken objectToken = parts[parts.Count - 1].Value;
            string objectText = text.Substring(objectToken.Start, objectToken.Length);
            if (objectToken.Kind == TsqlTokenKind.Identifier)
            {
                if (objectText.StartsWith("#", StringComparison.Ordinal))
                    return new SqlNameLookupResult(SqlNameLookupStatus.TemporaryObject, null, spanStart, spanLength);
                if (TsqlKeywords.IsReserved(objectText))
                    return new SqlNameLookupResult(SqlNameLookupStatus.Keyword, null, spanStart, spanLength);
            }

            string[] values = new string[4];
            for (int p = 0; p < parts.Count; p++)
            {
                TsqlToken? part = parts[parts.Count - 1 - p];
                values[3 - p] = part.HasValue ? Unescape(text, part.Value) : null;
            }

            if (string.IsNullOrEmpty(values[3])) return Nothing();

            var name = new SqlMultipartName(values[0], values[1], values[2], values[3]);
            return new SqlNameLookupResult(SqlNameLookupStatus.Found, name, spanStart, spanLength);
        }

        /// <summary>Unescapes one name part: [a]]b] → a]b, "a""b" → a"b, plain names as-is.</summary>
        internal static string Unescape(string text, TsqlToken token)
        {
            string raw = text.Substring(token.Start, token.Length);
            switch (token.Kind)
            {
                case TsqlTokenKind.BracketIdentifier:
                    return StripDelimiters(raw, '[', ']').Replace("]]", "]");
                case TsqlTokenKind.QuotedIdentifier:
                    return StripDelimiters(raw, '"', '"').Replace("\"\"", "\"");
                default:
                    return raw;
            }
        }

        private static string StripDelimiters(string raw, char open, char close)
        {
            int start = raw.Length > 0 && raw[0] == open ? 1 : 0;
            int end = raw.Length;
            // Only strip a closer that is really a closer (unterminated "[abc" has none).
            if (end - start >= 1 && raw[end - 1] == close && IsRealCloser(raw, start, close)) end--;
            return raw.Substring(start, Math.Max(0, end - start));
        }

        private static bool IsRealCloser(string raw, int start, char close)
        {
            // Scan the body the same way the lexer does; the token is terminated iff the scan
            // stops on a single closer exactly at the last character.
            int i = start;
            while (i < raw.Length)
            {
                if (raw[i] == close)
                {
                    if (i + 1 < raw.Length && raw[i + 1] == close) { i += 2; continue; }
                    return i == raw.Length - 1;
                }
                i++;
            }
            return false;
        }

        private static int TokenIndexAt(List<TsqlToken> tokens, int position)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                TsqlToken t = tokens[i];
                if (position >= t.Start && position < t.End)
                {
                    // Caret just after a name but sitting on following whitespace/punctuation:
                    // prefer the name it touches.
                    if (!IsInteresting(t.Kind) && position == t.Start && i > 0 && IsNameLike(tokens[i - 1].Kind))
                        return i - 1;
                    return i;
                }
            }
            // Position == text length: the token ending there, if any.
            if (tokens.Count > 0 && tokens[tokens.Count - 1].End == position && IsNameLike(tokens[tokens.Count - 1].Kind))
                return tokens.Count - 1;
            return -1;
        }

        private static bool IsNameLike(TsqlTokenKind kind) =>
            kind == TsqlTokenKind.Identifier || kind == TsqlTokenKind.BracketIdentifier
            || kind == TsqlTokenKind.QuotedIdentifier || kind == TsqlTokenKind.Variable;

        private static bool IsInteresting(TsqlTokenKind kind) =>
            IsNameLike(kind) || kind == TsqlTokenKind.Dot || kind == TsqlTokenKind.String
            || kind == TsqlTokenKind.LineComment || kind == TsqlTokenKind.BlockComment;

        private static int PreviousSignificant(List<TsqlToken> tokens, int index)
        {
            int i = index - 1;
            bool skippedWhitespace = false;
            if (i >= 0 && tokens[i].Kind == TsqlTokenKind.Whitespace) { i--; skippedWhitespace = true; }
            if (i < 0) return -1;
            // Whitespace is only allowed next to a dot.
            if (skippedWhitespace && tokens[index].Kind != TsqlTokenKind.Dot && tokens[i].Kind != TsqlTokenKind.Dot) return -1;
            return i;
        }

        private static int NextSignificant(List<TsqlToken> tokens, int index)
        {
            int i = index + 1;
            bool skippedWhitespace = false;
            if (i < tokens.Count && tokens[i].Kind == TsqlTokenKind.Whitespace) { i++; skippedWhitespace = true; }
            if (i >= tokens.Count) return -1;
            if (skippedWhitespace && tokens[index].Kind != TsqlTokenKind.Dot && tokens[i].Kind != TsqlTokenKind.Dot) return -1;
            return i;
        }

        private static SqlNameLookupResult Nothing() =>
            new SqlNameLookupResult(SqlNameLookupStatus.NothingAtPosition, null, 0, 0);

        private static SqlNameLookupResult Reject(SqlNameLookupStatus status, TsqlToken token) =>
            new SqlNameLookupResult(status, null, token.Start, token.Length);
    }
}
