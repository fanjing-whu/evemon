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
    /// Local fallback names for player-owned structures.
    ///
    /// Structures are not part of the static geography: EVEMon asks ESI for them, and ESI
    /// refuses to describe a structure the character has no docking rights for. Those end up
    /// as "Inaccessible Structure". This table, generated from a Serenity market snapshot,
    /// supplies a name and a solar system so the UI stays useful for public structures.
    /// </summary>
    public static class StructureNameCache
    {
        private const string Filename = "eve-structures-zh-CN.txt.gzip";

        /// <summary>
        /// A structure name with the solar system it sits in.
        /// </summary>
        public struct Entry
        {
            public string Name;
            public int SolarSystemID;
        }

        private static Dictionary<long, Entry> s_entries;
        private static readonly object s_lock = new object();

        /// <summary>
        /// Looks up a structure by ID. Returns false when the table is unavailable, the server
        /// is not Seren, or the structure is not listed.
        /// </summary>
        /// <param name="structureID">The structure ID to look up.</param>
        /// <param name="name">The structure name when found.</param>
        /// <param name="solarSystemID">The solar system the structure sits in.</param>
        /// <returns>True when the structure has a local name.</returns>
        public static bool TryGet(long structureID, out string name, out int solarSystemID)
        {
            name = null;
            solarSystemID = 0;

            // The table is generated for the Chinese datafiles
            if (EveServerConfig.CurrentServerType != EveServerType.Seren)
                return false;

            var map = GetMap();
            if (map.Count == 0)
                return false;

            if (!map.TryGetValue(structureID, out Entry entry))
                return false;

            name = entry.Name;
            solarSystemID = entry.SolarSystemID;
            return true;
        }

        /// <summary>
        /// Loads the table once, on first use.
        /// </summary>
        private static Dictionary<long, Entry> GetMap()
        {
            if (s_entries != null)
                return s_entries;

            lock (s_lock)
            {
                if (s_entries != null)
                    return s_entries;

                var map = new Dictionary<long, Entry>();
                try
                {
                    string path = Datafile.GetFullPath(Filename);
                    using (var stream = File.OpenRead(path))
                    using (var gzipStream = new GZipStream(stream, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string[] parts = line.Split('\t');
                            if (parts.Length < 3)
                                continue;

                            if (!long.TryParse(parts[0], out long id) ||
                                !int.TryParse(parts[2], out int solarSystemID))
                                continue;

                            map[id] = new Entry { Name = parts[1], SolarSystemID = solarSystemID };
                        }
                    }
                    EveMonClient.Trace($"StructureNameCache: loaded {map.Count} structure names");
                }
                catch (Exception ex)
                {
                    // A missing table is not fatal: structures simply stay "inaccessible"
                    EveMonClient.Trace($"StructureNameCache: table unavailable ({ex.Message})");
                }

                s_entries = map;
                return s_entries;
            }
        }
    }
}
