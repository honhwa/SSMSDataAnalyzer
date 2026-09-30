using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>§13.7 step 8 as amended by §13b.4: tables compare columns only.</summary>
    public class SyncCheckerTableTests
    {
        private static readonly ModuleRef Table = new ModuleRef("dbo", "Widget", DbObjectKind.Table);

        private const string RepoTable =
            "CREATE TABLE [dbo].[Widget] (\r\n    [Id]   INT           IDENTITY (1, 1) NOT NULL,\r\n" +
            "    [Name] NVARCHAR (50) NULL,\r\n    CONSTRAINT [PK_Widget] PRIMARY KEY CLUSTERED ([Id] ASC)\r\n);";

        private static ChangeCandidate Candidate() =>
            new ChangeCandidate("SRV1", "DB1", Table, DateTime.UtcNow, ChangeSource.QueryHistory, DdlAction.Alter);

        private static RepoIndex Repo(string text)
        {
            var files = new List<RepoFile> { new RepoFile("Widget.sql", text) };
            SqlProjectFiles project = SqlProjectReader.Read(
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                "<Build Include=\"Widget.sql\" /></ItemGroup></Project>");
            return RepoIndex.Build(files, project);
        }

        private static IReadOnlyList<TableColumn> ServerColumns(string nameType = "nvarchar", short nameMax = 100) =>
            new[]
            {
                TableColumn.FromCatalog("Id", "int", 4, 10, 0, false, true, false),
                TableColumn.FromCatalog("Name", nameType, nameMax, 0, 0, true, false, false),
            };

        [Fact]
        public void Step8_Columns_Null_Is_NotCompared_And_Points_At_The_History_Source()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, null), Repo(RepoTable));
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("History", f.Reason);
            Assert.Contains("Widget.sql", f.Reason);
        }

        [Fact]
        public void Step8_Three_Argument_Constructor_Leaves_Columns_Null()
        {
            var s = new ServerObjectState(true, DbObjectKind.Table, null);
            Assert.Null(s.Columns);
        }

        [Fact]
        public void Step8_Unparseable_File_Is_NotCompared()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, ServerColumns()),
                Repo("CREATE TABLE [dbo].[Widget] ([Id] INT NOT NULL, [X] SOMETHING WEIRD NULL);"));
            Assert.Equal(SyncStatus.NotCompared, f.Status);
            Assert.Contains("could not read the CREATE TABLE in Widget.sql", f.Reason);
        }

        [Fact]
        public void Step8_Same_Columns_Matches()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, ServerColumns()), Repo(RepoTable));
            Assert.Equal(SyncStatus.Matches, f.Status);
            Assert.Contains("indexes and constraints not compared", f.Reason);
        }

        [Fact]
        public void Step8_Different_Columns_DiffersFromRepo_And_Lists_The_Differences()
        {
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, ServerColumns(nameMax: 200)), Repo(RepoTable));
            Assert.Equal(SyncStatus.DiffersFromRepo, f.Status);
            Assert.Equal("Widget.sql", f.RelativePath);
            Assert.Contains("[Name]: nvarchar(50) NULL in repo, nvarchar(100) NULL on server", f.Reason);
        }

        [Fact]
        public void Step8_Column_Only_On_Server_Is_Reported()
        {
            var cols = new List<TableColumn>(ServerColumns())
            {
                TableColumn.FromCatalog("Extra", "int", 4, 10, 0, true, false, false)
            };
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, cols), Repo(RepoTable));
            Assert.Equal(SyncStatus.DiffersFromRepo, f.Status);
            Assert.Contains("[Extra] only on server", f.Reason);
        }

        [Fact]
        public void Steps_Before_8_Still_Win_For_Tables()
        {
            // Not in the .sqlproj: step 7 answers before any column comparison.
            var files = new List<RepoFile> { new RepoFile("Widget.sql", RepoTable) };
            SqlProjectFiles project = SqlProjectReader.Read(
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                "<Build Include=\"Other.sql\" /></ItemGroup></Project>");
            SyncFinding f = SyncChecker.Check(Candidate(),
                new ServerObjectState(true, DbObjectKind.Table, null, ServerColumns(nameMax: 200)),
                RepoIndex.Build(files, project));
            Assert.Equal(SyncStatus.OnDiskNotInProject, f.Status);
        }
    }
}
