using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// "Did it really change?" -- implements the §7 comparison rules the frozen interface names,
    /// plus a line-ending rule added after the spike (verified live, 2026-09-28): SQL Server keeps
    /// a module's line endings exactly as sent, so a repo file (often CRLF) and the server
    /// definition can legitimately differ only in line endings -- that must not count as a change.
    ///
    /// 0. Line endings are normalised (CRLF and lone CR -&gt; LF) before anything else. Whitespace
    ///    BETWEEN tokens is already harmless (the lexer treats CR/LF as Whitespace), so this only
    ///    matters INSIDE a token -- a multi-line string literal, which rule 5 compares exactly.
    /// 1. Everything before the first CREATE/ALTER is ignored (headers, BOM, SET lines).
    /// 2. CREATE, ALTER and CREATE OR ALTER are equivalent.
    /// 3. Whitespace and comments are ignored.
    /// 4. Keywords and identifiers compare case-insensitively; [ABB], "ABB" and ABB are the same.
    /// 5. String literals compare exactly (case and spaces included).
    /// 6. A trailing GO is ignored.
    ///
    /// The canonical form is a token sequence joined with U+0001, a separator that cannot occur
    /// in T-SQL text, so a bracketed identifier that happens to contain a literal dot
    /// ("[ABB].[ABB.ChangeStatus]" -- identifier, dot, identifier) never collapses onto three
    /// plain identifiers joined by two real dots ("ABB.ABB.ChangeStatus").
    /// </summary>
    public static class ModuleDefinitionNormalizer
    {
        private const char Separator = '\u0001';

        public static string Normalize(string definition)
        {
            if (string.IsNullOrEmpty(definition)) return string.Empty;

            // Rule 0: line endings never carry meaning, inside a string literal included.
            string text = NormalizeLineEndings(definition);

            List<TsqlToken> tokens = TsqlLexer.Tokenize(text);
            int start = FindStart(text, tokens);
            int i = SkipActionClause(text, tokens, start);

            var pieces = new List<string>();
            for (; i < tokens.Count; i++)
            {
                TsqlToken t = tokens[i];
                switch (t.Kind)
                {
                    case TsqlTokenKind.Whitespace:
                    case TsqlTokenKind.LineComment:
                    case TsqlTokenKind.BlockComment:
                        continue;

                    case TsqlTokenKind.String:
                        // Rule 5: exact, case and spaces included.
                        pieces.Add(DdlScanning.TokenText(text, t));
                        break;

                    case TsqlTokenKind.BracketIdentifier:
                    case TsqlTokenKind.QuotedIdentifier:
                        // Rule 4: [ABB], "ABB" and ABB must all normalize the same way.
                        pieces.Add(DdlScanning.Unescape(text, t).ToLowerInvariant());
                        break;

                    case TsqlTokenKind.Dot:
                        pieces.Add(".");
                        break;

                    default:
                        // Identifier, Variable, Number, Other: case doesn't carry meaning here.
                        pieces.Add(DdlScanning.TokenText(text, t).ToLowerInvariant());
                        break;
                }
            }

            // Rule 6: a trailing GO is ignored.
            while (pieces.Count > 0 && string.Equals(pieces[pieces.Count - 1], "go", StringComparison.Ordinal))
                pieces.RemoveAt(pieces.Count - 1);

            return string.Join(Separator.ToString(), pieces);
        }

        /// <summary>Rule 0: CRLF and lone CR both become LF, so line endings never affect the
        /// comparison even inside a token the rest of the rules keep verbatim (a multi-line string).</summary>
        private static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n");

        public static bool AreEquivalent(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

        /// <summary>Rule 1: index of the first CREATE/ALTER identifier token, or 0 when there is
        /// none (best-effort fallback -- normalizing the whole text is still safe and never throws).</summary>
        private static int FindStart(string text, List<TsqlToken> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TsqlTokenKind.Identifier) continue;
                string word = DdlScanning.TokenText(text, tokens[i]);
                if (DdlScanning.EqualsKeyword(word, "CREATE") || DdlScanning.EqualsKeyword(word, "ALTER"))
                    return i;
            }
            return 0;
        }

        /// <summary>Rule 2: drops the leading CREATE / ALTER / CREATE OR ALTER so all three
        /// collapse to the same canonical form; the object-type keyword that follows (PROC, ...)
        /// stays, so a CREATE PROC is still distinguished from a CREATE VIEW.</summary>
        private static int SkipActionClause(string text, List<TsqlToken> tokens, int i)
        {
            if (i >= tokens.Count || tokens[i].Kind != TsqlTokenKind.Identifier) return i;
            string word = DdlScanning.TokenText(text, tokens[i]);

            if (DdlScanning.EqualsKeyword(word, "ALTER")) return i + 1;

            if (DdlScanning.EqualsKeyword(word, "CREATE"))
            {
                int j = NextSignificant(tokens, i + 1);
                if (j >= 0 && tokens[j].Kind == TsqlTokenKind.Identifier
                    && DdlScanning.EqualsKeyword(DdlScanning.TokenText(text, tokens[j]), "OR"))
                {
                    int k = NextSignificant(tokens, j + 1);
                    if (k >= 0 && tokens[k].Kind == TsqlTokenKind.Identifier
                        && DdlScanning.EqualsKeyword(DdlScanning.TokenText(text, tokens[k]), "ALTER"))
                        return k + 1;
                }
                return i + 1;
            }
            return i;
        }

        private static int NextSignificant(List<TsqlToken> tokens, int i)
        {
            while (i < tokens.Count
                && (tokens[i].Kind == TsqlTokenKind.Whitespace
                    || tokens[i].Kind == TsqlTokenKind.LineComment
                    || tokens[i].Kind == TsqlTokenKind.BlockComment))
                i++;
            return i < tokens.Count ? i : -1;
        }
    }
}
