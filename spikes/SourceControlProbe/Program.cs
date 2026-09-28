using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SsmsDataAnalyzer.Core.ScriptObject;

namespace SourceControlProbe
{
    // S-5 timing probe: read-only. Never writes, renames, or touches anything under the target
    // folder. Reports only timings, file counts and byte totals -- no file contents or names of
    // objects are printed beyond a final count.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string root = args.Length > 0
                ? args[0]
                : @"C:\CISA\APPRRR-Agriculture\Database\KingICT.Demeter.Database.Finances";

            if (!Directory.Exists(root))
            {
                Console.WriteLine($"Directory not found: {root}");
                return 1;
            }

            var files = Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories).ToList();
            Console.WriteLine($"File count: {files.Count}");

            // ---- Cold pass: read every file from disk + lex it ----
            var swReadCold = Stopwatch.StartNew();
            long totalBytes = 0;
            var texts = new string[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                var bytes = File.ReadAllBytes(files[i]);
                totalBytes += bytes.Length;
                texts[i] = System.Text.Encoding.UTF8.GetString(bytes);
            }
            swReadCold.Stop();

            var swLexCold = Stopwatch.StartNew();
            long totalTokens = 0;
            foreach (var t in texts)
            {
                var tokens = TsqlLexer.Tokenize(t);
                totalTokens += tokens.Count;
            }
            swLexCold.Stop();

            Console.WriteLine($"Total bytes read: {totalBytes:N0}");
            Console.WriteLine($"COLD  read  elapsed: {swReadCold.ElapsedMilliseconds} ms");
            Console.WriteLine($"COLD  lex   elapsed: {swLexCold.ElapsedMilliseconds} ms");
            Console.WriteLine($"COLD  total (read+lex): {swReadCold.ElapsedMilliseconds + swLexCold.ElapsedMilliseconds} ms");
            Console.WriteLine($"Total tokens produced: {totalTokens:N0}");

            // ---- Warm pass: files are now in the OS file cache; re-read + re-lex ----
            var swReadWarm = Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                var bytes = File.ReadAllBytes(files[i]);
                texts[i] = System.Text.Encoding.UTF8.GetString(bytes);
            }
            swReadWarm.Stop();

            var swLexWarm = Stopwatch.StartNew();
            long totalTokensWarm = 0;
            foreach (var t in texts)
            {
                var tokens = TsqlLexer.Tokenize(t);
                totalTokensWarm += tokens.Count;
            }
            swLexWarm.Stop();

            Console.WriteLine($"WARM  read  elapsed: {swReadWarm.ElapsedMilliseconds} ms");
            Console.WriteLine($"WARM  lex   elapsed: {swLexWarm.ElapsedMilliseconds} ms");
            Console.WriteLine($"WARM  total (read+lex): {swReadWarm.ElapsedMilliseconds + swLexWarm.ElapsedMilliseconds} ms");

            // ---- Second warm pass, lex-only (files already decoded in memory) ----
            var swLexOnly = Stopwatch.StartNew();
            long totalTokens2 = 0;
            foreach (var t in texts)
            {
                var tokens = TsqlLexer.Tokenize(t);
                totalTokens2 += tokens.Count;
            }
            swLexOnly.Stop();
            Console.WriteLine($"LEX-ONLY (text already in memory) elapsed: {swLexOnly.ElapsedMilliseconds} ms");

            return 0;
        }
    }
}
