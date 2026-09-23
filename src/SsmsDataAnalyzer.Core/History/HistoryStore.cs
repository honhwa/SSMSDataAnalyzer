using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// Owns the on-disk query history: append-only month files (<c>yyyy-MM.jsonl</c>) plus the
    /// <c>edits.jsonl</c> sidecar for stars and deletes, and the in-memory index built from
    /// them. Every public member is guarded by a single lock; the store does no threading of
    /// its own -- the Vsix owns the background writer queue and calls <see cref="Append"/> from
    /// it. Nothing plaintext is ever written: every line handed to
    /// <see cref="IHistoryFileSystem"/> has already gone through
    /// <see cref="HistoryCipher.EncryptLine"/>.
    /// </summary>
    public sealed class HistoryStore
    {
        private const string EditsFileName = "edits.jsonl";

        private readonly IHistoryFileSystem _fileSystem;
        private readonly string _rootDirectory;
        private readonly HistoryCipher _cipher;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();

        private readonly Dictionary<Guid, HistoryEntry> _index = new Dictionary<Guid, HistoryEntry>();

        public event EventHandler Changed;

        public HistoryStore(IHistoryFileSystem fileSystem, string rootDirectory, HistoryCipher cipher, Func<DateTime> utcNow)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
            _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public void Load()
        {
            lock (_lock)
            {
                _fileSystem.EnsureDirectory(_rootDirectory);
                _index.Clear();

                foreach (string monthPath in ListMonthFiles())
                {
                    foreach (string rawLine in _fileSystem.ReadAllLines(monthPath))
                    {
                        if (!_cipher.TryDecryptLine(rawLine, out string json)) continue;
                        if (!HistoryJson.TryParse(json, out HistoryEntry entry)) continue;
                        entry.Starred = false;
                        _index[entry.Id] = entry;
                    }
                }

                ApplyEdits();
            }

            RaiseChanged();
        }

        public void Append(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            lock (_lock)
            {
                _fileSystem.EnsureDirectory(_rootDirectory);

                if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
                DateTime startedUtc = entry.StartedUtc.Kind == DateTimeKind.Utc
                    ? entry.StartedUtc
                    : DateTime.SpecifyKind(entry.StartedUtc, DateTimeKind.Utc);
                entry.StartedUtc = startedUtc;

                string json = HistoryJson.Write(entry);
                // Re-parse so the in-memory copy matches exactly what will be on disk
                // (truncation applied identically to both).
                HistoryJson.TryParse(json, out HistoryEntry stored);
                stored.Starred = false;

                string encrypted = _cipher.EncryptLine(json);
                string monthPath = MonthFilePath(startedUtc);
                _fileSystem.AppendLines(monthPath, new[] { encrypted });

                _index[stored.Id] = stored;
            }

            RaiseChanged();
        }

        public void SetStarred(Guid id, bool starred)
        {
            lock (_lock)
            {
                _fileSystem.EnsureDirectory(_rootDirectory);
                WriteEdit(new HistoryEdit { Id = id, Starred = starred, Deleted = false });

                if (_index.TryGetValue(id, out HistoryEntry entry))
                {
                    entry.Starred = starred;
                }
            }

            RaiseChanged();
        }

        public void Delete(Guid id)
        {
            lock (_lock)
            {
                _fileSystem.EnsureDirectory(_rootDirectory);
                WriteEdit(new HistoryEdit { Id = id, Starred = null, Deleted = true });
                _index.Remove(id);
            }

            RaiseChanged();
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                foreach (string file in SafeListFiles("*.jsonl"))
                {
                    _fileSystem.DeleteFile(file);
                }
                _index.Clear();
            }

            RaiseChanged();
        }

        public int Trim(int retentionDays)
        {
            int removedTotal = 0;

            lock (_lock)
            {
                DateTime cutoffUtc = _utcNow().AddDays(-Math.Max(0, retentionDays));

                foreach (string monthPath in ListMonthFiles())
                {
                    string[] rawLines = _fileSystem.ReadAllLines(monthPath);
                    var keptLines = new List<string>(rawLines.Length);
                    int originalCount = 0;

                    foreach (string rawLine in rawLines)
                    {
                        if (!_cipher.TryDecryptLine(rawLine, out string json)) continue;
                        if (!HistoryJson.TryParse(json, out HistoryEntry entry)) continue;
                        originalCount++;

                        bool survives = _index.TryGetValue(entry.Id, out HistoryEntry indexed)
                            && (indexed.Starred || entry.StartedUtc >= cutoffUtc);

                        if (survives)
                        {
                            keptLines.Add(_cipher.EncryptLine(json));
                        }
                        else
                        {
                            _index.Remove(entry.Id);
                        }
                    }

                    int removedHere = originalCount - keptLines.Count;
                    if (removedHere <= 0) continue;

                    removedTotal += removedHere;

                    if (keptLines.Count == 0)
                    {
                        _fileSystem.DeleteFile(monthPath);
                    }
                    else
                    {
                        string tempPath = monthPath + ".trim.tmp";
                        // A previous trim may have been interrupted between these two calls,
                        // leaving a stale temp file behind. AppendLines would append to it and
                        // the swap would then duplicate every surviving entry, so start clean.
                        _fileSystem.DeleteFile(tempPath);
                        _fileSystem.AppendLines(tempPath, keptLines);
                        _fileSystem.ReplaceFile(tempPath, monthPath);
                    }
                }
            }

            if (removedTotal > 0) RaiseChanged();
            return removedTotal;
        }

        public IReadOnlyList<HistoryEntry> Query(HistoryFilter filter, int max)
        {
            filter = filter ?? HistoryFilter.Parse(null);

            lock (_lock)
            {
                DateTime now = _utcNow();
                IEnumerable<HistoryEntry> matches = _index.Values
                    .Where(e => filter.Matches(e, now))
                    .OrderByDescending(e => e.StartedUtc);

                if (filter.GroupIdenticalText)
                {
                    matches = matches
                        .GroupBy(e => e.Text ?? string.Empty)
                        .Select(g => g.First())
                        .OrderByDescending(e => e.StartedUtc);
                }

                if (max >= 0)
                {
                    matches = matches.Take(max);
                }

                return matches.ToList();
            }
        }

        // ---- internals ----------------------------------------------------

        private void ApplyEdits()
        {
            string editsPath = Path.Combine(_rootDirectory, EditsFileName);
            var deleted = new HashSet<Guid>();
            var starred = new Dictionary<Guid, bool>();

            foreach (string rawLine in _fileSystem.ReadAllLines(editsPath))
            {
                if (!_cipher.TryDecryptLine(rawLine, out string json)) continue;
                if (!HistoryJson.TryParseEdit(json, out HistoryEdit edit)) continue;

                if (edit.Deleted) deleted.Add(edit.Id);
                if (edit.Starred.HasValue) starred[edit.Id] = edit.Starred.Value;
            }

            foreach (Guid id in deleted)
            {
                _index.Remove(id);
            }

            foreach (var kvp in starred)
            {
                if (deleted.Contains(kvp.Key)) continue;
                if (_index.TryGetValue(kvp.Key, out HistoryEntry entry))
                {
                    entry.Starred = kvp.Value;
                }
            }
        }

        private void WriteEdit(HistoryEdit edit)
        {
            string json = HistoryJson.WriteEdit(edit);
            string encrypted = _cipher.EncryptLine(json);
            string editsPath = Path.Combine(_rootDirectory, EditsFileName);
            _fileSystem.AppendLines(editsPath, new[] { encrypted });
        }

        private string MonthFilePath(DateTime startedUtc)
        {
            string fileName = startedUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";
            return Path.Combine(_rootDirectory, fileName);
        }

        private IEnumerable<string> ListMonthFiles()
        {
            return SafeListFiles("*.jsonl")
                .Where(f => !string.Equals(Path.GetFileName(f), EditsFileName, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<string> SafeListFiles(string pattern)
        {
            string[] files = _fileSystem.ListFiles(_rootDirectory, pattern);
            return files ?? Array.Empty<string>();
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
