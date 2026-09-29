using System.Collections.ObjectModel;
using System.Globalization;
using SsmsDataAnalyzer.Core.SourceControl;

namespace SsmsDataAnalyzer.Vsix.SourceControl
{
    /// <summary>UI-bound wrapper around one Core <see cref="SyncFinding"/> -- flat display
    /// strings only, no logic (docs/source-control-sync-plan.md §9 Phase 1 item 2).</summary>
    internal sealed class FindingItem
    {
        public FindingItem(SyncFinding finding)
        {
            Finding = finding;
        }

        public SyncFinding Finding { get; }

        public string Server => Finding.Candidate?.Server ?? string.Empty;
        public string Database => Finding.Candidate?.Database ?? string.Empty;
        public string Object => Finding.Candidate?.Module?.ToString() ?? string.Empty;
        public string Kind => Finding.Candidate?.Module?.Kind.ToString() ?? string.Empty;
        public string ChangedText => Finding.Candidate == null
            ? string.Empty
            : Finding.Candidate.ChangedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        public string Source => Finding.Candidate?.Source.ToString() ?? string.Empty;
        public string StatusGlyph
        {
            get
            {
                switch (Finding.Status)
                {
                    case SyncStatus.Matches: return "✓"; // check
                    case SyncStatus.DiffersFromRepo: return "⚠"; // warning
                    case SyncStatus.DroppedButInRepo: return "⚠";
                    case SyncStatus.MissingFromRepo: return "❌"; // cross
                    case SyncStatus.OnDiskNotInProject: return "❌";
                    default: return "ℹ"; // info
                }
            }
        }
        /// <summary>The raw status, for the row-colour triggers. Not shown to the user.</summary>
        public string StatusText => Finding.Status.ToString();

        /// <summary>What the Status column shows. The raw enum names ("NotCompared",
        /// "DiffersFromRepo") were on screen in the first preview.</summary>
        public string StatusDisplay
        {
            get
            {
                switch (Finding.Status)
                {
                    case SyncStatus.Matches: return "Matches repo";
                    case SyncStatus.DiffersFromRepo: return "Differs from repo";
                    case SyncStatus.MissingFromRepo: return "Missing from repo";
                    case SyncStatus.OnDiskNotInProject: return "Not in .sqlproj";
                    case SyncStatus.DroppedButInRepo: return "Dropped, still in repo";
                    default: return "Not compared";
                }
            }
        }

        /// <summary>Where the change was noticed, in words.</summary>
        public string SourceDisplay
        {
            get
            {
                switch (Finding.Candidate?.Source)
                {
                    case ChangeSource.QueryHistory: return "Your history";
                    case ChangeSource.ServerModifyDate: return "Server";
                    case ChangeSource.Both: return "History + server";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>One group per (server, database) — see SourceControlViewModel.RebuildGroups.</summary>
        public string GroupHeader => string.IsNullOrEmpty(Server) ? Database : Database + " (" + Server + ")";

        /// <summary>Orders groups by database, then server, so the environments of one database
        /// sit next to each other. \u0001 cannot occur in either name.</summary>
        public string GroupSortKey => Database + "\u0001" + Server;
        public string RelativePath => Finding.RelativePath ?? string.Empty;
        public string Reason => Finding.Reason ?? string.Empty;
        public bool IsMatch => Finding.Status == SyncStatus.Matches;

        /// <summary>Whether "Compare" makes sense for this row -- a real repo file and a real
        /// server definition to diff against.</summary>
        public bool CanCompare => !string.IsNullOrEmpty(RelativePath)
            && (Finding.Status == SyncStatus.DiffersFromRepo || Finding.Status == SyncStatus.Matches);

        public bool CanOpenRepoFile => !string.IsNullOrEmpty(RelativePath);

        public bool CanScriptServerDefinition => Finding.Status == SyncStatus.DiffersFromRepo
            || Finding.Status == SyncStatus.MissingFromRepo || Finding.Status == SyncStatus.Matches;

        public bool IsUnmapped => Finding.Status == SyncStatus.NotCompared
            && (Finding.Reason ?? string.Empty).IndexOf("not mapped to a project", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
