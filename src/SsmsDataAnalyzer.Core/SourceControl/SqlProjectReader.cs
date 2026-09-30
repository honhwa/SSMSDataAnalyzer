using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace SsmsDataAnalyzer.Core.SourceControl
{
    /// <summary>
    /// The <c>.sqlproj</c> inventory: which files SSDT actually builds. A file on disk that this
    /// does not list is silently ignored by SSDT -- it never deploys and never fails -- which is
    /// exactly the <c>OnDiskNotInProject</c> finding this feature exists to catch.
    /// </summary>
    public sealed class SqlProjectFiles
    {
        private readonly HashSet<string> _includes;

        internal SqlProjectFiles(bool includesAreImplicit, IEnumerable<string> includes)
        {
            IncludesAreImplicit = includesAreImplicit;
            _includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includes != null)
            {
                foreach (string inc in includes)
                    _includes.Add(Normalize(inc));
            }
        }

        /// <summary>SDK-style or wildcard project, no Build items, or unreadable XML: every
        /// <c>.sql</c> counts as included, so <c>OnDiskNotInProject</c> can never fire.</summary>
        public bool IncludesAreImplicit { get; }

        public bool Contains(string relativePath)
        {
            if (IncludesAreImplicit) return true;
            if (relativePath == null) return false;
            return _includes.Contains(Normalize(relativePath));
        }

        private static string Normalize(string path) => (path ?? string.Empty).Trim().Replace('\\', '/');
    }

    public static class SqlProjectReader
    {
        /// <summary>Unreadable XML -&gt; implicit (see <see cref="SqlProjectFiles.IncludesAreImplicit"/>);
        /// reporting 1,000+ files as "not in project" on a parse error would be exactly the false
        /// alarm this feature must not produce. Never throws.</summary>
        public static SqlProjectFiles Read(string sqlprojXml)
        {
            if (string.IsNullOrWhiteSpace(sqlprojXml))
                return new SqlProjectFiles(true, null);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(sqlprojXml);
            }
            catch
            {
                return new SqlProjectFiles(true, null);
            }

            XElement root = doc.Root;
            if (root == null)
                return new SqlProjectFiles(true, null);

            XNamespace ns = root.Name.Namespace;
            List<XElement> buildElements = new List<XElement>(root.Descendants(ns + "Build"));
            if (buildElements.Count == 0)
                return new SqlProjectFiles(true, null);

            var includes = new List<string>();
            foreach (XElement el in buildElements)
            {
                string include = (string)el.Attribute("Include");
                if (string.IsNullOrEmpty(include)) continue;
                if (include.IndexOf('*') >= 0)
                {
                    // A wildcard include means the project doesn't enumerate files explicitly at
                    // all -- treat the whole project as implicit rather than half-trusting the list.
                    return new SqlProjectFiles(true, null);
                }
                includes.Add(include);
            }

            if (includes.Count == 0)
                return new SqlProjectFiles(true, null);

            return new SqlProjectFiles(false, includes);
        }
    }
}
