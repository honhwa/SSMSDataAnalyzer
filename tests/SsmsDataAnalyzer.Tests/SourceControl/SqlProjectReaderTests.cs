using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class SqlProjectReaderTests
    {
        [Fact]
        public void Explicit_Includes_Are_Honoured()
        {
            string xml =
                "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <ItemGroup>\n" +
                "    <Build Include=\"ABB\\Stored Procedures\\ABB.ChangeStatus.sql\" />\n" +
                "    <Build Include=\"dbo\\Tables\\Zasticeni racuni.sql\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>";

            SqlProjectFiles files = SqlProjectReader.Read(xml);

            Assert.False(files.IncludesAreImplicit);
            Assert.True(files.Contains(@"ABB\Stored Procedures\ABB.ChangeStatus.sql"));
            Assert.True(files.Contains("ABB/Stored Procedures/ABB.ChangeStatus.sql"));
            Assert.True(files.Contains(@"dbo\Tables\Zasticeni racuni.sql"));
            Assert.False(files.Contains(@"dbo\Tables\NotListed.sql"));
        }

        [Fact]
        public void Wildcard_Include_Makes_The_Project_Implicit()
        {
            string xml =
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <ItemGroup>\n" +
                "    <Build Include=\"**\\*.sql\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>";

            SqlProjectFiles files = SqlProjectReader.Read(xml);

            Assert.True(files.IncludesAreImplicit);
            Assert.True(files.Contains(@"anything\at\all.sql"));
        }

        [Fact]
        public void No_Build_Items_Makes_The_Project_Implicit()
        {
            string xml =
                "<Project Sdk=\"MSBuild.Sdk.SqlProj\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetDatabaseSet>true</TargetDatabaseSet>\n" +
                "  </PropertyGroup>\n" +
                "</Project>";

            SqlProjectFiles files = SqlProjectReader.Read(xml);

            Assert.True(files.IncludesAreImplicit);
            Assert.True(files.Contains(@"whatever.sql"));
        }

        [Fact]
        public void Malformed_Xml_Makes_The_Project_Implicit_Not_An_Exception()
        {
            string malformed = "<Project><ItemGroup><Build Include=\"a.sql\" ></ItemGroup>"; // unclosed

            SqlProjectFiles files = SqlProjectReader.Read(malformed);

            Assert.True(files.IncludesAreImplicit);
            Assert.True(files.Contains("anything.sql"));
        }

        [Fact]
        public void Empty_And_Null_Text_Never_Throws()
        {
            Assert.True(SqlProjectReader.Read("").IncludesAreImplicit);
            Assert.True(SqlProjectReader.Read(null).IncludesAreImplicit);
        }
    }
}
