using System.Collections.Generic;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class RepoIndexTests
    {
        [Fact]
        public void Find_Returns_The_File_That_Defines_The_Module()
        {
            var files = new List<RepoFile>
            {
                new RepoFile(@"ABB\Stored Procedures\ABB.ChangeStatus.sql", "CREATE PROCEDURE [ABB].[ABB.ChangeStatus] AS SELECT 1"),
                new RepoFile(@"dbo\Tables\Zasticeni racuni.sql", "CREATE TABLE dbo.[Zasticeni racuni] (Id INT)"),
            };
            SqlProjectFiles project = SqlProjectReader.Read(
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                "<Build Include=\"ABB\\Stored Procedures\\ABB.ChangeStatus.sql\" />" +
                "<Build Include=\"dbo\\Tables\\Zasticeni racuni.sql\" />" +
                "</ItemGroup></Project>");

            RepoIndex index = RepoIndex.Build(files, project);

            Assert.Equal(2, index.FileCount);
            Assert.Equal(0, index.UnrecognisedFileCount);

            var target = new ModuleRef("ABB", "ABB.ChangeStatus", DbObjectKind.Procedure);
            IReadOnlyList<RepoEntry> found = index.Find(target);
            RepoEntry entry = Assert.Single(found);
            Assert.Equal(@"ABB\Stored Procedures\ABB.ChangeStatus.sql", entry.RelativePath);
            Assert.True(entry.InProject);
        }

        [Fact]
        public void File_Not_In_Sqlproj_Is_Found_But_Flagged()
        {
            var files = new List<RepoFile> { new RepoFile("Extra.sql", "CREATE VIEW dbo.Extra AS SELECT 1") };
            SqlProjectFiles project = SqlProjectReader.Read(
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
                "<Build Include=\"Other.sql\" /></ItemGroup></Project>");

            RepoIndex index = RepoIndex.Build(files, project);

            RepoEntry entry = Assert.Single(index.Find(new ModuleRef("dbo", "Extra", DbObjectKind.View)));
            Assert.False(entry.InProject);
        }

        [Fact]
        public void Two_Files_Defining_The_Same_Object_Are_Both_Reported()
        {
            var files = new List<RepoFile>
            {
                new RepoFile("A.sql", "CREATE PROCEDURE dbo.Dup AS SELECT 1"),
                new RepoFile("B.sql", "CREATE PROCEDURE dbo.Dup AS SELECT 2"),
            };
            RepoIndex index = RepoIndex.Build(files, SqlProjectReader.Read(null));

            IReadOnlyList<RepoEntry> found = index.Find(new ModuleRef("dbo", "Dup", DbObjectKind.Procedure));
            Assert.Equal(2, found.Count);
        }

        [Fact]
        public void File_Defining_Nothing_Is_Not_Indexed_But_Still_Counted()
        {
            var files = new List<RepoFile> { new RepoFile("Junk.sql", "PRINT 'hi'") };
            RepoIndex index = RepoIndex.Build(files, SqlProjectReader.Read(null));

            Assert.Equal(1, index.FileCount);
            Assert.Equal(1, index.UnrecognisedFileCount);
        }

        [Fact]
        public void Find_On_Empty_Index_Returns_Empty_Not_Null()
        {
            RepoIndex index = RepoIndex.Build(null, SqlProjectReader.Read(null));
            Assert.Empty(index.Find(new ModuleRef("dbo", "Nothing", DbObjectKind.Procedure)));
            Assert.Empty(index.Find(null));
        }
    }
}
