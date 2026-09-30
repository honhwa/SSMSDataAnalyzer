using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    public static class TableColumnComparer
    {
        /// <summary>Empty = same columns. Otherwise one short, value-free line per difference:
        /// repo columns in file order first (changed or "only in repo"), then "only on server",
        /// then "column order differs". Names compare case-insensitively.</summary>
        public static IReadOnlyList<string> Compare(IReadOnlyList<TableColumn> repo, IReadOnlyList<TableColumn> server)
        {
            var lines = new List<string>();
            repo = repo ?? new List<TableColumn>();
            server = server ?? new List<TableColumn>();

            var serverByName = new Dictionary<string, TableColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (TableColumn s in server)
                if (s != null && !serverByName.ContainsKey(s.Name)) serverByName[s.Name] = s;

            var repoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commonInRepoOrder = new List<string>();
            foreach (TableColumn r in repo)
            {
                if (r == null || !repoNames.Add(r.Name)) continue;
                if (!serverByName.TryGetValue(r.Name, out TableColumn s))
                {
                    lines.Add("[" + r.Name + "] only in repo");
                    continue;
                }
                commonInRepoOrder.Add(r.Name);
                if (!r.SameAs(s))
                    lines.Add("[" + r.Name + "]: " + r.Describe() + " in repo, " + s.Describe() + " on server");
            }

            var commonInServerOrder = new List<string>();
            foreach (TableColumn s in server)
            {
                if (s == null) continue;
                if (!repoNames.Contains(s.Name)) lines.Add("[" + s.Name + "] only on server");
                else if (!commonInServerOrder.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) commonInServerOrder.Add(s.Name);
            }

            for (int i = 0; i < commonInRepoOrder.Count; i++)
            {
                if (!string.Equals(commonInRepoOrder[i], commonInServerOrder[i], StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add("column order differs");
                    break;
                }
            }
            return lines;
        }
    }
}
