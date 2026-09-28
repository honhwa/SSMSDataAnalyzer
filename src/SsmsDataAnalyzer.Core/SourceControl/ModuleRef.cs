using System;
using SsmsDataAnalyzer.Core.Sql;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    public enum DbObjectKind { Procedure, View, Function, Trigger, Table }

    /// <summary>
    /// Identifies a database module by name, the way <c>sys.objects</c> does: procedures, views,
    /// functions, tables and triggers share one namespace per schema, so a history entry saying
    /// "ALTER FUNCTION" must still find a file that says "CREATE FUNCTION" for the same name.
    /// <see cref="Kind"/> is informational only and deliberately excluded from equality.
    /// </summary>
    public sealed class ModuleRef : IEquatable<ModuleRef>
    {
        public ModuleRef(string schema, string name, DbObjectKind kind)
        {
            Schema = string.IsNullOrEmpty(schema) ? null : schema;
            Name = name ?? string.Empty;
            Kind = kind;
        }

        /// <summary>null = the text did not say (unqualified) -- Core never assumes "dbo".</summary>
        public string Schema { get; }

        /// <summary>e.g. "ABB.ChangeStatus" -- dots are part of the name.</summary>
        public string Name { get; }

        public DbObjectKind Kind { get; }

        public ModuleRef WithSchema(string schema) => new ModuleRef(schema, Name, Kind);

        public bool Equals(ModuleRef other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => Equals(obj as ModuleRef);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Schema == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Schema));
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
                return h;
            }
        }

        /// <summary>"[ABB].[ABB.ChangeStatus]" -- ']' doubled inside, same escaping as query text.</summary>
        public override string ToString()
        {
            string bracketedName = SqlIdentifier.Bracket(Name);
            return Schema == null ? bracketedName : SqlIdentifier.Bracket(Schema) + "." + bracketedName;
        }
    }
}
