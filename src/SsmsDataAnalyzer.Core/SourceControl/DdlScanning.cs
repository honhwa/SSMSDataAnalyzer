using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// Shared token-scanning helpers for <see cref="DdlDetector"/> and <see cref="ModuleFileParser"/>:
    /// both walk T-SQL text looking for a CREATE/ALTER/DROP of a recognised object kind, so the
    /// low-level bookkeeping (keyword matching, dotted names, temp/variable exclusion) lives once.
    /// Built entirely on the existing <see cref="TsqlLexer"/> -- never touches the file system or
    /// throws on any input text.
    /// </summary>
    internal static class DdlScanning
    {
        /// <summary>Tokens with whitespace and comments removed, in order. A comment or a string
        /// stays a single token of its own kind, so text inside either never matches a keyword.</summary>
        public static List<TsqlToken> Significant(string text)
        {
            List<TsqlToken> all = TsqlLexer.Tokenize(text);
            var result = new List<TsqlToken>(all.Count);
            foreach (TsqlToken t in all)
            {
                if (t.Kind == TsqlTokenKind.Whitespace
                    || t.Kind == TsqlTokenKind.LineComment
                    || t.Kind == TsqlTokenKind.BlockComment)
                    continue;
                result.Add(t);
            }
            return result;
        }

        public static string TokenText(string text, TsqlToken t) => text.Substring(t.Start, t.Length);

        public static bool EqualsKeyword(string word, string keyword) =>
            string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase);

        public static bool IsNamePart(TsqlTokenKind kind) =>
            kind == TsqlTokenKind.Identifier
            || kind == TsqlTokenKind.BracketIdentifier
            || kind == TsqlTokenKind.QuotedIdentifier;

        /// <summary>PROC/PROCEDURE/VIEW/FUNCTION/TRIGGER/TABLE at sig[j]; advances j past it when found.</summary>
        public static DbObjectKind? TryReadObjectKind(string text, List<TsqlToken> sig, ref int j)
        {
            if (j >= sig.Count || sig[j].Kind != TsqlTokenKind.Identifier) return null;
            string word = TokenText(text, sig[j]);

            DbObjectKind? kind = null;
            if (EqualsKeyword(word, "PROC") || EqualsKeyword(word, "PROCEDURE")) kind = DbObjectKind.Procedure;
            else if (EqualsKeyword(word, "VIEW")) kind = DbObjectKind.View;
            else if (EqualsKeyword(word, "FUNCTION")) kind = DbObjectKind.Function;
            else if (EqualsKeyword(word, "TRIGGER")) kind = DbObjectKind.Trigger;
            else if (EqualsKeyword(word, "TABLE")) kind = DbObjectKind.Table;

            if (kind.HasValue) j++;
            return kind;
        }

        /// <summary>Reads a dotted chain of name tokens starting at sig[i]; advances i past the
        /// chain. Empty when there is no name-part token there. A bracketed/quoted part is
        /// unescaped and kept whole even if it contains a literal dot -- that dot is text, not
        /// a <see cref="TsqlTokenKind.Dot"/> token, so it never splits the chain.</summary>
        public static List<string> ReadNameParts(string text, List<TsqlToken> sig, ref int i)
        {
            var parts = new List<string>();
            if (i >= sig.Count || !IsNamePart(sig[i].Kind)) return parts;

            parts.Add(Unescape(text, sig[i]));
            i++;
            while (i + 1 < sig.Count && sig[i].Kind == TsqlTokenKind.Dot && IsNamePart(sig[i + 1].Kind))
            {
                parts.Add(Unescape(text, sig[i + 1]));
                i += 2;
            }
            return parts;
        }

        /// <summary>True when the name chain starting at sig[nameStartIndex] is a #temp/##global
        /// table or an @variable -- both are skipped, never reported.</summary>
        public static bool IsTempOrVariable(string text, List<TsqlToken> sig, int nameStartIndex)
        {
            if (nameStartIndex < 0 || nameStartIndex >= sig.Count) return false;
            TsqlToken t = sig[nameStartIndex];
            if (t.Kind == TsqlTokenKind.Variable) return true;
            if (t.Kind == TsqlTokenKind.Identifier)
            {
                string word = TokenText(text, t);
                return word.Length > 0 && word[0] == '#';
            }
            return false;
        }

        /// <summary>The last two name parts become schema/name (extra leading parts, e.g. a
        /// database qualifier, are dropped); a single part is an unqualified name (Schema = null).</summary>
        public static ModuleRef BuildTarget(List<string> parts, DbObjectKind kind)
        {
            if (parts == null || parts.Count == 0) return null;
            if (parts.Count == 1) return new ModuleRef(null, parts[0], kind);
            return new ModuleRef(parts[parts.Count - 2], parts[parts.Count - 1], kind);
        }

        /// <summary>Unescapes one name-part token: [a]]b] -&gt; a]b, "a""b" -&gt; a"b, plain as-is.</summary>
        public static string Unescape(string text, TsqlToken token)
        {
            string raw = TokenText(text, token);
            switch (token.Kind)
            {
                case TsqlTokenKind.BracketIdentifier:
                    return StripDelimiters(raw, '[', ']');
                case TsqlTokenKind.QuotedIdentifier:
                    return StripDelimiters(raw, '"', '"');
                default:
                    return raw;
            }
        }

        private static string StripDelimiters(string raw, char open, char close)
        {
            int start = raw.Length > 0 && raw[0] == open ? 1 : 0;
            int end = raw.Length;
            bool terminated = end - start >= 1 && raw[end - 1] == close && IsRealCloser(raw, start, close);
            if (terminated) end--;
            string body = raw.Substring(start, Math.Max(0, end - start));
            string doubled = new string(close, 2);
            return body.Replace(doubled, close.ToString());
        }

        private static bool IsRealCloser(string raw, int start, char close)
        {
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
    }
}
