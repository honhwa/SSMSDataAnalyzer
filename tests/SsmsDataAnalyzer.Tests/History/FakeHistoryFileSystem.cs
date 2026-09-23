using System;
using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Tests.History
{
    /// <summary>
    /// In-memory stand-in for <see cref="IHistoryFileSystem"/>. Paths are treated as opaque
    /// keys (no real directory semantics), which is enough for <see cref="HistoryStore"/> and
    /// <see cref="HistoryKeyStore"/> since they only ever combine a root directory with a file
    /// name and never enumerate subdirectories.
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
            _bytes[path] = bytes;
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
