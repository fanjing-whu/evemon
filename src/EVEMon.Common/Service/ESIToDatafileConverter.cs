using EVEMon.Common.Enumerations;
using EVEMon.Common.Serialization.Datafiles;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
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
        /// Represents a group from ESI.
        /// </summary>
        private class EsiGroup
        {
            public int group_id { get; set; }
            public string name { get; set; }
            public int category_id { get; set; }
            public bool published { get; set; }
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
        /// Represents a market group from ESI.
        /// </summary>
        private class EsiMarketGroup
        {
            public int market_group_id { get; set; }
            public string name { get; set; }
            public int? parent_group_id { get; set; }
            public int? icon_id { get; set; }
            public bool visible { get; set; }
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

            // Build market group hierarchy
            var marketGroupDict = new Dictionary<int, EsiMarketGroup>();
            var rootGroups = new List<SerializableMarketGroup>();

            foreach (var mg in marketGroups)
            {
                marketGroupDict[mg.market_group_id] = mg;
            }

            // Create market group tree
            foreach (var mg in marketGroups)
            {
                var serializableMg = new SerializableMarketGroup
                {
                    ID = mg.market_group_id,
                    Name = mg.name ?? $"Market Group {mg.market_group_id}"
                };

                if (mg.parent_group_id.HasValue && marketGroupDict.ContainsKey(mg.parent_group_id.Value))
                {
                    // Will be added as child later
                }
                else
                {
                    rootGroups.Add(serializableMg);
                }
            }

            // Build parent-child relationships
            BuildMarketGroupHierarchy(marketGroups, marketGroupDict, rootGroups);

            // Assign items to market groups
            var typeByMarketGroup = new Dictionary<int, List<EsiType>>();
            foreach (var type in types)
            {
                if (type.published && type.market_group_id.HasValue)
                {
                    if (!typeByMarketGroup.ContainsKey(type.market_group_id.Value))
                    {
                        typeByMarketGroup[type.market_group_id.Value] = new List<EsiType>();
                    }
                    typeByMarketGroup[type.market_group_id.Value].Add(type);
                }
            }

            // Populate items into market groups
            PopulateMarketGroupItems(rootGroups, marketGroupDict, typeByMarketGroup, groupDict);

            // Add root groups to datafile
            foreach (var rootGroup in rootGroups)
            {
                datafile.MarketGroups.Add(rootGroup);
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

        /// <summary>
        /// Builds the market group hierarchy.
        /// </summary>
        private static void BuildMarketGroupHierarchy(
            List<EsiMarketGroup> allGroups,
            Dictionary<int, EsiMarketGroup> groupDict,
            List<SerializableMarketGroup> rootGroups)
        {
            // Build parent-child relationships
            var childGroups = new Dictionary<int, List<SerializableMarketGroup>>();

            foreach (var rootGroup in rootGroups)
            {
                childGroups[rootGroup.ID] = new List<SerializableMarketGroup>();
            }

            // Create serializable market groups and build hierarchy
            var serializableGroups = new Dictionary<int, SerializableMarketGroup>();

            foreach (var mg in allGroups)
            {
                serializableGroups[mg.market_group_id] = new SerializableMarketGroup
                {
                    ID = mg.market_group_id,
                    Name = mg.name ?? $"Market Group {mg.market_group_id}"
                };
            }

            // Add children to parents
            foreach (var mg in allGroups)
            {
                if (mg.parent_group_id.HasValue && serializableGroups.ContainsKey(mg.parent_group_id.Value))
                {
                    serializableGroups[mg.parent_group_id.Value].SubGroups.Add(serializableGroups[mg.market_group_id]);
                }
            }
        }

        /// <summary>
        /// Populates market groups with their items.
        /// </summary>
        private static void PopulateMarketGroupItems(
            List<SerializableMarketGroup> groups,
            Dictionary<int, EsiMarketGroup> groupDict,
            Dictionary<int, List<EsiType>> typesByMarketGroup,
            Dictionary<int, EsiGroup> groupDictForItems)
        {
            foreach (var group in groups)
            {
                // Add items for this market group
                if (typesByMarketGroup.TryGetValue(group.ID, out var types))
                {
                    foreach (var type in types)
                    {
                        var item = new SerializableItem
                        {
                            ID = type.type_id,
                            Name = type.name ?? $"Item {type.type_id}",
                            Description = type.description ?? "",
                            Category = "Item",
                            PortionSize = (int)(type.portion_size ?? 1)
                        };

                        // Set icon if available
                        if (type.icon_id.HasValue)
                        {
                            item.Icon = type.icon_id.Value.ToString();
                        }

                        // Determine family from group
                        if (type.group_id.HasValue && groupDictForItems.TryGetValue(type.group_id.Value, out var esiGroup))
                        {
                            item.Group = esiGroup.name ?? $"Group {type.group_id}";
                        }

                        group.Items.Add(item);
                    }
                }

                // Recursively populate sub-groups
                if (group.SubGroups != null && group.SubGroups.Count > 0)
                {
                    PopulateMarketGroupItemsRecursive(group.SubGroups, groupDict, typesByMarketGroup, groupDictForItems);
                }
            }
        }

        /// <summary>
        /// Recursively populates items into sub-groups.
        /// </summary>
        private static void PopulateMarketGroupItemsRecursive(
            Collection<SerializableMarketGroup> groups,
            Dictionary<int, EsiMarketGroup> groupDict,
            Dictionary<int, List<EsiType>> typesByMarketGroup,
            Dictionary<int, EsiGroup> groupDictForItems)
        {
            foreach (var group in groups)
            {
                // Add items for this market group
                if (typesByMarketGroup.TryGetValue(group.ID, out var types))
                {
                    foreach (var type in types)
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
                        {
                            item.Icon = type.icon_id.Value.ToString();
                        }

                        if (type.group_id.HasValue && groupDictForItems.TryGetValue(type.group_id.Value, out var esiGroup))
                        {
                            item.Group = esiGroup.name ?? $"Group {type.group_id}";
                        }

                        group.Items.Add(item);
                    }
                }

                // Recurse into sub-groups
                if (group.SubGroups != null && group.SubGroups.Count > 0)
                {
                    PopulateMarketGroupItemsRecursive(group.SubGroups, groupDict, typesByMarketGroup, groupDictForItems);
                }
            }
        }

        #endregion
    }
}
