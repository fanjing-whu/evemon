using EVEMon.Common.Constants;
using EVEMon.Common.Enumerations;
using EVEMon.Common.Extensions;
using EVEMon.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Downloads Static Data Export (SDE) data from ESI for the Chinese Seren server.
    /// This is the core component of the C1 solution (Pure ESI mode + Local Cache).
    ///
    /// Dual-cache system: Seren (primary) + Tranquility (fallback).
    /// When Seren data is missing or slow, TQ data is used as fallback and cached for Seren.
    /// </summary>
    public static class ESISdeDownloader
    {
        private static readonly HttpClient s_httpClient;
        private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);
        private static readonly int s_maxRetries = 3;

        // Max concurrency for fetching individual item details to avoid rate-limiting
        private const int DetailFetchConcurrency = 20;

        /// <summary>
        /// Static constructor to initialize the HTTP client.
        /// </summary>
        static ESISdeDownloader()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            s_httpClient = new HttpClient(handler) { Timeout = s_defaultTimeout };
        }

        /// <summary>
        /// Represents the result of an ESI download operation.
        /// </summary>
        public sealed class DownloadResult
        {
            public bool Success { get; set; }
            public string Data { get; set; }
            public string ETag { get; set; }
            public DateTime Expires { get; set; }
            public int TotalPages { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Represents an ESI SDE endpoint configuration.
        /// </summary>
        public sealed class SdeEndpoint
        {
            public string Name { get; set; }
            public string Path { get; set; }
            /// <summary>True if the list endpoint returns multiple pages of IDs.</summary>
            public bool IsPaged { get; set; }
            public int Priority { get; set; }
            public string Description { get; set; }
            /// <summary>
            /// If set, the IDs from the list endpoint will be fetched individually at this detail path.
            /// Use {0} as placeholder for the ID. E.g. "/v1/universe/categories/{0}/"
            /// </summary>
            public string DetailPath { get; set; }
        }

        /// <summary>
        /// Defines the standard ESI SDE endpoints used for Seren support.
        /// Endpoints with DetailPath will perform two-phase download: list IDs then fetch details.
        /// </summary>
        public static readonly List<SdeEndpoint> SDEEndpoints = new List<SdeEndpoint>
        {
            // P0 - Critical endpoints (two-phase: list IDs then fetch each detail)
            new SdeEndpoint { Name = "universe_categories", Path = "/v1/universe/categories/", IsPaged = false, Priority = 0, Description = "Item categories", DetailPath = "/v1/universe/categories/{0}/" },
            new SdeEndpoint { Name = "universe_groups",     Path = "/v1/universe/groups/",     IsPaged = true,  Priority = 0, Description = "Item/skill groups",  DetailPath = "/v1/universe/groups/{0}/" },
            // NOTE: universe_types (54k+ items) is NOT fetched in two-phase here.
            // ConvertSkills/ConvertItems will fetch only the types they actually need,
            // by calling FetchTypeDetailsAsync() with the specific type IDs referenced by groups.
            new SdeEndpoint { Name = "universe_types",      Path = "/v1/universe/types/",      IsPaged = true,  Priority = 0, Description = "All item/skill type IDs (details fetched on-demand)" },

            // P1 - Important endpoints
            new SdeEndpoint { Name = "market_groups",       Path = "/v1/markets/groups/",           IsPaged = false, Priority = 1, Description = "Market groups",         DetailPath = "/v1/markets/groups/{0}/" },
            new SdeEndpoint { Name = "universe_regions",    Path = "/v1/universe/regions/",         IsPaged = false, Priority = 1, Description = "Regions for geography",   DetailPath = "/v1/universe/regions/{0}/" },
            new SdeEndpoint { Name = "universe_constellations", Path = "/v1/universe/constellations/", IsPaged = true, Priority = 1, Description = "Constellations for geography", DetailPath = "/v1/universe/constellations/{0}/" },
            new SdeEndpoint { Name = "universe_systems",    Path = "/v1/universe/systems/",         IsPaged = true,  Priority = 1, Description = "Solar systems for geography", DetailPath = "/v1/universe/solarsystems/{0}/" },
            new SdeEndpoint { Name = "industry_systems",    Path = "/v1/industry/systems/",         IsPaged = false, Priority = 1, Description = "Industry system costs" },

            // P2 - Nice to have (two-phase for dogma)
            new SdeEndpoint { Name = "dogma_attributes",    Path = "/v1/dogma/attributes/",         IsPaged = true,  Priority = 2, Description = "Item attributes", DetailPath = "/v1/dogma/attributes/{0}/" },
            new SdeEndpoint { Name = "dogma_effects",       Path = "/v1/dogma/effects/",            IsPaged = true,  Priority = 2, Description = "Item effects",    DetailPath = "/v1/dogma/effects/{0}/" },
            new SdeEndpoint { Name = "character_races",     Path = "/v1/universe/races/",           IsPaged = false, Priority = 2, Description = "Character races" },
            new SdeEndpoint { Name = "character_factions",  Path = "/v2/universe/factions/",        IsPaged = false, Priority = 2, Description = "NPC factions" },
        };

        /// <summary>
        /// Downloads data from an ESI endpoint with caching support.
        /// If the endpoint has a DetailPath, performs two-phase download.
        /// </summary>
        public static async Task<DownloadResult> DownloadEndpointAsync(SdeEndpoint endpoint, bool forceRefresh = false)
        {
            // Check cache first unless force refresh is requested
            if (!forceRefresh && ESISdeCacheManager.IsCacheValid(endpoint.Name))
            {
                EveMonClient.Trace($"Using cached data for endpoint: {endpoint.Name}");
                string cachedData = await ESISdeCacheManager.ReadCacheAsync(endpoint.Name);
                if (!string.IsNullOrEmpty(cachedData))
                {
                    return new DownloadResult
                    {
                        Success = true,
                        Data = cachedData,
                        ETag = ESISdeCacheManager.GetETag(endpoint.Name)
                    };
                }
            }

            // Download from ESI
            return await DownloadEndpointWithPaginationAsync(endpoint, forceRefresh);
        }

        /// <summary>
        /// Downloads data from ESI with pagination support.
        /// If the endpoint has a DetailPath, fetches IDs first then fetches details.
        /// </summary>
        public static async Task<DownloadResult> DownloadEndpointWithPaginationAsync(SdeEndpoint endpoint, bool forceRefresh = false)
        {
            // Check cache first
            if (!forceRefresh && ESISdeCacheManager.IsCacheValid(endpoint.Name))
            {
                EveMonClient.Trace($"Using cached data for endpoint: {endpoint.Name}");
                string cachedData = await ESISdeCacheManager.ReadCacheAsync(endpoint.Name);
                if (!string.IsNullOrEmpty(cachedData))
                {
                    return new DownloadResult { Success = true, Data = cachedData };
                }
            }

            var baseUrl = EveServerConfig.ESIBase;
            var listUrl = new Uri(baseUrl, endpoint.Path.TrimStart('/'));

            // Step 1: Download the ID list (possibly paged)
            List<int> ids;
            try
            {
                ids = await DownloadIdListAsync(endpoint, listUrl);
                if (ids == null)
                {
                    // Endpoint returns full objects, not ID list — fall back to old path
                    return endpoint.IsPaged
                        ? await DownloadPagedDataAsync(endpoint, listUrl)
                        : await DownloadSinglePageAsync(endpoint, listUrl);
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to download ID list for {endpoint.Name}: {ex.Message}");
                return new DownloadResult { Success = false, ErrorMessage = ex.Message };
            }

            // If no DetailPath, save the ID list directly and return
            if (string.IsNullOrEmpty(endpoint.DetailPath))
            {
                string idJson = JsonSerializer.Serialize(ids);
                await ESISdeCacheManager.WriteCacheAsync(endpoint.Name, idJson, "", DateTime.UtcNow.AddDays(1));
                return new DownloadResult { Success = true, Data = idJson };
            }

            // Step 2: Fetch details for each ID concurrently
            EveMonClient.Trace($"{endpoint.Name}: fetching details for {ids.Count} items...");
            var detailObjects = await FetchDetailsAsync(endpoint, baseUrl, ids);

            string json = JsonSerializer.Serialize(detailObjects);
            await ESISdeCacheManager.WriteCacheAsync(endpoint.Name, json, "", DateTime.UtcNow.AddDays(1));

            EveMonClient.Trace($"{endpoint.Name}: downloaded and cached {detailObjects.Count} detail objects");
            return new DownloadResult
            {
                Success = true,
                Data = json,
                Expires = DateTime.UtcNow.AddDays(1)
            };
        }

        /// <summary>
        /// Downloads the list of IDs from a paged or non-paged ESI endpoint.
        /// Returns null if the first page does not look like an int array.
        /// </summary>
        private static async Task<List<int>> DownloadIdListAsync(SdeEndpoint endpoint, Uri listUrl)
        {
            var baseUrl = EveServerConfig.ESIBase;
            var allIds = new List<int>();

            int currentPage = 1;
            int totalPages = 1;

            while (currentPage <= totalPages)
            {
                Uri pageUrl = currentPage == 1 && !endpoint.IsPaged
                    ? listUrl
                    : new Uri(baseUrl, $"{endpoint.Path.TrimStart('/')}?datasource=serenity&page={currentPage}");

                // Non-paged: just use the list URL
                if (!endpoint.IsPaged)
                    pageUrl = new Uri(listUrl + "?datasource=serenity");

                using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                request.Headers.Add("Accept", "application/json");

                using var response = await s_httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    EveMonClient.Trace($"{endpoint.Name}: list page {currentPage} returned {response.StatusCode}");
                    break;
                }

                string content = await response.Content.ReadAsStringAsync();

                // Try to parse as int array
                List<int> pageIds;
                try
                {
                    pageIds = JsonSerializer.Deserialize<List<int>>(content);
                }
                catch
                {
                    // Not an int array — endpoint returns full objects directly
                    return null;
                }

                if (pageIds == null || pageIds.Count == 0)
                    break;

                allIds.AddRange(pageIds);

                // Check X-Pages header
                if (endpoint.IsPaged && response.Headers.TryGetValues("X-Pages", out var pagesHeader))
                {
                    if (int.TryParse(pagesHeader.FirstOrDefault(), out int pages))
                        totalPages = pages;
                }

                if (!endpoint.IsPaged)
                    break;

                currentPage++;
            }

            return allIds;
        }

        /// <summary>
        /// Fetches detail objects for a list of IDs with concurrency limiting.
        /// </summary>
        private static async Task<List<JsonElement>> FetchDetailsAsync(SdeEndpoint endpoint, Uri baseUrl, List<int> ids)
        {
            var results = new JsonElement[ids.Count];
            var sem = new SemaphoreSlim(DetailFetchConcurrency);
            var tasks = ids.Select(async (id, index) =>
            {
                await sem.WaitAsync();
                try
                {
                    string detailPath = string.Format(endpoint.DetailPath, id);
                    var url = new Uri(baseUrl, detailPath.TrimStart('/') + "?datasource=serenity");

                    for (int retry = 0; retry < s_maxRetries; retry++)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            req.Headers.Add("Accept", "application/json");
                            using var resp = await s_httpClient.SendAsync(req);

                            if (!resp.IsSuccessStatusCode)
                            {
                                if (retry < s_maxRetries - 1)
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                                    continue;
                                }
                                break;
                            }

                            string body = await resp.Content.ReadAsStringAsync();
                            results[index] = JsonSerializer.Deserialize<JsonElement>(body);
                            break;
                        }
                        catch (Exception ex) when (retry < s_maxRetries - 1)
                        {
                            EveMonClient.Trace($"{endpoint.Name} detail {id} retry {retry + 1}: {ex.Message}");
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    EveMonClient.Trace($"{endpoint.Name} detail {id} failed: {ex.Message}");
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Filter out default/null elements
            var finalList = new List<JsonElement>();
            foreach (var el in results)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                    finalList.Add(el);
            }
            return finalList;
        }

        /// <summary>
        /// Controls fallback behavior when Seren ESI data is unavailable.
        /// </summary>
        public enum FallbackMode
        {
            /// <summary>Only use Seren ESI, no fallback.</summary>
            SerenOnly,
            /// <summary>Try Seren first, fall back to TQ if Seren fails or is slow.</summary>
            FallbackToTQ
        }

        /// <summary>
        /// Fetches type details from ESI for a batch of type IDs.
        /// Uses on-demand loading for the specific types needed by converters
        /// (e.g., skill types referenced by groups), rather than downloading all 54k+ types upfront.
        /// Results are cached in memory for the lifetime of the process.
        ///
        /// Supports dual-cache fallback: when Seren data is missing/failed, TQ data is used as fallback
        /// and cached for Seren so future lookups are faster.
        /// </summary>
        /// <param name="typeIds">The type IDs to fetch.</param>
        /// <param name="progress">Optional progress reporter (current, total, status).</param>
        /// <param name="forceRefresh">If true, bypass in-memory cache but keep TQ fallback.</param>
        /// <param name="fallbackMode">Whether to fall back to TQ when Seren data is missing.</param>
        /// <returns>A dictionary mapping type_id to EsiType objects.</returns>
        public static async Task<Dictionary<int, EsiType>> FetchTypeDetailsAsync(
            IEnumerable<int> typeIds,
            IProgress<(int current, int total, string status)> progress = null,
            bool forceRefresh = false,
            FallbackMode fallbackMode = FallbackMode.SerenOnly)
        {
            var uniqueIds = typeIds.Distinct().ToList();
            if (uniqueIds.Count == 0)
                return new Dictionary<int, EsiType>();

            // Step 1: Check in-memory cache (never bypassed — always serves cached data)
            var uncachedIds = new List<int>();
            if (s_typeDetailCache != null)
            {
                foreach (int id in uniqueIds)
                {
                    if (!s_typeDetailCache.ContainsKey(id))
                        uncachedIds.Add(id);
                }
            }
            else
            {
                uncachedIds = uniqueIds;
            }

            if (uncachedIds.Count == 0 && !forceRefresh)
            {
                progress?.Report((uniqueIds.Count, uniqueIds.Count, $"Types loaded from memory cache ({uniqueIds.Count} items)"));
                return uniqueIds.ToDictionary(id => id, id => s_typeDetailCache[id]);
            }

            // Step 2: Try Seren ESI for uncached IDs
            var serenFetched = await FetchTypeDetailsFromServerAsync(
                uncachedIds, EveServerConfig.ESIBase, "serenity", s_httpClient, progress,
                forceRefresh ? 0 : -1); // skipCacheIdx=-1 means don't check in-mem (already filtered)
            var serenMissing = new List<int>();
            for (int i = 0; i < uncachedIds.Count; i++)
            {
                if (serenFetched[i] == null && s_typeDetailCache != null && !s_typeDetailCache.ContainsKey(uncachedIds[i]))
                    serenMissing.Add(uncachedIds[i]);
            }

            // Step 3: Fall back to TQ LOCAL resource if needed (no network requests)
            if (fallbackMode == FallbackMode.FallbackToTQ && serenMissing.Count > 0)
            {
                EveMonClient.Trace($"Fallback: {serenMissing.Count} types not found in Seren, trying local TQ resource...");

                // Try local TQ resource (eve-items-en-US.xml.gzip) — zero network cost
                var tqLocal = await LoadTQTypeDetailsFromLocalResourceAsync();
                if (tqLocal.Count > 0)
                {
                    if (s_typeDetailCache == null)
                        s_typeDetailCache = new Dictionary<int, EsiType>();
                    int localResolved = 0;
                    foreach (int id in serenMissing.ToArray())
                    {
                        if (tqLocal.TryGetValue(id, out var localType))
                        {
                            s_typeDetailCache[id] = localType;
                            localResolved++;
                        }
                    }
                    EveMonClient.Trace($"Local TQ resource resolved {localResolved}/{serenMissing.Count} types");
                }
                else
                {
                    EveMonClient.Trace("No local TQ resource found, types remain unresolved");
                }
            }

            // Step 4: Return all requested IDs
            var result = new Dictionary<int, EsiType>();
            foreach (int id in typeIds)
            {
                if (s_typeDetailCache != null && s_typeDetailCache.TryGetValue(id, out var cachedType))
                    result[id] = cachedType;
            }
            return result;
        }

        /// <summary>
        /// Internal: fetches a batch of type details from a specific ESI server.
        /// </summary>
        private static async Task<EsiType[]> FetchTypeDetailsFromServerAsync(
            List<int> ids,
            Uri baseUrl,
            string datasource,
            HttpClient httpClient,
            IProgress<(int current, int total, string status)> progress,
            int skipCacheIdx = -1)
        {
            var fetched = new EsiType[ids.Count];
            var sem = new SemaphoreSlim(DetailFetchConcurrency);
            int completed = 0;
            var tasks = ids.Select(async (id, index) =>
            {
                await sem.WaitAsync();
                try
                {
                    // Skip if already in memory cache (when called as secondary pass)
                    if (skipCacheIdx >= 0 && s_typeDetailCache != null && s_typeDetailCache.ContainsKey(id))
                    {
                        return;
                    }

                    var url = new Uri(baseUrl, $"/v1/universe/types/{id}/?datasource={datasource}");
                    for (int retry = 0; retry < s_maxRetries; retry++)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            req.Headers.Add("Accept", "application/json");
                            using var resp = await httpClient.SendAsync(req);
                            if (!resp.IsSuccessStatusCode)
                            {
                                if (retry < s_maxRetries - 1)
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                                    continue;
                                }
                                break;
                            }
                            string body = await resp.Content.ReadAsStringAsync();
                            var type = JsonSerializer.Deserialize<EsiType>(body);
                            if (type != null)
                                fetched[index] = type;
                            break;
                        }
                        catch (Exception ex) when (retry < s_maxRetries - 1)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                        }
                    }
                }
                finally
                {
                    var c = Interlocked.Increment(ref completed);
                    if (progress != null && (c % 50 == 0 || c == ids.Count))
                    {
                        progress.Report((c, ids.Count, $"Fetching type details: {c}/{ids.Count}"));
                    }
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Update in-memory cache with successfully fetched types
            if (s_typeDetailCache == null)
                s_typeDetailCache = new Dictionary<int, EsiType>();
            foreach (var type in fetched)
            {
                if (type != null)
                    s_typeDetailCache[type.type_id] = type;
            }

            return fetched;
        }

        /// <summary>
        /// Writes a single type detail to the Seren ESI cache (fire-and-forget).
        /// </summary>
        private static async Task CacheTypeDetailAsync(int typeId, EsiType type)
        {
            try
            {
                string json = JsonSerializer.Serialize(type);
                await ESISdeCacheManager.WriteCacheAsync(
                    $"type_detail_{typeId}",
                    ESISdeCacheManager.SdeServer.Seren,
                    json, "", DateTime.UtcNow.AddDays(7));
            }
            catch { /* non-critical */ }
        }

        /// <summary>
        /// Represents a type object from ESI /v1/universe/types/{type_id}/.
        /// </summary>
        public class EsiType
        {
            public int type_id { get; set; }
            public string name { get; set; }
            public string description { get; set; }
            public bool published { get; set; }
            public int? group_id { get; set; }
            public int? market_group_id { get; set; }
            public int? icon_id { get; set; }
            public int? portion_size { get; set; }
        }

        /// <summary>
        /// In-memory cache for type details fetched on-demand.
        /// </summary>
        private static Dictionary<int, EsiType> s_typeDetailCache;

        /// <summary>
        /// In-memory cache for TQ type details loaded from local resources.
        /// </summary>
        private static Dictionary<int, EsiType> s_tqLocalResourceCache;

        /// <summary>
        /// Loads type details from the local TQ datafile (eve-items-en-US.xml.gzip).
        /// This is used as a fallback when Seren ESI data is unavailable,
        /// avoiding any network requests to TQ ESI.
        /// Thread-safe, loaded only once on first use.
        /// </summary>
        /// <returns>A dictionary mapping type_id to EsiType objects.</returns>
        private static async Task<Dictionary<int, EsiType>> LoadTQTypeDetailsFromLocalResourceAsync()
        {
            if (s_tqLocalResourceCache != null)
                return s_tqLocalResourceCache;

            var localCache = new Dictionary<int, EsiType>();
            string[] possiblePaths = new[]
            {
                // Installed resources directory (normal install)
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "eve-items-en-US.xml.gzip"),
                // %APPDATA% (copied by Datafile.GetFullPath)
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "EVEMon", "eve-items-en-US.xml.gzip"),
            };

            foreach (string path in possiblePaths)
            {
                try
                {
                    if (!File.Exists(path))
                        continue;

                    EveMonClient.Trace($"Loading TQ type details from local resource: {path}");

                    using var stream = File.OpenRead(path);
                    using var gzipStream = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
                    var serializer = new XmlSerializer(typeof(Serialization.Datafiles.ItemsDatafile));

                    if (serializer.Deserialize(gzipStream) is Serialization.Datafiles.ItemsDatafile datafile)
                    {
                        foreach (var mg in datafile.MarketGroups)
                        {
                            CollectItemsFromMarketGroupRecursive(mg, localCache);
                        }
                    }

                    EveMonClient.Trace($"Loaded {localCache.Count} type details from local TQ resource");
                    s_tqLocalResourceCache = localCache;
                    return localCache;
                }
                catch (Exception ex)
                {
                    EveMonClient.Trace($"Failed to load TQ resource from {path}: {ex.Message}");
                }
            }

            EveMonClient.Trace("No local TQ resource found, TQ fallback will require network");
            s_tqLocalResourceCache = localCache;
            return localCache;
        }

        /// <summary>
        /// Recursively collects items from a market group and its children into the target dictionary.
        /// </summary>
        private static void CollectItemsFromMarketGroupRecursive(
            Serialization.Datafiles.SerializableMarketGroup mg,
            Dictionary<int, EsiType> target)
        {
            if (mg == null)
                return;
            foreach (var item in mg.Items)
            {
                if (!target.ContainsKey(item.ID))
                {
                    target[item.ID] = new EsiType
                    {
                        type_id = item.ID,
                        name = item.Name,
                        description = item.Description ?? "",
                        published = true,
                        group_id = null,
                        market_group_id = mg.ID,
                        icon_id = null,
                        portion_size = item.PortionSize > 0 ? item.PortionSize : 1
                    };
                }
            }
            foreach (var child in mg.SubGroups)
            {
                CollectItemsFromMarketGroupRecursive(child, target);
            }
        }

        /// <summary>
        /// Downloads a single page from an endpoint (legacy path for non-ID-list endpoints).
        /// </summary>
        private static async Task<DownloadResult> DownloadSinglePageAsync(SdeEndpoint endpoint, Uri url)
        {
            for (int retry = 0; retry < s_maxRetries; retry++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Accept", "application/json");
                    request.Headers.Add("Accept-Encoding", "gzip, deflate");

                    // Add ETag if available from cache
                    string cachedETag = ESISdeCacheManager.GetETag(endpoint.Name);
                    if (!string.IsNullOrEmpty(cachedETag))
                    {
                        request.Headers.Add("If-None-Match", cachedETag);
                    }

                    using var response = await s_httpClient.SendAsync(request);
                    return await ProcessResponseAsync(response, endpoint);
                }
                catch (HttpRequestException ex)
                {
                    EveMonClient.Trace($"HTTP error for {endpoint.Name} (retry {retry + 1}): {ex.Message}");
                    if (retry == s_maxRetries - 1)
                    {
                        return new DownloadResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to download {endpoint.Name}: {ex.Message}"
                        };
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                }
                catch (TaskCanceledException)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = $"Timeout downloading {endpoint.Name}"
                    };
                }
            }

            return new DownloadResult { Success = false, ErrorMessage = "Max retries exceeded" };
        }

        /// <summary>
        /// Downloads paginated data from an endpoint (legacy path — for endpoints returning full objects per page).
        /// </summary>
        private static async Task<DownloadResult> DownloadPagedDataAsync(SdeEndpoint endpoint, Uri baseUrl)
        {
            var allData = new List<object>();
            int currentPage = 1;
            int totalPages = 1;
            string lastETag = null;
            DateTime expires = DateTime.UtcNow.AddDays(7);

            for (int retry = 0; retry < s_maxRetries; retry++)
            {
                try
                {
                    while (currentPage <= totalPages)
                    {
                        var pageUrl = new Uri(baseUrl, $"?page={currentPage}");
                        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                        request.Headers.Add("Accept", "application/json");
                        request.Headers.Add("Accept-Encoding", "gzip, deflate");

                        using var response = await s_httpClient.SendAsync(request);

                        // Check for 304 Not Modified
                        if (response.StatusCode == HttpStatusCode.NotModified)
                        {
                            EveMonClient.Trace($"Cache hit for {endpoint.Name} page {currentPage}");
                            string cachedData = await ESISdeCacheManager.ReadCacheAsync($"{endpoint.Name}_page{currentPage}");
                            if (!string.IsNullOrEmpty(cachedData))
                            {
                                var cachedPageData = JsonSerializer.Deserialize<List<object>>(cachedData);
                                if (cachedPageData != null)
                                {
                                    allData.AddRange(cachedPageData);
                                }
                            }
                            currentPage++;
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            return new DownloadResult
                            {
                                Success = false,
                                ErrorMessage = $"Failed to download {endpoint.Name} page {currentPage}: {response.StatusCode}"
                            };
                        }

                        // Get ETag header from response headers
                        if (response.Headers.ETag != null)
                        {
                            lastETag = response.Headers.ETag.Tag;
                        }
                        // Get Expires header from content headers (Cache-Control: max-age)
                        if (response.Content.Headers.Expires != null)
                        {
                            expires = response.Content.Headers.Expires.Value.UtcDateTime;
                        }

                        // Get total pages from header
                        if (response.Headers.TryGetValues("X-Pages", out var pagesHeader))
                        {
                            if (int.TryParse(pagesHeader.FirstOrDefault(), out int pages))
                            {
                                totalPages = pages;
                            }
                        }

                        // Read content
                        string content = await response.Content.ReadAsStringAsync();
                        var pageData = JsonSerializer.Deserialize<List<object>>(content);
                        if (pageData != null)
                        {
                            allData.AddRange(pageData);
                        }

                        // Cache this page
                        await ESISdeCacheManager.WriteCacheAsync($"{endpoint.Name}_page{currentPage}", content, lastETag ?? "", expires);

                        currentPage++;
                    }

                    // Combine all pages into a single JSON array
                    string combinedJson = JsonSerializer.Serialize(allData);
                    await ESISdeCacheManager.WriteCacheAsync(endpoint.Name, combinedJson, lastETag ?? "", expires);

                    return new DownloadResult
                    {
                        Success = true,
                        Data = combinedJson,
                        ETag = lastETag,
                        Expires = expires,
                        TotalPages = totalPages
                    };
                }
                catch (HttpRequestException ex)
                {
                    EveMonClient.Trace($"HTTP error for paginated {endpoint.Name} (retry {retry + 1}): {ex.Message}");
                    if (retry == s_maxRetries - 1)
                    {
                        return new DownloadResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to download paginated {endpoint.Name}: {ex.Message}"
                        };
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                }
                catch (TaskCanceledException)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = $"Timeout downloading paginated {endpoint.Name}"
                    };
                }
            }

            return new DownloadResult { Success = false, ErrorMessage = "Max retries exceeded" };
        }

        /// <summary>
        /// Downloads data from ESI without pagination.
        /// </summary>
        private static async Task<DownloadResult> DownloadFromEsiAsync(SdeEndpoint endpoint)
        {
            var baseUrl = EveServerConfig.ESIBase;
            var url = new Uri(baseUrl, endpoint.Path.TrimStart('/'));

            return await DownloadSinglePageAsync(endpoint, url);
        }

        /// <summary>
        /// Processes the HTTP response.
        /// </summary>
        private static async Task<DownloadResult> ProcessResponseAsync(HttpResponseMessage response, SdeEndpoint endpoint)
        {
            // Check for 304 Not Modified
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                EveMonClient.Trace($"Cache hit for {endpoint.Name}");
                string cachedData = await ESISdeCacheManager.ReadCacheAsync(endpoint.Name);
                return new DownloadResult
                {
                    Success = true,
                    Data = cachedData,
                    ETag = ESISdeCacheManager.GetETag(endpoint.Name)
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult
                {
                    Success = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            // Get ETag header from response headers
            string etag = null;
            DateTime expires = DateTime.UtcNow.AddDays(7);

            if (response.Headers.ETag != null)
            {
                etag = response.Headers.ETag.Tag;
            }
            // Get Expires header from content headers
            if (response.Content.Headers.Expires != null)
            {
                expires = response.Content.Headers.Expires.Value.UtcDateTime;
            }

            // Read content
            string content = await response.Content.ReadAsStringAsync();

            // Cache the data
            await ESISdeCacheManager.WriteCacheAsync(endpoint.Name, content, etag ?? "", expires);

            return new DownloadResult
            {
                Success = true,
                Data = content,
                ETag = etag,
                Expires = expires
            };
        }

        /// <summary>
        /// Downloads all SDE data with progress reporting.
        /// </summary>
        public static async Task<bool> DownloadAllSdeDataAsync(IProgress<(int current, int total, string status)> progress = null, bool forceRefresh = false)
        {
            int total = SDEEndpoints.Count;
            int current = 0;
            int successCount = 0;
            var errors = new List<string>();

            // Sort by priority
            SDEEndpoints.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var endpoint in SDEEndpoints)
            {
                current++;
                progress?.Report((current, total, $"Downloading {endpoint.Description ?? endpoint.Name}..."));

                var result = await DownloadEndpointWithPaginationAsync(endpoint, forceRefresh);
                if (result.Success)
                {
                    successCount++;
                    EveMonClient.Trace($"Successfully downloaded {endpoint.Name}");
                }
                else
                {
                    errors.Add($"{endpoint.Name}: {result.ErrorMessage}");
                    EveMonClient.Trace($"Failed to download {endpoint.Name}: {result.ErrorMessage}");
                }
            }

            progress?.Report((total, total, $"Completed: {successCount}/{total} endpoints"));

            if (errors.Count > 0)
            {
                EveMonClient.Trace($"ESI download completed with {errors.Count} errors: {string.Join(", ", errors)}");
            }

            return successCount == total;
        }

        /// <summary>
        /// Checks if ESI is accessible.
        /// </summary>
        public static async Task<bool> CheckEsiAvailabilityAsync()
        {
            try
            {
                var baseUrl = EveServerConfig.ESIBase;
                // Note: /v1/status/ is not available on Seren server (times out)
                // Use /v1/universe/categories/ as a lightweight check - it's fast and always available
                var checkUrl = new Uri(baseUrl, "/v1/universe/categories/");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await s_httpClient.GetAsync(checkUrl, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"ESI availability check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the current cache status.
        /// </summary>
        public static string GetCacheStatus()
        {
            var manifest = ESISdeCacheManager.LoadManifest();
            long totalSize = ESISdeCacheManager.GetTotalCacheSize();
            DateTime? lastUpdate = ESISdeCacheManager.GetLastUpdateTime();

            return $"ESI Cache Status:\n" +
                   $"- Cached endpoints: {manifest.Endpoints.Count}\n" +
                   $"- Total size: {FormatBytes(totalSize)}\n" +
                   $"- Last update: {(lastUpdate.HasValue ? lastUpdate.Value.ToString("yyyy-MM-dd HH:mm") : "Never")}";
        }

        /// <summary>
        /// Formats bytes into a human-readable string.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}
