using System;
using System.Text;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// One column of a table in the shape both sides are reduced to before comparing (§13b.4):
    /// name, type, facets, nullability, identity, computed. A computed column carries no type and
    /// no facets -- only its name and the fact that it is computed are compared.
    /// </summary>
    public sealed class TableColumn
    {
        public TableColumn(string name, string typeName, int? length, int? precision, int? scale,
                           bool isNullable, bool isIdentity, bool isComputed)
        {
            Name = name ?? string.Empty;
            IsComputed = isComputed;
            if (isComputed)
            {
                TypeName = null; Length = null; Precision = null; Scale = null;
                IsNullable = false; IsIdentity = false;
            }
            else
            {
                TypeName = typeName;
                Length = length;
                Precision = precision;
                Scale = scale;
                IsNullable = isNullable;
                IsIdentity = isIdentity;
            }
        }

        public string Name { get; }
        public string TypeName { get; }
        public int? Length { get; }
        public int? Precision { get; }
        public int? Scale { get; }
        public bool IsNullable { get; }
        public bool IsIdentity { get; }
        public bool IsComputed { get; }

        /// <summary>"nvarchar(50) NOT NULL", "int IDENTITY NOT NULL", "computed".</summary>
        public string Describe()
        {
            if (IsComputed) return "computed";
            var sb = new StringBuilder(TypeName ?? "?");
            if (Length.HasValue) sb.Append('(').Append(Length.Value == -1 ? "max" : Length.Value.ToString()).Append(')');
            else if (Precision.HasValue && Scale.HasValue) sb.Append('(').Append(Precision.Value).Append(',').Append(Scale.Value).Append(')');
            else if (Precision.HasValue) sb.Append('(').Append(Precision.Value).Append(')');
            else if (Scale.HasValue) sb.Append('(').Append(Scale.Value).Append(')');
            if (IsIdentity) sb.Append(" IDENTITY");
            sb.Append(IsNullable ? " NULL" : " NOT NULL");
            return sb.ToString();
        }

        internal bool SameAs(TableColumn other)
        {
            if (other == null) return false;
            if (IsComputed || other.IsComputed) return IsComputed == other.IsComputed;
            return string.Equals(TypeName, other.TypeName, StringComparison.OrdinalIgnoreCase)
                && Length == other.Length && Precision == other.Precision && Scale == other.Scale
                && IsNullable == other.IsNullable && IsIdentity == other.IsIdentity;
        }

        /// <summary>
        /// sys.columns -&gt; the shape the file parser produces. Owns the catalog quirks:
        /// <c>max_length</c> is BYTES (nchar/nvarchar are halved), -1 is max, and only some types
        /// carry facets: char/varchar/nchar/nvarchar/binary/varbinary a length; decimal/numeric
        /// precision and scale; datetime2/time/datetimeoffset a scale. float and real carry none
        /// (the catalog reports float(24) as real; the parser normalises the same way).
        /// Every other type -- int, bit, date, uniqueidentifier, xml, text, a user type -- has no
        /// facets, whatever the catalog says about its storage size.
        /// </summary>
        public static TableColumn FromCatalog(string name, string typeName, short maxLength,
            byte precision, byte scale, bool isNullable, bool isIdentity, bool isComputed)
        {
            string type = string.IsNullOrWhiteSpace(typeName) ? null : typeName.Trim().ToLowerInvariant();
            int? length = null, prec = null, sc = null;
            switch (type)
            {
                case "char":
                case "varchar":
                case "binary":
                case "varbinary":
                    length = maxLength;
                    break;
                case "nchar":
                case "nvarchar":
                    length = maxLength == -1 ? -1 : maxLength / 2;
                    break;
                case "decimal":
                case "numeric":
                    prec = precision;
                    sc = scale;
                    break;
                case "datetime2":
                case "time":
                case "datetimeoffset":
                    sc = scale;
                    break;
            }
            return new TableColumn(name, type, length, prec, sc, isNullable, isIdentity, isComputed);
        }
    }
}
