using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    /// <summary>
    /// The in-memory fake cannot prove the feature works on a real disk, and v0.19.0 proved why
    /// that matters: every unit test passed while query history was completely dead in SSMS,
    /// because nothing created the history folder before the key file was written. These tests
    /// run the real Core path against real files in a real (initially MISSING) directory.
    ///
    /// Only DPAPI stays faked -- it is a Vsix concern and needs no disk.
    /// </summary>
    public sealed class RealDiskHistoryTests : IDisposable
    {
        private readonly string _root;

        public RealDiskHistoryTests()
        {
            // Deliberately NOT created: the first run on a clean machine starts with nothing.
            _root = Path.Combine(Path.GetTempPath(), "SsmsDataAnalyzerTests", Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void FirstRun_OnAMissingDirectory_RecordsAndReloadsTheExecution()
        {
            var fileSystem = new DiskHistoryFileSystem();
            Assert.False(Directory.Exists(_root)); // the state the user's machine was actually in

            byte[] key = new HistoryKeyStore(fileSystem, new FakeKeyProtector(), Path.Combine(_root, "key.bin")).LoadOrCreate();

            var store = new HistoryStore(fileSystem, _root, new HistoryCipher(key), () => DateTime.UtcNow);
            store.Load();
            store.Append(new HistoryEntry
            {
                Id = Guid.NewGuid(),
                StartedUtc = DateTime.UtcNow,
                Server = "SQLTEST7",
                Database = "AgricultureFinances",
                Text = "SELECT TOP 100 * FROM DebtLedger.Debt ORDER BY 1 DESC"
            });

            // A brand-new store over the same folder: what SSMS sees on the next start.
            byte[] sameKey = new HistoryKeyStore(fileSystem, new FakeKeyProtector(), Path.Combine(_root, "key.bin")).LoadOrCreate();
            var reopened = new HistoryStore(fileSystem, _root, new HistoryCipher(sameKey), () => DateTime.UtcNow);
            reopened.Load();

            HistoryEntry entry = Assert.Single(reopened.Query(HistoryFilter.Parse(null), 100));
            Assert.Equal("SELECT TOP 100 * FROM DebtLedger.Debt ORDER BY 1 DESC", entry.Text);
            Assert.Equal("SQLTEST7", entry.Server);
        }

        [Fact]
        public void NothingReadableIsWrittenToDisk()
        {
            var fileSystem = new DiskHistoryFileSystem();
            byte[] key = new HistoryKeyStore(fileSystem, new FakeKeyProtector(), Path.Combine(_root, "key.bin")).LoadOrCreate();
            var store = new HistoryStore(fileSystem, _root, new HistoryCipher(key), () => DateTime.UtcNow);
            store.Load();
            store.Append(new HistoryEntry
            {
                Id = Guid.NewGuid(),
                StartedUtc = DateTime.UtcNow,
                Server = "PRODSERVER",
                Database = "Payroll",
                Text = "CREATE LOGIN auditor WITH PASSWORD = 'hunter2-should-never-be-readable'"
            });

            foreach (string file in Directory.GetFiles(_root, "*.jsonl"))
            {
                string raw = File.ReadAllText(file);
                Assert.DoesNotContain("hunter2", raw, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PRODSERVER", raw, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Payroll", raw, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CREATE LOGIN", raw, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Mirrors the Vsix's HistoryFileSystem, minus the Windows-only ACL.</summary>
        private sealed class DiskHistoryFileSystem : IHistoryFileSystem
        {
            public void EnsureDirectory(string path) => Directory.CreateDirectory(path);
            public bool FileExists(string path) => File.Exists(path);
            public string[] ReadAllLines(string path) => File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            public void AppendLines(string path, IEnumerable<string> lines) => File.AppendAllLines(path, lines.ToList());
            public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
            public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
            public void DeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }
            public string[] ListFiles(string directory, string searchPattern) =>
                Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern) : Array.Empty<string>();
            public void ReplaceFile(string sourcePath, string destinationPath)
            {
                if (File.Exists(destinationPath)) File.Replace(sourcePath, destinationPath, null);
                else File.Move(sourcePath, destinationPath);
            }
        }
    }
}
