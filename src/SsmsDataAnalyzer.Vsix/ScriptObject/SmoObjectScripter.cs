using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SsmsDataAnalyzer.Vsix.ScriptObject
{
    /// <summary>
    /// Scripts one resolved object with SMO — the same engine behind SSMS's own
    /// "Script … as CREATE/ALTER To". SMO is host-owned (SSMS 22 IDE root, 18.100.0.0), referenced
    /// with Private=false and never packaged. Runs on a background thread; no UI access.
    ///
    /// Options mirror SSMS's defaults for "Script Table as CREATE": the object only (no
    /// dependencies, no permissions), with its keys/constraints, indexes, triggers and extended
    /// properties, schema-qualified, no collation, with the descriptive header.
    /// </summary>
    internal static class SmoObjectScripter
    {
        /// <summary>Returns the script batches (without GO), or throws when SMO cannot script
        /// the object (the caller falls back to OBJECT_DEFINITION where possible).</summary>
        public static IReadOnlyList<string> Script(SqlConnection openConnection, ResolvedSqlObject obj, bool forAlter)
        {
            if (openConnection == null) throw new ArgumentNullException(nameof(openConnection));
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var serverConnection = new ServerConnection(openConnection);
            var server = new Server(serverConnection);
            Database database = server.Databases[obj.Database]
                ?? throw new InvalidOperationException("SMO could not find the database.");

            SqlSmoObject target = Find(database, obj)
                ?? throw new InvalidOperationException("SMO could not find the object.");

            bool alter = forAlter && SqlObjectKinds.SupportsAlter(obj.Kind);
            var options = new ScriptingOptions
            {
                IncludeHeaders = true,
                SchemaQualify = true,
                SchemaQualifyForeignKeysReferences = true,
                WithDependencies = false,
                Permissions = false,
                // ALTER re-runs only the module itself: indexes, triggers, keys and extended
                // properties already exist and would fail if re-created.
                DriAll = !alter,
                Indexes = !alter,
                Triggers = !alter,
                FullTextIndexes = !alter,
                ExtendedProperties = !alter,
                NoCollation = true,
                ScriptBatchTerminator = false,
                AllowSystemObjects = true
            };

            // Always scripted as CREATE through Scripter (the path SSMS's "Script as CREATE"
            // uses). For ALTER the leading CREATE of the module batch is then rewritten by token
            // position. SMO's own ScriptForAlter was tried first and is not usable here (verified
            // against SSMS 22's SMO 18.100 by the scratch harness): Scripter ignores it, and
            // Touch() + ScriptMaker keeps the stored "CREATE" header unless TextMode is switched
            // off, which regenerates the header from metadata instead of the user's own text.
            var batches = new List<string>();
            foreach (string batch in new Scripter(server) { Options = options }.EnumScript(new[] { target }))
            {
                if (!string.IsNullOrWhiteSpace(batch)) batches.Add(batch);
            }
            if (batches.Count > 0 && alter)
            {
                return ObjectScriptText.RewriteCreateBatchToAlter(batches)
                    ?? throw new InvalidOperationException("SMO's script did not start with CREATE, so it could not be turned into ALTER.");
            }
            if (batches.Count == 0) throw new InvalidOperationException("SMO returned an empty script.");
            return batches;
        }

        private static SqlSmoObject Find(Database db, ResolvedSqlObject obj)
        {
            string name = obj.Name;
            string schema = obj.Schema;
            switch (obj.Kind)
            {
                case SqlObjectKind.Table:
                    return db.Tables[name, schema];
                case SqlObjectKind.View:
                    return db.Views[name, schema];
                case SqlObjectKind.StoredProcedure:
                case SqlObjectKind.ClrStoredProcedure:
                    return db.StoredProcedures[name, schema];
                case SqlObjectKind.ExtendedStoredProcedure:
                    return db.ExtendedStoredProcedures[name, schema];
                case SqlObjectKind.ScalarFunction:
                case SqlObjectKind.InlineTableFunction:
                case SqlObjectKind.TableFunction:
                case SqlObjectKind.ClrScalarFunction:
                case SqlObjectKind.ClrTableFunction:
                    return db.UserDefinedFunctions[name, schema];
                case SqlObjectKind.ClrAggregate:
                    return db.UserDefinedAggregates[name, schema];
                case SqlObjectKind.Synonym:
                    return db.Synonyms[name, schema];
                case SqlObjectKind.Sequence:
                    return db.Sequences[name, schema];
                case SqlObjectKind.UserDefinedDataType:
                    return db.UserDefinedDataTypes[name, schema];
                case SqlObjectKind.UserDefinedTableType:
                    return db.UserDefinedTableTypes[name, schema];
                case SqlObjectKind.UserDefinedClrType:
                    return db.UserDefinedTypes[name, schema];
                case SqlObjectKind.Trigger:
                case SqlObjectKind.ClrTrigger:
                    if (obj.ParentName == null) return null;
                    Table table = db.Tables[obj.ParentName, obj.ParentSchema];
                    if (table != null) return table.Triggers[name];
                    View view = db.Views[obj.ParentName, obj.ParentSchema];
                    return view?.Triggers[name];
                default:
                    return null;
            }
        }
    }
}
