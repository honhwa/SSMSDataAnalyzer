using System;

namespace SsmsDataAnalyzer.Core.ScriptObject
{
    public enum SqlObjectKind
    {
        Unsupported,
        Table,
        View,
        StoredProcedure,
        ClrStoredProcedure,
        ExtendedStoredProcedure,
        ScalarFunction,
        InlineTableFunction,
        TableFunction,
        ClrScalarFunction,
        ClrTableFunction,
        ClrAggregate,
        Trigger,
        ClrTrigger,
        Synonym,
        Sequence,
        UserDefinedDataType,
        UserDefinedTableType,
        UserDefinedClrType
    }

    /// <summary>
    /// Maps sys.objects.type (and the pseudo-codes <see cref="ScriptObjectQueries"/> returns for
    /// sys.types rows) to what can be scripted and whether an ALTER form exists.
    /// </summary>
    public static class SqlObjectKinds
    {
        public const string TableTypeCode = "TT";
        public const string AliasTypeCode = "UDDT";
        public const string ClrTypeCode = "UDT";

        public static SqlObjectKind FromTypeCode(string typeCode)
        {
            switch ((typeCode ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "U": return SqlObjectKind.Table;
                case "V": return SqlObjectKind.View;
                case "P": return SqlObjectKind.StoredProcedure;
                case "PC": return SqlObjectKind.ClrStoredProcedure;
                case "X": return SqlObjectKind.ExtendedStoredProcedure;
                case "FN": return SqlObjectKind.ScalarFunction;
                case "IF": return SqlObjectKind.InlineTableFunction;
                case "TF": return SqlObjectKind.TableFunction;
                case "FS": return SqlObjectKind.ClrScalarFunction;
                case "FT": return SqlObjectKind.ClrTableFunction;
                case "AF": return SqlObjectKind.ClrAggregate;
                case "TR": return SqlObjectKind.Trigger;
                case "TA": return SqlObjectKind.ClrTrigger;
                case "SN": return SqlObjectKind.Synonym;
                case "SO": return SqlObjectKind.Sequence;
                case TableTypeCode: return SqlObjectKind.UserDefinedTableType;
                case AliasTypeCode: return SqlObjectKind.UserDefinedDataType;
                case ClrTypeCode: return SqlObjectKind.UserDefinedClrType;
                default: return SqlObjectKind.Unsupported;
            }
        }

        /// <summary>True where "Script … as ALTER" is meaningful: module-backed objects. Tables,
        /// types, synonyms and sequences get a CREATE script with an explanatory first line
        /// (SSMS itself greys out ALTER for them).</summary>
        public static bool SupportsAlter(SqlObjectKind kind)
        {
            switch (kind)
            {
                case SqlObjectKind.View:
                case SqlObjectKind.StoredProcedure:
                case SqlObjectKind.ClrStoredProcedure:
                case SqlObjectKind.ScalarFunction:
                case SqlObjectKind.InlineTableFunction:
                case SqlObjectKind.TableFunction:
                case SqlObjectKind.ClrScalarFunction:
                case SqlObjectKind.ClrTableFunction:
                case SqlObjectKind.Trigger:
                case SqlObjectKind.ClrTrigger:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True for T-SQL modules whose text is in sys.sql_modules — the objects the
        /// OBJECT_DEFINITION fallback can script when SMO is unavailable.</summary>
        public static bool HasSqlModule(SqlObjectKind kind)
        {
            switch (kind)
            {
                case SqlObjectKind.View:
                case SqlObjectKind.StoredProcedure:
                case SqlObjectKind.ScalarFunction:
                case SqlObjectKind.InlineTableFunction:
                case SqlObjectKind.TableFunction:
                case SqlObjectKind.Trigger:
                    return true;
                default:
                    return false;
            }
        }

        public static string DisplayName(SqlObjectKind kind)
        {
            switch (kind)
            {
                case SqlObjectKind.Table: return "Table";
                case SqlObjectKind.View: return "View";
                case SqlObjectKind.StoredProcedure: return "Stored procedure";
                case SqlObjectKind.ClrStoredProcedure: return "CLR stored procedure";
                case SqlObjectKind.ExtendedStoredProcedure: return "Extended stored procedure";
                case SqlObjectKind.ScalarFunction: return "Scalar function";
                case SqlObjectKind.InlineTableFunction: return "Inline table-valued function";
                case SqlObjectKind.TableFunction: return "Table-valued function";
                case SqlObjectKind.ClrScalarFunction: return "CLR scalar function";
                case SqlObjectKind.ClrTableFunction: return "CLR table-valued function";
                case SqlObjectKind.ClrAggregate: return "CLR aggregate";
                case SqlObjectKind.Trigger: return "Trigger";
                case SqlObjectKind.ClrTrigger: return "CLR trigger";
                case SqlObjectKind.Synonym: return "Synonym";
                case SqlObjectKind.Sequence: return "Sequence";
                case SqlObjectKind.UserDefinedDataType: return "User-defined data type";
                case SqlObjectKind.UserDefinedTableType: return "User-defined table type";
                case SqlObjectKind.UserDefinedClrType: return "User-defined CLR type";
                default: return "Object";
            }
        }

        /// <summary>First line of an F12 script for an object without an ALTER form.</summary>
        public static string NoAlterFormComment(SqlObjectKind kind) =>
            "-- " + DisplayName(kind) + " has no ALTER form; CREATE script shown.";
    }
}
