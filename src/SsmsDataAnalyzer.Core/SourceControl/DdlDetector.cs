using System.Collections.Generic;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    public enum DdlAction { Create, Alter, CreateOrAlter, Drop }

    public sealed class DdlStatement
    {
        public DdlStatement(DdlAction action, ModuleRef target)
            : this(action, target, null)
        {
        }

        public DdlStatement(DdlAction action, ModuleRef target, string database)
        {
            Action = action;
            Target = target;
            Database = database;
        }

        public DdlAction Action { get; }
        public ModuleRef Target { get; }

        /// <summary>
        /// The database the statement itself names (<c>DROP TABLE OtherDb.dbo.Orders</c>), or null
        /// when it names none. Lead amendment to plan §13.2: without this the database part was
        /// dropped, and the change was attributed to whichever database the query window happened
        /// to be using — a false "dropped but still in repo" against the wrong project. The caller
        /// uses this over the session database whenever it is set.
        /// </summary>
        public string Database { get; }
    }

    /// <summary>
    /// Finds what executed T-SQL text changed: CREATE/ALTER/CREATE OR ALTER/DROP of a PROC,
    /// VIEW, FUNCTION, TRIGGER or TABLE. Lexer-based, not a parser -- good enough for Query
    /// History text, which is always a batch of real statements, never a fragment under edit.
    /// Never throws; anything it does not recognise is simply not returned.
    /// </summary>
    public static class DdlDetector
    {
        public static IReadOnlyList<DdlStatement> Find(string sqlText)
        {
            var result = new List<DdlStatement>();
            if (string.IsNullOrEmpty(sqlText)) return result;

            List<TsqlToken> sig = DdlScanning.Significant(sqlText);
            int i = 0;
            while (i < sig.Count)
            {
                if (sig[i].Kind != TsqlTokenKind.Identifier) { i++; continue; }
                string word = DdlScanning.TokenText(sqlText, sig[i]);

                if (DdlScanning.EqualsKeyword(word, "CREATE"))
                {
                    int j = i + 1;
                    DdlAction action = DdlAction.Create;
                    if (IsWord(sqlText, sig, j, "OR") && IsWord(sqlText, sig, j + 1, "ALTER"))
                    {
                        action = DdlAction.CreateOrAlter;
                        j += 2;
                    }
                    i = ConsumeSingleTarget(sqlText, sig, j, action, result, i);
                }
                else if (DdlScanning.EqualsKeyword(word, "ALTER"))
                {
                    i = ConsumeSingleTarget(sqlText, sig, i + 1, DdlAction.Alter, result, i);
                }
                else if (DdlScanning.EqualsKeyword(word, "DROP"))
                {
                    i = ConsumeDropTargets(sqlText, sig, i + 1, result, i);
                }
                else
                {
                    i++;
                }
            }
            return result;
        }

        private static bool IsWord(string text, List<TsqlToken> sig, int index, string word) =>
            index < sig.Count && sig[index].Kind == TsqlTokenKind.Identifier
            && DdlScanning.EqualsKeyword(DdlScanning.TokenText(text, sig[index]), word);

        /// <summary>The database part of a three-part name, else null.</summary>
        private static string DatabasePart(List<string> parts) =>
            parts != null && parts.Count == 3 ? parts[0] : null;

        private static int ConsumeSingleTarget(string text, List<TsqlToken> sig, int j, DdlAction action,
            List<DdlStatement> result, int fallback)
        {
            DbObjectKind? kind = DdlScanning.TryReadObjectKind(text, sig, ref j);
            if (!kind.HasValue) return fallback + 1;

            int nameStart = j;
            List<string> parts = DdlScanning.ReadNameParts(text, sig, ref j);
            ModuleRef target = parts.Count > 3 ? null : DdlScanning.BuildTarget(parts, kind.Value);
            if (target != null && !DdlScanning.IsTempOrVariable(text, sig, nameStart))
                result.Add(new DdlStatement(action, target, DatabasePart(parts)));

            return j > fallback ? j : fallback + 1;
        }

        private static int ConsumeDropTargets(string text, List<TsqlToken> sig, int j,
            List<DdlStatement> result, int fallback)
        {
            DbObjectKind? kind = DdlScanning.TryReadObjectKind(text, sig, ref j);
            if (!kind.HasValue) return fallback + 1;

            if (IsWord(text, sig, j, "IF") && IsWord(text, sig, j + 1, "EXISTS")) j += 2;

            bool any = false;
            while (true)
            {
                int nameStart = j;
                List<string> parts = DdlScanning.ReadNameParts(text, sig, ref j);
                if (parts.Count == 0) break;
                any = true;

                ModuleRef target = parts.Count > 3 ? null : DdlScanning.BuildTarget(parts, kind.Value);
                if (target != null && !DdlScanning.IsTempOrVariable(text, sig, nameStart))
                    result.Add(new DdlStatement(DdlAction.Drop, target, DatabasePart(parts)));

                if (j < sig.Count && sig[j].Kind == TsqlTokenKind.Other && DdlScanning.TokenText(text, sig[j]) == ",")
                {
                    j++;
                    continue;
                }
                break;
            }
            return any ? j : fallback + 1;
        }
    }
}
