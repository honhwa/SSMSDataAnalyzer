using System;
using System.Linq;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public class HistoryStoreTests
    {
        private const string Root = @"C:\hist";

        private static HistoryEntry Entry(DateTime startedUtc, string text = "SELECT 1", string server = "SRV1")
        {
            return new HistoryEntry
            {
                Id = Guid.NewGuid(),
                StartedUtc = startedUtc,
                Server = server,
                Database = "DB1",
                Login = "sa",
                AuthKind = HistoryAuthKind.SqlLogin,
                DocumentName = "Query1.sql",
                Text = text,
                Outcome = HistoryOutcome.Success,
            };
        }

        private static (HistoryStore store, FakeHistoryFileSystem fs, HistoryCipher cipher) MakeStore(DateTime now)
        {
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var store = new HistoryStore(fs, Root, cipher, () => now);
            return (store, fs, cipher);
        }

        [Fact]
        public void Append_ThenQuery_ReturnsEntry()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            var entry = Entry(now, text: "SELECT * FROM Foo");
            store.Append(entry);

            var results = store.Query(HistoryFilter.Parse(null), 10);

            Assert.Single(results);
            Assert.Equal(entry.Id, results[0].Id);
            Assert.Equal("SELECT * FROM Foo", results[0].Text);
        }

        [Fact]
        public void Append_NothingPlaintext_OnDisk()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, fs, _) = MakeStore(now);

            store.Append(Entry(now, text: "SELECT SuperSecretColumn FROM T", server: "PRODSERVER"));

            string monthPath = Root + @"\2026-09.jsonl";
            string[] lines = fs.ReadAllLines(monthPath);
            Assert.Single(lines);
            Assert.DoesNotContain("SuperSecretColumn", lines[0]);
            Assert.DoesNotContain("PRODSERVER", lines[0]);
            Assert.StartsWith(HistoryCipher.LinePrefix, lines[0], StringComparison.Ordinal);
        }

        [Fact]
        public void Load_RebuildsIndex_FromDisk()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());

            var writer = new HistoryStore(fs, Root, cipher, () => now);
            writer.Append(Entry(now, text: "A"));
            writer.Append(Entry(now, text: "B"));

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();
            var results = reader.Query(HistoryFilter.Parse(null), 10);

            Assert.Equal(2, results.Count);
        }

        [Fact]
        public void Load_SkipsTornLastLine()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var writer = new HistoryStore(fs, Root, cipher, () => now);
            writer.Append(Entry(now, text: "good entry"));

            string monthPath = Root + @"\2026-09.jsonl";
            fs.AppendLines(monthPath, new[] { "v1:dGhpcyBpcyBub3QgdmFsaWQ=" }); // garbage, torn line

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();
            var results = reader.Query(HistoryFilter.Parse(null), 10);

            Assert.Single(results);
            Assert.Equal("good entry", results[0].Text);
        }

        [Fact]
        public void SetStarred_PersistsAcrossLoad()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var writer = new HistoryStore(fs, Root, cipher, () => now);
            var entry = Entry(now);
            writer.Append(entry);
            writer.SetStarred(entry.Id, true);

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();
            var results = reader.Query(HistoryFilter.Parse(null), 10);

            Assert.True(results.Single().Starred);
        }

        [Fact]
        public void SetStarred_UpdatesInMemoryIndex_Immediately()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);
            var entry = Entry(now);
            store.Append(entry);

            store.SetStarred(entry.Id, true);

            Assert.True(store.Query(HistoryFilter.Parse(null), 10).Single().Starred);
        }

        [Fact]
        public void Delete_RemovesFromQuery_Immediately_AndAfterReload()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var writer = new HistoryStore(fs, Root, cipher, () => now);
            var entry = Entry(now);
            writer.Append(entry);

            writer.Delete(entry.Id);
            Assert.Empty(writer.Query(HistoryFilter.Parse(null), 10));

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();
            Assert.Empty(reader.Query(HistoryFilter.Parse(null), 10));
        }

        [Fact]
        public void ClearAll_RemovesEverything()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, fs, _) = MakeStore(now);
            store.Append(Entry(now, text: "A"));
            store.Append(Entry(now, text: "B"));
            store.SetStarred(Guid.NewGuid(), true); // exercise edits.jsonl too

            store.ClearAll();

            Assert.Empty(store.Query(HistoryFilter.Parse(null), 10));
            Assert.Empty(fs.ListFiles(Root, "*.jsonl"));
        }

        [Fact]
        public void Trim_RemovesEntriesOlderThanRetention_KeepsRecent()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            var old = Entry(now.AddDays(-40), text: "old");
            var recent = Entry(now.AddDays(-5), text: "recent");
            store.Append(old);
            store.Append(recent);

            int removed = store.Trim(30);

            Assert.Equal(1, removed);
            var results = store.Query(HistoryFilter.Parse(null), 10);
            Assert.Single(results);
            Assert.Equal("recent", results[0].Text);
        }

        [Fact]
        public void Trim_KeepsStarredEntries_EvenIfOld()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            var old = Entry(now.AddDays(-100), text: "old but starred");
            store.Append(old);
            store.SetStarred(old.Id, true);

            int removed = store.Trim(30);

            Assert.Equal(0, removed);
            Assert.Single(store.Query(HistoryFilter.Parse(null), 10));
        }

        [Fact]
        public void Trim_SurvivesReload_ReEncryptedCorrectly()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var writer = new HistoryStore(fs, Root, cipher, () => now);

            var old = Entry(now.AddDays(-40), text: "old");
            var recent = Entry(now.AddDays(-1), text: "recent");
            writer.Append(old);
            writer.Append(recent);
            writer.Trim(30);

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();
            var results = reader.Query(HistoryFilter.Parse(null), 10);

            Assert.Single(results);
            Assert.Equal("recent", results[0].Text);
        }

        [Fact]
        public void Trim_NeverWritesPlaintext()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, fs, _) = MakeStore(now);

            var old = Entry(now.AddDays(-40), text: "SuperSecretOldText");
            var recent = Entry(now.AddDays(-1), text: "SuperSecretRecentText");
            store.Append(old);
            store.Append(recent);
            store.Trim(30);

            string monthPath = Root + @"\2026-09.jsonl";
            string[] lines = fs.ReadAllLines(monthPath);
            foreach (string line in lines)
            {
                Assert.StartsWith(HistoryCipher.LinePrefix, line, StringComparison.Ordinal);
                Assert.DoesNotContain("SuperSecret", line);
            }
        }

        [Fact]
        public void MonthRollover_WritesToSeparateFiles()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, fs, _) = MakeStore(now);

            store.Append(Entry(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), text: "august"));
            store.Append(Entry(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), text: "september"));

            Assert.True(fs.FileExists(Root + @"\2026-08.jsonl"));
            Assert.True(fs.FileExists(Root + @"\2026-09.jsonl"));

            var results = store.Query(HistoryFilter.Parse(null), 10);
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public void MonthRollover_LoadMergesAllMonths()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var fs = new FakeHistoryFileSystem();
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            var writer = new HistoryStore(fs, Root, cipher, () => now);
            writer.Append(Entry(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), text: "july"));
            writer.Append(Entry(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), text: "august"));
            writer.Append(Entry(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), text: "september"));

            var reader = new HistoryStore(fs, Root, cipher, () => now);
            reader.Load();

            Assert.Equal(3, reader.Query(HistoryFilter.Parse(null), 10).Count);
        }

        [Fact]
        public void Query_OrdersNewestFirst()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            store.Append(Entry(now.AddMinutes(-30), text: "oldest"));
            store.Append(Entry(now.AddMinutes(-10), text: "newest"));
            store.Append(Entry(now.AddMinutes(-20), text: "middle"));

            var results = store.Query(HistoryFilter.Parse(null), 10);

            Assert.Equal(new[] { "newest", "middle", "oldest" }, results.Select(r => r.Text).ToArray());
        }

        [Fact]
        public void Query_RespectsMax()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            for (int i = 0; i < 5; i++)
            {
                store.Append(Entry(now.AddMinutes(-i), text: "entry" + i));
            }

            var results = store.Query(HistoryFilter.Parse(null), 2);

            Assert.Equal(2, results.Count);
            Assert.Equal("entry0", results[0].Text);
            Assert.Equal("entry1", results[1].Text);
        }

        [Fact]
        public void Query_AppliesFilter()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);
            store.Append(Entry(now, text: "SELECT 1", server: "SRV1"));
            store.Append(Entry(now, text: "SELECT 2", server: "SRV2"));

            var results = store.Query(HistoryFilter.Parse("server:SRV1"), 10);

            Assert.Single(results);
            Assert.Equal("SRV1", results[0].Server);
        }

        [Fact]
        public void Changed_Fires_OnAppendStarDeleteClear()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);
            int count = 0;
            store.Changed += (s, e) => count++;

            var entry = Entry(now);
            store.Append(entry);
            store.SetStarred(entry.Id, true);
            store.Delete(entry.Id);
            store.ClearAll();

            Assert.Equal(4, count);
        }

        [Fact]
        public void Append_GeneratesId_WhenEmpty()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);
            var entry = Entry(now);
            entry.Id = Guid.Empty;

            store.Append(entry);

            var results = store.Query(HistoryFilter.Parse(null), 10);
            Assert.NotEqual(Guid.Empty, results.Single().Id);
        }

        [Fact]
        public void Query_WithoutGrouping_SetsGroupCountToOne()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);
            store.Append(Entry(now, text: "SELECT 1"));

            var results = store.Query(HistoryFilter.Parse(null), 10);

            Assert.Equal(1, results.Single().GroupCount);
        }

        [Fact]
        public void Query_WithGrouping_SetsGroupCount_AndKeepsNewestOfEachGroup()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            var older = Entry(now.AddMinutes(-10), text: "SELECT 1");
            var newer = Entry(now, text: "SELECT 1");
            var other = Entry(now.AddMinutes(-5), text: "SELECT 2");
            store.Append(older);
            store.Append(newer);
            store.Append(other);

            var filter = HistoryFilter.Parse(null);
            filter.GroupIdenticalText = true;
            var results = store.Query(filter, 10);

            Assert.Equal(2, results.Count);
            var select1 = results.Single(e => e.Text == "SELECT 1");
            Assert.Equal(newer.Id, select1.Id);
            Assert.Equal(2, select1.GroupCount);
            var select2 = results.Single(e => e.Text == "SELECT 2");
            Assert.Equal(1, select2.GroupCount);
        }

        [Fact]
        public void Query_GroupedThenUngrouped_DoesNotLeakGroupCount_IntoLaterUngroupedQuery()
        {
            var now = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            var (store, _, _) = MakeStore(now);

            store.Append(Entry(now.AddMinutes(-10), text: "SELECT 1"));
            store.Append(Entry(now, text: "SELECT 1"));

            var groupedFilter = HistoryFilter.Parse(null);
            groupedFilter.GroupIdenticalText = true;
            var groupedResults = store.Query(groupedFilter, 10);
            Assert.Equal(2, groupedResults.Single().GroupCount);

            var ungroupedResults = store.Query(HistoryFilter.Parse(null), 10);

            Assert.All(ungroupedResults, e => Assert.Equal(1, e.GroupCount));
        }
    }
}
