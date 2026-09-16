using System.Collections.Generic;
using System.Text;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    /// <summary>Text-level helpers for object scripts: CREATE→ALTER rewriting for the
    /// OBJECT_DEFINITION fallback, and assembling batches into the final editor text.</summary>
    public static class ObjectScriptText
    {
        /// <summary>
        /// Rewrites a module definition's leading CREATE (or CREATE OR ALTER) to ALTER, using
        /// token positions so a "create" inside a leading comment, a string, or a name is never
        /// touched. Returns null when the first real token is not CREATE.
        /// </summary>
        public static string RewriteCreateToAlter(string definition)
        {
            if (string.IsNullOrEmpty(definition)) return null;

            List<TsqlToken> tokens = TsqlLexer.Tokenize(definition);
            int create = NextCode(tokens, -1);
            if (create < 0 || !IsWord(definition, tokens[create], "CREATE")) return null;

            int replaceEnd = tokens[create].End;
            int or = NextCode(tokens, create);
            if (or >= 0 && IsWord(definition, tokens[or], "OR"))
            {
                int alter = NextCode(tokens, or);
                if (alter >= 0 && IsWord(definition, tokens[alter], "ALTER"))
                    replaceEnd = tokens[alter].End;
            }

            int start = tokens[create].Start;
            return definition.Substring(0, start) + "ALTER" + definition.Substring(replaceEnd);
        }

        /// <summary>
        /// Turns a CREATE script's batches into the ALTER script: the first batch whose first code
        /// token is CREATE is rewritten (<see cref="RewriteCreateToAlter"/>), everything else
        /// (SET options, the descriptive header comment) is kept. Returns null when no batch
        /// starts with CREATE.
        /// </summary>
        public static IReadOnlyList<string> RewriteCreateBatchToAlter(IReadOnlyList<string> batches)
        {
            if (batches == null) return null;
            var result = new List<string>(batches);
            for (int i = 0; i < result.Count; i++)
            {
                string rewritten = RewriteCreateToAlter(result[i]);
                if (rewritten == null) continue;
                result[i] = rewritten;
                return result;
            }
            return null;
        }

        /// <summary>
        /// Final script text: optional first-line comment, USE [database] like SSMS's own
        /// "Script as … New Query Editor Window", then each batch followed by GO.
        /// </summary>
        public static string Compose(string database, IEnumerable<string> batches, string leadingComment)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(leadingComment)) sb.Append(leadingComment).Append("\r\n");
            if (!string.IsNullOrEmpty(database))
                sb.Append("USE ").Append(SqlIdentifier.Bracket(database)).Append("\r\nGO\r\n");

            foreach (string batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                sb.Append(NormalizeNewLines(batch).Trim('\r', '\n').TrimEnd()).Append("\r\nGO\r\n");
            }
            return sb.ToString();
        }

        private static string NormalizeNewLines(string s) =>
            s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        private static int NextCode(List<TsqlToken> tokens, int after)
        {
            for (int i = after + 1; i < tokens.Count; i++)
            {
                switch (tokens[i].Kind)
                {
                    case TsqlTokenKind.Whitespace:
                    case TsqlTokenKind.LineComment:
                    case TsqlTokenKind.BlockComment:
                        continue;
                    default:
                        return i;
                }
            }
            return -1;
        }

        private static bool IsWord(string text, TsqlToken token, string word) =>
            token.Kind == TsqlTokenKind.Identifier
            && token.Length == word.Length
            && string.Compare(text, token.Start, word, 0, word.Length, System.StringComparison.OrdinalIgnoreCase) == 0;
    }
}
