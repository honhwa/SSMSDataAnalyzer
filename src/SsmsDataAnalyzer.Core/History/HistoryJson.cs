using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// Hand-rolled JSON writer/reader for the one fixed <see cref="HistoryEntry"/> /
    /// <see cref="HistoryEdit"/> shape. Core is netstandard2.0 with exactly one package
    /// reference (Microsoft.Data.SqlClient) -- no Json.NET, no <c>System.Text.Json</c>.
    /// <para>
    /// Only a flat object of strings/numbers/bools/null is ever produced or consumed, so a
    /// minimal single-pass parser is enough; there is no need for a general JSON document
    /// model. Unknown fields in the input are ignored (forward compatibility with newer
    /// builds' files). A line that doesn't parse, or is missing its required identity field,
    /// is reported via the `false` return -- callers must never throw on file content.
    /// </para>
    /// </summary>
    public static class HistoryJson
    {
        public const int MaxTextBytes = 1024 * 1024; // 1 MB, then truncate + flag

        private const string DateFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        public static string Write(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            string text = entry.Text ?? string.Empty;
            bool truncated = entry.TextTruncated;
            int byteCount = Encoding.UTF8.GetByteCount(text);
            if (byteCount > MaxTextBytes)
            {
                text = TruncateToUtf8ByteLimit(text, MaxTextBytes);
                truncated = true;
            }

            var sb = new StringBuilder();
            sb.Append('{');
            WriteMember(sb, "id", entry.Id.ToString("D"), first: true);
            WriteMember(sb, "startedUtc", ToUtcString(entry.StartedUtc));
            WriteMember(sb, "server", entry.Server);
            WriteMember(sb, "database", entry.Database);
            WriteMember(sb, "login", entry.Login);
            WriteMemberRaw(sb, "authKind", ((int)entry.AuthKind).ToString(CultureInfo.InvariantCulture));
            WriteMember(sb, "documentName", entry.DocumentName);
            WriteMember(sb, "text", text);
            WriteMemberRaw(sb, "textTruncated", truncated ? "true" : "false");
            WriteMemberRaw(sb, "durationMs", entry.DurationMs.HasValue
                ? entry.DurationMs.Value.ToString(CultureInfo.InvariantCulture) : "null");
            WriteMemberRaw(sb, "outcome", entry.Outcome.HasValue
                ? ((int)entry.Outcome.Value).ToString(CultureInfo.InvariantCulture) : "null");
            WriteMemberRaw(sb, "rowCount", entry.RowCount.HasValue
                ? entry.RowCount.Value.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append('}');
            return sb.ToString();
        }

        public static bool TryParse(string line, out HistoryEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            try
            {
                if (!JsonObjectParser.TryParseObject(line, out var fields)) return false;

                if (!TryGetGuid(fields, "id", out var id)) return false;
                if (!TryGetDateTime(fields, "startedUtc", out var startedUtc)) return false;

                entry = new HistoryEntry
                {
                    Id = id,
                    StartedUtc = startedUtc,
                    Server = GetString(fields, "server"),
                    Database = GetString(fields, "database"),
                    Login = GetString(fields, "login"),
                    AuthKind = (HistoryAuthKind)GetInt(fields, "authKind", (int)HistoryAuthKind.Unknown),
                    DocumentName = GetString(fields, "documentName"),
                    Text = GetString(fields, "text") ?? string.Empty,
                    TextTruncated = GetBool(fields, "textTruncated", false),
                    DurationMs = GetNullableInt(fields, "durationMs"),
                    Outcome = TryGetNullableInt(fields, "outcome", out var outcomeVal)
                        ? (HistoryOutcome?)outcomeVal : null,
                    RowCount = GetNullableLong(fields, "rowCount"),
                };
                return true;
            }
            catch
            {
                entry = null;
                return false;
            }
        }

        public static string WriteEdit(HistoryEdit edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));

            var sb = new StringBuilder();
            sb.Append('{');
            WriteMember(sb, "id", edit.Id.ToString("D"), first: true);
            WriteMemberRaw(sb, "starred", edit.Starred.HasValue
                ? (edit.Starred.Value ? "true" : "false") : "null");
            WriteMemberRaw(sb, "deleted", edit.Deleted ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        public static bool TryParseEdit(string line, out HistoryEdit edit)
        {
            edit = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            try
            {
                if (!JsonObjectParser.TryParseObject(line, out var fields)) return false;
                if (!TryGetGuid(fields, "id", out var id)) return false;

                edit = new HistoryEdit
                {
                    Id = id,
                    Starred = TryGetNullableBool(fields, "starred", out var starredVal) ? starredVal : (bool?)null,
                    Deleted = GetBool(fields, "deleted", false),
                };
                return true;
            }
            catch
            {
                edit = null;
                return false;
            }
        }

        // ---- writing helpers -------------------------------------------------

        private static void WriteMember(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            WriteJsonString(sb, name);
            sb.Append(':');
            if (value == null) sb.Append("null");
            else WriteJsonString(sb, value);
        }

        private static void WriteMemberRaw(StringBuilder sb, string name, string rawValue)
        {
            sb.Append(',');
            WriteJsonString(sb, name);
            sb.Append(':');
            sb.Append(rawValue);
        }

        internal static void WriteJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // Raw UTF-16 code units (including surrogate pairs for non-BMP
                            // characters) are valid inside a JSON string and are encoded to
                            // UTF-8 correctly when the line is later written to disk.
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static string ToUtcString(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc
                ? value
                : (value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : value.ToUniversalTime());
            return utc.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        private static string TruncateToUtf8ByteLimit(string text, int maxBytes)
        {
            // Walk by whole UTF-16 code points (respecting surrogate pairs) so we never split
            // a character, keeping under the byte budget.
            var encoding = Encoding.UTF8;
            int low = 0, high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                int end = mid;
                if (end > 0 && char.IsHighSurrogate(text[end - 1]) && end < text.Length && char.IsLowSurrogate(text[end]))
                {
                    end++;
                }
                int bytes = encoding.GetByteCount(text.Substring(0, Math.Min(end, text.Length)));
                if (bytes <= maxBytes) low = mid; else high = mid - 1;
            }
            int cut = low;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut);
        }

        // ---- reading helpers ---------------------------------------------------

        private static string GetString(Dictionary<string, object> fields, string key)
        {
            return fields.TryGetValue(key, out var v) ? v as string : null;
        }

        private static bool GetBool(Dictionary<string, object> fields, string key, bool fallback)
        {
            return fields.TryGetValue(key, out var v) && v is bool b ? b : fallback;
        }

        private static bool TryGetNullableBool(Dictionary<string, object> fields, string key, out bool value)
        {
            value = false;
            if (fields.TryGetValue(key, out var v) && v is bool b)
            {
                value = b;
                return true;
            }
            return false;
        }

        private static int GetInt(Dictionary<string, object> fields, string key, int fallback)
        {
            if (fields.TryGetValue(key, out var v) && v is double d) return (int)d;
            return fallback;
        }

        private static int? GetNullableInt(Dictionary<string, object> fields, string key)
        {
            return TryGetNullableInt(fields, key, out var v) ? v : (int?)null;
        }

        private static bool TryGetNullableInt(Dictionary<string, object> fields, string key, out int value)
        {
            value = 0;
            if (fields.TryGetValue(key, out var v) && v is double d)
            {
                value = (int)d;
                return true;
            }
            return false;
        }

        private static long? GetNullableLong(Dictionary<string, object> fields, string key)
        {
            if (fields.TryGetValue(key, out var v) && v is double d) return (long)d;
            return null;
        }

        private static bool TryGetGuid(Dictionary<string, object> fields, string key, out Guid value)
        {
            value = Guid.Empty;
            if (fields.TryGetValue(key, out var v) && v is string s && Guid.TryParse(s, out value))
            {
                return true;
            }
            return false;
        }

        private static bool TryGetDateTime(Dictionary<string, object> fields, string key, out DateTime value)
        {
            value = default(DateTime);
            if (fields.TryGetValue(key, out var v) && v is string s &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                value = parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }
            return false;
        }
    }
}
