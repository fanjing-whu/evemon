using EVEMon.Common.Collections.Global;
using EVEMon.Common.Enumerations;
using EVEMon.Common.Helpers;
using EVEMon.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Handles data loading for Seren (Chinese server) in ESI-only mode.
    /// This is the integration point for C1 solution (Pure ESI mode + Local Cache).
    /// </summary>
    public static class SerenDataLoader
    {
        /// <summary>
        /// Progress reporter for download operations.
        /// </summary>
        public static event EventHandler<SerenLoadProgressEventArgs> LoadProgress;

        /// <summary>
        /// Gets whether Seren mode is active (Chinese server selected).
        /// </summary>
        public static bool IsSerenMode => EveServerConfig.CurrentServerType == EveServerType.Seren;

        /// <summary>
        /// Loads all static data for Seren mode.
        /// 
        /// Non-blocking strategy:
        /// 1. Check if ESI cache exists -> use it
        /// 2. If no cache -> load from local TQ resource immediately (zero network)
        /// 3. Background -> refresh ESI cache
        /// 
        /// This ensures UI never blocks on network operations.
        /// </summary>
        /// <param name="forceRefresh">If true, bypass cache and download fresh data.</param>
        /// <returns>True if all data loaded successfully.</returns>
        public static async Task<bool> LoadAsync(bool forceRefresh = false)
        {
            if (!IsSerenMode)
            {
                EveMonClient.Trace("SerenDataLoader: Not in Seren mode, skipping");
                return false;
            }

            try
            {
                ReportProgress(0, "Starting Seren data load...");

                // Step 1: Check if ESI cache has all required data
                ReportProgress(5, "Checking ESI cache...");
                bool hasCachedData = await CheckCachedDataAvailableAsync();
                
                if (hasCachedData && !forceRefresh)
                {
                    EveMonClient.Trace("SerenDataLoader: Using cached ESI data");
                }
                else
                {
                    // Step 2: No cache -> use local TQ resource immediately (zero network)
                    EveMonClient.Trace("SerenDataLoader: No cache found, using local TQ resource");
                    ReportProgress(10, "Loading from local TQ resource...");
                    
                    bool localLoaded = await LoadFromLocalResourceAsync();
                    if (!localLoaded)
                    {
                        EveMonClient.Trace("SerenDataLoader: Local TQ resource not available, queuing ESI download");
                        // Queue background ESI download
                        QueueBackgroundESIDownload();
                    }
                    else
                    {
                        // Successfully loaded from local TQ, queue background refresh
                        EveMonClient.Trace("SerenDataLoader: Loaded from local TQ, queuing background ESI refresh");
                        QueueBackgroundESIDownload();
                        
                        ReportProgress(100, "Seren data load complete (from local TQ)");
                        return true;
                    }
                }

                // Step 3: Load from ESI cache
                ReportProgress(60, "Loading static data from cache...");
                bool loadSuccess = await LoadStaticDataAsync();

                if (!loadSuccess)
                {
                    EveMonClient.Trace("SerenDataLoader: Failed to load from ESI cache, trying fallback...");
                    return false;
                }

                // Queue background refresh for next time
                QueueBackgroundESIDownload();

                ReportProgress(100, "Seren data load complete");
                EveMonClient.Trace("SerenDataLoader: Load complete");
                return true;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"SerenDataLoader: Error during load: {ex.Message}");
                ExceptionHandler.LogException(ex, true);
                ReportProgress(0, $"Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if all required ESI cached data is available.
        /// </summary>
        private static async Task<bool> CheckCachedDataAvailableAsync()
        {
            string[] requiredEndpoints = { "universe_categories", "universe_groups", "universe_types", "market_groups" };
            foreach (var endpoint in requiredEndpoints)
            {
                var data = await ESISdeCacheManager.ReadCacheAsync(endpoint, ESISdeCacheManager.SdeServer.Seren);
                if (string.IsNullOrEmpty(data))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Loads data from local resource files (TQ SDE items + Pyfa/TQ skills).
        /// This is the primary path when ESI cache is not available.
        /// </summary>
        private static async Task<bool> LoadFromLocalResourceAsync()
        {
            try
            {
                ReportProgress(15, "Loading local type details...");
                var tqCache = await ESISdeDownloader.LoadLocalTypeDetailsAsync();
                if (tqCache.Count == 0)
                {
                    EveMonClient.Trace("SerenDataLoader: Local TQ resource not found");
                    return false;
                }

                EveMonClient.Trace($"SerenDataLoader: Loaded {tqCache.Count} types from local TQ resource");

                // Convert to Items datafile and load
                ReportProgress(50, "Creating items datafile...");
                var itemsDatafile = ConvertTQToItemsDatafile(tqCache);
                
                // Properties must be loaded before items: every item property value resolves its
                // definition against this table at construction time (see GlobalDatafileCollection
                // .LoadAsync where the same ordering is documented). Without it the implant slot
                // property (331) can never be resolved, so implants collapse to zero bonus.
                ReportProgress(45, "Loading properties...");
                await Task.Run(() => Data.StaticProperties.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                await Task.Run(() => Data.StaticItems.LoadFromDatafile(itemsDatafile));
                GlobalDatafileCollection.OnDatafileLoaded();

                // Load other static data (non-ESI)
                ReportProgress(70, "Loading geography...");
                await Task.Run(() => Data.StaticGeography.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                ReportProgress(80, "Loading certificates...");
                await Task.Run(() => Data.StaticCertificates.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                ReportProgress(85, "Loading blueprints...");
                await Task.Run(() => Data.StaticBlueprints.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                ReportProgress(90, "Loading skills (Pyfa/TQ fallback)...");
                // Level 2 → Level 3 → Level 4 (ESI not available at this point)
                await Task.Run(() => LoadSkillsWithFallback());
                GlobalDatafileCollection.OnDatafileLoaded();

                ReportProgress(95, "Loading reprocessing...");
                await Task.Run(() => Data.StaticReprocessing.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                ReportProgress(99, "Loading masteries...");
                await Task.Run(() => Data.StaticMasteries.Load());
                GlobalDatafileCollection.OnDatafileLoaded();

                EveMonClient.Trace("SerenDataLoader: Loaded all data from local TQ resource");
                return true;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"SerenDataLoader: Failed to load from local TQ: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
                return false;
            }
        }

        /// <summary>
        /// Converts TQ type cache to Items datafile format.
        /// </summary>
        private static Serialization.Datafiles.ItemsDatafile ConvertTQToItemsDatafile(Dictionary<int, ESISdeDownloader.EsiType> tqCache)
        {
            var datafile = new Serialization.Datafiles.ItemsDatafile();
            var rootGroup = new Serialization.Datafiles.SerializableMarketGroup
            {
                ID = 0,
                Name = "Root"
            };
            
            // Group types by market_group_id
            var groups = tqCache.Values
                .Where(t => t.market_group_id.HasValue)
                .GroupBy(t => t.market_group_id.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Add items to the root (simplified - no proper hierarchy)
            foreach (var type in tqCache.Values)
            {
                var item = new Serialization.Datafiles.SerializableItem
                {
                    ID = type.type_id,
                    Name = type.name,
                    Description = type.description ?? "",
                    PortionSize = type.portion_size ?? 1
                };
                rootGroup.Items.Add(item);
            }

            datafile.MarketGroups.Add(rootGroup);
            return datafile;
        }

        /// <summary>
        /// Loads skills using the three-level fallback strategy for Seren mode:
        ///   Level 1: ESI cache (Seren Chinese data) — already handled by caller
        ///   Level 2: Pyfa static data (Chinese names, eve-skills-pyfa-zh-CN.xml.gzip)
        ///   Level 3: TQ SDE static data (English names, eve-skills-en-US.xml.gzip)
        ///   Level 4: Error — log and throw so the caller knows nothing loaded
        /// </summary>
        private static void LoadSkillsWithFallback()
        {
            // Level 2: Pyfa Chinese static data
            if (TryLoadSkillsFromFile("eve-skills-pyfa-zh-CN.xml.gzip", "Pyfa Chinese static data (zh-CN)"))
                return;

            // Level 3: TQ SDE English static data
            if (TryLoadSkillsFromFile("eve-skills-en-US.xml.gzip", "TQ SDE English static data (en-US)"))
                return;

            // Level 4: Nothing loaded — report error
            EveMonClient.Trace("SerenDataLoader: [ERROR] All skill data sources exhausted. No skills loaded.");
            EveMonClient.Trace("SerenDataLoader: [ERROR] Expected files in Resources/: eve-skills-pyfa-zh-CN.xml.gzip or eve-skills-en-US.xml.gzip");
            throw new InvalidOperationException(
                "Seren mode: Failed to load skill data from all sources. " +
                "Tried: ESI cache, Pyfa Chinese (eve-skills-pyfa-zh-CN.xml.gzip), TQ SDE (eve-skills-en-US.xml.gzip). " +
                "Please ensure at least one data file exists in the Resources folder.");
        }

        /// <summary>
        /// Attempts to load skills from a gzip XML file by filename.
        /// Searches in Resources/ folder and AppData/EVEMon/ folder.
        /// </summary>
        /// <param name="filename">The filename to look for (e.g., "eve-skills-pyfa-zh-CN.xml.gzip").</param>
        /// <param name="sourceName">Human-readable name for logging.</param>
        /// <returns>True if skills were loaded successfully.</returns>
        private static bool TryLoadSkillsFromFile(string filename, string sourceName)
        {
            string[] searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", filename),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVEMon", filename),
            };

            foreach (string path in searchPaths)
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    EveMonClient.Trace($"SerenDataLoader: [Level2/3] Loading skills from {sourceName}: {path}");

                    using var stream = File.OpenRead(path);
                    using var gzipStream = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);

                    var settings = new System.Xml.XmlReaderSettings
                    {
                        IgnoreWhitespace = true,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true
                    };

                    using var reader = System.Xml.XmlReader.Create(gzipStream, settings);
                    var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Serialization.Datafiles.SkillsDatafile));

                    if (serializer.Deserialize(reader) is Serialization.Datafiles.SkillsDatafile datafile)
                    {
                        bool hasSkills = datafile.SkillGroups?.Count > 0 &&
                                         datafile.SkillGroups.Any(g => g.Skills?.Count > 0);

                        if (!hasSkills)
                        {
                            EveMonClient.Trace($"SerenDataLoader: {sourceName} loaded but has no skills — skipping.");
                            continue;
                        }

                        EveMonClient.Trace($"SerenDataLoader: {sourceName} OK — {datafile.SkillGroups.Count} groups, " +
                                           $"{datafile.SkillGroups.Sum(g => g.Skills?.Count ?? 0)} skills");
                        Data.StaticSkills.LoadFromDatafile(datafile);
                        return true;
                    }

                    EveMonClient.Trace($"SerenDataLoader: {sourceName} deserialization returned null.");
                }
                catch (Exception ex)
                {
                    EveMonClient.Trace($"SerenDataLoader: {sourceName} failed at {path}: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Queues background ESI data download (fire-and-forget).
        /// </summary>
        private static void QueueBackgroundESIDownload()
        {
            EveMonClient.Trace("SerenDataLoader: Queuing background ESI download");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ESISdeDownloader.DownloadAllSdeDataAsync();
                    EveMonClient.Trace("SerenDataLoader: Background ESI download completed");
                }
                catch (Exception ex)
                {
                    EveMonClient.Trace($"SerenDataLoader: Background ESI download failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Downloads ESI data for a specific endpoint.
        /// </summary>
        /// <param name="endpointName">The endpoint name.</param>
        /// <param name="forceRefresh">If true, bypass cache.</param>
        /// <returns>True if download succeeded.</returns>
        public static async Task<bool> DownloadEndpointAsync(string endpointName, bool forceRefresh = false)
        {
            var endpoint = ESISdeDownloader.SDEEndpoints.Find(e => e.Name == endpointName);
            if (endpoint == null)
            {
                EveMonClient.Trace($"SerenDataLoader: Unknown endpoint {endpointName}");
                return false;
            }

            var result = await ESISdeDownloader.DownloadEndpointWithPaginationAsync(endpoint, forceRefresh);
            return result.Success;
        }

        /// <summary>
        /// Gets the cache status for Seren mode.
        /// </summary>
        /// <returns>A string describing the cache status.</returns>
        public static string GetCacheStatus()
        {
            return ESISdeDownloader.GetCacheStatus();
        }

        /// <summary>
        /// Clears the ESI cache.
        /// </summary>
        public static async Task ClearCacheAsync()
        {
            await ESISdeCacheManager.ClearAllCacheAsync();
            EveMonClient.Trace("SerenDataLoader: Cache cleared");
        }

        /// <summary>
        /// Gets the cached ESI data as a string.
        /// </summary>
        /// <param name="endpointName">The endpoint name.</param>
        /// <returns>The cached data or null if not found.</returns>
        public static async Task<string> GetCachedEndpointDataAsync(string endpointName)
        {
            return await ESISdeCacheManager.ReadCacheAsync(endpointName);
        }

        #region Private Methods

        /// <summary>
        /// Converts ESI data to datafile format and saves to cache.
        /// </summary>
        private static async Task ConvertAndSaveDatafilesAsync()
        {
            ReportProgress(60, "Converting categories...");
            string categoriesJson = await ReadCacheWithFallbackAsync("universe_categories");
            if (string.IsNullOrEmpty(categoriesJson))
            {
                categoriesJson = "[]";
            }

            ReportProgress(65, "Converting groups...");
            string groupsJson = await ReadCacheWithFallbackAsync("universe_groups");
            if (string.IsNullOrEmpty(groupsJson))
            {
                groupsJson = "[]";
            }

            ReportProgress(70, "Converting types...");
            string typesJson = await ReadCacheWithFallbackAsync("universe_types");
            if (string.IsNullOrEmpty(typesJson))
            {
                typesJson = "[]";
            }

            ReportProgress(75, "Converting market groups...");
            string marketGroupsJson = await ReadCacheWithFallbackAsync("market_groups");
            if (string.IsNullOrEmpty(marketGroupsJson))
            {
                marketGroupsJson = "[]";
            }

            // Convert to Items datafile
            ReportProgress(78, "Creating items datafile...");
            var itemsDatafile = ESIToDatafileConverter.ConvertItems(
                categoriesJson, groupsJson, typesJson, marketGroupsJson);
            ESIToDatafileConverter.SaveDatafileToCache(itemsDatafile, "eve-items-esi.xml.gzip");

            // Convert to Skills datafile
            ReportProgress(82, "Creating skills datafile...");
            string dogmaJson = await ReadCacheWithFallbackAsync("dogma_attributes");
            if (string.IsNullOrEmpty(dogmaJson))
            {
                dogmaJson = "[]";
            }
            var skillsDatafile = ESIToDatafileConverter.ConvertSkills(groupsJson, typesJson, dogmaJson);
            ESIToDatafileConverter.SaveDatafileToCache(skillsDatafile, "eve-skills-esi.xml.gzip");

            EveMonClient.Trace("SerenDataLoader: Converted and saved datafiles");
        }

        /// <summary>
        /// Reads cache with automatic TQ fallback.
        /// Tries Seren cache first, then falls back to TQ cache (from local resources or TQ ESI).
        /// </summary>
        private static async Task<string> ReadCacheWithFallbackAsync(string endpoint)
        {
            // Try Seren first
            var serenData = await ESISdeCacheManager.ReadCacheAsync(endpoint, ESISdeCacheManager.SdeServer.Seren);
            if (!string.IsNullOrEmpty(serenData))
            {
                EveMonClient.Trace($"Using Seren cache for endpoint: {endpoint}");
                return serenData;
            }
            // Fall back to TQ cache
            var tqResult = await ESISdeCacheManager.ReadCacheWithFallbackAsync(endpoint);
            if (tqResult.HasValue)
            {
                EveMonClient.Trace($"Using TQ fallback cache for endpoint: {endpoint} (server: {tqResult.Value.server})");
                return tqResult.Value.data;
            }
            return null;
        }

        /// <summary>
        /// Loads the static data into memory from the converted datafiles.
        /// </summary>
        /// <returns>True if at least items and skills were loaded successfully.</returns>
        private static async Task<bool> LoadStaticDataAsync()
        {
            bool itemsLoaded = false;
            bool skillsLoaded = false;

            // Load items
            ReportProgress(85, "Loading items...");
            try
            {
                var itemsDatafile = ESIToDatafileConverter.LoadDatafileFromCache<Serialization.Datafiles.ItemsDatafile>(
                    "eve-items-esi.xml.gzip");
                if (itemsDatafile != null)
                {
                    // Properties must be loaded before items (implant slots and every other item
                    // property depend on this table being populated first).
                    await Task.Run(() => Data.StaticProperties.Load());
                    await Task.Run(() => Data.StaticItems.LoadFromDatafile(itemsDatafile));
                    // Notify MainWindow to update progress bar
                    GlobalDatafileCollection.OnDatafileLoaded();
                    itemsLoaded = true;
                }
                else
                {
                    EveMonClient.Trace("SerenDataLoader: Items datafile not found in cache");
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"SerenDataLoader: Failed to load items: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }

            // Load skills — 3-level fallback:
            //   Level 1: ESI cache (Seren Chinese, eve-skills-esi.xml.gzip)
            //   Level 2: Pyfa Chinese static data (eve-skills-pyfa-zh-CN.xml.gzip)
            //   Level 3: TQ SDE English static data (eve-skills-en-US.xml.gzip)
            //   Level 4: Error
            ReportProgress(90, "Loading skills...");
            try
            {
                // Level 1: try ESI cache
                EveMonClient.Trace("SerenDataLoader: [Level1] Loading skills from Seren ESI cache...");
                var skillsDatafile = ESIToDatafileConverter.LoadDatafileFromCache<Serialization.Datafiles.SkillsDatafile>(
                    "eve-skills-esi.xml.gzip");

                bool hasValidSkills = skillsDatafile != null &&
                    skillsDatafile.SkillGroups?.Count > 0 &&
                    skillsDatafile.SkillGroups.Any(g => g.Skills?.Count > 0);

                if (hasValidSkills)
                {
                    EveMonClient.Trace($"SerenDataLoader: [Level1] ESI skills OK — {skillsDatafile.SkillGroups.Count} groups, " +
                                       $"{skillsDatafile.SkillGroups.Sum(g => g.Skills?.Count ?? 0)} skills");
                    await Task.Run(() => Data.StaticSkills.LoadFromDatafile(skillsDatafile));
                    GlobalDatafileCollection.OnDatafileLoaded();
                    skillsLoaded = true;
                }
                else
                {
                    EveMonClient.Trace("SerenDataLoader: [Level1] ESI skills cache is empty/invalid, trying Level 2/3...");
                    // Level 2 & 3 handled inside LoadSkillsWithFallback()
                    await Task.Run(() => LoadSkillsWithFallback());
                    GlobalDatafileCollection.OnDatafileLoaded();
                    skillsLoaded = true;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Level 4: all sources exhausted — this is a real error
                EveMonClient.Trace($"SerenDataLoader: [Level4 ERROR] {ex.Message}");
                ExceptionHandler.LogException(ex, true);
                skillsLoaded = false;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"SerenDataLoader: Unexpected error loading skills: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
                // Still try Level 2/3
                try
                {
                    await Task.Run(() => LoadSkillsWithFallback());
                    GlobalDatafileCollection.OnDatafileLoaded();
                    skillsLoaded = true;
                }
                catch (InvalidOperationException innerEx)
                {
                    EveMonClient.Trace($"SerenDataLoader: [Level4 ERROR] {innerEx.Message}");
                    skillsLoaded = false;
                }
                catch { /* Suppress to allow startup to continue */ }
            }

            // If core data (items or skills) failed to load, return false
            if (!itemsLoaded && !skillsLoaded)
            {
                EveMonClient.Trace("SerenDataLoader: Critical data files missing, load failed");
                return false;
            }

            // Load other datafiles that are not ESI-based
            ReportProgress(92, "Loading certificates...");
            try { await Task.Run(() => Data.StaticCertificates.Load()); } catch { /* Non-critical */ }
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(94, "Loading geography...");
            try { await Task.Run(() => Data.StaticGeography.Load()); } catch { /* Non-critical */ }
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(96, "Loading blueprints...");
            try { await Task.Run(() => Data.StaticBlueprints.Load()); } catch { /* Non-critical */ }
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(98, "Loading reprocessing...");
            try { await Task.Run(() => Data.StaticReprocessing.Load()); } catch { /* Non-critical */ }
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(99, "Loading masteries...");
            try { await Task.Run(() => Data.StaticMasteries.Load()); } catch { /* Non-critical */ }
            GlobalDatafileCollection.OnDatafileLoaded();

            EveMonClient.Trace("SerenDataLoader: Loaded static data from converted files");
            return itemsLoaded || skillsLoaded;
        }

        /// <summary>
        /// Reports progress to subscribers.
        /// </summary>
        private static void ReportProgress(int percent, string status)
        {
            LoadProgress?.Invoke(null, new SerenLoadProgressEventArgs(percent, status));
        }

        #endregion
    }

    /// <summary>
    /// Event args for Seren load progress.
    /// </summary>
    public class SerenLoadProgressEventArgs : EventArgs
    {
        public int Percent { get; }
        public string Status { get; }

        public SerenLoadProgressEventArgs(int percent, string status)
        {
            Percent = percent;
            Status = status;
        }
    }
}
