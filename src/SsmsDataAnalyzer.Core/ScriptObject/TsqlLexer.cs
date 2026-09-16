using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    public enum TsqlTokenKind
    {
        Whitespace,
        LineComment,
        BlockComment,
        String,
        BracketIdentifier,
        QuotedIdentifier,
        Identifier,
        Variable,
        Number,
        Dot,
        Other
    }

    public struct TsqlToken
    {
        public TsqlToken(TsqlTokenKind kind, int start, int length)
        {
            Kind = kind;
            Start = start;
            Length = length;
        }

        public TsqlTokenKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;

        public bool IsNamePart =>
            Kind == TsqlTokenKind.Identifier
            || Kind == TsqlTokenKind.BracketIdentifier
            || Kind == TsqlTokenKind.QuotedIdentifier;
    }

    /// <summary>
    /// A deliberately small T-SQL lexer: just enough to tell names apart from comments, strings,
    /// variables and punctuation, so "the object name under the caret" can be found without a
    /// full parser. Never throws; unterminated comments/strings/brackets run to the end of the text
    /// (that is what the editor is showing the user while they type).
    ///
    /// Why not ScriptDom's tokenizer: Core is netstandard2.0 and host-agnostic; ScriptDom is an
    /// SSMS-owned assembly the VSIX only references. The rules below are T-SQL's own lexical rules
    /// for the token kinds that matter here (nested block comments, ]] and "" escapes, N'' strings).
    /// </summary>
    public static class TsqlLexer
    {
        public static List<TsqlToken> Tokenize(string text)
        {
            var tokens = new List<TsqlToken>();
            if (string.IsNullOrEmpty(text)) return tokens;

            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                int start = i;

                if (char.IsWhiteSpace(c))
                {
                    while (i < n && char.IsWhiteSpace(text[i])) i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.Whitespace, start, i - start));
                }
                else if (c == '-' && i + 1 < n && text[i + 1] == '-')
                {
                    while (i < n && text[i] != '\r' && text[i] != '\n') i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.LineComment, start, i - start));
                }
                else if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    // T-SQL block comments nest.
                    int depth = 0;
                    while (i < n)
                    {
                        if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') { depth++; i += 2; }
                        else if (text[i] == '*' && i + 1 < n && text[i + 1] == '/')
                        {
                            depth--; i += 2;
                            if (depth == 0) break;
                        }
                        else i++;
                    }
                    tokens.Add(new TsqlToken(TsqlTokenKind.BlockComment, start, i - start));
                }
                else if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < n && text[i + 1] == '\''))
                {
                    i += c == '\'' ? 1 : 2;
                    i = SkipQuoted(text, i, '\'');
                    tokens.Add(new TsqlToken(TsqlTokenKind.String, start, i - start));
                }
                else if (c == '[')
                {
                    i = SkipQuoted(text, i + 1, ']');
                    tokens.Add(new TsqlToken(TsqlTokenKind.BracketIdentifier, start, i - start));
                }
                else if (c == '"')
                {
                    i = SkipQuoted(text, i + 1, '"');
                    tokens.Add(new TsqlToken(TsqlTokenKind.QuotedIdentifier, start, i - start));
                }
                else if (c == '@')
                {
                    i++;
                    while (i < n && IsIdentifierPart(text[i])) i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.Variable, start, i - start));
                }
                else if (char.IsDigit(c))
                {
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    // Decimal part, but never swallow a dot that starts a name (e.g. "1.e" is odd
                    // T-SQL either way; a digit after the dot is the only unambiguous case).
                    if (i + 1 < n && text[i] == '.' && char.IsDigit(text[i + 1]))
                    {
                        i++;
                        while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    }
                    tokens.Add(new TsqlToken(TsqlTokenKind.Number, start, i - start));
                }
                else if (IsIdentifierStart(c))
                {
                    i++;
                    while (i < n && IsIdentifierPart(text[i])) i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.Identifier, start, i - start));
                }
                else if (c == '.')
                {
                    i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.Dot, start, 1));
                }
                else
                {
                    i++;
                    tokens.Add(new TsqlToken(TsqlTokenKind.Other, start, 1));
                }
            }
            return tokens;
        }

        /// <summary>Advances past a closing <paramref name="close"/>, treating a doubled close
        /// character as an escaped literal. Returns the index just after the closer, or the text
        /// length when unterminated.</summary>
        private static int SkipQuoted(string text, int i, char close)
        {
            int n = text.Length;
            while (i < n)
            {
                if (text[i] == close)
                {
                    if (i + 1 < n && text[i + 1] == close) { i += 2; continue; }
                    return i + 1;
                }
                i++;
            }
            return n;
        }

        internal static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '#';

        internal static bool IsIdentifierPart(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$';
    }
}
