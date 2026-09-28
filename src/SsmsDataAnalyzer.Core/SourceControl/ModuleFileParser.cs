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
