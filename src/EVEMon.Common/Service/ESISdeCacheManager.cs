using EVEMon.Common.Enumerations;
using EVEMon.Common.Extensions;
using EVEMon.Common.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace EVEMon.Common.Service
{
    /// <summary>
    /// Manages the local cache for ESI Static Data Export (SDE) data.
    /// Supports dual-server caching: Seren (primary) and Tranquility (fallback).
    /// </summary>
    public static class ESISdeCacheManager
    {
        private const string ESICacheSubDir_Seren = "esi_seren";
        private const string ESICacheSubDir_TQ = "esi_tq";
        private const string CacheManifestFile = "cache_manifest.json";
        private static readonly TimeSpan DefaultCacheExpiry = TimeSpan.FromDays(7);

        /// <summary>
        /// Identifies which server's ESI data a cache entry belongs to.
        /// </summary>
        public enum SdeServer
        {
            /// <summary>Chinese Seren server (ali-esi.evepc.163.com)</summary>
            Seren,
            /// <summary>International Tranquility server (esi.evetech.net)</summary>
            TQ
        }

        /// <summary>
        /// Gets the ESI cache directory path for a specific server.
        /// </summary>
        public static string GetESICacheDirectory(SdeServer server)
        {
            string subDir = server == SdeServer.TQ ? ESICacheSubDir_TQ : ESICacheSubDir_Seren;
            string cacheDir = Path.Combine(EveMonClient.EVEMonCacheDir, subDir);
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }
            return cacheDir;
        }

        /// <summary>
        /// Gets the ESI cache directory for Seren (current server mode).
        /// </summary>
        public static string ESICacheDirectory => GetESICacheDirectory(SdeServer.Seren);

        /// <summary>
        /// Gets the TQ cache directory (for fallback data).
        /// </summary>
        public static string TQCacheDirectory => GetESICacheDirectory(SdeServer.TQ);

        /// <summary>
        /// Gets the cache manifest file path for a specific server.
        /// </summary>
        private static string GetManifestPath(SdeServer server) => Path.Combine(GetESICacheDirectory(server), CacheManifestFile);

        /// <summary>
        /// Gets the cache manifest file path (Seren, default).
        /// </summary>
        private static string ManifestPath => GetManifestPath(SdeServer.Seren);

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
        /// Loads the cache manifest from disk for a specific server.
        /// </summary>
        public static CacheManifest LoadManifest(SdeServer server)
        {
            try
            {
                string path = GetManifestPath(server);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<CacheManifest>(json) ?? new CacheManifest();
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to load ESI cache manifest for {server}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return new CacheManifest();
        }

        /// <summary>
        /// Loads the cache manifest from disk (Seren default).
        /// </summary>
        public static CacheManifest LoadManifest() => LoadManifest(SdeServer.Seren);

        /// <summary>
        /// Saves the cache manifest to disk for a specific server.
        /// </summary>
        public static async Task SaveManifestAsync(CacheManifest manifest, SdeServer server)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string path = GetManifestPath(server);
                string json = JsonSerializer.Serialize(manifest, options);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to save ESI cache manifest for {server}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
        }

        /// <summary>
        /// Saves the cache manifest to disk (Seren default).
        /// </summary>
        public static async Task SaveManifestAsync(CacheManifest manifest) => await SaveManifestAsync(manifest, SdeServer.Seren);

        /// <summary>
        /// Gets the cache file path for a given endpoint and server.
        /// </summary>
        public static string GetCacheFilePath(string endpoint, SdeServer server)
        {
            string safeFileName = SanitizeFileName(endpoint);
            return Path.Combine(GetESICacheDirectory(server), $"{safeFileName}.json");
        }

        /// <summary>
        /// Gets the cache file path for a given endpoint (Seren default).
        /// </summary>
        public static string GetCacheFilePath(string endpoint) => GetCacheFilePath(endpoint, SdeServer.Seren);

        /// <summary>
        /// Reads cached data for an endpoint and server.
        /// </summary>
        public static async Task<string> ReadCacheAsync(string endpoint, SdeServer server)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint, server);
                if (File.Exists(filePath))
                {
                    return await File.ReadAllTextAsync(filePath);
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to read ESI cache for {endpoint} ({server}): {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return null;
        }

        /// <summary>
        /// Reads cached data for an endpoint (Seren default).
        /// </summary>
        public static async Task<string> ReadCacheAsync(string endpoint) => await ReadCacheAsync(endpoint, SdeServer.Seren);

        /// <summary>
        /// Writes data to the cache for an endpoint and server.
        /// </summary>
        public static async Task<bool> WriteCacheAsync(string endpoint, SdeServer server, string data, string etag, DateTime expires)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint, server);
                await File.WriteAllTextAsync(filePath, data);

                // Update manifest for the specific server
                var manifest = LoadManifest(server);
                manifest.Endpoints[endpoint] = new EndpointCacheInfo
                {
                    Endpoint = endpoint,
                    FileName = Path.GetFileName(filePath),
                    ETag = etag,
                    Expires = expires,
                    LastUpdated = DateTime.UtcNow,
                    Size = data.Length
                };
                await SaveManifestAsync(manifest, server);

                EveMonClient.Trace($"Cached ESI data for {endpoint} on {server}");
                return true;
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to write ESI cache for {endpoint} ({server}): {ex.Message}");
                ExceptionHandler.LogException(ex, false);
                return false;
            }
        }

        /// <summary>
        /// Writes data to the cache for an endpoint (Seren default).
        /// </summary>
        public static async Task<bool> WriteCacheAsync(string endpoint, string data, string etag, DateTime expires)
            => await WriteCacheAsync(endpoint, SdeServer.Seren, data, etag, expires);

        /// <summary>
        /// Checks if a cached endpoint is still valid for a specific server.
        /// </summary>
        public static bool IsCacheValid(string endpoint, SdeServer server, TimeSpan? maxAge = null)
        {
            try
            {
                var manifest = LoadManifest(server);
                if (manifest.Endpoints.TryGetValue(endpoint, out var info))
                {
                    TimeSpan age = maxAge ?? DefaultCacheExpiry;
                    return info.Expires > DateTime.UtcNow ||
                           (DateTime.UtcNow - info.LastUpdated) < age;
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to check ESI cache validity for {endpoint} ({server}): {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
            return false;
        }

        /// <summary>
        /// Checks if a cached endpoint is still valid (Seren default).
        /// </summary>
        public static bool IsCacheValid(string endpoint, TimeSpan? maxAge = null)
            => IsCacheValid(endpoint, SdeServer.Seren, maxAge);

        /// <summary>
        /// Checks if TQ has cached data for an endpoint (for fallback lookup).
        /// </summary>
        public static bool HasTQCachedData(string endpoint)
        {
            try
            {
                string filePath = GetCacheFilePath(endpoint, SdeServer.TQ);
                return File.Exists(filePath);
            }
            catch { return false; }
        }

        /// <summary>
        /// Checks if any server has cached data for an endpoint (Seren first, then TQ).
        /// Returns the server that has the data, or null if neither does.
        /// </summary>
        public static SdeServer? GetServerWithCachedData(string endpoint)
        {
            if (IsCacheValid(endpoint, SdeServer.Seren))
                return SdeServer.Seren;
            if (HasTQCachedData(endpoint))
                return SdeServer.TQ;
            return null;
        }

        /// <summary>
        /// Reads cached data with automatic TQ fallback.
        /// If Seren cache exists and is valid, returns it.
        /// Otherwise, tries TQ cache as fallback.
        /// Returns (data, server) tuple.
        /// </summary>
        public static async Task<(string data, SdeServer server)?> ReadCacheWithFallbackAsync(string endpoint)
        {
            // Try Seren first
            if (IsCacheValid(endpoint, SdeServer.Seren))
            {
                var data = await ReadCacheAsync(endpoint, SdeServer.Seren);
                if (data != null)
                    return (data, SdeServer.Seren);
            }
            // Fall back to TQ
            if (HasTQCachedData(endpoint))
            {
                EveMonClient.Trace($"Using TQ fallback cache for endpoint: {endpoint}");
                var data = await ReadCacheAsync(endpoint, SdeServer.TQ);
                if (data != null)
                    return (data, SdeServer.TQ);
            }
            return null;
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
        /// Clears all ESI cache for a specific server.
        /// </summary>
        public static async Task ClearCacheAsync(SdeServer server)
        {
            try
            {
                string cacheDir = GetESICacheDirectory(server);
                if (Directory.Exists(cacheDir))
                {
                    foreach (string file in Directory.GetFiles(cacheDir, "*.json"))
                    {
                        try { File.Delete(file); } catch { /* ignore */ }
                    }
                }
                await SaveManifestAsync(new CacheManifest(), server);
                EveMonClient.Trace($"Cleared ESI cache for {server}");
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to clear ESI cache for {server}: {ex.Message}");
                ExceptionHandler.LogException(ex, false);
            }
        }

        /// <summary>
        /// Clears all ESI cache (both Seren and TQ).
        /// </summary>
        public static async Task ClearAllCacheAsync()
        {
            await ClearCacheAsync(SdeServer.Seren);
            await ClearCacheAsync(SdeServer.TQ);
            EveMonClient.Trace("Cleared all ESI cache (Seren + TQ)");
        }

        /// <summary>
        /// Gets the total cache size in bytes for a specific server.
        /// </summary>
        public static long GetCacheSize(SdeServer server)
        {
            try
            {
                string cacheDir = GetESICacheDirectory(server);
                if (Directory.Exists(cacheDir))
                {
                    return Directory.GetFiles(cacheDir, "*.json")
                        .Sum(file => new FileInfo(file).Length);
                }
            }
            catch (Exception ex)
            {
                EveMonClient.Trace($"Failed to get ESI cache size for {server}: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Gets the total cache size in bytes (Seren only).
        /// </summary>
        public static long GetTotalCacheSize() => GetCacheSize(SdeServer.Seren);

        /// <summary>
        /// Gets the combined total cache size (Seren + TQ).
        /// </summary>
        public static long GetTotalCacheSizeIncludingTQ() => GetCacheSize(SdeServer.Seren) + GetCacheSize(SdeServer.TQ);

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
