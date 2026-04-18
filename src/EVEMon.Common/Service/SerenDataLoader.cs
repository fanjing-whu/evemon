using EVEMon.Common.Collections.Global;
using EVEMon.Common.Enumerations;
using EVEMon.Common.Models;
using System;
using System.IO;
using System.IO.Compression;
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
        /// Downloads from ESI if cache is missing or expired, otherwise loads from cache.
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

                // Check if ESI is accessible
                ReportProgress(5, "Checking ESI connectivity...");
                bool esiAvailable = await ESISdeDownloader.CheckEsiAvailabilityAsync();
                if (!esiAvailable)
                {
                    EveMonClient.Trace("SerenDataLoader: ESI is not accessible");
                    ReportProgress(0, "Error: Cannot connect to ESI");
                    return false;
                }

                // Download and cache all SDE data
                ReportProgress(10, "Downloading SDE data from ESI...");
                bool downloadSuccess = await ESISdeDownloader.DownloadAllSdeDataAsync(
                    progress => ReportProgress(10 + progress.current * 50 / progress.total, progress.status),
                    forceRefresh);

                if (!downloadSuccess)
                {
                    EveMonClient.Trace("SerenDataLoader: Failed to download some ESI data");
                    // Continue with partial data if available
                }

                // Convert ESI data to datafile format and save to cache
                ReportProgress(60, "Converting data to datafile format...");
                await ConvertAndSaveDatafilesAsync();

                // Convert and load data
                ReportProgress(80, "Loading static data...");
                await LoadStaticDataAsync();

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
        public static void ClearCache()
        {
            ESISdeCacheManager.ClearAll();
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
            string categoriesJson = await ESISdeCacheManager.ReadCacheAsync("universe_categories");
            if (string.IsNullOrEmpty(categoriesJson))
            {
                categoriesJson = "[]";
            }

            ReportProgress(65, "Converting groups...");
            string groupsJson = await ESISdeCacheManager.ReadCacheAsync("universe_groups");
            if (string.IsNullOrEmpty(groupsJson))
            {
                groupsJson = "[]";
            }

            ReportProgress(70, "Converting types...");
            string typesJson = await ESISdeCacheManager.ReadCacheAsync("universe_types");
            if (string.IsNullOrEmpty(typesJson))
            {
                typesJson = "[]";
            }

            ReportProgress(75, "Converting market groups...");
            string marketGroupsJson = await ESISdeCacheManager.ReadCacheAsync("market_groups");
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
            string dogmaJson = await ESISdeCacheManager.ReadCacheAsync("dogma_attributes");
            if (string.IsNullOrEmpty(dogmaJson))
            {
                dogmaJson = "[]";
            }
            var skillsDatafile = ESIToDatafileConverter.ConvertSkills(groupsJson, typesJson, dogmaJson);
            ESIToDatafileConverter.SaveDatafileToCache(skillsDatafile, "eve-skills-esi.xml.gzip");

            EveMonClient.Trace("SerenDataLoader: Converted and saved datafiles");
        }

        /// <summary>
        /// Loads the static data into memory from the converted datafiles.
        /// </summary>
        private static async Task LoadStaticDataAsync()
        {
            // Load items
            ReportProgress(85, "Loading items...");
            var itemsDatafile = ESIToDatafileConverter.LoadDatafileFromCache<Serialization.Datafiles.ItemsDatafile>(
                "eve-items-esi.xml.gzip");
            if (itemsDatafile != null)
            {
                await Task.Run(() => Data.StaticItems.LoadFromDatafile(itemsDatafile));
                // Notify MainWindow to update progress bar
                GlobalDatafileCollection.OnDatafileLoaded();
            }

            // Load skills
            ReportProgress(90, "Loading skills...");
            var skillsDatafile = ESIToDatafileConverter.LoadDatafileFromCache<Serialization.Datafiles.SkillsDatafile>(
                "eve-skills-esi.xml.gzip");
            if (skillsDatafile != null)
            {
                await Task.Run(() => Data.StaticSkills.LoadFromDatafile(skillsDatafile));
                // Notify MainWindow to update progress bar
                GlobalDatafileCollection.OnDatafileLoaded();
            }

            // Load other datafiles that are not ESI-based
            ReportProgress(92, "Loading certificates...");
            await Task.Run(() => Data.StaticCertificates.Load());
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(94, "Loading geography...");
            await Task.Run(() => Data.StaticGeography.Load());
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(96, "Loading blueprints...");
            await Task.Run(() => Data.StaticBlueprints.Load());
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(98, "Loading reprocessing...");
            await Task.Run(() => Data.StaticReprocessing.Load());
            GlobalDatafileCollection.OnDatafileLoaded();

            ReportProgress(99, "Loading masteries...");
            await Task.Run(() => Data.StaticMasteries.Load());
            GlobalDatafileCollection.OnDatafileLoaded();

            EveMonClient.Trace("SerenDataLoader: Loaded static data from converted files");
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
