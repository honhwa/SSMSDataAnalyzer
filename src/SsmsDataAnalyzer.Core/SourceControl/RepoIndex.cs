using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    public sealed class RepoFile
    {
        public RepoFile(string relativePath, string text)
        {
            RelativePath = relativePath ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public string RelativePath { get; }
        public string Text { get; }
    }

    public sealed class RepoEntry
    {
        internal RepoEntry(string relativePath, string text, ModuleRef module, bool inProject)
        {
            RelativePath = relativePath;
            Text = text;
            Module = module;
            InProject = inProject;
        }

        public string RelativePath { get; }
        public string Text { get; }
        public ModuleRef Module { get; }
        public bool InProject { get; }
    }

    /// <summary>
    /// (schema, name, kind) -&gt; file, built once per <c>.sqlproj</c> and cached by the caller
    /// (Core does no I/O and no caching of its own -- the Vsix decides when to rebuild). A file
    /// that defines no recognisable object is simply not indexed; two files defining the same
    /// object both come back from <see cref="Find"/>, which is itself the finding.
    /// </summary>
    public sealed class RepoIndex
    {
        private readonly Dictionary<ModuleRef, List<RepoEntry>> _byModule;

        private RepoIndex(Dictionary<ModuleRef, List<RepoEntry>> byModule, int fileCount, int unrecognisedFileCount)
        {
            _byModule = byModule;
            FileCount = fileCount;
            UnrecognisedFileCount = unrecognisedFileCount;
        }

        public int FileCount { get; }

        /// <summary>.sql files that defined nothing DdlDetector's kinds recognise.</summary>
        public int UnrecognisedFileCount { get; }

        public static RepoIndex Build(IEnumerable<RepoFile> files, SqlProjectFiles project)
        {
            // Only a CREATE defines an object. SSDT writes constraints as separate
            // "ALTER TABLE x ADD CONSTRAINT" batches in the table's own file, and a repository may
            // keep foreign keys in files of their own; counting each of those as another
            // definition made the table look "defined in N files" and gave the column
            // comparison an ALTER batch instead of the CREATE TABLE (found by C2, measured on the
            // real repository). An ALTER counts only for an object nothing in the repository
            // creates — a repository that keeps procedures as ALTER scripts still works.
            var byModule = new Dictionary<ModuleRef, List<RepoEntry>>(ModuleRefComparer.Instance);
            var alteredOnly = new Dictionary<ModuleRef, List<RepoEntry>>(ModuleRefComparer.Instance);
            int fileCount = 0;
            int unrecognised = 0;

            if (files != null)
            {
                foreach (RepoFile f in files)
                {
                    if (f == null) continue;
                    fileCount++;

                    // Every object in the file, each with its own batch text — a table file
                    // also defines that table's triggers (see ModuleFileParser.IdentifyAll).
                    var defined = ModuleFileParser.IdentifyAll(f.Text);
                    if (defined.Count == 0)
                    {
                        unrecognised++;
                        continue;
                    }

                    bool inProject = project != null && project.Contains(f.RelativePath);
                    foreach (var (module, batchText, isCreate) in defined)
                    {
                        var entry = new RepoEntry(f.RelativePath, batchText, module, inProject);
                        var target = isCreate ? byModule : alteredOnly;

                        if (!target.TryGetValue(module, out List<RepoEntry> list))
                        {
                            list = new List<RepoEntry>();
                            target[module] = list;
                        }
                        list.Add(entry);
                    }
                }
            }

            foreach (var pair in alteredOnly)
            {
                if (!byModule.ContainsKey(pair.Key)) byModule[pair.Key] = pair.Value;
            }

            return new RepoIndex(byModule, fileCount, unrecognised);
        }

        /// <summary>0 = not in repo, &gt;1 = defined twice (reported, not guessed between).</summary>
        public IReadOnlyList<RepoEntry> Find(ModuleRef module)
        {
            if (module == null) return Array.Empty<RepoEntry>();
            return _byModule.TryGetValue(module, out List<RepoEntry> list)
                ? (IReadOnlyList<RepoEntry>)list
                : Array.Empty<RepoEntry>();
        }

        private sealed class ModuleRefComparer : IEqualityComparer<ModuleRef>
        {
            public static readonly ModuleRefComparer Instance = new ModuleRefComparer();

            public bool Equals(ModuleRef x, ModuleRef y) => x == null ? y == null : x.Equals(y);

            public int GetHashCode(ModuleRef obj) => obj == null ? 0 : obj.GetHashCode();
        }
    }
}
