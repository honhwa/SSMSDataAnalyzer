using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Resources;

// Reads a standalone .NET .resources file (as extracted by `res --out`) and lists/extracts
// its named entries. This is one level deeper than ResDump: ResDump pulls the assembly's
// manifest resource blob (e.g. "...Resources.resources") out of the PE file; this reads
// the entries INSIDE that blob (e.g. an entry literally named "Menus.ctmenu").
internal static class ResReader
{
    public static void Run(string path, Dictionary<string, string> o)
    {
        var nameFilter = o.TryGetValue("name", out var n) ? n : null;
        var outDir = o.TryGetValue("out", out var d) ? d : null;
        using (var reader = new ResourceReader(path))
        {
            foreach (DictionaryEntry entry in reader)
            {
                var key = (string)entry.Key;
                var val = entry.Value;
                int len = val switch
                {
                    byte[] b => b.Length,
                    string s => s.Length,
                    Stream st => (int)st.Length,
                    _ => -1
                };
                var preview = val is string sv ? "  \"" + sv.Replace("\r", " ").Replace("\n", " ") + "\"" : "";
                Console.WriteLine($"  [{val?.GetType().Name ?? "null"}] {key}  ({len}){preview}");
                if (nameFilter != null && key.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (outDir == null) continue;
                Directory.CreateDirectory(outDir);
                var safe = key.Replace('/', '_').Replace('\\', '_');
                var file = Path.Combine(outDir, safe);
                if (val is byte[] bytes) File.WriteAllBytes(file, bytes);
                else if (val is string str) File.WriteAllText(file, str);
                else if (val is Stream stream)
                {
                    using var fs = File.Create(file);
                    stream.CopyTo(fs);
                }
                Console.WriteLine("    -> " + file);
            }
        }
    }
}
