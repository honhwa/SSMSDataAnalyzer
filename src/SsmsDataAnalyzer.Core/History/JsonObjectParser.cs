using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// A minimal, single-pass parser for the one JSON shape <see cref="HistoryJson"/> needs: a
    /// flat object whose values are strings, numbers, booleans or null. No nested objects or
    /// arrays are ever produced by <see cref="HistoryJson"/>, so this does not attempt to
    /// support them beyond being able to skip a nested array/object value without corrupting
    /// the cursor (kept simple; the fixed schema never emits one).
    /// <para>
    /// Every method here is defensive: malformed input results in a `false` return or an
    /// exception that <see cref="HistoryJson"/> catches, never a thrown parser bug reaching a
    /// caller that must not throw on file content.
    /// </para>
    /// </summary>
    internal static class JsonObjectParser
    {
        public static bool TryParseObject(string text, out Dictionary<string, object> fields)
        {
            fields = null;
            if (string.IsNullOrEmpty(text)) return false;

            int pos = 0;
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != '{') return false;
            pos++;

            var result = new Dictionary<string, object>(StringComparer.Ordinal);

            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == '}')
            {
                fields = result;
                return true;
            }

            while (true)
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] != '"') return false;
                string key = ParseString(text, ref pos);

                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] != ':') return false;
                pos++;
                SkipWhitespace(text, ref pos);

                object value = ParseValue(text, ref pos);
                result[key] = value;

                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) return false;

                if (text[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (text[pos] == '}')
                {
                    pos++;
                    break;
                }
                return false;
            }

            SkipWhitespace(text, ref pos);
            fields = result;
            return true;
        }

        private static object ParseValue(string text, ref int pos)
        {
            if (pos >= text.Length) throw new FormatException("Unexpected end of JSON.");

            char c = text[pos];
            if (c == '"') return ParseString(text, ref pos);
            if (c == 't')
            {
                Expect(text, ref pos, "true");
                return true;
            }
            if (c == 'f')
            {
                Expect(text, ref pos, "false");
                return false;
            }
            if (c == 'n')
            {
                Expect(text, ref pos, "null");
                return null;
            }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(text, ref pos);
            if (c == '[') return SkipArray(text, ref pos);
            if (c == '{') return SkipObject(text, ref pos);

            throw new FormatException("Unexpected character in JSON value.");
        }

        private static void Expect(string text, ref int pos, string literal)
        {
            if (pos + literal.Length > text.Length || string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Unexpected literal in JSON.");
            }
            pos += literal.Length;
        }

        private static double ParseNumber(string text, ref int pos)
        {
            int start = pos;
            if (pos < text.Length && text[pos] == '-') pos++;
            while (pos < text.Length && char.IsDigit(text[pos])) pos++;
            if (pos < text.Length && text[pos] == '.')
            {
                pos++;
                while (pos < text.Length && char.IsDigit(text[pos])) pos++;
            }
            if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
            {
                pos++;
                if (pos < text.Length && (text[pos] == '+' || text[pos] == '-')) pos++;
                while (pos < text.Length && char.IsDigit(text[pos])) pos++;
            }
            string token = text.Substring(start, pos - start);
            return double.Parse(token, CultureInfo.InvariantCulture);
        }

        private static string ParseString(string text, ref int pos)
        {
            // Assumes text[pos] == '"'.
            pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= text.Length) throw new FormatException("Unterminated JSON string.");
                char c = text[pos];
                if (c == '"')
                {
                    pos++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    pos++;
                    if (pos >= text.Length) throw new FormatException("Unterminated JSON escape.");
                    char esc = text[pos];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); pos++; break;
                        case '\\': sb.Append('\\'); pos++; break;
                        case '/': sb.Append('/'); pos++; break;
                        case 'b': sb.Append('\b'); pos++; break;
                        case 'f': sb.Append('\f'); pos++; break;
                        case 'n': sb.Append('\n'); pos++; break;
                        case 'r': sb.Append('\r'); pos++; break;
                        case 't': sb.Append('\t'); pos++; break;
                        case 'u':
                            pos++;
                            if (pos + 4 > text.Length) throw new FormatException("Truncated \\u escape.");
                            string hex = text.Substring(pos, 4);
                            int code = int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            pos += 4;
                            break;
                        default:
                            throw new FormatException("Unknown JSON escape sequence.");
                    }
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
        }

        private static object SkipArray(string text, ref int pos)
        {
            pos++; // '['
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == ']')
            {
                pos++;
                return null;
            }
            while (true)
            {
                SkipWhitespace(text, ref pos);
                ParseValue(text, ref pos);
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) throw new FormatException("Unterminated JSON array.");
                if (text[pos] == ',') { pos++; continue; }
                if (text[pos] == ']') { pos++; break; }
                throw new FormatException("Malformed JSON array.");
            }
            return null;
        }

        private static object SkipObject(string text, ref int pos)
        {
            pos++; // '{'
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == '}')
            {
                pos++;
                return null;
            }
            while (true)
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] != '"') throw new FormatException("Malformed nested JSON object.");
                ParseString(text, ref pos);
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] != ':') throw new FormatException("Malformed nested JSON object.");
                pos++;
                SkipWhitespace(text, ref pos);
                ParseValue(text, ref pos);
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) throw new FormatException("Unterminated nested JSON object.");
                if (text[pos] == ',') { pos++; continue; }
                if (text[pos] == '}') { pos++; break; }
                throw new FormatException("Malformed nested JSON object.");
            }
            return null;
        }

        private static void SkipWhitespace(string text, ref int pos)
        {
            while (pos < text.Length)
            {
                char c = text[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }
    }
}
