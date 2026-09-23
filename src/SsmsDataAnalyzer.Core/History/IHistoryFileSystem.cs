using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// All disk access <see cref="HistoryStore"/> and <see cref="HistoryKeyStore"/> need, kept
    /// behind an interface so Core stays free of any Windows-specific or Visual Studio
    /// reference. The real implementation (with the user-only ACL applied in
    /// <see cref="EnsureDirectory"/>) lives in the Vsix project; tests use an in-memory fake.
    /// </summary>
    public interface IHistoryFileSystem
    {
        void EnsureDirectory(string path);

        bool FileExists(string path);

        /// <summary>Returns an empty array when the file does not exist.</summary>
        string[] ReadAllLines(string path);

        void AppendLines(string path, IEnumerable<string> lines);

        byte[] ReadAllBytes(string path);

        void WriteAllBytes(string path, byte[] bytes);

        void DeleteFile(string path);

        string[] ListFiles(string directory, string searchPattern);

        /// <summary>
        /// Atomically replaces <paramref name="destinationPath"/> with the contents of
        /// <paramref name="sourcePath"/>, then removes <paramref name="sourcePath"/>. Used by
        /// <see cref="HistoryStore.Trim"/>, which writes trimmed, re-encrypted content to a new
        /// file and swaps it in -- the destination must never be observably empty or partially
        /// written between the two, and no plaintext is ever involved.
        /// </summary>
        void ReplaceFile(string sourcePath, string destinationPath);
    }
}
