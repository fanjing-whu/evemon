using EVEMon.Common.Enumerations;
using EVEMon.Common.Extensions;
using EVEMon.Common.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Manages the local cache for ESI Static Data Export (SDE) data.
    /// Used for Chinese Seren server support when no local SDE files are available.
    /// </summary>
    public static class ESISdeCacheManager
    {
        private const string ESICacheSubDir = "esi";
        private const string CacheManifestFile = "cache_manifest.json";
        private static readonly TimeSpan DefaultCacheExpiry = TimeSpan.FromDays(7);

        /// <summary>
        /// Gets the ESI cache directory path.
        /// </summary>
        public static string ESICacheDirectory
        {
            get
            {
                string cacheDir = Path.Combine(EveMonClient.EVEMonCacheDir, ESICacheSubDir);
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
                return cacheDir;
            }
        }

        /// <summary>
        /// Gets the cache manifest file path.
        /// </summary>
        private static string ManifestPath => Path.Combine(ESICacheDirectory, CacheManifestFile);

        /// <summary>
        /// Represents cache metadata for an endpoint.
        /// </summary>
        public sealed class EndpointCacheInfo
        {
            public string Endpoint { get; set; }
            public string FileName { get; set; }
            public string ETag { get; set; }
            public DateTime Expires { get; set; }
            public DateTime LastUpdated { get; set; }
            public long Size { get; set; }
        }

        /// <summary>
        /// Represents the cache manifest containing metadata for all cached endpoints.
        /// </summary>
        public sealed class CacheManifest
        {
            public DateTime LastFullUpdate { get; set; }
            public Dictionary<string, EndpointCacheInfo> Endpoints { get; set; } = new Dictionary<string, EndpointCacheInfo>();
        }

        /// <summary>
        /// Loads the cache manifest from disk.
        /// </summary>
        /// <returns>The cache manifest, or a new empty manifest if none exists.</returns>
        public static CacheManifest LoadManifest()
        {
            try
            {
                if (File.Exists(ManifestPath))
                {
                    string json = File.ReadAllText(ManifestPath);
                    return JsonSerializer.Deserialize<CacheManifest>(json) ?? new CacheManifest();
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to load ESI cache manifest: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return new CacheManifest();
        }

        /// <summary>
        /// Saves the cache manifest to disk.
        /// </summary>
        /// <param name="manifest">The manifest to save.</param>
        public static async Task SaveManifestAsync(CacheManifest manifest)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(manifest, options);
                await File.WriteAllTextAsync(ManifestPath, json);
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to save ESI cache manifest: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
        }

        /// <summary>
        /// Gets the cache file path for a given endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        /// <returns>The full path to the cache file.</returns>
        public static string GetCacheFilePath(string endpoint)
        {
            string safeFileName = SanitizeFileName(endpoint);
            return Path.Combine(ESICacheDirectory, $"{safeFileName}.json");
        }

        /// <summary>
        /// Reads cached data for an endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        /// <returns>The cached JSON string, or null if not cached.</returns>
        public static async Task<string> ReadCacheAsync(string endpoint)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint);
                if (File.Exists(filePath))
                {
                    return await File.ReadAllTextAsync(filePath);
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to read ESI cache for {endpoint}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return null;
        }

        /// <summary>
        /// Writes data to the cache for an endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        /// <param name="data">The JSON data to cache.</param>
        /// <param name="etag">The ETag from the response.</param>
        /// <param name="expires">The expiry time from the response.</param>
        /// <returns>True if successful, false otherwise.</returns>
        public static async Task<bool> WriteCacheAsync(string endpoint, string data, string etag, DateTime expires)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint);
                await File.WriteAllTextAsync(filePath, data);

                // Update manifest
                var manifest = LoadManifest();
                manifest.Endpoints[endpoint] = new EndpointCacheInfo
                {
                    Endpoint = endpoint,
                    FileName = Path.GetFileName(filePath),
                    ETag = etag,
                    Expires = expires,
                    LastUpdated = DateTime.UtcNow,
                    Size = data.Length
                };
                await SaveManifestAsync(manifest);

                EveMonClient.Trace($"Cached ESI data for endpoint: {endpoint}");
                return true;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to write ESI cache for {endpoint}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if a cached endpoint is still valid.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        /// <param name="maxAge">Maximum age before cache is considered stale.</param>
        /// <returns>True if cache exists and is not expired, false otherwise.</returns>
        public static bool IsCacheValid(string endpoint, TimeSpan? maxAge = null)
        {
            try
            {
                var manifest = LoadManifest();
                if (manifest.Endpoints.TryGetValue(endpoint, out var info))
                {
                    TimeSpan age = maxAge ?? DefaultCacheExpiry;
                    return info.Expires > DateTime.UtcNow || 
                           (DateTime.UtcNow - info.LastUpdated) < age;
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to check ESI cache validity for {endpoint}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return false;
        }

        /// <summary>
        /// Gets the ETag for a cached endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        /// <returns>The ETag, or null if not cached.</returns>
        public static string GetETag(string endpoint)
        {
            try
            {
                var manifest = LoadManifest();
                if (manifest.Endpoints.TryGetValue(endpoint, out var info))
                {
                    return info.ETag;
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to get ETag for {endpoint}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return null;
        }

        /// <summary>
        /// Invalidates the cache for a specific endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint name.</param>
        public static async Task InvalidateCacheAsync(string endpoint)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                // Update manifest
                var manifest = LoadManifest();
                if (manifest.Endpoints.Remove(endpoint))
                {
                    await SaveManifestAsync(manifest);
                }

                EveMonClient.Trace($"Invalidated ESI cache for endpoint: {endpoint}");
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to invalidate ESI cache for {endpoint}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
        }

        /// <summary>
        /// Clears all ESI cache.
        /// </summary>
        public static async Task ClearAllCacheAsync()
        {
            try
            {
                // Delete all cache files
                if (Directory.Exists(ESICacheDirectory))
                {
                    foreach (string file in Directory.GetFiles(ESICacheDirectory, "*.json"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch { /* Ignore individual file deletion errors */ }
                    }
                }

                // Reset manifest
                await SaveManifestAsync(new CacheManifest());
                EveMonClient.Trace("Cleared all ESI cache");
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to clear ESI cache: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
        }

        /// <summary>
        /// Gets the total cache size in bytes.
        /// </summary>
        /// <returns>The total size in bytes.</returns>
        public static long GetTotalCacheSize()
        {
            try
            {
                long totalSize = 0;
                if (Directory.Exists(ESICacheDirectory))
                {
                    foreach (string file in Directory.GetFiles(ESICacheDirectory, "*.json"))
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                    }
                }
                return totalSize;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to get ESI cache size: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return 0;
        }

        /// <summary>
        /// Gets the last update time for any cached endpoint.
        /// </summary>
        /// <returns>The most recent update time, or null if no cache exists.</returns>
        public static DateTime? GetLastUpdateTime()
        {
            try
            {
                var manifest = LoadManifest();
                DateTime? lastUpdate = null;
                foreach (var endpoint in manifest.Endpoints.Values)
                {
                    if (lastUpdate == null || endpoint.LastUpdated > lastUpdate)
                    {
                        lastUpdate = endpoint.LastUpdated;
                    }
                }
                return lastUpdate;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to get ESI last update time: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return null;
        }

        /// <summary>
        /// Sanitizes a file name by removing invalid characters.
        /// </summary>
        /// <param name="name">The original name.</param>
        /// <returns>A safe file name.</returns>
        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safeName = name;
            foreach (char c in invalidChars)
            {
                safeName = safeName.Replace(c, '_');
            }
            // Replace path separators
            safeName = safeName.Replace("/", "_").Replace("\\", "_").Replace("?", "_");
            return safeName;
        }
    }
}
