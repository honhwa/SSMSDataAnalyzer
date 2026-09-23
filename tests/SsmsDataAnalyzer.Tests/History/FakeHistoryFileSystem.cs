using System;
using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Tests.History
{
    /// <summary>
    /// In-memory stand-in for <see cref="IHistoryFileSystem"/>.
    ///
    /// It ENFORCES that a file's directory was created first, because the original version did
    /// not: it treated paths as opaque keys, every test passed, and the feature was still dead
    /// on a real disk -- HistoryKeyStore wrote key.bin into a folder nobody had created, which
    /// throws DirectoryNotFoundException for real and silently disabled query history
    /// (v0.19.0 field report). A fake that is laxer than the real thing hides exactly the bugs
    /// it exists to catch, so writes here fail the same way Windows would.
    /// </summary>
    public sealed class FakeHistoryFileSystem : IHistoryFileSystem
    {
        private readonly Dictionary<string, List<string>> _lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void EnsureDirectory(string path)
        {
            _directories.Add(path);
        }

        public bool FileExists(string path)
        {
            return _lines.ContainsKey(path) || _bytes.ContainsKey(path);
        }

        public string[] ReadAllLines(string path)
        {
            return _lines.TryGetValue(path, out List<string> lines) ? lines.ToArray() : Array.Empty<string>();
        }

        public void AppendLines(string path, IEnumerable<string> lines)
        {
            RequireDirectory(path);
            if (!_lines.TryGetValue(path, out List<string> existing))
            {
                existing = new List<string>();
                _lines[path] = existing;
            }
            existing.AddRange(lines);
        }

        public byte[] ReadAllBytes(string path)
        {
            if (!_bytes.TryGetValue(path, out byte[] value))
            {
                throw new System.IO.FileNotFoundException("No such fake file.", path);
            }
            return value;
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            RequireDirectory(path);
            _bytes[path] = bytes;
        }

        /// <summary>Throws the way Windows does when a file is written into a directory that
        /// was never created.</summary>
        private void RequireDirectory(string path)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return;
            if (!_directories.Contains(directory))
            {
                throw new System.IO.DirectoryNotFoundException(
                    "Could not find a part of the path '" + path + "' -- EnsureDirectory was never called for '" + directory + "'.");
            }
        }

        public void DeleteFile(string path)
        {
            _lines.Remove(path);
            _bytes.Remove(path);
        }

        public string[] ListFiles(string directory, string searchPattern)
        {
            // searchPattern is always "*.jsonl" in this codebase's usage; a small glob-to-suffix
            // handling is enough.
            string suffix = searchPattern.StartsWith("*", StringComparison.Ordinal)
                ? searchPattern.Substring(1)
                : searchPattern;

            return _lines.Keys
                .Where(k => IsInDirectory(k, directory) && k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public void ReplaceFile(string sourcePath, string destinationPath)
        {
            if (!_lines.TryGetValue(sourcePath, out List<string> sourceLines))
            {
                throw new System.IO.FileNotFoundException("No such fake file.", sourcePath);
            }
            _lines[destinationPath] = sourceLines;
            _lines.Remove(sourcePath);
        }

        private static bool IsInDirectory(string path, string directory)
        {
            // Our own paths are built with Path.Combine(directory, fileName); a simple prefix
            // check is sufficient for the fake.
            return path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }
    }
}
