using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// docs/query-history-plan.md §4 Phase 3 item 13: the filtered history as a .sql file, one
    /// block per entry with a header comment.
    ///
    /// Headers use line comments (<c>--</c>), never block comments: a query containing
    /// <c>*/</c> would otherwise end the comment early and turn the rest of the header into
    /// executable text. Query text is written verbatim -- an export you cannot trust to be what
    /// you ran would be worse than no export -- so the file is as sensitive as the history it
    /// came from, which is why the caller warns before writing it outside the encrypted store.
    /// </summary>
    public static class HistoryExport
    {
        public static string ToSqlScript(IEnumerable<HistoryEntry> entries, DateTime generatedLocal, string filterDescription = null)
        {
            var builder = new StringBuilder();

            builder.Append("-- SSMS Data Analyzer -- query history export").AppendLine();
            builder.Append("-- Generated ").Append(generatedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine();
            if (!string.IsNullOrWhiteSpace(filterDescription))
            {
                builder.Append("-- Filter: ").Append(SingleLine(filterDescription)).AppendLine();
            }
            builder.Append("-- This file is NOT encrypted. It contains the queries exactly as they were run.").AppendLine();

            int count = 0;
            if (entries != null)
            {
                foreach (HistoryEntry entry in entries)
                {
                    if (entry == null) continue;
                    count++;

                    builder.AppendLine();
                    builder.Append("-- ").Append(new string('-', 70)).AppendLine();
                    builder.Append("-- ")
                        .Append(entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                        .Append("   ").Append(Or(entry.Server, "(unknown server)"))
                        .Append("   ").Append(Or(entry.Database, "(unknown database)"))
                        .AppendLine();

                    if (!string.IsNullOrEmpty(entry.DocumentName) || entry.DurationMs.HasValue || entry.Outcome.HasValue)
                    {
                        builder.Append("--");
                        if (!string.IsNullOrEmpty(entry.DocumentName)) builder.Append("   ").Append(SingleLine(entry.DocumentName));
                        if (entry.DurationMs.HasValue) builder.Append("   ").Append(entry.DurationMs.Value.ToString(CultureInfo.InvariantCulture)).Append(" ms");
                        if (entry.Outcome.HasValue) builder.Append("   ").Append(entry.Outcome.Value.ToString());
                        builder.AppendLine();
                    }

                    if (entry.TextTruncated)
                    {
                        builder.Append("-- NOTE: this query was too long to store in full and is truncated.").AppendLine();
                    }

                    builder.Append("-- ").Append(new string('-', 70)).AppendLine();
                    builder.Append(entry.Text ?? string.Empty);
                    if (!EndsWithNewLine(entry.Text)) builder.AppendLine();
                    builder.AppendLine("GO");
                }
            }

            if (count == 0)
            {
                builder.AppendLine().Append("-- (no entries matched the current filter)").AppendLine();
            }

            return builder.ToString();
        }

        private static string Or(string value, string fallback) =>
            string.IsNullOrEmpty(value) ? fallback : SingleLine(value);

        /// <summary>A stray newline would push the rest of a header line out of its comment and
        /// into executable text.</summary>
        private static string SingleLine(string value) =>
            value == null ? string.Empty : value.Replace("\r", " ").Replace("\n", " ");

        private static bool EndsWithNewLine(string text) =>
            !string.IsNullOrEmpty(text) && (text[text.Length - 1] == '\n' || text[text.Length - 1] == '\r');
    }
}
