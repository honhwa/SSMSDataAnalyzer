using System.Collections.Generic;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    /// <summary>
    /// A T-SQL multipart object name as written in the editor (server.database.schema.object),
    /// with every part already unescaped ([a]]b] → a]b). Absent parts are null.
    /// </summary>
    public sealed class SqlMultipartName
    {
        public SqlMultipartName(string server, string database, string schema, string name)
        {
            Server = NullIfEmpty(server);
            Database = NullIfEmpty(database);
            Schema = NullIfEmpty(schema);
            Name = name ?? string.Empty;
        }

        public string Server { get; }
        public string Database { get; }
        public string Schema { get; }
        public string Name { get; }

        /// <summary>"[schema].[name]" or "[name]" — the argument for OBJECT_ID/TYPE_ID inside the
        /// target database. Bracketing (not string-splitting on dots) is what keeps names such as
        /// [Accounting.Compensation.Correction.Item] intact.</summary>
        public string ToObjectIdArgument() =>
            Schema == null
                ? SqlIdentifier.Bracket(Name)
                : SqlIdentifier.Bracket(Schema) + "." + SqlIdentifier.Bracket(Name);

        /// <summary>Every present part bracketed and dot-joined, for status messages. A missing
        /// schema between database and name is kept as an empty part ([db]..[name]).</summary>
        public override string ToString()
        {
            var parts = new List<string>();
            if (Server != null) parts.Add(SqlIdentifier.Bracket(Server));
            if (Server != null || Database != null) parts.Add(Database == null ? string.Empty : SqlIdentifier.Bracket(Database));
            if (Server != null || Database != null || Schema != null) parts.Add(Schema == null ? string.Empty : SqlIdentifier.Bracket(Schema));
            parts.Add(SqlIdentifier.Bracket(Name));
            return string.Join(".", parts);
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
