using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.ScriptObject;
using SsmsDataAnalyzer.Tests.TestSupport;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ScriptObject
{
    public class ObjectScriptTextTests
    {
        [Fact]
        public void CreateToAlter_ReplacesOnlyTheLeadingKeyword()
        {
            const string def = "/* CREATE PROCEDURE old */\r\n-- create note\r\ncreate   PROCEDURE dbo.p AS SELECT 'CREATE' AS [create];";
            Assert.Equal(
                "/* CREATE PROCEDURE old */\r\n-- create note\r\nALTER   PROCEDURE dbo.p AS SELECT 'CREATE' AS [create];",
                ObjectScriptText.RewriteCreateToAlter(def));
        }

        [Fact]
        public void CreateOrAlter_BecomesAlter()
        {
            Assert.Equal("ALTER VIEW v AS SELECT 1 AS x",
                ObjectScriptText.RewriteCreateToAlter("CREATE OR ALTER VIEW v AS SELECT 1 AS x"));
        }

        [Fact]
        public void NotStartingWithCreate_ReturnsNull()
        {
            Assert.Null(ObjectScriptText.RewriteCreateToAlter("SELECT 1"));
            Assert.Null(ObjectScriptText.RewriteCreateToAlter("[CREATE] VIEW"));
            Assert.Null(ObjectScriptText.RewriteCreateToAlter(""));
        }

        [Fact]
        public void CreateBatchToAlter_RewritesOnlyTheModuleBatch()
        {
            var batches = new[]
            {
                "/****** Object:  View [dbo].[v]    Script Date: x ******/\r\nSET ANSI_NULLS ON",
                "SET QUOTED_IDENTIFIER ON",
                "CREATE   VIEW [dbo].[v]\r\nAS\r\nSELECT 1 AS [create]"
            };
            var result = ObjectScriptText.RewriteCreateBatchToAlter(batches);
            Assert.Equal(batches[0], result[0]);
            Assert.Equal(batches[1], result[1]);
            Assert.Equal("ALTER   VIEW [dbo].[v]\r\nAS\r\nSELECT 1 AS [create]", result[2]);
            Assert.Null(ObjectScriptText.RewriteCreateBatchToAlter(new[] { "SET ANSI_NULLS ON" }));
        }

        [Fact]
        public void Compose_AddsCommentUseAndGoPerBatch()
        {
            string script = ObjectScriptText.Compose("My]Db", new[] { "SET ANSI_NULLS ON\n", "", "CREATE TABLE t (a int)\n\n" },
                SqlObjectKinds.NoAlterFormComment(SqlObjectKind.Table));
            Assert.Equal(
                "-- Table has no ALTER form; CREATE script shown.\r\nUSE [My]]Db]\r\nGO\r\nSET ANSI_NULLS ON\r\nGO\r\nCREATE TABLE t (a int)\r\nGO\r\n",
                script);
        }

        [Theory]
        [InlineData("U", SqlObjectKind.Table, false)]
        [InlineData("V ", SqlObjectKind.View, true)]
        [InlineData("P", SqlObjectKind.StoredProcedure, true)]
        [InlineData("IF", SqlObjectKind.InlineTableFunction, true)]
        [InlineData("TR", SqlObjectKind.Trigger, true)]
        [InlineData("SN", SqlObjectKind.Synonym, false)]
        [InlineData("TT", SqlObjectKind.UserDefinedTableType, false)]
        [InlineData("PK", SqlObjectKind.Unsupported, false)]
        public void Kinds_MapAndKnowAlterSupport(string code, SqlObjectKind kind, bool alter)
        {
            Assert.Equal(kind, SqlObjectKinds.FromTypeCode(code));
            Assert.Equal(alter, SqlObjectKinds.SupportsAlter(kind));
        }

        [Fact]
        [Trait("Category", "Integration")]
        public async Task Resolve_BracketedDottedTable_AndUnqualifiedName()
        {
            using (var connection = new SqlConnection(TestDb.ConnectionString))
            {
                await connection.OpenAsync();

                var dotted = SqlNameAtPosition.Find("SELECT * FROM [dbo].[Intervention.ABB.Request.Change.History]", 20).Name;
                var resolved = await ScriptObjectQueries.ResolveAsync(connection, dotted, CancellationToken.None);
                Assert.NotNull(resolved);
                Assert.Equal("dbo", resolved.Schema);
                Assert.Equal("Intervention.ABB.Request.Change.History", resolved.Name);
                Assert.Equal(SqlObjectKind.Table, resolved.Kind);

                // Unqualified: SQL Server's own resolution (default schema, then dbo) decides.
                var unqualified = await ScriptObjectQueries.ResolveAsync(connection, new SqlMultipartName(null, null, null, "ParentSingle"), CancellationToken.None);
                Assert.Null(unqualified); // lives in [ref], not reachable unqualified

                var bracket = await ScriptObjectQueries.ResolveAsync(connection, new SqlMultipartName(null, null, null, "Bracket]Table"), CancellationToken.None);
                Assert.Equal("Bracket]Table", bracket.Name);

                Assert.Null(await ScriptObjectQueries.ResolveAsync(connection, new SqlMultipartName(null, null, "dbo", "NoSuchObject"), CancellationToken.None));
            }
        }
    }
}
