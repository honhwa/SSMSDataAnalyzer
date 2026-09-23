using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// The real <see cref="IHistoryFileSystem"/> over <c>%LOCALAPPDATA%\SsmsDataAnalyzer\QueryHistory\</c>
    /// (docs/query-history-plan.md §8.4). Every line handed to <see cref="AppendLines"/>/
    /// <see cref="WriteAllBytes"/> is already encrypted by Core -- this class does plain,
    /// synchronous file I/O and nothing else. Every method is called from
    /// <see cref="QueryHistoryService"/>'s background writer thread (or, for <see cref="EnsureDirectory"/>,
    /// lazily from wherever a HistoryStore call first needs the directory), never from the UI
    /// thread -- see QueryHistoryService's doc comment.
    /// </summary>
    internal sealed class HistoryFileSystem : IHistoryFileSystem
    {
        // ASCII in practice (every line is "v1:" + base64), but written as UTF-8 without a BOM
        // per the plan -- a BOM would land at the start of the first line and break TryParse's
        // "v1:" prefix check.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public void EnsureDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            bool existedBefore = Directory.Exists(path);
            Directory.CreateDirectory(path);

            if (existedBefore) return;

            // Only when WE create it: an explicit ACL for the current user + SYSTEM,
            // inheritance disabled, so this holds even if %LOCALAPPDATA% was redirected
            // somewhere looser than its normal per-user-only default (docs/query-history-plan.md
            // §3 "Layer 3: file hygiene"). Best-effort -- a failure here must not stop query
            // history from working; the folder still exists with whatever ACL Windows gave it.
            try
            {
                ApplyUserOnlyAcl(path);
            }
            catch (Exception ex)
            {
                ObjectExplorer.OeDiagnostics.Warn("Query history: could not apply a user-only ACL to the history folder (" + ex.GetType().Name + ") -- continuing with the folder's default permissions.");
            }
        }

        private static void ApplyUserOnlyAcl(string path)
        {
            var directoryInfo = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            // Inheritance disabled: start from a blank slate, not whatever the parent folder grants.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent()?.User;
            if (currentUser != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

            directoryInfo.SetAccessControl(security);
        }

        public bool FileExists(string path) => File.Exists(path);

        public string[] ReadAllLines(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            return File.ReadAllLines(path, Utf8NoBom);
        }

        public void AppendLines(string path, IEnumerable<string> lines)
        {
            if (lines == null) return;
            File.AppendAllLines(path, lines, Utf8NoBom);
        }

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

        /// <summary>Silent no-op on a missing file -- HistoryStore/HistoryKeyStore rely on this
        /// exactly (docs/query-history-plan.md §8.4).</summary>
        public void DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                // Already gone -- a silent no-op either way.
            }
        }

        public string[] ListFiles(string directory, string searchPattern)
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();
            return Directory.GetFiles(directory, searchPattern);
        }

        /// <summary>
        /// Atomic from the caller's view: <see cref="File.Replace(string, string, string)"/>
        /// when the destination already exists (the normal trim case -- a month file being
        /// re-written), falling back to a plain move when it does not (nothing to replace yet).
        /// Never leaves the destination observably empty or partially written.
        /// </summary>
        public void ReplaceFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                // No backup file: this is a private per-user history file, not something the
                // user would ever want a ".bak" copy of sitting on disk. File.Replace already
                // does the swap in one filesystem operation.
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }
        }
    }
}
