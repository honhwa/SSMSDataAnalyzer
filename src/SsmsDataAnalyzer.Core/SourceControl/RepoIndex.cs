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
            var byModule = new Dictionary<ModuleRef, List<RepoEntry>>(ModuleRefComparer.Instance);
            int fileCount = 0;
            int unrecognised = 0;

            if (files != null)
            {
                foreach (RepoFile f in files)
                {
                    if (f == null) continue;
                    fileCount++;

                    ModuleRef module = ModuleFileParser.TryIdentify(f.Text);
                    if (module == null)
                    {
                        unrecognised++;
                        continue;
                    }

                    bool inProject = project != null && project.Contains(f.RelativePath);
                    var entry = new RepoEntry(f.RelativePath, f.Text, module, inProject);

                    if (!byModule.TryGetValue(module, out List<RepoEntry> list))
                    {
                        list = new List<RepoEntry>();
                        byModule[module] = list;
                    }
                    list.Add(entry);
                }
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
