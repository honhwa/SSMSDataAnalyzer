using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.ResultShape
{
    /// <summary>What a FROM-clause entry is, as far as a static source lookup cares.</summary>
    public enum StaticFromKind
    {
        /// <summary>A plain [db.][schema.]name table reference. The name may still be a temp
        /// table (#/##), a table variable (@) or a CTE — <see cref="StaticShapeResolver"/>
        /// decides, not the parser adapter.</summary>
        NamedTable = 0,
        /// <summary>Derived table, TVF/APPLY of a function, OPENxxx, PIVOT/UNPIVOT output,
        /// table variable reference, … — an alias that exists but can never name a base table.</summary>
        Unresolvable
    }

    /// <summary>One FROM-clause entry, flattened out of the join tree.</summary>
    public sealed class StaticFromItem
    {
        public StaticFromItem(string alias, string database, string schema, string table, StaticFromKind kind)
        {
            Alias = alias;
            Database = database;
            Schema = schema;
            Table = table;
            Kind = kind;
        }

        /// <summary>Explicit alias, or null when the reference is used under its own name.</summary>
        public string Alias { get; }
        public string Database { get; }
        public string Schema { get; }
        /// <summary>Base name, unbracketed. Null for an <see cref="StaticFromKind.Unresolvable"/> entry.</summary>
        public string Table { get; }
        public StaticFromKind Kind { get; }

        public static StaticFromItem Unresolvable(string alias) =>
            new StaticFromItem(alias, null, null, null, StaticFromKind.Unresolvable);
    }

    /// <summary>One select-list entry, reduced to the only two things that matter: the name the
    /// column gets on screen and, when it is a plain column reference, its qualifier + column.</summary>
    public sealed class StaticSelectItem
    {
        public StaticSelectItem(string outputName, IReadOnlyList<string> qualifierParts, string columnName, bool isStar = false)
        {
            OutputName = outputName;
            QualifierParts = qualifierParts ?? new string[0];
            ColumnName = columnName;
            IsStar = isStar;
        }

        /// <summary>Explicit alias, else the referenced column's own name, else null
        /// (an unnamed expression — SSMS shows "(No column name)").</summary>
        public string OutputName { get; }
        /// <summary>Everything before the column name in a column reference, in order
        /// (["FA"], ["Finances","Accounting"], ["Db","Finances","Accounting"]). Empty when the
        /// reference is unqualified — which is ambiguous across joins, so it yields no source.</summary>
        public IReadOnlyList<string> QualifierParts { get; }
        /// <summary>Null when the entry is anything other than a plain column reference
        /// (literal, expression, function call, CASE, …) — that is "no source", not an error.</summary>
        public string ColumnName { get; }
        /// <summary><c>*</c> or <c>alias.*</c>. The column count is not knowable from the text
        /// alone, but it IS knowable from the catalog: see the star expander on
        /// <see cref="StaticShapeResolver.Resolve"/>. For <c>alias.*</c> the qualifier is kept
        /// in <see cref="QualifierParts"/>; a bare <c>*</c> has none.</summary>
        public bool IsStar { get; }

        public static StaticSelectItem Star(IReadOnlyList<string> qualifierParts = null) =>
            new StaticSelectItem(null, qualifierParts, null, true);
    }

    /// <summary>
    /// The parser-adapter output <see cref="StaticShapeResolver"/> works on: one statement's
    /// first query specification. Deliberately free of any ScriptDom type so the rules below
    /// are pure and unit-testable; the Vsix builds it with SSMS's own ScriptDom.
    /// </summary>
    public sealed class StaticQueryShape
    {
        public StaticQueryShape(
            IReadOnlyList<StaticSelectItem> selectItems,
            IReadOnlyList<StaticFromItem> fromItems,
            IReadOnlyList<string> cteNames,
            bool isSetOperation = false)
        {
            SelectItems = selectItems ?? throw new ArgumentNullException(nameof(selectItems));
            FromItems = fromItems ?? new StaticFromItem[0];
            CteNames = cteNames ?? new string[0];
            IsSetOperation = isSetOperation;
            Parsed = true;
        }

        private StaticQueryShape() { }

        /// <summary>False when the text could not be parsed into exactly one usable statement.</summary>
        public bool Parsed { get; private set; }
        public IReadOnlyList<StaticSelectItem> SelectItems { get; private set; } = new StaticSelectItem[0];
        public IReadOnlyList<StaticFromItem> FromItems { get; private set; } = new StaticFromItem[0];
        /// <summary>CTE names defined by this statement's WITH clause.</summary>
        public IReadOnlyList<string> CteNames { get; private set; } = new string[0];
        /// <summary>UNION / EXCEPT / INTERSECT: <see cref="SelectItems"/>/<see cref="FromItems"/>
        /// are the FIRST query specification's (SQL Server takes the names from it), but a row
        /// can come from any branch, so no column has a single source — exactly what
        /// sys.dm_exec_describe_first_result_set itself reports (verified: source_* NULL even
        /// when every branch reads the same table).</summary>
        public bool IsSetOperation { get; private set; }

        public static StaticQueryShape Unusable() => new StaticQueryShape();
    }

    /// <summary>One grid column's statically-derived source; <see cref="HasSource"/> false means
    /// "this column does not come from a single base table in the query text".</summary>
    public sealed class StaticColumnSource
    {
        public StaticColumnSource(string outputName, string database, string schema, string table, string column)
        {
            OutputName = outputName;
            Database = database;
            Schema = schema;
            Table = table;
            Column = column;
        }

        public string OutputName { get; }
        /// <summary>Null when the name was not three-part (use the connection's database).</summary>
        public string Database { get; }
        /// <summary>Null when the name was unqualified (let SQL Server's own name resolution decide).</summary>
        public string Schema { get; }
        public string Table { get; }
        public string Column { get; }

        public bool HasSource => Table != null && Column != null;

        public static StaticColumnSource NoSource(string outputName) =>
            new StaticColumnSource(outputName, null, null, null, null);
    }

    /// <summary>Outcome of <see cref="StaticShapeResolver.Resolve"/>.</summary>
    public sealed class StaticShapeResult
    {
        private StaticShapeResult() { }

        public bool IsMatch { get; private set; }
        /// <summary>Fixed text only (never query text, never a value) — safe to log.</summary>
        public string DeclineReason { get; private set; }
        /// <summary>One entry per grid column, in order. Empty on decline.</summary>
        public IReadOnlyList<StaticColumnSource> Columns { get; private set; } = new StaticColumnSource[0];

        internal static StaticShapeResult Decline(string reason) =>
            new StaticShapeResult { DeclineReason = reason };

        internal static StaticShapeResult Match(IReadOnlyList<StaticColumnSource> columns) =>
            new StaticShapeResult { IsMatch = true, Columns = columns };
    }

    /// <summary>
    /// LAST-RESORT source resolution from the query TEXT, for the one case
    /// sys.dm_exec_describe_first_result_set cannot serve: the query reads a session temp table
    /// (or anything else that will not compile on our own connection), so the describe call
    /// returns an error row and every column loses its source — even the columns that plainly
    /// come from real, permanent tables.
    ///
    /// This is only ever consulted when a candidate's describe ERRORED and NO candidate produced
    /// a full shape match (see the Vsix's StaticSourceResolution); a successful describe is never
    /// overridden. Because a wrong answer here opens the WRONG TABLE, every rule below declines
    /// rather than guesses:
    /// <list type="bullet">
    /// <item>the select list must give exactly one entry per grid column, with names matching
    /// 1:1 (<see cref="ResultShapeMatcher.NamesMatch"/>) — any <c>*</c> makes the count unknown,
    /// so the statement is unusable;</item>
    /// <item>a column only gets a source when it is a QUALIFIED reference to an alias that maps
    /// to exactly one real, non-temp, non-CTE, non-derived table — an unqualified column is
    /// ambiguous across joins and gets none;</item>
    /// <item>UNION / EXCEPT / INTERSECT: names from the first query specification, but no
    /// column has a source (a row may come from any branch — the DM agrees);</item>
    /// <item>anything else (expression, literal, function, an alias we could not resolve) is
    /// simply "no source" for that ordinal, which is a decline downstream, not a wrong table.</item>
    /// </list>
    /// </summary>
    public static class StaticShapeResolver
    {
        public const string NotParsedReason = "the query text could not be parsed into a single result-returning SELECT";
        public const string StarReason = "the select list uses * and its table's column list could not be read";
        public const string CountReason = "the select list does not have one entry per grid column";
        public const string NameReason = "the select list's column names do not match the grid's";

        /// <param name="gridColumnNames">Every grid column's header, in order. Required — without
        /// it there is nothing to match the select list against.</param>
        /// <param name="expandStar">
        /// Given the table a <c>*</c> stands for, returns that table's column names in catalog
        /// order, or null when they cannot be read. Optional: without it, any <c>*</c> declines
        /// exactly as before.
        ///
        /// Expansion does NOT weaken the safety rule. The expanded list still has to match the
        /// grid's headers one-for-one, by count and by name, before a single source is reported,
        /// so a stale, filtered or differently ordered expansion fails that check and declines
        /// rather than mislabelling a column.
        /// </param>
        public static StaticShapeResult Resolve(
            StaticQueryShape shape,
            IReadOnlyList<string> gridColumnNames,
            Func<StaticFromItem, IReadOnlyList<string>> expandStar = null)
        {
            if (gridColumnNames == null) throw new ArgumentNullException(nameof(gridColumnNames));
            if (shape == null || !shape.Parsed) return StaticShapeResult.Decline(NotParsedReason);

            var items = shape.SelectItems;
            if (items.Any(i => i.IsStar))
            {
                var expansion = ExpandStars(shape, expandStar);
                if (expansion == null) return StaticShapeResult.Decline(StarReason);
                if (expansion.HasUnknownBlock)
                    return ResolveAroundUnknownBlock(shape, gridColumnNames, expansion);
                items = expansion.Prefix;
            }

            if (items.Count != gridColumnNames.Count) return StaticShapeResult.Decline(CountReason);

            for (int i = 0; i < items.Count; i++)
            {
                if (!ResultShapeMatcher.NamesMatch(items[i].OutputName, gridColumnNames[i]))
                    return StaticShapeResult.Decline(NameReason);
            }

            var aliases = BuildAliasMap(shape);

            var columns = new StaticColumnSource[items.Count];
            for (int i = 0; i < items.Count; i++)
                columns[i] = shape.IsSetOperation
                    ? StaticColumnSource.NoSource(items[i].OutputName)
                    : ResolveOne(items[i], aliases);

            return StaticShapeResult.Match(columns);
        }

        /// <summary>The select list with every star expanded, split around at most one block of
        /// columns whose count cannot be known (see <see cref="ExpandStars"/>).</summary>
        private sealed class StarExpansion
        {
            public readonly List<StaticSelectItem> Prefix = new List<StaticSelectItem>();
            public readonly List<StaticSelectItem> Suffix = new List<StaticSelectItem>();
            public bool HasUnknownBlock;
        }

        /// <summary>
        /// Replaces every <c>*</c> / <c>alias.*</c> with one entry per real column, in catalog
        /// order, so a <c>SELECT FA.*, x, y</c> grid can still be traced back to its tables.
        ///
        /// A bare <c>*</c> across a join expands table by table in FROM-clause order — SQL
        /// Server's documented rule, so the order is not a guess. A table whose columns cannot
        /// be read (a <c>#temp</c> table, a table variable, a CTE, a derived table) becomes ONE
        /// block of unknown width. Field report: <c>SELECT 'x', * FROM Finances.[T] te JOIN
        /// #temp t ...</c> declined for every column, although everything before the #temp
        /// columns sits at positions that are certain.
        ///
        /// Returns null — declining the whole statement — when anything is uncertain: a set
        /// operation, a star whose qualifier names no known FROM item, a real table whose columns
        /// could not be read, a statement with nothing to expand, or MORE than one unknown block
        /// (with two, the position of the columns between them cannot be known).
        /// </summary>
        private static StarExpansion ExpandStars(
            StaticQueryShape shape, Func<StaticFromItem, IReadOnlyList<string>> expandStar)
        {
            if (expandStar == null) return null;

            // UNION/EXCEPT/INTERSECT report no sources at all further down, so expanding here
            // would only invent entries nobody can use.
            if (shape.IsSetOperation) return null;

            var aliases = BuildAliasMap(shape);
            var cteNames = new HashSet<string>(shape.CteNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var expansion = new StarExpansion();

            foreach (var item in shape.SelectItems)
            {
                if (!item.IsStar)
                {
                    (expansion.HasUnknownBlock ? expansion.Suffix : expansion.Prefix).Add(item);
                    continue;
                }

                var targets = TargetsFor(item, shape, aliases, cteNames);
                if (targets == null) return null;

                foreach (var target in targets)
                {
                    if (target.IsUnknownWidth)
                    {
                        if (expansion.HasUnknownBlock) return null; // a second one: positions unknowable
                        expansion.HasUnknownBlock = true;
                        continue;
                    }

                    IReadOnlyList<string> columnNames = expandStar(target.Table);
                    if (columnNames == null || columnNames.Count == 0) return null;

                    var into = expansion.HasUnknownBlock ? expansion.Suffix : expansion.Prefix;
                    foreach (string columnName in columnNames)
                    {
                        if (string.IsNullOrEmpty(columnName)) return null;
                        into.Add(new StaticSelectItem(columnName, target.Qualifier, columnName));
                    }
                }
            }

            // Nothing but an unknown block: nothing to verify and nothing to report.
            if (expansion.HasUnknownBlock && expansion.Prefix.Count == 0 && expansion.Suffix.Count == 0) return null;

            return expansion;
        }

        /// <summary>
        /// Columns before the unknown block are matched to the grid from the LEFT, columns after
        /// it from the RIGHT, and the grid columns in between — the #temp table's — get no source.
        /// The rule that makes the static path safe is unchanged: every column that gets a source
        /// must match its grid header by name, at a position that is certain.
        /// </summary>
        private static StaticShapeResult ResolveAroundUnknownBlock(
            StaticQueryShape shape, IReadOnlyList<string> grid, StarExpansion expansion)
        {
            int prefix = expansion.Prefix.Count;
            int suffix = expansion.Suffix.Count;
            int unknownWidth = grid.Count - prefix - suffix;
            if (unknownWidth < 1) return StaticShapeResult.Decline(CountReason);

            for (int i = 0; i < prefix; i++)
                if (!ResultShapeMatcher.NamesMatch(expansion.Prefix[i].OutputName, grid[i]))
                    return StaticShapeResult.Decline(NameReason);

            for (int k = 0; k < suffix; k++)
                if (!ResultShapeMatcher.NamesMatch(expansion.Suffix[k].OutputName, grid[prefix + unknownWidth + k]))
                    return StaticShapeResult.Decline(NameReason);

            var aliases = BuildAliasMap(shape);
            var columns = new StaticColumnSource[grid.Count];
            for (int i = 0; i < prefix; i++)
                columns[i] = ResolveOne(expansion.Prefix[i], aliases);
            for (int u = 0; u < unknownWidth; u++)
                columns[prefix + u] = StaticColumnSource.NoSource(grid[prefix + u]);
            for (int k = 0; k < suffix; k++)
                columns[prefix + unknownWidth + k] = ResolveOne(expansion.Suffix[k], aliases);

            return StaticShapeResult.Match(columns);
        }

        /// <summary>A star and the table it stands for, in select-list order.</summary>
        public sealed class StarTarget
        {
            public StarTarget(StaticFromItem table, IReadOnlyList<string> qualifier, bool isUnknownWidth = false)
            {
                Table = table;
                Qualifier = qualifier;
            IsUnknownWidth = isUnknownWidth;
            }

            public StaticFromItem Table { get; }
            /// <summary>What to qualify the expanded columns with, so they resolve back to this
            /// same table through the ordinary alias rules.</summary>
            public IReadOnlyList<string> Qualifier { get; }

            /// <summary>A #temp table, table variable, CTE or derived table: its columns cannot be read
            /// from the catalog, so it expands to a block of unknown width (never loaded).</summary>
            public bool IsUnknownWidth { get; }
        }

        /// <summary>
        /// The tables whose column lists a caller must read before <see cref="Resolve"/> can
        /// expand this shape's stars, in select-list order, or null when any star cannot be tied
        /// to exactly one real base table (in which case the shape is not statically usable).
        /// An empty list means there are no stars.
        /// </summary>
        public static IReadOnlyList<StarTarget> ResolveStarTargets(StaticQueryShape shape)
        {
            if (shape == null || !shape.Parsed) return null;
            if (shape.IsSetOperation && shape.SelectItems.Any(i => i.IsStar)) return null;

            var aliases = BuildAliasMap(shape);
            var cteNames = new HashSet<string>(shape.CteNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var all = new List<StarTarget>();

            foreach (var item in shape.SelectItems)
            {
                if (!item.IsStar) continue;
                var targets = TargetsFor(item, shape, aliases, cteNames);
                if (targets == null) return null;
                all.AddRange(targets);
            }

            return all;
        }

        /// <summary>What one star expands to, in order: one target for <c>alias.*</c>, one per
        /// FROM item (FROM-clause order) for a bare <c>*</c>. Null when a qualifier names no known
        /// FROM item or names one ambiguously.</summary>
        private static List<StarTarget> TargetsFor(StaticSelectItem item, StaticQueryShape shape,
            Dictionary<string, StaticFromItem> aliases, HashSet<string> cteNames)
        {
            IReadOnlyList<string> qualifier = item.QualifierParts;

            if (qualifier.Count == 0)
            {
                if (shape.FromItems.Count == 0) return null;

                var targets = new List<StarTarget>();
                foreach (var from in shape.FromItems)
                {
                    string key = from.Alias ?? from.Table;
                    if (!IsRealTable(from, cteNames))
                    {
                        targets.Add(new StarTarget(from, key == null ? new string[0] : new[] { key }, isUnknownWidth: true));
                        continue;
                    }
                    if (string.IsNullOrEmpty(key)) return null;
                    targets.Add(new StarTarget(from, new[] { key }));
                }
                return targets;
            }

            if (qualifier.Count > 3) return null;
            string last = qualifier[qualifier.Count - 1];

            // alias.* on a known FROM item that is not a real table (#temp, table variable, CTE,
            // derived): the alias map records those as null. Unknown width, never loaded.
            if (qualifier.Count == 1 && aliases.TryGetValue(last, out StaticFromItem mapped) && mapped == null)
            {
                var nonReal = shape.FromItems.First(f =>
                    string.Equals(f.Alias ?? f.Table, last, StringComparison.OrdinalIgnoreCase));
                return new List<StarTarget> { new StarTarget(nonReal, qualifier, isUnknownWidth: true) };
            }

            if (!aliases.TryGetValue(last, out StaticFromItem table) || table == null) return null;

            if (qualifier.Count >= 2)
            {
                if (table.Alias != null) return null;
                if (!EqualsName(table.Schema, qualifier[qualifier.Count - 2])) return null;
                if (qualifier.Count == 3 && !EqualsName(table.Database, qualifier[0])) return null;
            }

            return new List<StarTarget> { new StarTarget(table, qualifier) };
        }

        /// <summary>alias (or bare table name when there is no alias) → the real table it names,
        /// or null for "known but unresolvable". A name declared twice is removed entirely:
        /// ambiguous, so every column using it loses its source.</summary>
        private static Dictionary<string, StaticFromItem> BuildAliasMap(StaticQueryShape shape)
        {
            var cteNames = new HashSet<string>(shape.CteNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, StaticFromItem>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var from in shape.FromItems)
            {
                string key = from.Alias ?? from.Table;
                if (string.IsNullOrEmpty(key)) continue;

                if (map.ContainsKey(key)) { ambiguous.Add(key); continue; }
                map[key] = IsRealTable(from, cteNames) ? from : null;
            }

            foreach (var key in ambiguous) map.Remove(key);
            return map;
        }

        private static bool IsRealTable(StaticFromItem from, HashSet<string> cteNames)
        {
            if (from.Kind != StaticFromKind.NamedTable) return false;
            if (string.IsNullOrEmpty(from.Table)) return false;

            char first = from.Table[0];
            // #temp, ##global temp, @table variable — none of them live in sys.objects.
            if (first == '#' || first == '@') return false;

            // A one-part name that a WITH clause in this very statement defines.
            if (from.Schema == null && from.Database == null && cteNames.Contains(from.Table)) return false;

            return true;
        }

        private static StaticColumnSource ResolveOne(StaticSelectItem item, Dictionary<string, StaticFromItem> aliases)
        {
            // Not a plain column reference, or unqualified (ambiguous across joins).
            if (item.ColumnName == null) return StaticColumnSource.NoSource(item.OutputName);

            var parts = item.QualifierParts;
            if (parts.Count == 0 || parts.Count > 3) return StaticColumnSource.NoSource(item.OutputName);

            string key = parts[parts.Count - 1];
            if (!aliases.TryGetValue(key, out var from) || from == null)
                return StaticColumnSource.NoSource(item.OutputName);

            // A multi-part qualifier only names this table if the extra parts agree with it.
            if (parts.Count >= 2)
            {
                if (from.Alias != null) return StaticColumnSource.NoSource(item.OutputName);
                if (!EqualsName(from.Schema, parts[parts.Count - 2])) return StaticColumnSource.NoSource(item.OutputName);
                if (parts.Count == 3 && !EqualsName(from.Database, parts[0])) return StaticColumnSource.NoSource(item.OutputName);
            }

            return new StaticColumnSource(item.OutputName, from.Database, from.Schema, from.Table, item.ColumnName);
        }

        private static bool EqualsName(string declared, string used) =>
            declared != null && string.Equals(declared, used, StringComparison.OrdinalIgnoreCase);
    }
}
