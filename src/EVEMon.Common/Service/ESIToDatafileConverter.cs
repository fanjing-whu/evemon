using EVEMon.Common.Enumerations;
using EVEMon.Common.Serialization.Datafiles;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Converts ESI JSON data to the local XML datafile format.
    /// This is a core component of the C1 solution (Pure ESI mode + Local Cache).
    /// </summary>
    public static class ESIToDatafileConverter
    {
        /// <summary>
        /// Represents a category from ESI.
        /// </summary>
        private class EsiCategory
        {
            public int category_id { get; set; }
            public string name { get; set; }
            public List<int> groups { get; set; }
            public bool published { get; set; }
        }

        /// <summary>
        /// Represents a group from ESI (/v1/universe/groups/{id}/).
        /// Note: ESI returns "types" (list of type IDs in this group), not "type_ids".
        /// </summary>
        private class EsiGroup
        {
            public int group_id { get; set; }
            public string name { get; set; }
            public int category_id { get; set; }
            public bool published { get; set; }
            public List<int> types { get; set; }
        }

        /// <summary>
        /// Represents a type from ESI.
        /// </summary>
        private class EsiType
        {
            public int type_id { get; set; }
            public string name { get; set; }
            public string description { get; set; }
            public bool published { get; set; }
            public int? group_id { get; set; }
            public int? icon_id { get; set; }
            public int? market_group_id { get; set; }
            public double? portion_size { get; set; }
            public int? mass { get; set; }
            public double? volume { get; set; }
            public int? capacity { get; set; }
            public int? base_price { get; set; }
            public List<int> dogma_attributes { get; set; }
            public List<int> dogma_effects { get; set; }
        }

        /// <summary>
        /// Represents a dogma attribute from ESI.
        /// </summary>
        private class EsiDogmaAttribute
        {
            public int attribute_id { get; set; }
            public string name { get; set; }
            public string description { get; set; }
            public int icon_id { get; set; }
            public int default_value { get; set; }
            public double? high_is_good { get; set; }
            public bool published { get; set; }
        }

        /// <summary>
        /// Represents a dogma effect from ESI.
        /// </summary>
        private class EsiDogmaEffect
        {
            public int effect_id { get; set; }
            public string name { get; set; }
            public bool published { get; set; }
        }

        /// <summary>
        /// Represents a market group from ESI (/v1/markets/groups/{id}/).
        /// Note: ESI returns "types" (list of type IDs), no parent_group_id field.
        /// </summary>
        private class EsiMarketGroup
        {
            public int market_group_id { get; set; }
            public string name { get; set; }
            public string description { get; set; }
            public List<int> types { get; set; }
        }

        /// <summary>
        /// Converts universe types (items) JSON to a serializable items datafile.
        /// </summary>
        /// <param name="categoriesJson">JSON from /universe/categories/ endpoint.</param>
        /// <param name="groupsJson">JSON from /universe/groups/ endpoint.</param>
        /// <param name="typesJson">JSON from /universe/types/ endpoint.</param>
        /// <param name="marketGroupsJson">JSON from /market/groups/ endpoint.</param>
        /// <returns>A serializable ItemsDatafile object.</returns>
        public static ItemsDatafile ConvertItems(
            string categoriesJson,
            string groupsJson,
            string typesJson,
            string marketGroupsJson)
        {
            var datafile = new ItemsDatafile();

            // Parse JSON data
            var categories = ParseJson<List<EsiCategory>>(categoriesJson);
            var groups = ParseJson<List<EsiGroup>>(groupsJson);
            var types = ParseJson<List<EsiType>>(typesJson);
            var marketGroups = ParseJson<List<EsiMarketGroup>>(marketGroupsJson);

            if (categories == null || groups == null || types == null || marketGroups == null)
            {
                EveMonClient.Trace("ESIToDatafileConverter: Failed to parse ESI data for items conversion");
                return datafile;
            }

            // Build lookup dictionaries
            var groupDict = new Dictionary<int, EsiGroup>();
            foreach (var group in groups)
            {
                groupDict[group.group_id] = group;
            }

            // Build market group dictionary
            var marketGroupDict = new Dictionary<int, EsiMarketGroup>();
            foreach (var mg in marketGroups)
            {
                marketGroupDict[mg.market_group_id] = mg;
            }

            // ESI market group detail does not include parent_group_id, so build a flat list.
            // Each market group will contain its associated types directly.
            var serializableGroupDict = new Dictionary<int, SerializableMarketGroup>();
            foreach (var mg in marketGroups)
            {
                serializableGroupDict[mg.market_group_id] = new SerializableMarketGroup
                {
                    ID = mg.market_group_id,
                    Name = mg.name ?? $"Market Group {mg.market_group_id}"
                };
            }

            // Assign items to market groups using type.market_group_id
            foreach (var type in types)
            {
                if (!type.published || !type.market_group_id.HasValue)
                    continue;

                if (!serializableGroupDict.TryGetValue(type.market_group_id.Value, out var serializableMg))
                    continue;

                var item = new SerializableItem
                {
                    ID = type.type_id,
                    Name = type.name ?? $"Item {type.type_id}",
                    Description = type.description ?? "",
                    Category = "Item",
                    PortionSize = (int)(type.portion_size ?? 1)
                };

                if (type.icon_id.HasValue)
                    item.Icon = type.icon_id.Value.ToString();

                if (type.group_id.HasValue && groupDict.TryGetValue(type.group_id.Value, out var esiGroup))
                    item.Group = esiGroup.name ?? $"Group {type.group_id}";

                serializableMg.Items.Add(item);
            }

            // Also assign items from market_group.types (if ESI embeds type IDs in group detail)
            foreach (var mg in marketGroups)
            {
                if (mg.types == null || mg.types.Count == 0)
                    continue;
                if (!serializableGroupDict.TryGetValue(mg.market_group_id, out var serializableMg))
                    continue;

                var existingIds = new HashSet<int>(serializableMg.Items.Select(i => i.ID));
                foreach (int typeId in mg.types)
                {
                    if (existingIds.Contains(typeId))
                        continue;
                    // Find the type
                    var type = types.FirstOrDefault(t => t.type_id == typeId);
                    if (type == null || !type.published)
                        continue;

                    var item = new SerializableItem
                    {
                        ID = type.type_id,
                        Name = type.name ?? $"Item {type.type_id}",
                        Description = type.description ?? "",
                        Category = "Item",
                        PortionSize = (int)(type.portion_size ?? 1)
                    };
                    if (type.icon_id.HasValue)
                        item.Icon = type.icon_id.Value.ToString();
                    if (type.group_id.HasValue && groupDict.TryGetValue(type.group_id.Value, out var esiGroup))
                        item.Group = esiGroup.name ?? $"Group {type.group_id}";

                    serializableMg.Items.Add(item);
                    existingIds.Add(typeId);
                }
            }

            // Add all groups as flat root groups (no parent-child hierarchy since ESI doesn't provide it)
            foreach (var sg in serializableGroupDict.Values)
            {
                datafile.MarketGroups.Add(sg);
            }

            return datafile;
        }

        /// <summary>
        /// Converts universe types to skills datafile format.
        /// Skills in EVE are items with category_id = 16 (or skill group).
        /// </summary>
        /// <param name="groupsJson">JSON from /universe/groups/ endpoint.</param>
        /// <param name="typesJson">JSON from /universe/types/ endpoint.</param>
        /// <param name="dogmaAttributesJson">JSON from /dogma/attributes/ endpoint.</param>
        /// <returns>A serializable SkillsDatafile object.</returns>
        public static SkillsDatafile ConvertSkills(
            string groupsJson,
            string typesJson,
            string dogmaAttributesJson)
        {
            var datafile = new SkillsDatafile();

            // Parse JSON data
            var groups = ParseJson<List<EsiGroup>>(groupsJson);
            var types = ParseJson<List<EsiType>>(typesJson);

            if (groups == null || types == null)
            {
                EveMonClient.Trace("ESIToDatafileConverter: Failed to parse ESI data for skills conversion");
                return datafile;
            }

            // Skill category ID is 16 in EVE ESI
            const int SkillCategoryId = 16;

            // Filter skill groups
            var skillGroups = new List<EsiGroup>();
            foreach (var group in groups)
            {
                if (group.category_id == SkillCategoryId && group.published)
                {
                    skillGroups.Add(group);
                }
            }

            // Build skill group dictionary
            var skillGroupDict = new Dictionary<int, EsiGroup>();
            foreach (var group in skillGroups)
            {
                skillGroupDict[group.group_id] = group;
            }

            // Filter skills (published types in skill groups)
            var skillsByGroup = new Dictionary<int, List<EsiType>>();
            foreach (var type in types)
            {
                if (type.published && type.group_id.HasValue && skillGroupDict.ContainsKey(type.group_id.Value))
                {
                    if (!skillsByGroup.ContainsKey(type.group_id.Value))
                    {
                        skillsByGroup[type.group_id.Value] = new List<EsiType>();
                    }
                    skillsByGroup[type.group_id.Value].Add(type);
                }
            }

            // Build skill groups with skills
            foreach (var group in skillGroups)
            {
                var skillGroup = new SerializableSkillGroup
                {
                    ID = group.group_id,
                    Name = group.name ?? $"Skill Group {group.group_id}"
                };

                if (skillsByGroup.TryGetValue(group.group_id, out var skills))
                {
                    foreach (var skill in skills)
                    {
                        var serializableSkill = new SerializableSkill
                        {
                            ID = skill.type_id,
                            Name = skill.name ?? $"Skill {skill.type_id}",
                            Description = skill.description ?? "",
                            Public = skill.published,
                            Rank = 1, // Default rank, actual value requires dogma attributes
                            Cost = 0
                        };

                        // TODO: Extract actual skill attributes from dogma_attributes
                        // Primary/Secondary attribute and rank require parsing dogma attributes
                        // For now, use default values
                        serializableSkill.PrimaryAttribute = EveAttribute.Intelligence;
                        serializableSkill.SecondaryAttribute = EveAttribute.Memory;

                        skillGroup.Skills.Add(serializableSkill);
                    }
                }

                datafile.SkillGroups.Add(skillGroup);
            }

            return datafile;
        }

        /// <summary>
        /// Converts universe data to geography datafile format.
        /// </summary>
        /// <param name="regionsJson">JSON from /universe/regions/ endpoint.</param>
        /// <param name="constellationsJson">JSON from /universe/constellations/ endpoint.</param>
        /// <param name="systemsJson">JSON from /universe/systems/ endpoint.</param>
        /// <returns>A serializable GeoDatafile object.</returns>
        public static GeoDatafile ConvertGeography(
            string regionsJson,
            string constellationsJson,
            string systemsJson)
        {
            var datafile = new GeoDatafile();

            // Parse JSON data - ESI returns just IDs for regions/constellations/systems lists
            var regionIds = ParseJson<List<int>>(regionsJson);
            var constellationIds = ParseJson<List<int>>(constellationsJson);
            var systemIds = ParseJson<List<int>>(systemsJson);

            if (regionIds == null || constellationIds == null || systemIds == null)
            {
                EveMonClient.Trace("ESIToDatafileConverter: Failed to parse ESI data for geography conversion");
                return datafile;
            }

            // Note: For full data, we would need to fetch each region's details
            // For now, create placeholder regions with just the IDs
            foreach (var regionId in regionIds)
            {
                datafile.Regions.Add(new SerializableRegion
                {
                    ID = regionId,
                    Name = $"Region {regionId}"
                });
            }

            // TODO: Fetch and add constellation and system details
            // This would require additional API calls for each ID

            return datafile;
        }

        /// <summary>
        /// Saves a datafile to the cache in XML GZip format.
        /// </summary>
        /// <typeparam name="T">The datafile type.</typeparam>
        /// <param name="datafile">The datafile to save.</param>
        /// <param name="filename">The target filename.</param>
        public static void SaveDatafileToCache<T>(T datafile, string filename) where T : class
        {
            try
            {
                string cacheDir = Path.Combine(
                    EveMonClient.EVEMonDataDir ?? Path.GetTempPath(),
                    "cache", "esi");

                Directory.CreateDirectory(cacheDir);

                string filepath = Path.Combine(cacheDir, filename);

                var serializer = new XmlSerializer(typeof(T));
                using (var stream = File.Create(filepath))
                using (var gzipStream = new GZipStream(stream, CompressionLevel.Optimal))
                {
                    serializer.Serialize(gzipStream, datafile);
                }

                EveMonClient.Trace($"ESIToDatafileConverter: Saved {filename} to cache");
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"ESIToDatafileConverter: Failed to save {filename}: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads a datafile from the cache in XML GZip format.
        /// </summary>
        /// <typeparam name="T">The datafile type.</typeparam>
        /// <param name="filename">The filename to load.</param>
        /// <returns>The deserialized datafile, or null if not found.</returns>
        public static T LoadDatafileFromCache<T>(string filename) where T : class
        {
            try
            {
                string cacheDir = Path.Combine(
                    EveMonClient.EVEMonDataDir ?? Path.GetTempPath(),
                    "cache", "esi");

                string filepath = Path.Combine(cacheDir, filename);

                if (!File.Exists(filepath))
                {
                    return null;
                }

                var serializer = new XmlSerializer(typeof(T));
                using (var stream = File.OpenRead(filepath))
                using (var gzipStream = new GZipStream(stream, CompressionMode.Decompress))
                {
                    return (T)serializer.Deserialize(gzipStream);
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"ESIToDatafileConverter: Failed to load {filename}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if a cached datafile exists.
        /// </summary>
        /// <param name="filename">The filename to check.</param>
        /// <returns>True if the file exists.</returns>
        public static bool CachedDatafileExists(string filename)
        {
            string cacheDir = Path.Combine(
                EveMonClient.EVEMonDataDir ?? Path.GetTempPath(),
                "cache", "esi");

            string filepath = Path.Combine(cacheDir, filename);
            return File.Exists(filepath);
        }

        /// <summary>
        /// Gets the path to a cached datafile.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>The full path to the cached file.</returns>
        public static string GetCachedDatafilePath(string filename)
        {
            string cacheDir = Path.Combine(
                EveMonClient.EVEMonDataDir ?? Path.GetTempPath(),
                "cache", "esi");

            return Path.Combine(cacheDir, filename);
        }

        #region Private Helper Methods

        /// <summary>
        /// Parses JSON string safely.
        /// </summary>
        private static T ParseJson<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException ex)
            {
                EveMonClient.Trace($"ESIToDatafileConverter: JSON parse error: {ex.Message}");
                return null;
            }
        }


        #endregion
    }
}

