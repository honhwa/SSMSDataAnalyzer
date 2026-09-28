using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    public enum ChangeSource { QueryHistory, ServerModifyDate, Both }

    public enum SyncStatus
    {
        Matches, DiffersFromRepo, MissingFromRepo, OnDiskNotInProject, DroppedButInRepo, NotCompared
    }

    /// <summary>A module that Query History or <c>sys.objects.modify_date</c> says changed,
    /// waiting to be checked against the server's current state and the repository.</summary>
    public sealed class ChangeCandidate
    {
        public ChangeCandidate(string server, string database, ModuleRef module,
            DateTime changedUtc, ChangeSource source, DdlAction? lastAction)
        {
            Server = server;
            Database = database;
            Module = module;
            ChangedUtc = changedUtc;
            Source = source;
            LastAction = lastAction;
        }

        public string Server { get; }
        public string Database { get; }
        public ModuleRef Module { get; }
        public DateTime ChangedUtc { get; }
        public ChangeSource Source { get; }

        /// <summary>From history; null for a server-only candidate.</summary>
        public DdlAction? LastAction { get; }
    }

    /// <summary>
    /// Merges the two change sources (§4): Query History nominates, the server confirms. One
    /// candidate per (server, database, module) -- server and database compare OrdinalIgnoreCase.
    /// </summary>
    public static class ChangeCandidates
    {
        /// <summary>Present in both inputs -&gt; Source = Both, ChangedUtc = the later,
        /// LastAction taken from the history candidate (server-only candidates carry no action).</summary>
        public static IReadOnlyList<ChangeCandidate> Merge(
            IEnumerable<ChangeCandidate> fromHistory, IEnumerable<ChangeCandidate> fromServer)
        {
            var byKey = new Dictionary<Key, ChangeCandidate>();

            if (fromHistory != null)
            {
                foreach (ChangeCandidate c in fromHistory)
                {
                    if (c == null || c.Module == null) continue;
                    byKey[new Key(c.Server, c.Database, c.Module)] = c;
                }
            }

            if (fromServer != null)
            {
                foreach (ChangeCandidate c in fromServer)
                {
                    if (c == null || c.Module == null) continue;
                    var key = new Key(c.Server, c.Database, c.Module);

                    if (byKey.TryGetValue(key, out ChangeCandidate existing))
                    {
                        DateTime later = c.ChangedUtc > existing.ChangedUtc ? c.ChangedUtc : existing.ChangedUtc;
                        byKey[key] = new ChangeCandidate(existing.Server, existing.Database, existing.Module,
                            later, ChangeSource.Both, existing.LastAction);
                    }
                    else
                    {
                        byKey[key] = c;
                    }
                }
            }

            return byKey.Values.ToList();
        }

        private struct Key : IEquatable<Key>
        {
            private readonly string _server;
            private readonly string _database;
            private readonly ModuleRef _module;

            public Key(string server, string database, ModuleRef module)
            {
                _server = server ?? string.Empty;
                _database = database ?? string.Empty;
                _module = module;
            }

            public bool Equals(Key other) =>
                string.Equals(_server, other._server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_database, other._database, StringComparison.OrdinalIgnoreCase)
                && Equals(_module, other._module);

            public override bool Equals(object obj) => obj is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(_server);
                    h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(_database);
                    h = h * 31 + (_module == null ? 0 : _module.GetHashCode());
                    return h;
                }
            }
        }
    }

    /// <summary>What the server currently says about a module, read fresh so a history entry that
    /// failed or was superseded never produces a false alarm on its own.</summary>
    public sealed class ServerObjectState
    {
        public ServerObjectState(bool exists, DbObjectKind? kind, string definition)
        {
            Exists = exists;
            Kind = kind;
            Definition = definition;
        }

        public bool Exists { get; }
        public DbObjectKind? Kind { get; }

        /// <summary>null = table, encrypted, CLR, or unreadable.</summary>
        public string Definition { get; }
    }

    public sealed class SyncFinding
    {
        internal SyncFinding(ChangeCandidate candidate, SyncStatus status, string relativePath, string reason)
        {
            Candidate = candidate;
            Status = status;
            RelativePath = relativePath;
            Reason = reason;
        }

        public ChangeCandidate Candidate { get; }
        public SyncStatus Status { get; }

        /// <summary>null when no file is involved.</summary>
        public string RelativePath { get; }

        /// <summary>Always set; object and file names only.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// The verdict (§13.7): candidate + server state + repo index -&gt; one finding. The decision
    /// order below is frozen -- each numbered branch has its own unit test.
    /// </summary>
    public static class SyncChecker
    {
        /// <param name="candidate">What changed and where.</param>
        /// <param name="server">null = could not reach that server.</param>
        /// <param name="repo">null = that database is not mapped to a project.</param>
        public static SyncFinding Check(ChangeCandidate candidate, ServerObjectState server, RepoIndex repo)
        {
            if (candidate == null)
                return new SyncFinding(null, SyncStatus.NotCompared, null, "no change candidate given");

            // 1. Not mapped to a project.
            if (repo == null)
                return new SyncFinding(candidate, SyncStatus.NotCompared, null,
                    "database is not mapped to a project");

            // 2. Unqualified module: Core never assumes "dbo".
            if (candidate.Module == null || candidate.Module.Schema == null)
                return new SyncFinding(candidate, SyncStatus.NotCompared, null,
                    "could not tell which schema");

            // 3. No usable connection to the server.
            if (server == null)
                return new SyncFinding(candidate, SyncStatus.NotCompared, null,
                    "no open connection to " + candidate.Server);

            // 4. Gone from the server.
            if (!server.Exists)
            {
                IReadOnlyList<RepoEntry> stillInRepo = repo.Find(candidate.Module);
                if (stillInRepo.Count > 0)
                    return new SyncFinding(candidate, SyncStatus.DroppedButInRepo, stillInRepo[0].RelativePath,
                        candidate.Module + " was dropped from the server but is still in " + stillInRepo[0].RelativePath);

                return new SyncFinding(candidate, SyncStatus.Matches, null,
                    candidate.Module + " is gone from both the server and the repository");
            }

            IReadOnlyList<RepoEntry> found = repo.Find(candidate.Module);

            // 5. Defined more than once.
            if (found.Count > 1)
                return new SyncFinding(candidate, SyncStatus.NotCompared, null,
                    candidate.Module + " is defined in " + found.Count + " files: "
                    + string.Join(", ", found.Select(f => f.RelativePath)));

            // 6. No file at all.
            if (found.Count == 0)
                return new SyncFinding(candidate, SyncStatus.MissingFromRepo, null,
                    candidate.Module + " has no file in the project");

            RepoEntry entry = found[0];

            // 7. The one file is not in the .sqlproj -- checked before comparing on purpose.
            if (!entry.InProject)
                return new SyncFinding(candidate, SyncStatus.OnDiskNotInProject, entry.RelativePath,
                    entry.RelativePath + " defines " + candidate.Module + " but is not in the .sqlproj");

            DbObjectKind effectiveKind = server.Kind ?? candidate.Module.Kind;

            // 8. Tables are not compared in Phase 1.
            if (effectiveKind == DbObjectKind.Table)
                return new SyncFinding(candidate, SyncStatus.NotCompared, entry.RelativePath,
                    "tables are not compared yet -- check " + entry.RelativePath + " by hand");

            // 9. Encrypted / CLR / otherwise unreadable definition.
            if (server.Definition == null)
                return new SyncFinding(candidate, SyncStatus.NotCompared, entry.RelativePath,
                    "definition not readable: encrypted or CLR (" + entry.RelativePath + ")");

            // 10. The actual comparison.
            bool same = ModuleDefinitionNormalizer.AreEquivalent(server.Definition, entry.Text);
            return same
                ? new SyncFinding(candidate, SyncStatus.Matches, entry.RelativePath,
                    candidate.Module + " matches " + entry.RelativePath)
                : new SyncFinding(candidate, SyncStatus.DiffersFromRepo, entry.RelativePath,
                    candidate.Module + " differs from " + entry.RelativePath);
        }
    }
}
