using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public class HistoryFilterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        private static HistoryEntry Entry(
            string text = "SELECT 1",
            string server = "SRV1",
            string database = "DB1",
            bool starred = false,
            HistoryOutcome? outcome = HistoryOutcome.Success,
            DateTime? startedUtc = null,
            string documentName = null)
        {
            return new HistoryEntry
            {
                Id = Guid.NewGuid(),
                Text = text,
                Server = server,
                Database = database,
                Starred = starred,
                Outcome = outcome,
                StartedUtc = startedUtc ?? Now,
                DocumentName = documentName,
            };
        }

        [Fact]
        public void Parse_Null_MatchesEverything()
        {
            var filter = HistoryFilter.Parse(null);
            Assert.True(filter.Matches(Entry(), Now));
        }

        [Fact]
        public void Parse_NeverThrows_OnGarbage()
        {
            var filter = HistoryFilter.Parse("sql: server: \"unterminated starred:notabool error:");
            // Should not throw constructing or matching.
            filter.Matches(Entry(), Now);
        }

        [Fact]
        public void BareWord_MatchesInsideText_CaseInsensitive()
        {
            var filter = HistoryFilter.Parse("select");
            Assert.True(filter.Matches(Entry(text: "SELECT * FROM Foo"), Now));
            Assert.False(filter.Matches(Entry(text: "UPDATE Foo SET x=1"), Now));
        }

        [Fact]
        public void SqlPrefix_MatchesInsideText()
        {
            var filter = HistoryFilter.Parse("sql:foo");
            Assert.True(filter.Matches(Entry(text: "SELECT * FROM foo"), Now));
            Assert.False(filter.Matches(Entry(text: "SELECT * FROM bar"), Now));
        }

        [Fact]
        public void ServerPrefix_Matches()
        {
            var filter = HistoryFilter.Parse("server:SRV1");
            Assert.True(filter.Matches(Entry(server: "SRV1"), Now));
            Assert.False(filter.Matches(Entry(server: "SRV2"), Now));
        }

        [Fact]
        public void DatabasePrefix_Matches()
        {
            var filter = HistoryFilter.Parse("database:DB1");
            Assert.True(filter.Matches(Entry(database: "DB1"), Now));
            Assert.False(filter.Matches(Entry(database: "DB2"), Now));
        }

        [Fact]
        public void DbPrefix_IsAliasForDatabase()
        {
            var filter = HistoryFilter.Parse("db:DB1");
            Assert.True(filter.Matches(Entry(database: "DB1"), Now));
        }

        [Theory]
        [InlineData("starred:true", true, true)]
        [InlineData("starred:false", false, true)]
        [InlineData("starred:true", false, false)]
        [InlineData("starred:false", true, false)]
        public void StarredPrefix_Matches(string search, bool entryStarred, bool expected)
        {
            var filter = HistoryFilter.Parse(search);
            Assert.Equal(expected, filter.Matches(Entry(starred: entryStarred), Now));
        }

        [Theory]
        [InlineData("error:true", HistoryOutcome.Error, true)]
        [InlineData("error:false", HistoryOutcome.Error, false)]
        [InlineData("error:false", HistoryOutcome.Success, true)]
        [InlineData("error:true", HistoryOutcome.Success, false)]
        [InlineData("error:false", null, true)]
        public void ErrorPrefix_Matches(string search, HistoryOutcome? outcome, bool expected)
        {
            var filter = HistoryFilter.Parse(search);
            Assert.Equal(expected, filter.Matches(Entry(outcome: outcome), Now));
        }

        [Fact]
        public void QuotedPhrase_MatchesAsOneTerm()
        {
            var filter = HistoryFilter.Parse("\"select * from\"");
            Assert.True(filter.Matches(Entry(text: "select * from Customers"), Now));
            Assert.False(filter.Matches(Entry(text: "select top 1 * from Customers"), Now));
        }

        [Fact]
        public void QuotedPhrase_AfterPrefix_MatchesAsOneTerm()
        {
            var filter = HistoryFilter.Parse("server:\"My Server\"");
            Assert.True(filter.Matches(Entry(server: "My Server"), Now));
            Assert.False(filter.Matches(Entry(server: "My"), Now));
        }

        [Fact]
        public void MultipleTerms_AreAnded()
        {
            var filter = HistoryFilter.Parse("select server:SRV1 starred:true");

            Assert.True(filter.Matches(Entry(text: "select 1", server: "SRV1", starred: true), Now));
            Assert.False(filter.Matches(Entry(text: "select 1", server: "SRV1", starred: false), Now));
            Assert.False(filter.Matches(Entry(text: "select 1", server: "SRV2", starred: true), Now));
            Assert.False(filter.Matches(Entry(text: "update 1", server: "SRV1", starred: true), Now));
        }

        [Fact]
        public void DateRange_Today_ExcludesOlderEntries()
        {
            var filter = HistoryFilter.Parse(null);
            filter.DateRange = HistoryDateRange.Today;

            var today = Entry(startedUtc: Now);
            var yesterday = Entry(startedUtc: Now.AddDays(-1));

            Assert.True(filter.Matches(today, Now));
            Assert.False(filter.Matches(yesterday, Now));
        }

        [Fact]
        public void DateRange_Last7Days_Boundary()
        {
            var filter = HistoryFilter.Parse(null);
            filter.DateRange = HistoryDateRange.Last7Days;

            Assert.True(filter.Matches(Entry(startedUtc: Now.AddDays(-6)), Now));
            Assert.False(filter.Matches(Entry(startedUtc: Now.AddDays(-8)), Now));
        }

        [Fact]
        public void DateRange_Last30Days_Boundary()
        {
            var filter = HistoryFilter.Parse(null);
            filter.DateRange = HistoryDateRange.Last30Days;

            Assert.True(filter.Matches(Entry(startedUtc: Now.AddDays(-29)), Now));
            Assert.False(filter.Matches(Entry(startedUtc: Now.AddDays(-31)), Now));
        }

        [Fact]
        public void DateRange_All_IncludesEverything()
        {
            var filter = HistoryFilter.Parse(null);
            filter.DateRange = HistoryDateRange.All;

            Assert.True(filter.Matches(Entry(startedUtc: Now.AddYears(-5)), Now));
        }

        [Fact]
        public void DocPrefix_Matches_CaseInsensitive()
        {
            var filter = HistoryFilter.Parse("doc:SQLQuery1.sql");
            Assert.True(filter.Matches(Entry(documentName: "sqlquery1.sql"), Now));
            Assert.False(filter.Matches(Entry(documentName: "SQLQuery2.sql"), Now));
        }

        [Theory]
        [InlineData("closed:true", "Closed.sql", true)]   // not in open set -> matches closed:true
        [InlineData("closed:true", "Open.sql", false)]    // is open -> does not match closed:true
        [InlineData("closed:false", "Open.sql", true)]    // is open -> matches closed:false
        [InlineData("closed:false", "Closed.sql", false)] // not open -> does not match closed:false
        public void ClosedPrefix_Matches_WithPopulatedOpenSet(string search, string documentName, bool expected)
        {
            var filter = HistoryFilter.Parse(search);
            filter.OpenDocumentNames = new HashSet<string>(new[] { "Open.sql" }, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(expected, filter.Matches(Entry(documentName: documentName), Now));
        }

        [Fact]
        public void ClosedPrefix_MatchesCaseInsensitively_AgainstOpenSet()
        {
            var filter = HistoryFilter.Parse("closed:false");
            filter.OpenDocumentNames = new HashSet<string>(new[] { "Open.sql" }, StringComparer.OrdinalIgnoreCase);

            Assert.True(filter.Matches(Entry(documentName: "OPEN.SQL"), Now));
        }

        [Theory]
        [InlineData("closed:true")]
        [InlineData("closed:false")]
        public void ClosedPrefix_WithNullOpenDocumentNames_MatchesNothing(string search)
        {
            var filter = HistoryFilter.Parse(search);
            Assert.Null(filter.OpenDocumentNames);

            Assert.False(filter.Matches(Entry(documentName: "Anything.sql"), Now));
        }
    }
}
