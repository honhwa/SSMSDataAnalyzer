using System;
using System.IO;
using System.Threading.Tasks;
using SsmsDataAnalyzer.Core.SourceControl;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>
    /// Reads/writes the §5 mapping file (docs/source-control-sync-plan.md §5):
    /// <c>%LOCALAPPDATA%\SsmsDataAnalyzer\SourceControlMap.txt</c>. Core's
    /// <see cref="SourceControlMap"/> does the parsing/round-tripping; this class owns only the
    /// file I/O, always off the UI thread.
    /// </summary>
    internal static class SourceControlMapStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SsmsDataAnalyzer", "SourceControlMap.txt");

        public static Task<SourceControlMap> LoadAsync() => Task.Run(Load);

        private static SourceControlMap Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return SourceControlMap.Parse(string.Empty);
                string text = File.ReadAllText(FilePath);
                return SourceControlMap.Parse(text);
            }
            catch (Exception ex)
            {
                // Never the path contents -- just that reading it failed.
                ObjectExplorer.OeDiagnostics.Warn("Source control map: could not be read (" + ex.GetType().Name + "); treating it as empty this session.");
                return SourceControlMap.Parse(string.Empty);
            }
        }

        /// <summary>Merges <paramref name="database"/>/<paramref name="server"/> ->
        /// <paramref name="projectPath"/> into the current file and writes it back -- the ONLY
        /// write this feature ever performs against anything it owns (never the repository,
        /// never a database).</summary>
        public static Task<bool> AddMappingAsync(string database, string server, string projectPath) =>
            Task.Run(() =>
            {
                try
                {
                    SourceControlMap current = Load();
                    SourceControlMap updated = current.With(database, server, projectPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
                    File.WriteAllText(FilePath, updated.Serialize());
                    return true;
                }
                catch (Exception ex)
                {
                    ObjectExplorer.OeDiagnostics.Error("Source control map: could not be written (" + ex.GetType().Name + ").");
                    return false;
                }
            });
    }
}
