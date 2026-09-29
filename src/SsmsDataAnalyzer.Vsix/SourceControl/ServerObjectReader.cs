using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.SourceControl;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// SELECT-only, batched-per-database reads against <c>sys.objects</c> /
    /// <c>sys.sql_modules</c> (docs/source-control-sync-plan.md §9 Phase 1 item 3). Never writes
    /// anything, never stores a password beyond the lifetime of one connection, and every
    /// statement here is a plain SELECT -- no temp tables, no session state changes (three-part
    /// names are used instead of <c>USE</c>, so a reused connection's own current database is
    /// never touched).
    /// </summary>
    internal static class ServerObjectReader
    {
        private static readonly Dictionary<string, DbObjectKind?> TypeMap = new Dictionary<string, DbObjectKind?>(StringComparer.OrdinalIgnoreCase)
        {
            ["P"] = DbObjectKind.Procedure,
            ["V"] = DbObjectKind.View,
            ["FN"] = DbObjectKind.Function,
            ["IF"] = DbObjectKind.Function,
            ["TF"] = DbObjectKind.Function,
            ["TR"] = DbObjectKind.Trigger,
            ["U"] = DbObjectKind.Table,
        };

        /// <summary>
        /// One batch per database: resolves each unqualified module (Schema == null) the way SQL
        /// Server itself resolved it when the statement ran — the caller's DEFAULT schema in that
        /// database, then <c>dbo</c> — via <c>OBJECT_ID('[db]..[name]')</c>. The empty middle part is
        /// what applies the default-schema rule, and naming the database means no <c>USE</c> is
        /// needed, so a reused connection's session database is never changed.
        ///
        /// Lead fix on review: the first version searched every schema for the name and accepted
        /// it when exactly one matched. That is usually right but it is not SQL Server's rule,
        /// and this project's rule is to ask the server rather than approximate it. The same
        /// login runs this lookup (the user's own connection is reused), so "default schema"
        /// means the same thing here as when the statement ran.
        ///
        /// Returns only the modules that resolved; an unresolved one is reported by SyncChecker
        /// as "could not tell which schema", never guessed.
        /// </summary>
        public static async Task<Dictionary<ModuleRef, string>> ResolveSchemasAsync(
            string connectionString, string database, IReadOnlyList<ModuleRef> unqualified)
        {
            var all = new Dictionary<ModuleRef, string>();
            foreach (var chunk in Chunks(unqualified))
            {
                foreach (var pair in await ResolveSchemasChunkAsync(connectionString, database, chunk).ConfigureAwait(false))
                    all[pair.Key] = pair.Value;
            }
            return all;
        }

        private static async Task<Dictionary<ModuleRef, string>> ResolveSchemasChunkAsync(
            string connectionString, string database, IReadOnlyList<ModuleRef> unqualified)
        {
            var resolved = new Dictionary<ModuleRef, string>();
            if (unqualified == null || unqualified.Count == 0) return resolved;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 30;
                    string db = SqlIdentifier.Bracket(database);
                    command.Parameters.AddWithValue("@db", database);

                    var sql = new System.Text.StringBuilder();
                    for (int i = 0; i < unqualified.Count; i++)
                    {
                        string p = "@n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        // One row per statement, NULL when the name does not resolve.
                        sql.Append("SELECT OBJECT_SCHEMA_NAME(OBJECT_ID(").Append(p).Append("), DB_ID(@db));\n");
                        command.Parameters.AddWithValue(p, db + ".." + SqlIdentifier.Bracket(unqualified[i].Name));
                    }
                    command.CommandText = sql.ToString();

                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        int idx = 0;
                        do
                        {
                            if (idx < unqualified.Count
                                && await reader.ReadAsync().ConfigureAwait(false)
                                && !await reader.IsDBNullAsync(0).ConfigureAwait(false))
                            {
                                resolved[unqualified[idx]] = reader.GetString(0);
                            }
                            idx++;
                        }
                        while (await reader.NextResultAsync().ConfigureAwait(false));
                    }
                }
            }

            return resolved;
        }

        /// <summary>
        /// One batch per database: existence, type and definition for each (already schema-
        /// qualified) module, keyed by database-scoped <c>OBJECT_ID</c> so no <c>USE</c> is ever
        /// issued. Modules that do not exist come back with <c>Exists = false</c>.
        /// </summary>
        public static async Task<Dictionary<ModuleRef, ServerObjectState>> ReadStatesAsync(
            string connectionString, string database, IReadOnlyList<ModuleRef> modules)
        {
            var all = new Dictionary<ModuleRef, ServerObjectState>();
            foreach (var chunk in Chunks(modules))
            {
                foreach (var pair in await ReadStatesChunkAsync(connectionString, database, chunk).ConfigureAwait(false))
                    all[pair.Key] = pair.Value;
            }
            return all;
        }

        /// <summary>One column as sys.columns describes it, before Core's TableColumn.FromCatalog
        /// turns it into the shape the file parser produces.</summary>
        internal struct CatalogColumn
        {
            public string Name;
            public string TypeName;
            public short MaxLength;
            public byte Precision;
            public byte Scale;
            public bool IsNullable;
            public bool IsIdentity;
            public bool IsComputed;
        }

        /// <summary>
        /// Plan §13b: the columns of each table, in column order, one result set per table,
        /// batched in chunks like the other reads. A table that does not exist yields no entry.
        ///
        /// The type name comes from a join with that database's OWN sys.types, not TYPE_NAME():
        /// TYPE_NAME resolves in the connection's current database, which would misname a
        /// user-defined type the moment the two differ.
        /// </summary>
        public static async Task<Dictionary<ModuleRef, List<CatalogColumn>>> ReadTableColumnsAsync(
            string connectionString, string database, IReadOnlyList<ModuleRef> tables)
        {
            var all = new Dictionary<ModuleRef, List<CatalogColumn>>();
            foreach (var chunk in Chunks(tables))
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 30;
                        string db = SqlIdentifier.Bracket(database);
                        var sql = new System.Text.StringBuilder();
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            string p = "@t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            sql.Append("SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed FROM ")
                               .Append(db).Append(".sys.columns c JOIN ")
                               .Append(db).Append(".sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(")
                               .Append(p).Append(") ORDER BY c.column_id;" + "\n");
                            command.Parameters.AddWithValue(p, db + "." + chunk[i].ToString());
                        }
                        command.CommandText = sql.ToString();

                        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            int idx = 0;
                            do
                            {
                                var columns = new List<CatalogColumn>();
                                while (await reader.ReadAsync().ConfigureAwait(false))
                                {
                                    columns.Add(new CatalogColumn
                                    {
                                        Name = reader.GetString(0),
                                        TypeName = reader.GetString(1),
                                        MaxLength = reader.GetInt16(2),
                                        Precision = reader.GetByte(3),
                                        Scale = reader.GetByte(4),
                                        IsNullable = reader.GetBoolean(5),
                                        IsIdentity = reader.GetBoolean(6),
                                        IsComputed = reader.GetBoolean(7),
                                    });
                                }
                                if (idx < chunk.Count && columns.Count > 0) all[chunk[idx]] = columns;
                                idx++;
                            }
                            while (await reader.NextResultAsync().ConfigureAwait(false));
                        }
                    }
                }
            }
            return all;
        }

        /// <summary>
        /// Both batched reads send one parameter per object, and SQL Server rejects a request
        /// with more than 2,100 parameters. Field report: a single executed deployment script
        /// nominated 523 objects at once; a bigger one would have failed the entire check.
        /// 500 per round trip keeps every batch far under the limit.
        /// </summary>
        private const int ChunkSize = 500;

        private static IEnumerable<IReadOnlyList<ModuleRef>> Chunks(IReadOnlyList<ModuleRef> items)
        {
            if (items == null) yield break;
            for (int i = 0; i < items.Count; i += ChunkSize)
                yield return items.Skip(i).Take(ChunkSize).ToList();
        }

        private static async Task<Dictionary<ModuleRef, ServerObjectState>> ReadStatesChunkAsync(
            string connectionString, string database, IReadOnlyList<ModuleRef> modules)
        {
            var result = new Dictionary<ModuleRef, ServerObjectState>();
            if (modules == null || modules.Count == 0) return result;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 30;
                    string db = SqlIdentifier.Bracket(database);
                    var sql = new System.Text.StringBuilder();
                    int i = 0;
                    foreach (ModuleRef module in modules)
                    {
                        string qualified = db + "." + module.ToString(); // [db].[schema].[name]
                        string p = "@q" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        sql.Append("SELECT o.type AS ObjType, m.definition AS Def FROM ")
                           .Append(db).Append(".sys.objects o LEFT JOIN ")
                           .Append(db).Append(".sys.sql_modules m ON m.object_id = o.object_id WHERE o.object_id = OBJECT_ID(")
                           .Append(p).Append(");\n");
                        command.Parameters.AddWithValue(p, qualified);
                        i++;
                    }
                    command.CommandText = sql.ToString();

                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        int idx = 0;
                        do
                        {
                            ModuleRef module = modules[idx];
                            if (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                string type = await reader.IsDBNullAsync(0).ConfigureAwait(false) ? null : reader.GetString(0).Trim();
                                string definition = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1);
                                DbObjectKind? kind = type != null && TypeMap.TryGetValue(type, out DbObjectKind? k) ? k : null;
                                result[module] = new ServerObjectState(true, kind, definition);
                            }
                            else
                            {
                                result[module] = new ServerObjectState(false, null, null);
                            }
                            idx++;
                        }
                        while (await reader.NextResultAsync().ConfigureAwait(false));
                    }
                }
            }

            return result;
        }

        /// <summary>§9 Phase 1 item 2(b): everything <c>sys.objects.modify_date</c> says changed
        /// since <paramref name="sinceUtc"/> on the CURRENT database of the connection -- no
        /// cross-database read here, so this is a single, ordinary (same-database) query.</summary>
        public static async Task<List<(ModuleRef Module, DateTime ChangedUtc)>> ReadRecentlyModifiedAsync(
            string connectionString, DateTime sinceUtc)
        {
            var result = new List<(ModuleRef, DateTime)>();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 30;
                    // sys.objects.modify_date is in the SERVER's local time, not UTC. Lead fix on
                    // review: comparing it with a UTC cutoff shifted the window by the server's
                    // offset (1-2 hours in Croatia) and every "changed at" time on screen was off
                    // by the same amount. The server converts both ways with its own current
                    // offset: the cutoff into local time for the WHERE, the result back into UTC.
                    // Accepted imprecision: a daylight-saving change inside the window uses
                    // today's offset for older rows, so those can be off by one hour.
                    command.CommandText =
                        "DECLARE @offsetMinutes int = DATEDIFF(minute, SYSUTCDATETIME(), SYSDATETIME()); " +
                        "SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjType, " +
                        "       DATEADD(minute, -@offsetMinutes, o.modify_date) AS ModifiedUtc " +
                        "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                        "WHERE o.modify_date >= DATEADD(minute, @offsetMinutes, @sinceUtc) " +
                        "AND o.type IN ('P','V','FN','IF','TF','TR','U') " +
                        "AND o.is_ms_shipped = 0";
                    command.Parameters.Add(new SqlParameter("@sinceUtc", System.Data.SqlDbType.DateTime2)
                    {
                        Value = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified)
                    });

                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            string schema = reader.GetString(0);
                            string name = reader.GetString(1);
                            string type = reader.GetString(2).Trim();
                            DateTime modified = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
                            if (!TypeMap.TryGetValue(type, out DbObjectKind? kind) || kind == null) continue;

                            result.Add((new ModuleRef(schema, name, kind.Value), modified));
                        }
                    }
                }
            }

            return result;
        }
    }
}
