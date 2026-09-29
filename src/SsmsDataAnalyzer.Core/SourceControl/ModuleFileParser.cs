using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// Identifies which object a repository <c>.sql</c> file defines: the target of the first
    /// CREATE/ALTER (CREATE OR ALTER included) of a recognised kind. Handles a leading BOM, an
    /// author header comment and <c>SET</c> lines before <c>CREATE</c> simply by ignoring
    /// anything that is not that first CREATE/ALTER -- a file whose only DDL is a guard
    /// <c>DROP ... IF EXISTS</c> ahead of the real <c>CREATE</c> still resolves to the CREATE.
    /// </summary>
    public static class ModuleFileParser
    {
        /// <summary>null = defines nothing we recognise.</summary>
        public static ModuleRef TryIdentify(string fileText)
        {
            if (string.IsNullOrEmpty(fileText)) return null;

            List<TsqlToken> sig = DdlScanning.Significant(fileText);
            int i = 0;
            while (i < sig.Count)
            {
                if (sig[i].Kind != TsqlTokenKind.Identifier) { i++; continue; }
                string word = DdlScanning.TokenText(fileText, sig[i]);

                if (DdlScanning.EqualsKeyword(word, "CREATE"))
                {
                    int j = i + 1;
                    if (IsWord(fileText, sig, j, "OR") && IsWord(fileText, sig, j + 1, "ALTER")) j += 2;

                    ModuleRef target = TryConsumeTarget(fileText, sig, j);
                    if (target != null) return target;
                    i++;
                }
                else if (DdlScanning.EqualsKeyword(word, "ALTER"))
                {
                    ModuleRef target = TryConsumeTarget(fileText, sig, i + 1);
                    if (target != null) return target;
                    i++;
                }
                else
                {
                    i++;
                }
            }
            return null;
        }

        /// <summary>
        /// Every object the file defines, one per <c>GO</c> batch, each with its OWN batch text.
        ///
        /// Lead amendment to plan §13.3/§13.5 (2026-09-29, field report): SSDT puts DML triggers
        /// in their table's file — <c>DebtLedger\Tables\Debt.sql</c> holds the table, its
        /// indexes and two triggers, each after a <c>GO</c>. Identifying only the first object
        /// per file indexed the table and nothing else, so every changed trigger in the
        /// repository was reported "missing from repo". Each object also gets its own batch as
        /// its text, so a trigger is compared with the trigger and not with the whole table file.
        ///
        /// Batches split on a <c>GO</c> that stands alone on its line (optionally followed by a
        /// repeat count or a line comment). The lexer has already classified strings and
        /// comments, so a <c>GO</c> inside either can never split a batch.
        /// </summary>
        public static IReadOnlyList<(ModuleRef Module, string Text)> IdentifyAll(string fileText)
        {
            var result = new List<(ModuleRef, string)>();
            if (string.IsNullOrEmpty(fileText)) return result;

            foreach (string batch in SplitBatches(fileText))
            {
                ModuleRef module = TryIdentify(batch);
                if (module != null) result.Add((module, batch));
            }
            return result;
        }

        /// <summary>The file's GO-separated batches, in order, separators removed.</summary>
        internal static List<string> SplitBatches(string text)
        {
            var batches = new List<string>();
            List<TsqlToken> tokens = TsqlLexer.Tokenize(text);
            int batchStart = 0;

            foreach (TsqlToken token in tokens)
            {
                if (token.Kind != TsqlTokenKind.Identifier) continue;
                if (!DdlScanning.EqualsKeyword(text.Substring(token.Start, token.Length), "GO")) continue;
                if (!StandsAloneOnItsLine(text, token)) continue;

                int lineStart = LineStart(text, token.Start);
                batches.Add(text.Substring(batchStart, lineStart - batchStart));
                batchStart = LineEnd(text, token.End);
            }

            if (batchStart < text.Length) batches.Add(text.Substring(batchStart));
            return batches;
        }

        /// <summary>Only whitespace before GO on its line; after it, only whitespace, an optional
        /// repeat count and an optional line comment. "GOTO", "GOOD" or "GO" mid-statement never
        /// qualify: they are different tokens, or share their line with other code.</summary>
        private static bool StandsAloneOnItsLine(string text, TsqlToken token)
        {
            for (int k = LineStart(text, token.Start); k < token.Start; k++)
                if (!char.IsWhiteSpace(text[k])) return false;

            int end = LineEnd(text, token.End);
            int m = token.End;
            while (m < end && (text[m] == ' ' || text[m] == '\t')) m++;
            while (m < end && char.IsDigit(text[m])) m++;
            while (m < end && char.IsWhiteSpace(text[m])) m++;
            return m >= end || (m + 1 < end && text[m] == '-' && text[m + 1] == '-');
        }

        private static int LineStart(string text, int index)
        {
            int k = index;
            while (k > 0 && text[k - 1] != '\n') k--;
            return k;
        }

        /// <summary>Index just past the line terminator of the line containing index-1.</summary>
        private static int LineEnd(string text, int index)
        {
            int k = index;
            while (k < text.Length && text[k] != '\n') k++;
            return k < text.Length ? k + 1 : k;
        }

        private static bool IsWord(string text, List<TsqlToken> sig, int index, string word) =>
            index < sig.Count && sig[index].Kind == TsqlTokenKind.Identifier
            && DdlScanning.EqualsKeyword(DdlScanning.TokenText(text, sig[index]), word);

        private static ModuleRef TryConsumeTarget(string text, List<TsqlToken> sig, int j)
        {
            DbObjectKind? kind = DdlScanning.TryReadObjectKind(text, sig, ref j);
            if (!kind.HasValue) return null;

            int nameStart = j;
            List<string> parts = DdlScanning.ReadNameParts(text, sig, ref j);
            ModuleRef target = DdlScanning.BuildTarget(parts, kind.Value);
            if (target == null || DdlScanning.IsTempOrVariable(text, sig, nameStart)) return null;
            return target;
        }
    }
}
