using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.ResultShape;
using SsmsDataAnalyzer.Core.Sql;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// LAST-RESORT stage between "the describe call errored" and "no links at all".
    ///
    /// sys.dm_exec_describe_first_result_set compiles the query on OUR connection, which has
    /// none of the user's session objects — a query joining a #temp table therefore comes back
    /// as a single error row (208 Invalid object name '#…'), and every column of that grid loses
    /// its source, including the ones that plainly come from permanent tables.
    ///
    /// When (and only when) a candidate errored AND nothing shape-matched, this reads the source
    /// out of the query TEXT with <see cref="ScriptDomStaticShapeParser"/> +
    /// <see cref="StaticShapeResolver"/>, then fills in the type metadata downstream needs
    /// (SqlLiteralFormatter) with ONE sys.columns lookup per distinct (database, schema, table).
    /// A column the catalog does not confirm gets NO source — never a guess. A successful
    /// describe is never replaced or overridden.
    ///
    /// Unqualified schema names are resolved by SQL Server itself (OBJECT_ID on the same
    /// connection the editor uses), so "the default schema, then dbo" is its rule, not ours;
    /// a name that does not resolve there yields no source.
    /// </summary>
    internal static class StaticSourceResolution
    {
        /// <summary>Only user tables — a view/synonym/TVF column could never carry a
        /// declared FK we could follow (and a view column may be an expression).</summary>
        private const string ColumnsSql = @"
DECLARE @id int = OBJECT_ID(@name);
SELECT DB_NAME() AS database_name, SCHEMA_NAME(o.schema_id) AS schema_name, o.name AS table_name,
       c.name AS column_name, t.name AS type_name, c.max_length
FROM sys.objects AS o
JOIN sys.columns AS c ON c.object_id = o.object_id
LEFT JOIN sys.types AS t ON t.system_type_id = c.system_type_id AND t.user_type_id = t.system_type_id
WHERE o.object_id = @id AND o.type = 'U'
ORDER BY c.column_id;";

        /// <summary>The note is a suffix on an already-complete status message: keep SQL
        /// Server's text short (the full error is in the pre-static decline anyway).</summary>
        private const int NoteMessageLength = 100;

        private sealed class TableColumns
        {
            public string Database;
            public string Schema;
            public string Table;
            /// <summary>Column name (case-insensitive) → every catalog row with that name
            /// (more than one only in a case-sensitive database).</summary>
            public readonly Dictionary<string, List<DescribedColumn>> Columns =
                new Dictionary<string, List<DescribedColumn>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Column names in catalog (column_id) order -- what "SELECT alias.*"
            /// expands to. Order is the whole point here, so this is a list, not the dictionary
            /// above.</summary>
            public readonly List<string> Ordered = new List<string>();

            /// <summary>The exact-case column if there is one, else the only case-insensitive
            /// match; null when absent or ambiguous (never pick one of two).</summary>
            public DescribedColumn Find(string name)
            {
                if (!Columns.TryGetValue(name, out var list)) return null;
                var exact = list.Where(c => string.Equals(c.SourceColumn, name, StringComparison.Ordinal)).ToList();
                if (exact.Count == 1) return exact[0];
                return list.Count == 1 ? list[0] : null;
            }
        }

        /// <summary>
        /// Returns a static <see cref="ShapeMatch"/>, or null when static resolution does not
        /// apply (no describe error, no grid column names, nothing parsed/matched, no column
        /// resolved). Never throws for a resolution failure; cancellation propagates.
        /// </summary>
        public static async Task<ShapeMatch> TryResolveAsync(
            CandidateSet candidateSet,
            IReadOnlyList<IReadOnlyList<DescribedColumn>> described,
            IReadOnlyList<string> gridColumnNames,
            Func<string, string> buildConnectionStringForDatabase,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            // Rule 2 needs the grid's full header list; a degraded caller gets no static answer.
            if (gridColumnNames == null || gridColumnNames.Count == 0) return null;
            if (buildConnectionStringForDatabase == null) return null;

            var candidates = candidateSet.Candidates;
            // The error quoted to the user is the first one of a candidate we actually read
            // statically — that is the statement SQL Server "couldn't describe".
            DescribedColumn firstError = null;
            var staticMatches = new List<(DescribeCandidate Candidate, IReadOnlyList<StaticColumnSource> Columns)>();

            // Tables already read for star expansion, shared across candidates and reused as the
            // seed of the catalog load below, so no table is fetched twice.
            var starColumns = new Dictionary<string, TableColumns>(StringComparer.Ordinal);

            for (int i = 0; i < candidates.Count; i++)
            {
                // Rule 1: only a candidate whose describe returned an error row.
                var errorRow = (described[i] ?? new DescribedColumn[0]).FirstOrDefault(r => r.ErrorNumber != null);
                if (errorRow == null) continue;

                var shape = ScriptDomStaticShapeParser.Parse(candidates[i].Text);

                // "SELECT FA.*, x, y" is the commonest shape a #temp query takes, and the text
                // alone cannot say how many columns FA.* is. The catalog can: read the table's
                // columns in declaration order and expand. This never loosens the safety rule --
                // the expanded list still has to match the grid's headers one-for-one below, so
                // a table that has changed since the query ran fails that check and declines.
                var expander = await BuildStarExpanderAsync(
                    shape, starColumns, buildConnectionStringForDatabase, timeoutSeconds, cancellationToken).ConfigureAwait(true);

                var result = StaticShapeResolver.Resolve(shape, gridColumnNames, expander);
                if (!result.IsMatch) continue;

                staticMatches.Add((candidates[i], result.Columns));
                if (firstError == null) firstError = errorRow;
            }

            if (staticMatches.Count == 0) return null;

            var catalog = await LoadCatalogAsync(
                staticMatches.SelectMany(m => m.Columns).Where(c => c.HasSource).ToList(),
                buildConnectionStringForDatabase, timeoutSeconds, cancellationToken, starColumns).ConfigureAwait(true);

            var matched = new List<MatchedBatch>();
            bool anySource = false;
            foreach (var m in staticMatches)
            {
                var rows = new List<DescribedColumn>(m.Columns.Count);
                for (int ord = 1; ord <= m.Columns.Count; ord++)
                {
                    var source = m.Columns[ord - 1];
                    var row = BuildRow(ord, source, catalog);
                    if (row.SourceTable != null) anySource = true;
                    rows.Add(row);
                }
                matched.Add(new MatchedBatch(m.Candidate.BatchIndex, rows, m.Candidate.StatementNumber));
            }

            // Nothing at all resolved: a static "match" with no sources would only replace one
            // decline message with a worse one.
            if (!anySource) return null;

            string note = " (resolved from the query text — SQL Server couldn't describe it: "
                + ResultShapeMatcher.DescribeError(firstError, NoteMessageLength) + ")";
            return ResultShapeMatcher.MatchedStatic(matched, note);
        }

        private static DescribedColumn BuildRow(int ordinal, StaticColumnSource source, IReadOnlyDictionary<string, TableColumns> catalog)
        {
            var row = new DescribedColumn
            {
                Ordinal = ordinal,
                Name = source.OutputName,
                IsHidden = false,
                IsStatic = true
            };

            if (!source.HasSource) return row;
            if (!catalog.TryGetValue(TableKey(source), out var table)) return row;
            var catalogColumn = table.Find(source.Column);
            if (catalogColumn == null) return row;

            // Canonical names straight from the catalog, so the FK lookup and any later
            // comparison see exactly what the DM would have produced.
            row.SourceDatabase = table.Database;
            row.SourceSchema = table.Schema;
            row.SourceTable = table.Table;
            row.SourceColumn = catalogColumn.SourceColumn;
            row.SystemTypeName = catalogColumn.SystemTypeName;
            row.MaxLength = catalogColumn.MaxLength;
            return row;
        }

        /// <summary>One sys.columns lookup per distinct (database, schema, table) written in the
        /// query text, grouped into one connection per database. A table that cannot be read is
        /// simply absent from the result (its columns then have no source).</summary>
        private static async Task<IReadOnlyDictionary<string, TableColumns>> LoadCatalogAsync(
            IReadOnlyList<StaticColumnSource> sources,
            Func<string, string> buildConnectionStringForDatabase,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, TableColumns> alreadyLoaded = null)
        {
            var catalog = new Dictionary<string, TableColumns>(StringComparer.Ordinal);
            if (alreadyLoaded != null)
            {
                foreach (var pair in alreadyLoaded) catalog[pair.Key] = pair.Value;
            }

            var byDatabase = sources
                .GroupBy(s => s.Database ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            foreach (var dbGroup in byDatabase)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string database = dbGroup.First().Database;
                string connectionString = buildConnectionStringForDatabase(database);
                if (connectionString == null)
                {
                    OeDiagnostics.Warn("Go to source: static source resolution could not build a connection for a source database.");
                    continue;
                }

                try
                {
                    using (var connection = new SqlConnection(connectionString))
                    {
                        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);

                        // Distinct (schema, table) — ORDINAL keys, so two tables differing only
                        // by case in a case-sensitive database are never conflated.
                        var tables = dbGroup
                            .GroupBy(TableKey, StringComparer.Ordinal)
                            .Select(g => (Key: g.Key, Source: g.First()));

                        foreach (var table in tables)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (catalog.ContainsKey(table.Key)) continue; // already read for star expansion
                            var loaded = await LoadTableAsync(connection, table.Source, timeoutSeconds, cancellationToken).ConfigureAwait(true);
                            if (loaded != null) catalog[table.Key] = loaded;
                        }
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !(ex is OperationCanceledException))
                {
                    // Query text can appear in SQL error messages — log the type only.
                    OeDiagnostics.Warn("Go to source: static source resolution could not read the catalog (" + ex.GetType().Name + ").");
                }
            }

            return catalog;
        }

        private static async Task<TableColumns> LoadTableAsync(
            SqlConnection connection, StaticColumnSource source, int timeoutSeconds, CancellationToken cancellationToken)
        {
            // The connection is already in the right database, so a two-part name is enough —
            // and an unqualified one lets SQL Server apply the caller's own default schema.
            string name = source.Schema == null
                ? SqlIdentifier.Bracket(source.Table)
                : SqlIdentifier.Bracket(source.Schema) + "." + SqlIdentifier.Bracket(source.Table);

            using (var command = new SqlCommand(ColumnsSql, connection))
            {
                command.CommandTimeout = timeoutSeconds;
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 4000) { Value = name });

                TableColumns result = null;
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(true))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
                    {
                        if (result == null)
                        {
                            result = new TableColumns
                            {
                                Database = reader.GetString(0),
                                Schema = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(1),
                                Table = reader.GetString(2)
                            };
                        }

                        string columnName = reader.GetString(3);
                        result.Ordered.Add(columnName);
                        // A type we cannot name is a type SqlLiteralFormatter would decline
                        // anyway; keep the column so "no FK" stays the reason, not "no column".
                        if (!result.Columns.TryGetValue(columnName, out var sameName))
                            result.Columns[columnName] = sameName = new List<DescribedColumn>(1);
                        sameName.Add(new DescribedColumn
                        {
                            SourceColumn = columnName,
                            SystemTypeName = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(true) ? null : reader.GetString(4),
                            MaxLength = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(true) ? 0 : reader.GetInt16(5)
                        });
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Reads the column list of every table this shape's stars stand for, and returns a
        /// lookup Resolve can call. Returns null when the shape has no expandable star, or when
        /// any of those tables cannot be read -- Resolve then declines, exactly as it did before
        /// stars could be expanded at all.
        /// </summary>
        private static async Task<Func<StaticFromItem, IReadOnlyList<string>>> BuildStarExpanderAsync(
            StaticQueryShape shape,
            Dictionary<string, TableColumns> cache,
            Func<string, string> buildConnectionStringForDatabase,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var targets = StaticShapeResolver.ResolveStarTargets(shape);
            if (targets == null || targets.Count == 0) return null;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var asSource = new StaticColumnSource(
                    null, target.Table.Database, target.Table.Schema, target.Table.Table, null);
                string key = TableKey(asSource);
                if (cache.ContainsKey(key)) continue;

                string connectionString = buildConnectionStringForDatabase(target.Table.Database);
                if (connectionString == null) return null;

                try
                {
                    using (var connection = new SqlConnection(connectionString))
                    {
                        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
                        var loaded = await LoadTableAsync(connection, asSource, timeoutSeconds, cancellationToken).ConfigureAwait(true);
                        if (loaded == null) return null;
                        cache[key] = loaded;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !(ex is OperationCanceledException))
                {
                    // Query text can appear in SQL error messages -- log the type only.
                    OeDiagnostics.Warn("Go to source: could not read a table's columns to expand a * (" + ex.GetType().Name + ").");
                    return null;
                }
            }

            return from =>
            {
                var asSource = new StaticColumnSource(null, from.Database, from.Schema, from.Table, null);
                return cache.TryGetValue(TableKey(asSource), out var loaded) ? loaded.Ordered : null;
            };
        }

        private static string TableKey(StaticColumnSource source) =>
            (source.Database ?? "") + "" + (source.Schema ?? "") + "" + source.Table;
    }
}
