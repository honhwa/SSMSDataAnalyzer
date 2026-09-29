using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SsmsDataAnalyzer.Core.SourceControl;

// Throwaway: counts only. Prints no table or column names.
string root = args.Length > 0 ? args[0] : @"C:\CISA\APPRRR-Agriculture\Database\KingICT.Demeter.Database.Finances";
MethodInfo m = typeof(TableDefinitionParser).GetMethod("TryParseColumns", BindingFlags.NonPublic | BindingFlags.Static,
    null, new[] { typeof(string), typeof(string).MakeByRefType() }, null);

int shown = 0, other = 0, files = 0, seen = 0, parsed = 0, nulls = 0;
var tally = new SortedDictionary<string, int>();
foreach (string f in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
{
    files++;
    string text = File.ReadAllText(f);
    foreach (var (module, batch, _) in ModuleFileParser.IdentifyAll(text))
    {
        if (module.Kind != DbObjectKind.Table) continue;
        string head = string.Join(" ", batch.TrimStart((char)0xFEFF, (char)32, (char)13, (char)10, (char)9).Split(new[] { (char)32, (char)13, (char)10, (char)9 }, StringSplitOptions.RemoveEmptyEntries).Take(2)).ToUpperInvariant();
        if (head != "CREATE TABLE") { other++; continue; }
        seen++;
        object[] a = { batch, null };
        object r = m.Invoke(null, a);
        if (r != null) { parsed++; continue; }
        nulls++;
        string k = (string)a[1] ?? "?";
        if (k == "(not CREATE)")
            k += " starts: " + string.Join(" ", batch.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')
                .Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Take(2)).ToUpperInvariant();
        if (Environment.GetEnvironmentVariable("SHAPE") == "1" && shown++ < 6) Console.WriteLine("SHAPE " + k + " :: " + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(batch, @"\[[^\]]*\]", "[x]"), @"\s+", " "));
        tally[k] = tally.TryGetValue(k, out int c) ? c + 1 : 1;
    }
}
Console.WriteLine($"sql files {files}; CREATE TABLE batches {seen} (non-CREATE table batches skipped: {other}); parsed {parsed}; null {nulls}");
foreach (var kv in tally.OrderByDescending(x => x.Value)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
