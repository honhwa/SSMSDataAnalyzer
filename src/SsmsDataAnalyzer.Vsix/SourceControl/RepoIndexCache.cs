using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core.SourceControl;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// Builds and caches a <see cref="RepoIndex"/> per <c>.sqlproj</c> path
    /// (docs/source-control-sync-plan.md §8 / §9 Phase 1 item 4). Rebuilt only for files whose
    /// size or last-write time changed since the last build (§8's "rebuild only for files that
    /// changed"), so the ~1.2s cold cost (docs/source-control-api.md S-5) is paid once per
    /// project per session, not once per check. All file I/O here runs off the UI thread --
    /// callers must not invoke this from the UI thread.
    /// </summary>
    internal static class RepoIndexCache
    {
        private sealed class FileStamp
        {
            public long Length;
            public DateTime LastWriteUtc;
            public string Text;
        }

        private sealed class CacheEntry
        {
            public RepoIndex Index;
            public Dictionary<string, FileStamp> Files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        /// <summary>(index, error) -- error is non-null and index is null when the project
        /// could not be read at all (missing .sqlproj, folder gone, etc); the caller reports it
        /// on the status line rather than throwing.</summary>
        public static Task<(RepoIndex Index, string Error)> GetOrBuildAsync(string sqlprojPath) =>
            Task.Run(() => GetOrBuild(sqlprojPath));

        private static (RepoIndex, string) GetOrBuild(string sqlprojPath)
        {
            if (string.IsNullOrWhiteSpace(sqlprojPath))
                return (null, "no project path");

            string folder;
            string projectXml;
            try
            {
                folder = Path.GetDirectoryName(sqlprojPath);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return (null, "project folder not found: " + sqlprojPath);
                projectXml = File.Exists(sqlprojPath) ? File.ReadAllText(sqlprojPath) : null;
            }
            catch (Exception ex)
            {
                return (null, "could not read the .sqlproj (" + ex.GetType().Name + ")");
            }

            SqlProjectFiles project = SqlProjectReader.Read(projectXml);

            CacheEntry entry;
            lock (Gate)
            {
                if (!Cache.TryGetValue(sqlprojPath, out entry))
                {
                    entry = new CacheEntry();
                    Cache[sqlprojPath] = entry;
                }
            }

            List<string> sqlFiles;
            try
            {
                sqlFiles = EnumerateSqlFiles(folder).ToList();
            }
            catch (Exception ex)
            {
                return (null, "could not enumerate .sql files (" + ex.GetType().Name + ")");
            }

            var files = new List<RepoFile>(sqlFiles.Count);
            var freshStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in sqlFiles)
            {
                string relative = MakeRelative(folder, path);
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                }
                catch
                {
                    continue; // vanished between enumeration and stat -- just skip it this pass.
                }

                FileStamp stamp;
                if (entry.Files.TryGetValue(relative, out FileStamp cached)
                    && cached.Length == info.Length
                    && cached.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    stamp = cached; // unchanged -- reuse the cached text, no re-read.
                }
                else
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(path);
                    }
                    catch
                    {
                        continue; // unreadable this pass -- not indexed, same as "defines nothing".
                    }
                    stamp = new FileStamp { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Text = text };
                }

                freshStamps[relative] = stamp;
                files.Add(new RepoFile(relative, stamp.Text));
            }

            RepoIndex index = RepoIndex.Build(files, project);

            lock (Gate)
            {
                entry.Files = freshStamps;
                entry.Index = index;
            }

            return (index, null);
        }

        private static IEnumerable<string> EnumerateSqlFiles(string folder)
        {
            var stack = new Stack<string>();
            stack.Push(folder);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string name = Path.GetFileName(dir);

                // §3: 1,329 files under one project -- never walk build output/scratch folders.
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (string sub in subdirs) stack.Push(sub);

                IEnumerable<string> sqlFiles;
                try { sqlFiles = Directory.EnumerateFiles(dir, "*.sql"); }
                catch { continue; }
                foreach (string f in sqlFiles) yield return f;
            }
        }

        /// <summary>Relative path with backslashes, matching how a .sqlproj's own
        /// <c>Build Include</c> attributes are written (§9 Phase 1 item 4).</summary>
        private static string MakeRelative(string folder, string fullPath)
        {
            var folderUri = new Uri(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
            var fileUri = new Uri(fullPath);
            string relative = Uri.UnescapeDataString(folderUri.MakeRelativeUri(fileUri).ToString());
            return relative.Replace('/', '\\');
        }
    }
}
