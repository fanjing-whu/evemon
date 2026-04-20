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
        /// Type details are fetched on-demand from groups' and market_groups' types[] fields
        /// (not pre-downloaded from all 54k+ universe_types).
        /// </summary>
        /// <param name="categoriesJson">JSON from /universe/categories/ endpoint.</param>
        /// <param name="groupsJson">JSON from /universe/groups/ endpoint.</param>
        /// <param name="typesJson">Not used — type details are fetched on-demand.</param>
        /// <param name="marketGroupsJson">JSON from /market/groups/ endpoint.</param>
        /// <returns>A serializable ItemsDatafile object.</returns>
        public static ItemsDatafile ConvertItems(
            string categoriesJson,
            string groupsJson,
            string typesJson,
            string marketGroupsJson)
        {
            var datafile = new ItemsDatafile();

            // Parse JSON data (typesJson is ignored; types are fetched on-demand)
            var categories = ParseJson<List<EsiCategory>>(categoriesJson);
            var groups = ParseJson<List<EsiGroup>>(groupsJson);
            var marketGroups = ParseJson<List<EsiMarketGroup>>(marketGroupsJson);

            if (categories == null || groups == null || marketGroups == null)
            {
                EveMonClient.Trace("ESIToDatafileConverter: Failed to parse ESI data for items conversion");
                return datafile;
            }

            // Build group lookup dictionary
            var groupDict = new Dictionary<int, EsiGroup>();
            foreach (var group in groups)
            {
                groupDict[group.group_id] = group;
            }

            // Collect all type IDs referenced by market groups and groups
            var allItemTypeIds = new HashSet<int>();
            foreach (var mg in marketGroups)
            {
                if (mg.types != null)
                    foreach (int id in mg.types)
                        allItemTypeIds.Add(id);
            }
            foreach (var group in groups)
            {
                if (group.types != null)
                    foreach (int id in group.types)
                        allItemTypeIds.Add(id);
            }

            EveMonClient.Trace($"ESIToDatafileConverter: Fetching {allItemTypeIds.Count} item type details on-demand...");
            var itemTypes = ESISdeDownloader.FetchTypeDetailsAsync(allItemTypeIds).GetAwaiter().GetResult();

            // Build market group dictionary
            var serializableGroupDict = new Dictionary<int, SerializableMarketGroup>();
            foreach (var mg in marketGroups)
            {
                serializableGroupDict[mg.market_group_id] = new SerializableMarketGroup
                {
                    ID = mg.market_group_id,
                    Name = mg.name ?? $"Market Group {mg.market_group_id}"
                };
            }

            // Helper to create a SerializableItem from an EsiType
            SerializableItem CreateItem(ESISdeDownloader.EsiType type, Dictionary<int, EsiGroup> gd)
            {
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
                if (type.group_id.HasValue && gd.TryGetValue(type.group_id.Value, out var esiGroup))
                    item.Group = esiGroup.name ?? $"Group {type.group_id}";
                return item;
            }

            // Assign items to market groups using market_group.types[]
            foreach (var mg in marketGroups)
            {
                if (mg.types == null || mg.types.Count == 0)
                    continue;
                if (!serializableGroupDict.TryGetValue(mg.market_group_id, out var serializableMg))
                    continue;

                foreach (int typeId in mg.types)
                {
                    if (itemTypes.TryGetValue(typeId, out var type) && type.published)
                    {
                        serializableMg.Items.Add(CreateItem(type, groupDict));
                    }
                }
            }

            // Also assign items referenced by group.types[] that weren't already added
            var addedIds = new HashSet<int>();
            foreach (var mg in serializableGroupDict.Values)
            {
                foreach (var item in mg.Items)
                    addedIds.Add(item.ID);
            }
            foreach (var group in groups)
            {
                if (group.types == null)
                    continue;
                foreach (int typeId in group.types)
                {
                    if (addedIds.Contains(typeId))
                        continue;
                    if (itemTypes.TryGetValue(typeId, out var type) && type.published)
                    {
                        // Add to the market group this type belongs to, if any
                        if (type.market_group_id.HasValue &&
                            serializableGroupDict.TryGetValue(type.market_group_id.Value, out var targetMg))
                        {
                            targetMg.Items.Add(CreateItem(type, groupDict));
                            addedIds.Add(typeId);
                        }
                    }
                }
            }

            // Add all groups as flat root groups
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
        /// <param name="typesJson">Not used — type details are fetched on-demand via ESISdeDownloader.</param>
        /// <param name="dogmaAttributesJson">JSON from /dogma/attributes/ endpoint.</param>
        /// <returns>A serializable SkillsDatafile object.</returns>
        public static SkillsDatafile ConvertSkills(
            string groupsJson,
            string typesJson,
            string dogmaAttributesJson)
        {
            var datafile = new SkillsDatafile();

            // Parse groups from cache
            var groups = ParseJson<List<EsiGroup>>(groupsJson);
            if (groups == null)
            {
                EveMonClient.Trace("ESIToDatafileConverter: Failed to parse groups data for skills conversion");
                return datafile;
            }

            // Skill category ID is 16 in EVE ESI
            const int SkillCategoryId = 16;

            // Filter skill groups and collect all type IDs they reference
            var skillGroups = new List<EsiGroup>();
            var allSkillTypeIds = new HashSet<int>();
            foreach (var group in groups)
            {
                if (group.category_id == SkillCategoryId && group.published)
                {
                    skillGroups.Add(group);
                    if (group.types != null)
                    {
                        foreach (int typeId in group.types)
                            allSkillTypeIds.Add(typeId);
                    }
                }
            }

            if (allSkillTypeIds.Count == 0)
            {
                EveMonClient.Trace("ESIToDatafileConverter: No skill type IDs found in groups");
                return datafile;
            }

            // Fetch type details on-demand (only the skill types referenced by groups, not all 54k+)
            EveMonClient.Trace($"ESIToDatafileConverter: Fetching {allSkillTypeIds.Count} skill type details on-demand...");
            var skillTypes = ESISdeDownloader.FetchTypeDetailsAsync(allSkillTypeIds).GetAwaiter().GetResult();

            // Build lookup dictionary keyed by group_id
            var skillsByGroup = new Dictionary<int, List<ESISdeDownloader.EsiType>>();
            foreach (var kvp in skillTypes)
            {
                var type = kvp.Value;
                if (type.published && type.group_id.HasValue)
                {
                    if (!skillsByGroup.ContainsKey(type.group_id.Value))
                        skillsByGroup[type.group_id.Value] = new List<ESISdeDownloader.EsiType>();
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

