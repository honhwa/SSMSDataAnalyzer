using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>Every branch of the §13.7 decision order, named after the branch.</summary>
    public class SyncCheckerTests
    {
        private static readonly ModuleRef Qualified = new ModuleRef("dbo", "Foo", DbObjectKind.Procedure);
        private static readonly ModuleRef Unqualified = new ModuleRef(null, "Foo", DbObjectKind.Procedure);

        private static ChangeCandidate Candidate(ModuleRef module = null, string server = "SRV1", string database = "DB1") =>
            new ChangeCandidate(server, database, module ?? Qualified, DateTime.UtcNow, ChangeSource.QueryHistory, DdlAction.Alter);

        private static RepoIndex EmptyRepo() => RepoIndex.Build(null, SqlProjectReader.Read(null));

        private static RepoIndex RepoWith(string relativePath, string text, bool inProject = true)
        {
            var files = new List<RepoFile> { new RepoFile(relativePath, text) };
            SqlProjectFiles project = inProject
                ? SqlProjectReader.Read("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                                         "<Build Include=\"" + relativePath + "\" /></ItemGroup></Project>")
                : SqlProjectReader.Read("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                                         "<Build Include=\"SomethingElse.sql\" /></ItemGroup></Project>");
            return RepoIndex.Build(files, project);
        }

        [Fact]
        public void Branch1_Repo_Null_Is_NotCompared()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 1"), null);
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("not mapped", f.Reason);
        }

        [Fact]
        public void Branch2_Unqualified_Schema_Is_NotCompared()
        {
            SyncFinding f = SyncChecker.Check(Candidate(Unqualified),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE Foo AS SELECT 1"),
                EmptyRepo());
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("schema", f.Reason);
        }

        [Fact]
        public void Branch3_Server_Null_Is_NotCompared()
        {
            SyncFinding f = SyncChecker.Check(Candidate(), null, EmptyRepo());
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("no open connection", f.Reason);
        }

        [Fact]
        public void Branch4a_Gone_From_Server_But_File_Exists_Is_DroppedButInRepo()
        {
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
            SyncFinding f = SyncChecker.Check(Candidate(), new ServerObjectState(false, null, null), repo);
            Assert.Equal(SyncStatus.DroppedButInRepo, f.Status);
            Assert.Equal("Foo.sql", f.RelativePath);
        }

        [Fact]
        public void Branch4b_Gone_From_Server_And_No_File_Is_Matches()
        {
            SyncFinding f = SyncChecker.Check(Candidate(), new ServerObjectState(false, null, null), EmptyRepo());
            Assert.Equal(SyncStatus.Matches, f.Status);
            Assert.Null(f.RelativePath);
        }

        [Fact]
        public void Branch5_Defined_Twice_Is_NotCompared()
        {
            var files = new List<RepoFile>
            {
                new RepoFile("A.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1"),
                new RepoFile("B.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 2"),
            };
            RepoIndex repo = RepoIndex.Build(files, SqlProjectReader.Read(null));

            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 1"), repo);

            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("2 files", f.Reason);
        }

        [Fact]
        public void Branch6_No_File_Is_MissingFromRepo()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 1"), EmptyRepo());
            Assert.Equal(SyncStatus.MissingFromRepo, f.Status);
            Assert.Null(f.RelativePath);
        }

        [Fact]
        public void Branch7_File_Not_In_Project_Is_OnDiskNotInProject()
        {
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1", inProject: false);
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 999"), repo);
            // Checked BEFORE comparing: even though definitions differ, OnDiskNotInProject wins.
            Assert.Equal(SyncStatus.OnDiskNotInProject, f.Status);
            Assert.Equal("Foo.sql", f.RelativePath);
        }

        [Fact]
        public void Branch8_Table_Is_NotCompared()
        {
            var tableModule = new ModuleRef("dbo", "Foo", DbObjectKind.Table);
            RepoIndex repo = RepoWith("Foo.sql", "CREATE TABLE dbo.Foo (Id INT)");
            SyncFinding f = SyncChecker.Check(Candidate(tableModule),
                new ServerObjectState(true, DbObjectKind.Table, null), repo);
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("table not compared", f.Reason);
        }

        [Fact]
        public void Branch9_Unreadable_Definition_Is_NotCompared()
        {
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, null), repo);
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("encrypted or CLR", f.Reason);
        }

        [Fact]
        public void Branch10_Equivalent_Definitions_Are_Matches()
        {
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "ALTER   PROCEDURE dbo.Foo AS SELECT 1"), repo);
            Assert.Equal(SyncStatus.Matches, f.Status);
        }

        [Fact]
        public void ModifyDateMoved_But_Definition_Unchanged_Is_Matches()
        {
            // sp_refreshsqlmodule moves sys.objects.modify_date without touching the module's
            // definition (verified live, 2026-09-28). The candidate only carries a timestamp and
            // a module ref -- SyncChecker has no modify_date of its own to compare, so this is
            // exactly the same code path as Branch10's Matches case; named separately because the
            // scenario (a no-op refresh nominating a "change" that never happened) is its own risk.
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
            var candidate = new ChangeCandidate("SRV1", "DB1", Qualified, DateTime.UtcNow,
                ChangeSource.ServerModifyDate, null);

            SyncFinding f = SyncChecker.Check(candidate,
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 1"), repo);

            Assert.Equal(SyncStatus.Matches, f.Status);
        }

        [Fact]
        public void Branch10_Different_Definitions_Are_DiffersFromRepo()
        {
            RepoIndex repo = RepoWith("Foo.sql", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Procedure, "CREATE PROCEDURE dbo.Foo AS SELECT 2"), repo);
            Assert.Equal(SyncStatus.DiffersFromRepo, f.Status);
        }
    }
}
