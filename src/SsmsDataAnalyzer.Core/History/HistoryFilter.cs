using System;
using System.Collections.Generic;
using System.Text;

namespace SsmsDataAnalyzer.Core.History
{
    public enum HistoryDateRange { All = 0, Today = 1, Last7Days = 2, Last30Days = 3 }

    /// <summary>
    /// Parses the query history search box: <c>sql:</c>, <c>server:</c>,
    /// <c>database:</c>/<c>db:</c>, <c>starred:true|false</c>, <c>error:true|false</c> as
    /// prefixes; bare words match inside the query text case-insensitively; a
    /// <c>"quoted phrase"</c> (with or without a prefix) is one term; several terms are AND'd.
    /// <see cref="Parse"/> never throws -- unrecognized or malformed pieces are best-effort
    /// (an unparsable <c>starred:</c>/<c>error:</c> value is simply dropped).
    /// </summary>
    public sealed class HistoryFilter
    {
        private enum TermKind { Text, Server, Database, Starred, Error }

        private sealed class Term
        {
            public TermKind Kind;
            public string Text;
            public bool BoolValue;
        }

        private readonly List<Term> _terms = new List<Term>();

        public HistoryDateRange DateRange { get; set; }

        public bool GroupIdenticalText { get; set; }

        public static HistoryFilter Parse(string searchText)
        {
            var filter = new HistoryFilter();
            if (string.IsNullOrWhiteSpace(searchText)) return filter;

            foreach (string token in Tokenize(searchText))
            {
                if (token.Length == 0) continue;

                Term term = TryParsePrefixed(token, "sql:", TermKind.Text)
                    ?? TryParsePrefixed(token, "server:", TermKind.Server)
                    ?? TryParsePrefixed(token, "database:", TermKind.Database)
                    ?? TryParsePrefixed(token, "db:", TermKind.Database)
                    ?? TryParseBoolPrefixed(token, "starred:", TermKind.Starred)
                    ?? TryParseBoolPrefixed(token, "error:", TermKind.Error);

                if (term != null)
                {
                    filter._terms.Add(term);
                    continue;
                }

                string bare = StripQuotes(token);
                if (bare.Length > 0)
                {
                    filter._terms.Add(new Term { Kind = TermKind.Text, Text = bare });
                }
            }

            return filter;
        }

        public bool Matches(HistoryEntry entry, DateTime utcNow)
        {
            if (entry == null) return false;

            if (!MatchesDateRange(entry, utcNow)) return false;

            foreach (var term in _terms)
            {
                switch (term.Kind)
                {
                    case TermKind.Text:
                        if (!ContainsIgnoreCase(entry.Text, term.Text)) return false;
                        break;
                    case TermKind.Server:
                        if (!ContainsIgnoreCase(entry.Server, term.Text)) return false;
                        break;
                    case TermKind.Database:
                        if (!ContainsIgnoreCase(entry.Database, term.Text)) return false;
                        break;
                    case TermKind.Starred:
                        if (entry.Starred != term.BoolValue) return false;
                        break;
                    case TermKind.Error:
                        bool isError = entry.Outcome == HistoryOutcome.Error;
                        if (isError != term.BoolValue) return false;
                        break;
                }
            }

            return true;
        }

        private bool MatchesDateRange(HistoryEntry entry, DateTime utcNow)
        {
            switch (DateRange)
            {
                case HistoryDateRange.Today:
                    return entry.StartedUtc.Date == utcNow.Date;
                case HistoryDateRange.Last7Days:
                    return entry.StartedUtc <= utcNow && (utcNow - entry.StartedUtc) <= TimeSpan.FromDays(7);
                case HistoryDateRange.Last30Days:
                    return entry.StartedUtc <= utcNow && (utcNow - entry.StartedUtc) <= TimeSpan.FromDays(30);
                default:
                    return true;
            }
        }

        private static Term TryParsePrefixed(string token, string prefix, TermKind kind)
        {
            if (token.Length <= prefix.Length) return null;
            if (string.Compare(token, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0) return null;

            string value = StripQuotes(token.Substring(prefix.Length));
            if (value.Length == 0) return null;
            return new Term { Kind = kind, Text = value };
        }

        private static Term TryParseBoolPrefixed(string token, string prefix, TermKind kind)
        {
            if (token.Length <= prefix.Length) return null;
            if (string.Compare(token, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0) return null;

            string value = StripQuotes(token.Substring(prefix.Length));
            if (!bool.TryParse(value, out bool parsed)) return null;
            return new Term { Kind = kind, BoolValue = parsed };
        }

        private static string StripQuotes(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Splits on whitespace, but a <c>"..."</c> run (anywhere in a token, e.g. right after
        /// a <c>prefix:</c>) is kept together even if it contains spaces.
        /// </summary>
        private static List<string> Tokenize(string s)
        {
            var tokens = new List<string>();
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(s[i])) i++;
                if (i >= n) break;

                var sb = new StringBuilder();
                while (i < n && !char.IsWhiteSpace(s[i]))
                {
                    if (s[i] == '"')
                    {
                        sb.Append(s[i]);
                        i++;
                        while (i < n && s[i] != '"')
                        {
                            sb.Append(s[i]);
                            i++;
                        }
                        if (i < n)
                        {
                            sb.Append(s[i]);
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append(s[i]);
                        i++;
                    }
                }
                tokens.Add(sb.ToString());
            }
            return tokens;
        }
    }
}
