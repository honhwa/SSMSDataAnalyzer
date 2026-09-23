using System;

namespace SsmsDataAnalyzer.Core.History
{
    public enum HistoryAuthKind { Unknown = 0, Windows = 1, SqlLogin = 2, Entra = 3 }

    public enum HistoryOutcome { Unknown = 0, Success = 1, Error = 2, Cancelled = 3 }

    /// <summary>
    /// One recorded query execution. Text is stored verbatim -- secrets included -- per the
    /// project decision that encryption at rest (see <see cref="HistoryCipher"/>) is the
    /// protection, not redaction. Never log <see cref="Text"/> or any cell value.
    /// </summary>
    public sealed class HistoryEntry
    {
        public Guid Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Login { get; set; }
        public HistoryAuthKind AuthKind { get; set; }
        public string DocumentName { get; set; }
        public string Text { get; set; }
        public bool TextTruncated { get; set; }
        public int? DurationMs { get; set; }
        public HistoryOutcome? Outcome { get; set; }
        public long? RowCount { get; set; }

        /// <summary>
        /// Populated from the edits sidecar when the store builds its index, not from the
        /// month file itself. Month files are append-only and immutable; see §8.1/§8.5.
        /// </summary>
        public bool Starred { get; set; }
    }

    /// <summary>
    /// An append-only edit record against an existing <see cref="HistoryEntry"/>: a star
    /// toggle, a delete (tombstone), or both. Written to the <c>edits.jsonl</c> sidecar.
    /// </summary>
    public sealed class HistoryEdit
    {
        public Guid Id { get; set; }

        /// <summary>Null means this edit does not change the starred state.</summary>
        public bool? Starred { get; set; }

        public bool Deleted { get; set; }
    }
}
