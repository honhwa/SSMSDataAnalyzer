using System;
using System.Text;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public class HistoryJsonTests
    {
        private static HistoryEntry SampleEntry(string text = "SELECT 1")
        {
            return new HistoryEntry
            {
                Id = Guid.NewGuid(),
                StartedUtc = new DateTime(2026, 9, 23, 12, 34, 56, 789, DateTimeKind.Utc),
                Server = "MYSERVER",
                Database = "MyDb",
                Login = "sa",
                AuthKind = HistoryAuthKind.SqlLogin,
                DocumentName = "SQLQuery1.sql",
                Text = text,
                TextTruncated = false,
                DurationMs = 42,
                Outcome = HistoryOutcome.Success,
                RowCount = 17,
            };
        }

        [Fact]
        public void RoundTrips_AllFields()
        {
            var entry = SampleEntry();

            string line = HistoryJson.Write(entry);
            bool ok = HistoryJson.TryParse(line, out HistoryEntry parsed);

            Assert.True(ok);
            Assert.Equal(entry.Id, parsed.Id);
            Assert.Equal(entry.StartedUtc, parsed.StartedUtc);
            Assert.Equal(DateTimeKind.Utc, parsed.StartedUtc.Kind);
            Assert.Equal(entry.Server, parsed.Server);
            Assert.Equal(entry.Database, parsed.Database);
            Assert.Equal(entry.Login, parsed.Login);
            Assert.Equal(entry.AuthKind, parsed.AuthKind);
            Assert.Equal(entry.DocumentName, parsed.DocumentName);
            Assert.Equal(entry.Text, parsed.Text);
            Assert.Equal(entry.TextTruncated, parsed.TextTruncated);
            Assert.Equal(entry.DurationMs, parsed.DurationMs);
            Assert.Equal(entry.Outcome, parsed.Outcome);
            Assert.Equal(entry.RowCount, parsed.RowCount);
        }

        [Fact]
        public void RoundTrips_NullableFieldsAsNull()
        {
            var entry = SampleEntry();
            entry.DurationMs = null;
            entry.Outcome = null;
            entry.RowCount = null;

            string line = HistoryJson.Write(entry);
            bool ok = HistoryJson.TryParse(line, out HistoryEntry parsed);

            Assert.True(ok);
            Assert.Null(parsed.DurationMs);
            Assert.Null(parsed.Outcome);
            Assert.Null(parsed.RowCount);
        }

        [Theory]
        [InlineData("simple")]
        [InlineData("with \"quotes\" inside")]
        [InlineData("back\\slash")]
        [InlineData("line1\nline2\ttabbed\rcarriage")]
        [InlineData("control-\u0001-char")]
        [InlineData("emoji \U0001F600 surrogate pair")]
        [InlineData("password = 'P@ss\"w0rd\\!'")]
        [InlineData("")]
        public void RoundTrips_NastyStrings(string text)
        {
            var entry = SampleEntry(text);

            string line = HistoryJson.Write(entry);
            bool ok = HistoryJson.TryParse(line, out HistoryEntry parsed);

            Assert.True(ok);
            Assert.Equal(text, parsed.Text);
        }

        [Fact]
        public void Write_ProducesSingleLine_NoTrailingNewline()
        {
            var entry = SampleEntry("line1\nline2");
            string line = HistoryJson.Write(entry);

            Assert.DoesNotContain("\r", line.TrimEnd('\r', '\n')); // escaped, not literal
            Assert.False(line.EndsWith("\n", StringComparison.Ordinal));
        }

        [Fact]
        public void TryParse_UnknownFields_AreIgnored()
        {
            var entry = SampleEntry();
            string line = HistoryJson.Write(entry);
            // Inject an unknown field before the closing brace.
            string withExtra = line.Substring(0, line.Length - 1) + ",\"futureField\":\"whatever\",\"futureNum\":123}";

            bool ok = HistoryJson.TryParse(withExtra, out HistoryEntry parsed);

            Assert.True(ok);
            Assert.Equal(entry.Id, parsed.Id);
            Assert.Equal(entry.Text, parsed.Text);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{\"id\":\"not-a-guid\",\"startedUtc\":\"2026-09-23T00:00:00.0000000Z\"}")]
        [InlineData("{\"startedUtc\":\"2026-09-23T00:00:00.0000000Z\"}")] // missing id
        [InlineData("{\"id\":\"11111111-1111-1111-1111-111111111111\"")] // torn / unterminated
        [InlineData(null)]
        public void TryParse_TornOrInvalidLines_ReturnFalse_NeverThrows(string line)
        {
            bool ok = HistoryJson.TryParse(line, out HistoryEntry entry);

            Assert.False(ok);
            Assert.Null(entry);
        }

        [Fact]
        public void Write_TruncatesTextOverLimit_AndSetsFlag()
        {
            string huge = new string('x', HistoryJson.MaxTextBytes + 100);
            var entry = SampleEntry(huge);
            entry.TextTruncated = false;

            string line = HistoryJson.Write(entry);
            bool ok = HistoryJson.TryParse(line, out HistoryEntry parsed);

            Assert.True(ok);
            Assert.True(parsed.TextTruncated);
            Assert.True(Encoding.UTF8.GetByteCount(parsed.Text) <= HistoryJson.MaxTextBytes);
        }

        [Fact]
        public void Write_DoesNotTruncate_TextUnderLimit()
        {
            var entry = SampleEntry("short text");

            string line = HistoryJson.Write(entry);
            HistoryJson.TryParse(line, out HistoryEntry parsed);

            Assert.False(parsed.TextTruncated);
            Assert.Equal("short text", parsed.Text);
        }

        [Fact]
        public void EditRoundTrips_StarredTrue()
        {
            var edit = new HistoryEdit { Id = Guid.NewGuid(), Starred = true, Deleted = false };

            string line = HistoryJson.WriteEdit(edit);
            bool ok = HistoryJson.TryParseEdit(line, out HistoryEdit parsed);

            Assert.True(ok);
            Assert.Equal(edit.Id, parsed.Id);
            Assert.Equal(true, parsed.Starred);
            Assert.False(parsed.Deleted);
        }

        [Fact]
        public void EditRoundTrips_Deleted()
        {
            var edit = new HistoryEdit { Id = Guid.NewGuid(), Starred = null, Deleted = true };

            string line = HistoryJson.WriteEdit(edit);
            bool ok = HistoryJson.TryParseEdit(line, out HistoryEdit parsed);

            Assert.True(ok);
            Assert.Null(parsed.Starred);
            Assert.True(parsed.Deleted);
        }

        [Fact]
        public void TryParseEdit_TornLine_ReturnsFalse()
        {
            bool ok = HistoryJson.TryParseEdit("{\"id\":\"bad", out HistoryEdit edit);

            Assert.False(ok);
            Assert.Null(edit);
        }
    }
}
