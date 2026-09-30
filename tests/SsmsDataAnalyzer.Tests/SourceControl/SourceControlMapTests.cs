using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class SourceControlMapTests
    {
        [Fact]
        public void Server_Specific_Line_Wins_Over_Database_Only()
        {
            string text =
                "# database[@server] = path to the .sqlproj\n" +
                "AgricultureFinances = C:\\Db\\Finances.sqlproj\n" +
                "AgricultureFinances@SQLTEST7 = C:\\Db\\Finances.Test.sqlproj\n";

            SourceControlMap map = SourceControlMap.Parse(text);

            Assert.Equal("C:\\Db\\Finances.Test.sqlproj", map.FindProject("SQLTEST7", "AgricultureFinances"));
            Assert.Equal("C:\\Db\\Finances.sqlproj", map.FindProject("SQLPROD1", "AgricultureFinances"));
            Assert.Empty(map.Problems);
        }

        [Fact]
        public void Path_Containing_At_Sign_Is_Parsed_Correctly()
        {
            // Key splits on the LAST '@', so a path containing '@' does not get mistaken for a server marker.
            string text = "AgricultureFarm@SQLTEST7 = C:\\Db@Copy\\Farm.sqlproj\n";

            SourceControlMap map = SourceControlMap.Parse(text);

            Assert.Equal("C:\\Db@Copy\\Farm.sqlproj", map.FindProject("SQLTEST7", "AgricultureFarm"));
        }

        [Fact]
        public void Comments_Are_Preserved_By_Serialize_After_With()
        {
            string text =
                "# top comment\n" +
                "AgricultureFinances = C:\\Db\\Finances.sqlproj\n" +
                "# another comment\n";

            SourceControlMap map = SourceControlMap.Parse(text);
            SourceControlMap updated = map.With("AgricultureFarm", null, "C:\\Db\\Farm.sqlproj");

            string serialized = updated.Serialize();
            Assert.Contains("# top comment", serialized);
            Assert.Contains("# another comment", serialized);
            Assert.Contains("AgricultureFarm = C:\\Db\\Farm.sqlproj", serialized);
        }

        [Fact]
        public void With_Replaces_Matching_Line_In_Place_Rather_Than_Appending()
        {
            string text = "AgricultureFinances = C:\\Old.sqlproj\n";
            SourceControlMap map = SourceControlMap.Parse(text);

            SourceControlMap updated = map.With("AgricultureFinances", null, "C:\\New.sqlproj");

            string serialized = updated.Serialize();
            Assert.Equal("AgricultureFinances = C:\\New.sqlproj", serialized.Trim());
            Assert.Equal("C:\\New.sqlproj", updated.FindProject("AnyServer", "AgricultureFinances"));
        }

        [Fact]
        public void Malformed_Line_Is_Reported_As_A_Problem_Not_An_Exception()
        {
            string text = "this line has no equals sign\nAgricultureFinances = C:\\Db\\Finances.sqlproj\n";

            SourceControlMap map = SourceControlMap.Parse(text);

            Assert.Single(map.Problems);
            Assert.Contains("line 1", map.Problems[0]);
            Assert.Equal("C:\\Db\\Finances.sqlproj", map.FindProject("Any", "AgricultureFinances"));
        }

        [Fact]
        public void Unmapped_Database_Returns_Null()
        {
            SourceControlMap map = SourceControlMap.Parse("AgricultureFinances = C:\\Db\\Finances.sqlproj\n");
            Assert.Null(map.FindProject("Any", "SomethingElse"));
        }

        [Fact]
        public void Empty_And_Null_Text_Never_Throws()
        {
            Assert.Null(SourceControlMap.Parse("").FindProject("s", "d"));
            Assert.Null(SourceControlMap.Parse(null).FindProject("s", "d"));
        }
    }
}
