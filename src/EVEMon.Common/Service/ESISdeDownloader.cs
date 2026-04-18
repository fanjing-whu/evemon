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
using System.Threading.Tasks;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Downloads Static Data Export (SDE) data from ESI for the Chinese Seren server.
    /// This is the core component of the C1 solution (Pure ESI mode + Local Cache).
    /// </summary>
    public static class ESISdeDownloader
    {
        private static readonly HttpClient s_httpClient;
        private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);
        private static readonly int s_maxRetries = 3;

        /// <summary>
        /// Static constructor to initialize the HTTP client.
        /// </summary>
        static ESISdeDownloader()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            s_httpClient = new HttpClient(handler)
            {
                Timeout = s_defaultTimeout
            };
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
            public bool IsPaged { get; set; }
            public int Priority { get; set; }
            public string Description { get; set; }
        }

        /// <summary>
        /// Defines the standard ESI SDE endpoints used for Seren support.
        /// </summary>
        public static readonly List<SdeEndpoint> SDEEndpoints = new List<SdeEndpoint>
        {
            // P0 - Critical endpoints (required for basic functionality)
            new SdeEndpoint { Name = "universe_types", Path = "/universe/types/", IsPaged = true, Priority = 0, Description = "All item/skill types" },
            new SdeEndpoint { Name = "universe_groups", Path = "/universe/groups/", IsPaged = true, Priority = 0, Description = "Item/skill groups" },
            new SdeEndpoint { Name = "universe_categories", Path = "/universe/categories/", IsPaged = false, Priority = 0, Description = "Item categories" },

            // P1 - Important endpoints (used for detailed information)
            new SdeEndpoint { Name = "universe_regions", Path = "/universe/regions/", IsPaged = false, Priority = 1, Description = "Regions for geography" },
            new SdeEndpoint { Name = "universe_constellations", Path = "/universe/constellations/", IsPaged = true, Priority = 1, Description = "Constellations for geography" },
            new SdeEndpoint { Name = "universe_systems", Path = "/universe/systems/", IsPaged = true, Priority = 1, Description = "Solar systems for geography" },
            new SdeEndpoint { Name = "universe_stations", Path = "/universe/stations/", IsPaged = false, Priority = 1, Description = "Stations" },
            new SdeEndpoint { Name = "universe_structures", Path = "/universe/structures/", IsPaged = true, Priority = 1, Description = "Citadel structures" },
            new SdeEndpoint { Name = "market_groups", Path = "/market/groups/", IsPaged = false, Priority = 1, Description = "Market groups" },
            new SdeEndpoint { Name = "industry_systems", Path = "/industry/systems/", IsPaged = false, Priority = 1, Description = "Industry system costs" },

            // P2 - Nice to have endpoints
            new SdeEndpoint { Name = "dogma_attributes", Path = "/dogma/attributes/", IsPaged = true, Priority = 2, Description = "Item attributes" },
            new SdeEndpoint { Name = "dogma_effects", Path = "/dogma/effects/", IsPaged = true, Priority = 2, Description = "Item effects" },
            new SdeEndpoint { Name = "character_races", Path = "/universe/races/", IsPaged = false, Priority = 2, Description = "Character races" },
            new SdeEndpoint { Name = "character_factions", Path = "/universe/factions/", IsPaged = false, Priority = 2, Description = "NPC factions" },
        };

        /// <summary>
        /// Downloads data from an ESI endpoint with caching support.
        /// </summary>
        /// <param name="endpoint">The endpoint configuration.</param>
        /// <param name="forceRefresh">If true, bypass cache and force a fresh download.</param>
        /// <returns>The download result.</returns>
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
            return await DownloadFromEsiAsync(endpoint);
        }

        /// <summary>
        /// Downloads data from ESI with pagination support.
        /// </summary>
        /// <param name="endpoint">The endpoint configuration.</param>
        /// <param name="forceRefresh">If true, bypass cache.</param>
        /// <returns>The download result with all pages combined.</returns>
        public static async Task<DownloadResult> DownloadEndpointWithPaginationAsync(SdeEndpoint endpoint, bool forceRefresh = false)
        {
            // Check cache first
            if (!forceRefresh && ESISdeCacheManager.IsCacheValid(endpoint.Name))
            {
                EveMonClient.Trace($"Using cached data for endpoint: {endpoint.Name}");
                string cachedData = await ESISdeCacheManager.ReadCacheAsync(endpoint.Name);
                if (!string.IsNullOrEmpty(cachedData))
                {
                    return new DownloadResult
                    {
                        Success = true,
                        Data = cachedData
                    };
                }
            }

            // Build the URL
            var baseUrl = EveServerConfig.ESIBase;
            var url = new Uri(baseUrl, endpoint.Path.TrimStart('/'));

            // For paged endpoints, we need to get all pages
            if (endpoint.IsPaged)
            {
                return await DownloadPagedDataAsync(endpoint, url);
            }
            else
            {
                return await DownloadSinglePageAsync(endpoint, url);
            }
        }

        /// <summary>
        /// Downloads a single page from an endpoint.
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
        /// Downloads paginated data from an endpoint.
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
                                currentPage++;
                                continue;
                            }
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
        /// <param name="progress">Progress reporter.</param>
        /// <param name="forceRefresh">If true, bypass all caches.</param>
        /// <returns>True if all downloads succeeded, false otherwise.</returns>
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
                progress?.Report((current, total, $"Downloading {endpoint.Name}..."));

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
        /// <returns>True if ESI is accessible, false otherwise.</returns>
        public static async Task<bool> CheckEsiAvailabilityAsync()
        {
            try
            {
                var baseUrl = EveServerConfig.ESIBase;
                var statusUrl = new Uri(baseUrl, "/v1/status/");
                using var response = await s_httpClient.GetAsync(statusUrl);
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
        /// <returns>A status string describing the cache.</returns>
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
