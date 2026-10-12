using EVEMon.Common.Enumerations;
using EVEMon.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EVEMon.Common.Data
{
    /// <summary>
    /// Maps English object names to the Chinese names used by the datafiles.
    ///
    /// Several EVEMon features receive English names from outside the datafiles: ESI
    /// responses, EFT fitting text and fitting XML files. When the datafiles are
    /// translated into Chinese, those lookups would fail, so this table (generated
    /// alongside the Chinese datafiles) is consulted before giving up.
    /// </summary>
    public static class NameAlias
    {
        private const string AliasFilename = "eve-name-alias-zh.txt.gzip";

        private static Dictionary<string, string> s_englishToChinese;
        private static readonly object s_lock = new object();

        /// <summary>
        /// Resolves an English name to the Chinese name used by the datafiles. Returns the
        /// input unchanged when aliases are unavailable, the server is not Seren, or the
        /// name has no Chinese counterpart.
        /// </summary>
        /// <param name="name">The English name to resolve.</param>
        /// <returns>The Chinese name, or the input when no alias exists.</returns>
        public static string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // Aliases only make sense for the Chinese datafiles
            if (EveServerConfig.CurrentServerType != EveServerType.Seren)
                return name;

            var map = GetMap();
            if (map.Count == 0)
                return name;

            return map.TryGetValue(name, out string chinese) ? chinese : name;
        }

        /// <summary>
        /// Loads the alias table once, on first use.
        /// </summary>
        private static Dictionary<string, string> GetMap()
        {
            if (s_englishToChinese != null)
                return s_englishToChinese;

            lock (s_lock)
            {
                if (s_englishToChinese != null)
                    return s_englishToChinese;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    string path = Datafile.GetFullPath(AliasFilename);
                    using (var stream = File.OpenRead(path))
                    using (var gzipStream = new GZipStream(stream, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            int tab = line.IndexOf('\t');
                            if (tab > 0 && tab < line.Length - 1)
                                map[line.Substring(0, tab)] = line.Substring(tab + 1);
                        }
                    }
                    EveMonClient.Trace($"NameAlias: loaded {map.Count} English->Chinese aliases");
                }
                catch (Exception ex)
                {
                    // A missing alias table is not fatal: names simply stay untranslated
                    EveMonClient.Trace($"NameAlias: alias table unavailable ({ex.Message})");
                }

                s_englishToChinese = map;
                return s_englishToChinese;
            }
        }
    }
}
