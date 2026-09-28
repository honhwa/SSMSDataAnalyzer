using System.Collections.Generic;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class DdlDetectorTests
    {
        [Fact]
        public void CreateOrAlter_Is_Recognised()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("CREATE OR ALTER PROCEDURE ABB.ChangeStatus AS SELECT 1");
            DdlStatement s = Assert.Single(found);
            Assert.Equal(DdlAction.CreateOrAlter, s.Action);
            Assert.Equal(DbObjectKind.Procedure, s.Target.Kind);
            Assert.Equal("ABB", s.Target.Schema);
            Assert.Equal("ChangeStatus", s.Target.Name);
        }

        [Fact]
        public void CreateProc_Is_Recognised()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("CREATE PROC dbo.Foo AS SELECT 1");
            DdlStatement s = Assert.Single(found);
            Assert.Equal(DdlAction.Create, s.Action);
            Assert.Equal(DbObjectKind.Procedure, s.Target.Kind);
            Assert.Equal("dbo", s.Target.Schema);
            Assert.Equal("Foo", s.Target.Name);
        }

        [Fact]
        public void DropProcedureIfExists_CommaList_Produces_Two_Statements()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("DROP PROCEDURE IF EXISTS dbo.a, dbo.b");
            Assert.Equal(2, found.Count);
            Assert.All(found, s => Assert.Equal(DdlAction.Drop, s.Action));
            Assert.Equal("a", found[0].Target.Name);
            Assert.Equal("b", found[1].Target.Name);
        }

        [Fact]
        public void TempTable_Is_Skipped()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("CREATE TABLE #temp (Id INT)");
            Assert.Empty(found);
        }

        [Fact]
        public void TableVariable_Is_Skipped()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("DROP TABLE @t");
            Assert.Empty(found);
        }

        [Fact]
        public void Ddl_Inside_A_String_Is_Skipped()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("EXEC('CREATE PROCEDURE dbo.Ghost AS SELECT 1')");
            Assert.Empty(found);
        }

        [Fact]
        public void Ddl_Inside_A_Comment_Is_Skipped()
        {
            IReadOnlyList<DdlStatement> found = DdlDetector.Find("-- CREATE PROCEDURE dbo.Ghost AS SELECT 1\nSELECT 1");
            Assert.Empty(found);
        }

        [Fact]
        public void Several_Statements_In_One_Script_Are_All_Found()
        {
            string sql =
                "CREATE PROCEDURE dbo.A AS SELECT 1\n" +
                "GO\n" +
                "ALTER VIEW dbo.B AS SELECT 2\n" +
                "GO\n" +
                "DROP FUNCTION dbo.C\n";

            IReadOnlyList<DdlStatement> found = DdlDetector.Find(sql);

            Assert.Equal(3, found.Count);
            Assert.Equal(("A", DdlAction.Create, DbObjectKind.Procedure), (found[0].Target.Name, found[0].Action, found[0].Target.Kind));
            Assert.Equal(("B", DdlAction.Alter, DbObjectKind.View), (found[1].Target.Name, found[1].Action, found[1].Target.Kind));
            Assert.Equal(("C", DdlAction.Drop, DbObjectKind.Function), (found[2].Target.Name, found[2].Action, found[2].Target.Kind));
        }

        [Fact]
        public void Empty_And_Null_Text_Never_Throws()
        {
            Assert.Empty(DdlDetector.Find(""));
            Assert.Empty(DdlDetector.Find(null));
            Assert.Empty(DdlDetector.Find("this is not sql at all {[}]"));
        }
    
        [Fact]
        public void ThreePartName_KeepsTheDatabase_SoTheChangeIsNotBlamedOnTheWrongProject()
        {
            // Run while the query window is in database A: this changes OtherDb, not A.
            var found = DdlDetector.Find("DROP TABLE OtherDb.dbo.Orders");

            Assert.Single(found);
            Assert.Equal("OtherDb", found[0].Database);
            Assert.Equal("dbo", found[0].Target.Schema);
            Assert.Equal("Orders", found[0].Target.Name);
        }

        [Fact]
        public void TwoPartName_HasNoDatabase()
        {
            var found = DdlDetector.Find("ALTER PROCEDURE [ABB].[ABB.ChangeStatus] AS SELECT 1");

            Assert.Single(found);
            Assert.Null(found[0].Database);
            Assert.Equal("ABB.ChangeStatus", found[0].Target.Name);
        }

        [Fact]
        public void FourPartLinkedServerName_IsSkipped()
        {
            Assert.Empty(DdlDetector.Find("DROP TABLE LinkedSrv.OtherDb.dbo.Orders"));
        }
    }
}
