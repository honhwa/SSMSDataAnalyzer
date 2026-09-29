using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>One test per §13b.5 rule. Table and column names are invented; only the format
    /// (spacing, brackets, SSDT idioms) is modelled on real SSDT output.</summary>
    public class TableDefinitionParserTests
    {
        private static IReadOnlyList<TableColumn> Parse(string body, string tail = ";") =>
            TableDefinitionParser.TryParseColumns("CREATE TABLE [dbo].[Widget] (\r\n" + body + "\r\n)" + tail);

        private static TableColumn Only(string body) => Assert.Single(Parse(body));

        [Fact]
        public void Spaces_Before_Parenthesis_Brackets_And_Any_Case()
        {
            TableColumn c = Only("    [Label] NVARCHAR (50) NOT NULL");
            Assert.Equal("Label", c.Name);
            Assert.Equal("nvarchar", c.TypeName);
            Assert.Equal(50, c.Length);
            Assert.False(c.IsNullable);

            TableColumn d = Only("label nVarChar(50) not null");
            Assert.Equal("nvarchar", d.TypeName);
            Assert.Equal(50, d.Length);
        }

        [Fact]
        public void Identity_With_Seed_Is_NotNull_Even_When_Nullability_Is_Unspecified()
        {
            TableColumn c = Only("    [Id] INT IDENTITY (1, 1)");
            Assert.True(c.IsIdentity);
            Assert.False(c.IsNullable);
            Assert.True(Only("[Id] BIGINT IDENTITY(10,5) NOT NULL").IsIdentity);
        }

        [Fact]
        public void Unspecified_Nullability_Is_Nullable()
        {
            Assert.True(Only("[A] INT").IsNullable);
            Assert.True(Only("[A] INT NULL").IsNullable);
            Assert.False(Only("[A] INT NOT NULL").IsNullable);
        }

        [Fact]
        public void Table_Level_Primary_Key_Makes_Unspecified_Column_NotNull()
        {
            IReadOnlyList<TableColumn> cols = Parse("[A] INT,\r\n[B] INT,\r\nCONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([A] ASC)");
            Assert.False(cols[0].IsNullable);
            Assert.True(cols[1].IsNullable);
        }

        [Fact]
        public void Default_Is_Skipped_And_Never_Read_As_A_Type()
        {
            TableColumn c = Only("[Flag] BIT DEFAULT (0) NOT NULL");
            Assert.Equal("bit", c.TypeName);
            Assert.False(c.IsNullable);

            Assert.False(Only("[At] DATETIME CONSTRAINT [DF_Widget_At] DEFAULT (getdate()) NOT NULL").IsNullable);
            Assert.False(Only("[N] INT DEFAULT -1 NOT NULL").IsNullable);
            Assert.False(Only("[S] VARCHAR (5) DEFAULT 'a' NOT NULL").IsNullable);
            Assert.False(Only("[T] NVARCHAR (9) DEFAULT (N'NOT NULL') NOT NULL").IsNullable);
            Assert.True(Only("[U] INT DEFAULT NULL").IsNullable);
        }

        [Fact]
        public void Inline_Constraint_Is_Skipped()
        {
            TableColumn c = Only("[Id] INT NOT NULL CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([Id] ASC)");
            Assert.Equal("int", c.TypeName);
            Assert.False(c.IsNullable);
        }

        [Fact]
        public void Inline_Primary_Key_And_Unique_Are_Skipped()
        {
            TableColumn pk = Only("[Id] INT PRIMARY KEY");
            Assert.False(pk.IsNullable);
            Assert.True(Only("[Code] VARCHAR (5) UNIQUE").IsNullable);
        }

        [Fact]
        public void Collate_Sparse_Rowguidcol_Filestream_Hidden_Are_Skipped()
        {
            TableColumn c = Only("[A] VARCHAR (10) COLLATE Latin1_General_CI_AS SPARSE NULL");
            Assert.Equal("varchar", c.TypeName);
            Assert.Equal(10, c.Length);
            Assert.True(c.IsNullable);

            Assert.Equal("uniqueidentifier", Only("[G] UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL").TypeName);
            Assert.Equal("varbinary", Only("[F] VARBINARY (MAX) FILESTREAM NULL").TypeName);
            Assert.Equal("datetime2", Only("[H] DATETIME2 (2) HIDDEN NOT NULL").TypeName);
        }

        [Fact]
        public void Masked_With_Is_Skipped()
        {
            TableColumn c = Only("[Mail] NVARCHAR (100) MASKED WITH (FUNCTION = 'email()') NULL");
            Assert.Equal("nvarchar", c.TypeName);
            Assert.Equal(100, c.Length);
            Assert.True(c.IsNullable);
        }

        [Fact]
        public void Temporal_Generated_Always_And_Period_Are_Not_Mis_Read()
        {
            IReadOnlyList<TableColumn> cols = Parse(
                "[Id] INT NOT NULL,\r\n" +
                "[From] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,\r\n" +
                "[To] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,\r\n" +
                "PERIOD FOR SYSTEM_TIME ([From], [To])",
                "\r\nWITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE=[dbo].[WidgetHistory]));");
            Assert.Equal(new[] { "Id", "From", "To" }, cols.Select(c => c.Name).ToArray());
            Assert.Equal(7, cols[1].Scale);
            Assert.False(cols[2].IsNullable);
        }

        [Fact]
        public void Computed_Column_Has_No_Type_And_Expression_Is_Ignored()
        {
            IReadOnlyList<TableColumn> cols = Parse("[A] INT NOT NULL,\r\n[B] AS ([A] * 2) PERSISTED NOT NULL,\r\n[C] AS (CONVERT(VARCHAR(5), [A]))");
            Assert.False(cols[0].IsComputed);
            Assert.True(cols[1].IsComputed);
            Assert.Null(cols[1].TypeName);
            Assert.True(cols[2].IsComputed);
            Assert.Equal(3, cols.Count);
        }

        [Fact]
        public void Table_Level_Items_Are_Not_Columns()
        {
            IReadOnlyList<TableColumn> cols = Parse(
                "[A] INT NOT NULL,\r\n[B] INT NOT NULL,\r\n" +
                "CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([A] ASC),\r\n" +
                "CONSTRAINT [FK_Widget_B] FOREIGN KEY ([B]) REFERENCES [dbo].[Other] ([Id]),\r\n" +
                "CONSTRAINT [UQ_Widget] UNIQUE NONCLUSTERED ([B] ASC),\r\n" +
                "CONSTRAINT [CK_Widget] CHECK ([A] > 0),\r\n" +
                "UNIQUE ([A], [B]),\r\n" +
                "CHECK ([B] > 0),\r\n" +
                "FOREIGN KEY ([B]) REFERENCES [dbo].[Other] ([Id]),\r\n" +
                "INDEX [IX_Widget] NONCLUSTERED ([B])");
            Assert.Equal(new[] { "A", "B" }, cols.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void Bare_Table_Level_Primary_Key_Is_Not_A_Column()
        {
            Assert.Single(Parse("[A] INT NOT NULL,\r\nPRIMARY KEY CLUSTERED ([A] ASC)"));
        }

        [Fact]
        public void A_Column_Named_Period_Is_Still_A_Column()
        {
            IReadOnlyList<TableColumn> cols = Parse("Period INT NOT NULL,\r\n[Index] INT NULL");
            Assert.Equal(2, cols.Count);
        }

        [Fact]
        public void Max_Is_Length_Minus_One()
        {
            Assert.Equal(-1, Only("[A] NVARCHAR (MAX) NULL").Length);
            Assert.Equal(-1, Only("[A] varchar(max) NULL").Length);
            Assert.Equal(-1, Only("[A] VARBINARY (MAX) NULL").Length);
        }

        [Fact]
        public void Length_Defaults_To_One_When_Not_Written()
        {
            Assert.Equal(1, Only("[A] CHAR NULL").Length);
            Assert.Equal(1, Only("[A] NVARCHAR NULL").Length);
        }

        [Fact]
        public void Decimal_Facets_And_Defaults()
        {
            TableColumn c = Only("[A] NUMERIC (18, 2) NULL");
            Assert.Equal(18, c.Precision);
            Assert.Equal(2, c.Scale);

            TableColumn d = Only("[A] DECIMAL (10) NULL");
            Assert.Equal(10, d.Precision);
            Assert.Equal(0, d.Scale);

            TableColumn e = Only("[A] DECIMAL NULL");
            Assert.Equal(18, e.Precision);
            Assert.Equal(0, e.Scale);
        }

        [Fact]
        public void Fractional_Seconds_Default_To_Seven()
        {
            Assert.Equal(7, Only("[A] DATETIME2 NULL").Scale);
            Assert.Equal(7, Only("[A] TIME NULL").Scale);
            Assert.Equal(7, Only("[A] DATETIMEOFFSET NULL").Scale);
            Assert.Equal(3, Only("[A] DATETIME2 (3) NULL").Scale);
        }

        [Fact]
        public void Float_Real_And_Synonyms_Are_Normalised_To_The_Catalog_Names()
        {
            Assert.Equal("float", Only("[A] FLOAT NULL").TypeName);
            Assert.Equal("float", Only("[A] FLOAT (53) NULL").TypeName);
            Assert.Equal("real", Only("[A] FLOAT (24) NULL").TypeName);
            Assert.Equal("float", Only("[A] DOUBLE PRECISION NULL").TypeName);
            Assert.Equal("int", Only("[A] INTEGER NULL").TypeName);
            Assert.Equal("decimal", Only("[A] DEC (5, 1) NULL").TypeName);
            Assert.Equal("timestamp", Only("[A] ROWVERSION NOT NULL").TypeName);
        }

        [Fact]
        public void User_Defined_Type_Is_Read_By_Its_Own_Name()
        {
            Assert.Equal("phonenumber", Only("[Ph] [PhoneNumber] NULL").TypeName);
            Assert.Equal("phonenumber", Only("[Ph] [dbo].[PhoneNumber] NOT NULL").TypeName);
            Assert.Null(Only("[Ph] [dbo].[PhoneNumber] NOT NULL").Length);
        }

        [Fact]
        public void Types_Without_Facets_Have_None()
        {
            TableColumn c = Only("[A] UNIQUEIDENTIFIER NOT NULL");
            Assert.Null(c.Length);
            Assert.Null(c.Precision);
            Assert.Null(c.Scale);
            Assert.Null(Only("[A] INT NULL").Length);
        }

        [Fact]
        public void Not_For_Replication_And_Xml_Schema_Collection_Are_Accepted()
        {
            Assert.True(Only("[A] INT IDENTITY (1, 1) NOT FOR REPLICATION NOT NULL").IsIdentity);
            Assert.Equal("xml", Only("[A] XML (CONTENT [dbo].[Docs]) NULL").TypeName);
        }

        [Fact]
        public void Storage_Clauses_After_The_Column_List_Are_Accepted()
        {
            Assert.Single(Parse("[A] INT NOT NULL", " ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]"));
            Assert.Single(Parse("[A] INT NOT NULL", "\r\nWITH (DATA_COMPRESSION = PAGE);"));
        }

        [Fact]
        public void Trailing_Comma_Before_The_Closing_Parenthesis_Is_Tolerated()
        {
            IReadOnlyList<TableColumn> cols = TableDefinitionParser.TryParseColumns(
                "CREATE TABLE [dbo].[Widget] (\r\n[A] INT NOT NULL,\r\nCONSTRAINT [PK] PRIMARY KEY CLUSTERED ([A] ASC),\r\n);");
            Assert.Single(cols);
        }

        [Fact]
        public void Constraint_Written_On_The_Column_With_Its_Own_Column_List_Is_Skipped()
        {
            TableColumn c = Only("[A] INT NOT NULL CONSTRAINT [PK_W] PRIMARY KEY CLUSTERED ([A] ASC) WITH (PAD_INDEX = OFF) ON [PRIMARY]");
            Assert.Equal("a", c.Name.ToLowerInvariant());
        }

        [Fact]
        public void Comments_And_Strings_Never_Confuse_The_Parser()
        {
            IReadOnlyList<TableColumn> cols = Parse(
                "[A] INT NOT NULL, -- , [Fake] INT\r\n/* [Also] fake, */ [B] NVARCHAR (5) DEFAULT (N'x, y') NULL");
            Assert.Equal(new[] { "A", "B" }, cols.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void Bracket_Escapes_In_Names_Are_Unescaped()
        {
            Assert.Equal("we]ird", Only("[we]]ird] INT NULL").Name);
        }

        // ---- everything not recognised is null, never a partial list -------------------------

        [Theory]
        [InlineData("[A] INT NOT NULL, [B] SOMETHING WEIRD NULL")]              // unknown column attribute
        [InlineData("[A] INT NOT NULL, [B] INT ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = k) NULL")]
        [InlineData("[A] INT NOT NULL, [B] NVARCHAR (abc) NULL")]                // bad type argument
        [InlineData("[A] INT (5) NOT NULL")]                                     // arguments on a facet-less type
        [InlineData("[A] DECIMAL (1, 2, 3) NULL")]
        [InlineData("[A] VARCHAR (0) NULL")]
        [InlineData("[A] FLOAT (99) NULL")]
        [InlineData("[A] INT NOT NULL, , [B] INT NULL")]                          // empty item in the middle
        [InlineData("[A] CHAR VARYING (5) NULL")]                                 // unsupported two-word type
        public void Unrecognised_Column_Returns_Null_For_The_Whole_Table(string body)
        {
            Assert.Null(Parse(body));
        }

        [Theory]
        [InlineData("CREATE TABLE [dbo].[T] ([A] INT) AS FILETABLE")]
        [InlineData("CREATE TABLE [dbo].[T] ([A] INT) AS NODE")]
        [InlineData("CREATE TABLE [dbo].[T] AS FILETABLE")]
        [InlineData("CREATE TABLE [dbo].[T] ([A] INT")]                          // unbalanced
        [InlineData("CREATE TABLE [dbo].[T] ()")]                                 // no columns
        [InlineData("CREATE TABLE [dbo].[T] (PRIMARY KEY ([A]))")]                // no columns, only a constraint
        [InlineData("ALTER TABLE [dbo].[T] ADD [A] INT NULL")]
        [InlineData("CREATE INDEX [IX] ON [dbo].[T] ([A])")]
        [InlineData("CREATE VIEW [dbo].[V] AS SELECT 1 AS [A]")]
        [InlineData("")]
        [InlineData(null)]
        public void Not_A_Readable_Create_Table_Returns_Null(string batch)
        {
            Assert.Null(TableDefinitionParser.TryParseColumns(batch));
        }

        [Fact]
        public void Never_Throws_On_Garbage()
        {
            foreach (string s in new[] { "CREATE", "CREATE TABLE", "CREATE TABLE [", "CREATE TABLE x (", "CREATE TABLE x ([a", "CREATE TABLE x (,)", "CREATE TABLE x ((((", "\0\0" })
                Assert.Null(TableDefinitionParser.TryParseColumns(s));
        }

        [Fact]
        public void Leading_Comment_And_Bom_Before_Create_Table_Are_Tolerated()
        {
            IReadOnlyList<TableColumn> cols = TableDefinitionParser.TryParseColumns(
                "﻿-- header\r\nCREATE TABLE [dbo].[Widget] ([A] INT NOT NULL);");
            Assert.Single(cols);
        }
    }
}
