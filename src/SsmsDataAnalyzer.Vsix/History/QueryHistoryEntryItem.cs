using System;
using System.ComponentModel;
using System.Globalization;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// One row of the Query History grid -- a thin, bindable wrapper around Core's
    /// <see cref="HistoryEntry"/> (docs/query-history-plan.md §4 Phase 1 item 3's column list:
    /// When, Server, Database, Duration/Outcome, first line of the query). Starred is mutable
    /// (raises PropertyChanged so the star column repaints immediately); everything else is a
    /// snapshot of what QueryHistoryService.QueryAsync returned.
    /// </summary>
    internal sealed class QueryHistoryEntryItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public HistoryEntry Entry { get; }

        public QueryHistoryEntryItem(HistoryEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        public Guid Id => Entry.Id;

        public DateTime StartedLocal => Entry.StartedUtc.ToLocalTime();

        public string WhenText
        {
            get
            {
                var local = StartedLocal;
                var today = DateTime.Now.Date;
                if (local.Date == today) return "Today " + local.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
                return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            }
        }

        public string Server => Entry.Server ?? string.Empty;

        public string Database => Entry.Database ?? string.Empty;

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 11: the Document column, and
        /// the value <c>doc:</c>/<c>closed:</c> search on (Core's HistoryFilter, §8.6a).</summary>
        public string DocumentName => Entry.DocumentName ?? string.Empty;

        /// <summary>docs/query-history-plan.md §4 Phase 2 item 9: shown only once "Group
        /// identical" is on and this row stands for more than one execution (Core sets
        /// GroupCount to 1 whenever grouping is off, per §8.6a).</summary>
        public string GroupCountText => Entry.GroupCount > 1
            ? "×" + Entry.GroupCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        public string DurationOutcomeText
        {
            get
            {
                string outcomeText = Entry.Outcome switch
                {
                    HistoryOutcome.Error => "error",
                    HistoryOutcome.Cancelled => "cancelled",
                    HistoryOutcome.Success => null, // shown as just the duration, not "success"
                    _ => null
                };

                string durationText = Entry.DurationMs.HasValue
                    ? TimeSpan.FromMilliseconds(Entry.DurationMs.Value).ToString(@"hh\:mm\:ss")
                    : null;

                if (outcomeText != null && durationText != null) return durationText + " (" + outcomeText + ")";
                if (outcomeText != null) return outcomeText;
                if (durationText != null) return durationText;
                return string.Empty;
            }
        }

        public bool IsError => Entry.Outcome == HistoryOutcome.Error;

        public string FirstLine
        {
            get
            {
                var text = Entry.Text;
                if (string.IsNullOrEmpty(text)) return string.Empty;
                int newline = text.IndexOfAny(new[] { '\r', '\n' });
                var line = newline >= 0 ? text.Substring(0, newline) : text;
                line = line.Trim();
                const int maxLength = 200;
                return line.Length > maxLength ? line.Substring(0, maxLength) + "…" : line;
            }
        }

        public string FullText => Entry.Text ?? string.Empty;

        public bool Starred
        {
            get => Entry.Starred;
            set
            {
                if (Entry.Starred == value) return;
                Entry.Starred = value;
                OnPropertyChanged(nameof(Starred));
                OnPropertyChanged(nameof(StarGlyph));
            }
        }

        public string StarGlyph => Starred ? "★" : "☆"; // filled / outline star

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
