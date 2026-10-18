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
        private const string AliasFilename = "eve-name-alias-zh-CN.txt.gzip";

        private static Dictionary<string, string> s_englishToChinese;
        private static Dictionary<string, List<string>> s_chineseToEnglish;
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
        /// Returns true when the Chinese name matches the search text, either directly or
        /// through one of its English counterparts in the alias table.
        ///
        /// The search boxes used to look at the description as well, which produced a lot of
        /// noise once the datafiles were translated: searching "net" matched every item whose
        /// English description contained "kinetic", "magnetic" or "planetary".
        /// </summary>
        /// <param name="name">The Chinese name of the object.</param>
        /// <param name="searchText">The text typed in the search box.</param>
        /// <returns>True when the object should be listed.</returns>
        public static bool MatchesName(string name, string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
                return true;

            if (string.IsNullOrEmpty(name))
                return false;

            if (name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Aliases only make sense for the Chinese datafiles
            if (EveServerConfig.CurrentServerType != EveServerType.Seren)
                return false;

            var reverse = GetReverseMap();
            if (reverse.Count == 0)
                return false;

            if (!reverse.TryGetValue(name, out List<string> englishNames))
                return false;

            foreach (string englishName in englishNames)
            {
                if (MatchesWordPrefix(englishName, searchText))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the search text starts a word inside the text. Matching on
        /// word starts keeps short queries out of the middle of longer words: searching
        /// "net" must not match "kinetic" or "magnetic".
        /// </summary>
        private static bool MatchesWordPrefix(string text, string searchText)
        {
            int index = text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
                    return true;

                int next = index + 1;
                if (next >= text.Length)
                    return false;

                index = text.IndexOf(searchText, next, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Builds the Chinese to English view of the alias table once, on first use.
        /// </summary>
        private static Dictionary<string, List<string>> GetReverseMap()
        {
            if (s_chineseToEnglish != null)
                return s_chineseToEnglish;

            lock (s_lock)
            {
                if (s_chineseToEnglish != null)
                    return s_chineseToEnglish;

                var reverse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> pair in GetMap())
                {
                    if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value))
                        continue;

                    if (!reverse.TryGetValue(pair.Value, out List<string> englishNames))
                    {
                        englishNames = new List<string>(1);
                        reverse[pair.Value] = englishNames;
                    }

                    englishNames.Add(pair.Key);
                }

                s_chineseToEnglish = reverse;
                return s_chineseToEnglish;
            }
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
