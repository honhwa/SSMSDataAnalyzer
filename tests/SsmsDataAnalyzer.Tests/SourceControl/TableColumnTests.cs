using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>FromCatalog owns the sys.columns quirks; each type family and each default.</summary>
    public class TableColumnTests
    {
        [Theory]
        [InlineData("varchar", 50, 50)]
        [InlineData("char", 10, 10)]
        [InlineData("binary", 16, 16)]
        [InlineData("varbinary", 100, 100)]
        [InlineData("varchar", -1, -1)]
        [InlineData("varbinary", -1, -1)]
        public void Catalog_Byte_Length_Types_Keep_MaxLength(string type, short maxLength, int expected)
        {
            TableColumn c = TableColumn.FromCatalog("A", type, maxLength, 0, 0, true, false, false);
            Assert.Equal(expected, c.Length);
            Assert.Null(c.Precision);
            Assert.Null(c.Scale);
        }

        [Theory]
        [InlineData("nvarchar", 100, 50)]
        [InlineData("nchar", 20, 10)]
        [InlineData("nvarchar", -1, -1)]
        public void Catalog_Unicode_MaxLength_Is_Bytes_And_Is_Halved(string type, short maxLength, int expected)
        {
            TableColumn c = TableColumn.FromCatalog("A", type, maxLength, 0, 0, true, false, false);
            Assert.Equal(expected, c.Length);
        }

        [Fact]
        public void Catalog_Decimal_Carries_Precision_And_Scale_Not_Length()
        {
            TableColumn c = TableColumn.FromCatalog("A", "decimal", 9, 18, 2, false, false, false);
            Assert.Equal(18, c.Precision);
            Assert.Equal(2, c.Scale);
            Assert.Null(c.Length);

            TableColumn n = TableColumn.FromCatalog("A", "numeric", 9, 10, 0, false, false, false);
            Assert.Equal(10, n.Precision);
            Assert.Equal(0, n.Scale);
        }

        [Theory]
        [InlineData("datetime2")]
        [InlineData("time")]
        [InlineData("datetimeoffset")]
        public void Catalog_Fractional_Seconds_Types_Carry_Scale_Only(string type)
        {
            TableColumn c = TableColumn.FromCatalog("A", type, 8, 27, 3, true, false, false);
            Assert.Equal(3, c.Scale);
            Assert.Null(c.Precision);
            Assert.Null(c.Length);
        }

        [Theory]
        [InlineData("int", 4, 10, 0)]
        [InlineData("bit", 1, 1, 0)]
        [InlineData("date", 3, 10, 0)]
        [InlineData("uniqueidentifier", 16, 0, 0)]
        [InlineData("xml", -1, 0, 0)]
        [InlineData("float", 8, 53, 0)]
        [InlineData("real", 4, 24, 0)]
        [InlineData("datetime", 8, 23, 3)]
        [InlineData("text", 16, 0, 0)]
        [InlineData("MyUserType", 8, 0, 0)]
        public void Catalog_Types_Without_Facets_Carry_None(string type, short maxLength, byte precision, byte scale)
        {
            TableColumn c = TableColumn.FromCatalog("A", type, maxLength, precision, scale, true, false, false);
            Assert.Null(c.Length);
            Assert.Null(c.Precision);
            Assert.Null(c.Scale);
        }

        [Fact]
        public void Catalog_TypeName_Is_Lowercased()
        {
            Assert.Equal("nvarchar", TableColumn.FromCatalog("A", "NVarChar", 10, 0, 0, true, false, false).TypeName);
        }

        [Fact]
        public void Catalog_Float_Equals_Parsed_Float()
        {
            var parsed = TableDefinitionParser.TryParseColumns("CREATE TABLE [dbo].[T] ([A] FLOAT NOT NULL);");
            TableColumn server = TableColumn.FromCatalog("A", "float", 8, 53, 0, false, false, false);
            Assert.Empty(TableColumnComparer.Compare(parsed, new[] { server }));
        }

        [Fact]
        public void Catalog_Datetime2_Scale7_Equals_Parsed_Datetime2_With_No_Scale_Written()
        {
            var parsed = TableDefinitionParser.TryParseColumns("CREATE TABLE [dbo].[T] ([A] DATETIME2 NOT NULL);");
            TableColumn server = TableColumn.FromCatalog("A", "datetime2", 8, 27, 7, false, false, false);
            Assert.Empty(TableColumnComparer.Compare(parsed, new[] { server }));
        }

        [Fact]
        public void Catalog_Decimal_18_0_Equals_Parsed_Decimal_With_No_Precision_Written()
        {
            var parsed = TableDefinitionParser.TryParseColumns("CREATE TABLE [dbo].[T] ([A] DECIMAL NOT NULL);");
            TableColumn server = TableColumn.FromCatalog("A", "decimal", 9, 18, 0, false, false, false);
            Assert.Empty(TableColumnComparer.Compare(parsed, new[] { server }));
        }

        [Fact]
        public void Catalog_Computed_Column_Has_No_Type()
        {
            TableColumn c = TableColumn.FromCatalog("A", "int", 4, 10, 0, true, false, true);
            Assert.True(c.IsComputed);
            Assert.Null(c.TypeName);
            Assert.Equal("computed", c.Describe());
        }

        [Fact]
        public void Describe_Formats()
        {
            Assert.Equal("nvarchar(50) NOT NULL", TableColumn.FromCatalog("A", "nvarchar", 100, 0, 0, false, false, false).Describe());
            Assert.Equal("nvarchar(max) NULL", TableColumn.FromCatalog("A", "nvarchar", -1, 0, 0, true, false, false).Describe());
            Assert.Equal("int IDENTITY NOT NULL", TableColumn.FromCatalog("A", "int", 4, 10, 0, false, true, false).Describe());
            Assert.Equal("decimal(18,2) NOT NULL", TableColumn.FromCatalog("A", "decimal", 9, 18, 2, false, false, false).Describe());
            Assert.Equal("datetime2(7) NULL", TableColumn.FromCatalog("A", "datetime2", 8, 27, 7, true, false, false).Describe());
        }
    }
}
