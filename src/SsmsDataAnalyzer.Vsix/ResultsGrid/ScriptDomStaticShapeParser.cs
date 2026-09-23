using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SsmsDataAnalyzer.Core.ResultShape;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// The ScriptDom half of the static (query-text) source fallback: turns one describe
    /// candidate's text into Core's parser-free <see cref="StaticQueryShape"/>, which
    /// <see cref="StaticShapeResolver"/> then applies its rules to. No ScriptDom type ever
    /// crosses into Core.
    ///
    /// Same shell/core JIT guard as <see cref="ScriptDomStatementParser"/>: <see cref="Parse"/>
    /// references no ScriptDom type, so a missing/changed assembly surfaces inside its
    /// try/catch. Any failure yields <see cref="StaticQueryShape.Unusable"/>, i.e. no static
    /// resolution — never a guess. Never logs query text.
    /// </summary>
    internal static class ScriptDomStaticShapeParser
    {
        private static volatile bool _unavailable;

        public static StaticQueryShape Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (_unavailable) return StaticQueryShape.Unusable();

            try
            {
                return ParseCore(text) ?? StaticQueryShape.Unusable();
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException ||
                                       ex is TypeLoadException || ex is MissingMethodException || ex is MissingFieldException)
            {
                _unavailable = true;
                OeDiagnostics.Warn("Go to source: static source resolution unavailable in this SSMS build (" + ex.GetType().Name + ").");
                return StaticQueryShape.Unusable();
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Go to source: static source resolution failed to parse the query (" + ex.GetType().Name + ").");
                return StaticQueryShape.Unusable();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static StaticQueryShape ParseCore(string text)
        {
            var parser = new TSql180Parser(true);
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(text))
                fragment = parser.Parse(reader, out errors);

            if (errors != null && errors.Count > 0) return null;
            if (!(fragment is TSqlScript script) || script.Batches.Count != 1) return null;

            // A USE / EXECUTE AS / SETUSER / REVERT anywhere would make the text's unqualified
            // names resolve in a context our catalog lookup does not share.
            var counter = new ContextChangeCounter();
            script.Accept(counter);
            if (counter.Count > 0) return null;

            // Exactly one result-returning SELECT, and every other top-level statement one that
            // cannot return a result set of its own — otherwise we cannot tell which statement
            // made the grid (an EXEC's result could share the SELECT's column names).
            SelectStatement select = null;
            foreach (var statement in script.Batches[0].Statements)
            {
                if (statement is SelectStatement s && s.Into == null && !IsVariableAssignment(s))
                {
                    if (select != null) return null;
                    select = s;
                    continue;
                }
                if (!CannotReturnResultSet(statement)) return null;
            }
            if (select == null) return null;

            // Set operations take their column names from the FIRST query specification.
            var spec = FirstQuerySpecification(select.QueryExpression);
            if (spec == null) return null;
            bool isSetOperation = !(UnwrapParentheses(select.QueryExpression) is QuerySpecification);

            var cteNames = select.WithCtesAndXmlNamespaces?.CommonTableExpressions
                ?.Select(c => c.ExpressionName?.Value)
                .Where(n => n != null)
                .ToList() ?? new List<string>();

            var items = new List<StaticSelectItem>(spec.SelectElements.Count);
            foreach (var element in spec.SelectElements)
            {
                switch (element)
                {
                    case SelectStarExpression star:
                        // Keep the qualifier: "FA.*" can be expanded from the catalog later,
                        // and that is the single most common shape this fallback has to handle.
                        items.Add(StaticSelectItem.Star(
                            star.Qualifier?.Identifiers?.Select(i => i.Value).ToList()));
                        break;
                    case SelectScalarExpression scalar:
                        items.Add(ToSelectItem(scalar));
                        break;
                    default:
                        // SelectSetVariable and anything future: unknown shape, so refuse.
                        return null;
                }
            }

            var fromItems = new List<StaticFromItem>();
            if (spec.FromClause != null)
            {
                foreach (var reference in spec.FromClause.TableReferences)
                    CollectFrom(reference, fromItems);
            }

            return new StaticQueryShape(items, fromItems, cteNames, isSetOperation);
        }

        /// <summary>Allow-list: statements that never send a result set to the client. Anything
        /// else (EXEC, IF/WHILE/BEGIN…END/TRY blocks that could hide a SELECT, DML with OUTPUT,
        /// SET STATISTICS …) makes the text unusable statically.</summary>
        private static bool CannotReturnResultSet(TSqlStatement statement)
        {
            switch (statement)
            {
                case DeclareVariableStatement _:
                case DeclareTableVariableStatement _:
                case SetVariableStatement _:
                case PredicateSetStatement _:
                case SetRowCountStatement _:
                case SetTransactionIsolationLevelStatement _:
                case CreateTableStatement _:
                case DropTableStatement _:
                case TruncateTableStatement _:
                    return true;
                case SelectStatement s:
                    return s.Into != null || IsVariableAssignment(s);
                case InsertStatement insert:
                    return insert.InsertSpecification?.OutputClause == null;
                case UpdateStatement update:
                    return update.UpdateSpecification?.OutputClause == null;
                case DeleteStatement delete:
                    return delete.DeleteSpecification?.OutputClause == null;
                default:
                    return false;
            }
        }

        private static QueryExpression UnwrapParentheses(QueryExpression expression)
        {
            while (expression is QueryParenthesisExpression paren) expression = paren.QueryExpression;
            return expression;
        }

        private sealed class ContextChangeCounter : TSqlFragmentVisitor
        {
            public int Count;
            public override void Visit(UseStatement node) => Count++;
            public override void Visit(ExecuteAsStatement node) => Count++;
            public override void Visit(RevertStatement node) => Count++;
            public override void Visit(SetUserStatement node) => Count++;
        }

        private static bool IsVariableAssignment(SelectStatement select) =>
            select.QueryExpression is QuerySpecification spec &&
            spec.SelectElements.Count > 0 &&
            spec.SelectElements[0] is SelectSetVariable;

        private static QuerySpecification FirstQuerySpecification(QueryExpression expression)
        {
            switch (expression)
            {
                case QuerySpecification spec: return spec;
                case QueryParenthesisExpression paren: return FirstQuerySpecification(paren.QueryExpression);
                case BinaryQueryExpression binary: return FirstQuerySpecification(binary.FirstQueryExpression);
                default: return null;
            }
        }

        private static StaticSelectItem ToSelectItem(SelectScalarExpression scalar)
        {
            string alias = scalar.ColumnName?.Value;

            // Only a PLAIN column reference gives a source; everything else is "no source"
            // for that ordinal but still one column with its output name.
            if (scalar.Expression is ColumnReferenceExpression column &&
                column.ColumnType == ColumnType.Regular &&
                column.MultiPartIdentifier?.Identifiers != null &&
                column.MultiPartIdentifier.Identifiers.Count >= 1)
            {
                var ids = column.MultiPartIdentifier.Identifiers;
                string columnName = ids[ids.Count - 1].Value;
                var qualifiers = ids.Take(ids.Count - 1).Select(i => i.Value).ToList();
                return new StaticSelectItem(alias ?? columnName, qualifiers, columnName);
            }

            return new StaticSelectItem(alias, null, null);
        }

        private static void CollectFrom(TableReference reference, List<StaticFromItem> into)
        {
            switch (reference)
            {
                case QualifiedJoin join:
                    CollectFrom(join.FirstTableReference, into);
                    CollectFrom(join.SecondTableReference, into);
                    return;
                case UnqualifiedJoin unqualified:
                    // CROSS/OUTER APPLY lands here too; the applied side becomes unresolvable.
                    CollectFrom(unqualified.FirstTableReference, into);
                    CollectFrom(unqualified.SecondTableReference, into);
                    return;
                case JoinParenthesisTableReference paren:
                    CollectFrom(paren.Join, into);
                    return;
                case NamedTableReference named:
                    var name = named.SchemaObject;
                    string table = name?.BaseIdentifier?.Value;
                    if (table == null) { into.Add(StaticFromItem.Unresolvable(named.Alias?.Value)); return; }
                    // A 4-part (linked server) name is out of scope for a catalog lookup.
                    if (name.ServerIdentifier != null) { into.Add(StaticFromItem.Unresolvable(named.Alias?.Value)); return; }
                    into.Add(new StaticFromItem(
                        named.Alias?.Value,
                        name.DatabaseIdentifier?.Value,
                        name.SchemaIdentifier?.Value,
                        table,
                        StaticFromKind.NamedTable));
                    return;
                case TableReferenceWithAlias withAlias:
                    // Derived table, TVF, OPENxxx, table variable, PIVOT/UNPIVOT output, …
                    into.Add(StaticFromItem.Unresolvable(withAlias.Alias?.Value));
                    return;
                default:
                    // Unknown/aliasless reference: recording nothing simply means no alias of
                    // ours resolves through it, which is the safe outcome.
                    return;
            }
        }
    }
}
