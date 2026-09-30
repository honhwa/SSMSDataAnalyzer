using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class ModuleFileParserTests
    {
        [Fact]
        public void Bom_Author_Header_Then_Dotted_Bracketed_Name_Is_Identified()
        {
            // Modelled on ABB\Stored Procedures\ABB.ChangeStatus.sql from the real repo (§3).
            string file =
                "﻿/*\n" +
                " * Author: mtusek\n" +
                " * Created: 2024-01-01\n" +
                " */\n" +
                "CREATE PROCEDURE [ABB].[ABB.ChangeStatus]\n" +
                "AS\n" +
                "BEGIN\n" +
                "    SELECT 1\n" +
                "END\n";

            ModuleRef m = ModuleFileParser.TryIdentify(file);

            Assert.NotNull(m);
            Assert.Equal("ABB", m.Schema);
            Assert.Equal("ABB.ChangeStatus", m.Name);
            Assert.Equal(DbObjectKind.Procedure, m.Kind);
        }

        [Fact]
        public void Table_File_Named_With_Spaces_Is_Identified_By_Contents_Not_File_Name()
        {
            // Modelled on dbo\Tables\Zasticeni racuni.sql -- the file NAME is irrelevant.
            string file = "CREATE TABLE [dbo].[Zasticeni racuni] (Id INT NOT NULL)";

            ModuleRef m = ModuleFileParser.TryIdentify(file);

            Assert.NotNull(m);
            Assert.Equal("dbo", m.Schema);
            Assert.Equal("Zasticeni racuni", m.Name);
            Assert.Equal(DbObjectKind.Table, m.Kind);
        }

        [Fact]
        public void Set_Lines_Before_Create_Are_Ignored()
        {
            string file =
                "SET ANSI_NULLS ON\n" +
                "GO\n" +
                "SET QUOTED_IDENTIFIER ON\n" +
                "GO\n" +
                "CREATE VIEW dbo.SomeView AS SELECT 1 AS X\n";

            ModuleRef m = ModuleFileParser.TryIdentify(file);

            Assert.NotNull(m);
            Assert.Equal("dbo", m.Schema);
            Assert.Equal("SomeView", m.Name);
            Assert.Equal(DbObjectKind.View, m.Kind);
        }

        [Fact]
        public void File_Defining_Nothing_Returns_Null()
        {
            string file = "-- just a comment, no DDL here\nPRINT 'hello'\n";
            Assert.Null(ModuleFileParser.TryIdentify(file));
        }

        [Fact]
        public void Empty_And_Null_Text_Never_Throws()
        {
            Assert.Null(ModuleFileParser.TryIdentify(""));
            Assert.Null(ModuleFileParser.TryIdentify(null));
        }

        [Fact]
        public void Guard_Drop_Before_Create_Still_Resolves_To_The_Create()
        {
            string file =
                "IF OBJECT_ID('dbo.Foo') IS NOT NULL\n" +
                "    DROP PROCEDURE dbo.Foo\n" +
                "GO\n" +
                "CREATE PROCEDURE dbo.Foo AS SELECT 1\n";

            ModuleRef m = ModuleFileParser.TryIdentify(file);

            Assert.NotNull(m);
            Assert.Equal("dbo", m.Schema);
            Assert.Equal("Foo", m.Name);
        }
    }
}
