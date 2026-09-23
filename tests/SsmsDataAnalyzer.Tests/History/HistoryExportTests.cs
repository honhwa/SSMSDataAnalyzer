using System;
using System.Collections.Generic;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public sealed class HistoryExportTests
    {
        private static readonly DateTime Generated = new DateTime(2026, 9, 23, 14, 5, 0, DateTimeKind.Local);

        private static HistoryEntry Entry(string text, string server = "SQLTEST7", string database = "TestDB") =>
            new HistoryEntry
            {
                Id = Guid.NewGuid(),
                StartedUtc = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc),
                Server = server,
                Database = database,
                Text = text
            };

        [Fact]
        public void WritesOneBlockPerEntry_SeparatedByGo()
        {
            string script = HistoryExport.ToSqlScript(
                new[] { Entry("SELECT 1"), Entry("SELECT 2") }, Generated);

            Assert.Contains("SELECT 1", script);
            Assert.Contains("SELECT 2", script);
            Assert.Equal(2, CountOccurrences(script, "\nGO"));
        }

        [Fact]
        public void QueryContainingBlockCommentEnd_CannotEscapeTheHeader()
        {
            // A block-comment header would be terminated early by this text, turning the rest of
            // the header into executable script. Headers are line comments for exactly this reason.
            string script = HistoryExport.ToSqlScript(new[] { Entry("SELECT '*/ DROP TABLE x --'") }, Generated);

            foreach (string line in script.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "GO") continue;
                if (trimmed.StartsWith("--")) continue;
                Assert.Equal("SELECT '*/ DROP TABLE x --'", trimmed);
            }
        }

        [Fact]
        public void NewlineInAServerName_StaysInsideTheComment()
        {
            string script = HistoryExport.ToSqlScript(
                new[] { Entry("SELECT 1", server: "EVIL\r\nDROP TABLE x") }, Generated);

            foreach (string line in script.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "GO" || trimmed.StartsWith("--")) continue;
                Assert.Equal("SELECT 1", trimmed);
            }
        }

        [Fact]
        public void SaysSoWhenNothingMatched()
        {
            string script = HistoryExport.ToSqlScript(new List<HistoryEntry>(), Generated);
            Assert.Contains("no entries matched", script);
        }

        [Fact]
        public void WarnsThatTheFileIsNotEncrypted()
        {
            string script = HistoryExport.ToSqlScript(new[] { Entry("SELECT 1") }, Generated);
            Assert.Contains("NOT encrypted", script);
        }

        [Fact]
        public void NullsAndEmptyInputAreTolerated()
        {
            Assert.Contains("no entries matched", HistoryExport.ToSqlScript(null, Generated));
            Assert.Contains("no entries matched", HistoryExport.ToSqlScript(new HistoryEntry[] { null }, Generated));
        }

        [Fact]
        public void MarksATruncatedQuery()
        {
            var entry = Entry("SELECT 1");
            entry.TextTruncated = true;
            Assert.Contains("truncated", HistoryExport.ToSqlScript(new[] { entry }, Generated));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
            return count;
        }
    }
}
