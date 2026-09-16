using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    /// <summary>An object name resolved inside its database.</summary>
    public sealed class ResolvedSqlObject
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public string TypeCode { get; set; }
        public string TypeDescription { get; set; }
        public SqlObjectKind Kind { get; set; }

        /// <summary>For triggers: the table/view the trigger belongs to (null otherwise).</summary>
        public string ParentSchema { get; set; }
        public string ParentName { get; set; }

        public string QualifiedName =>
            Sql.SqlIdentifier.Bracket(Schema ?? string.Empty) + "." + Sql.SqlIdentifier.Bracket(Name ?? string.Empty);
    }

    /// <summary>
    /// Resolution and the OBJECT_DEFINITION fallback. Every query is parameterized; the only
    /// name text that reaches SQL is <see cref="SqlMultipartName.ToObjectIdArgument"/> passed as
    /// a parameter to OBJECT_ID/TYPE_ID, so SQL Server's own name resolution (including the
    /// caller's default schema for unqualified names) decides which object is meant.
    /// The connection must already be in the target database.
    /// </summary>
    public static class ScriptObjectQueries
    {
        public const string ResolveSql = @"
DECLARE @id int = OBJECT_ID(@name);
IF @id IS NOT NULL
    SELECT DB_NAME() AS database_name, SCHEMA_NAME(o.schema_id) AS schema_name, o.name, RTRIM(o.type) AS type_code, o.type_desc,
           OBJECT_SCHEMA_NAME(o.parent_object_id) AS parent_schema, OBJECT_NAME(o.parent_object_id) AS parent_name
    FROM sys.objects AS o
    WHERE o.object_id = @id;
ELSE
    SELECT DB_NAME() AS database_name, SCHEMA_NAME(t.schema_id) AS schema_name, t.name,
           CASE WHEN t.is_table_type = 1 THEN 'TT' WHEN t.is_assembly_type = 1 THEN 'UDT' ELSE 'UDDT' END AS type_code,
           CASE WHEN t.is_table_type = 1 THEN 'TABLE_TYPE' WHEN t.is_assembly_type = 1 THEN 'CLR_TYPE' ELSE 'ALIAS_TYPE' END AS type_desc,
           CAST(NULL AS sysname) AS parent_schema, CAST(NULL AS sysname) AS parent_name
    FROM sys.types AS t
    WHERE t.is_user_defined = 1 AND t.user_type_id = TYPE_ID(@name);";

        public const string DefinitionSql = @"
SELECT m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
FROM sys.sql_modules AS m
WHERE m.object_id = OBJECT_ID(@name);";

        /// <summary>Null when the name does not resolve in the connection's current database.</summary>
        public static async Task<ResolvedSqlObject> ResolveAsync(SqlConnection connection, SqlMultipartName name, CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (name == null) throw new ArgumentNullException(nameof(name));

            using (var command = new SqlCommand(ResolveSql, connection))
            {
                command.CommandTimeout = 30;
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 4000) { Value = name.ToObjectIdArgument() });
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
                    string typeCode = reader.GetString(3);
                    return new ResolvedSqlObject
                    {
                        Database = reader.GetString(0),
                        Schema = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Name = reader.GetString(2),
                        TypeCode = typeCode,
                        TypeDescription = reader.GetString(4),
                        Kind = SqlObjectKinds.FromTypeCode(typeCode),
                        ParentSchema = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ParentName = reader.IsDBNull(6) ? null : reader.GetString(6)
                    };
                }
            }
        }

        /// <summary>
        /// Fallback script from sys.sql_modules for when SMO is unavailable. Returns null when the
        /// object has no readable module text (not a T-SQL module, or WITH ENCRYPTION).
        /// </summary>
        public static async Task<IReadOnlyList<string>> TryScriptFromDefinitionAsync(
            SqlConnection connection, ResolvedSqlObject obj, bool forAlter, CancellationToken cancellationToken)
        {
            if (!SqlObjectKinds.HasSqlModule(obj.Kind)) return null;

            using (var command = new SqlCommand(DefinitionSql, connection))
            {
                command.CommandTimeout = 30;
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 4000) { Value = obj.QualifiedName });
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0)) return null;

                    string definition = reader.GetString(0);
                    bool ansiNulls = !reader.IsDBNull(1) && reader.GetBoolean(1);
                    bool quotedIdentifier = !reader.IsDBNull(2) && reader.GetBoolean(2);

                    if (forAlter)
                    {
                        definition = ObjectScriptText.RewriteCreateToAlter(definition);
                        if (definition == null) return null;
                    }

                    return new[]
                    {
                        "SET ANSI_NULLS " + (ansiNulls ? "ON" : "OFF"),
                        "SET QUOTED_IDENTIFIER " + (quotedIdentifier ? "ON" : "OFF"),
                        definition
                    };
                }
            }
        }
    }
}
