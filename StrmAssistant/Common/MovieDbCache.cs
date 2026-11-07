using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Disk cache for TMDB JSON responses. The key is a hash of the final (rewritten) request
    /// URL, so switching between the default, local and alternative sources cannot mix entries.
    /// Only GET responses are cached by <see cref="MovieDbApi"/>; callers still decide whether a
    /// stale entry may be used when the network request fails.
    /// </summary>
    internal sealed class MovieDbCache
    {
        private const string CacheDirectoryName = "MovieDbCache";
        private const int MaxEntryBytes = 8 * 1024 * 1024;
        private const int LockStripes = 64;

        private static readonly Lazy<MovieDbCache> LazyInstance =
            new Lazy<MovieDbCache>(() => new MovieDbCache(), LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Regex SecretParamRegex = new Regex(
            @"([?&](?:api_key|access_token)=)[^&]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _directory;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly SemaphoreSlim[] _keyLocks;
        private readonly object _sizeLock = new object();
        private readonly object _evictionLock = new object();
        private readonly ConcurrentDictionary<string, long> _sizes =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private long _totalSize;
        private bool _sizeInitialized;

        private MovieDbCache()
        {
            _directory = GetCacheDirectory();
            _jsonSerializer = Plugin.Instance?.JsonSerializer;
            _keyLocks = Enumerable.Range(0, LockStripes)
                .Select(_ => new SemaphoreSlim(1, 1))
                .ToArray();
        }

        public static MovieDbCache Instance => LazyInstance.Value;

        public bool IsEnabled
        {
            get
            {
                var options = GetOptions();
                return options != null && options.EnableMovieDbCache;
            }
        }

        public string GetKey(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                var normalized = NormalizeUrl(url);
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    var builder = new StringBuilder(hash.Length * 2);

                    foreach (var b in hash)
                    {
                        builder.Append(b.ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch (Exception e)
            {
                LogDebug("MovieDbCache - Key calculation failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Reads an entry. When <paramref name="allowExpired"/> is false only entries inside the
        /// configured TTL are returned. A corrupt entry is deleted and treated as a miss.
        /// </summary>
        public bool TryGet(string key, bool allowExpired, out MovieDbCacheEntry entry, out bool isStale)
        {
            entry = null;
            isStale = false;

            if (!IsEnabled || string.IsNullOrEmpty(key) || _jsonSerializer is null) return false;

            var path = GetPath(key);

            if (!File.Exists(path))
            {
                ForgetSize(key);
                return false;
            }

            var gate = GetGate(key);
            gate.Wait();

            try
            {
                if (!File.Exists(path))
                {
                    ForgetSize(key);
                    return false;
                }

                string json;

                try
                {
                    json = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    LogDebug("MovieDbCache - Read failed: " + e.Message);
                    return false;
                }

                MovieDbCacheEntry parsed;

                try
                {
                    parsed = _jsonSerializer.DeserializeFromString<MovieDbCacheEntry>(json);
                }
                catch (Exception e)
                {
                    LogDebug("MovieDbCache - Corrupt entry discarded: " + e.Message);
                    DeleteEntry(key, path);
                    return false;
                }

                if (parsed is null || parsed.CachedAtUtc == default || string.IsNullOrEmpty(parsed.Body))
                {
                    DeleteEntry(key, path);
                    return false;
                }

                var fresh = parsed.CachedAtUtc.Add(GetTtl()) > DateTime.UtcNow;

                if (!fresh && !allowExpired) return false;

                entry = parsed;
                isStale = !fresh;
                Touch(path);

                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public void Store(string key, string url, string body, string contentType,
            MovieDbCacheMeta metadata = null)
        {
            if (!IsEnabled || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(body) ||
                _jsonSerializer is null)
            {
                return;
            }

            try
            {
                if (Utf8NoBom.GetByteCount(body) > MaxEntryBytes)
                {
                    LogDebug("MovieDbCache - Response too large to cache: " + SanitizeUrl(url));
                    return;
                }

                var entry = new MovieDbCacheEntry
                {
                    Url = SanitizeUrl(url),
                    CachedAtUtc = DateTime.UtcNow,
                    ContentType = contentType,
                    Body = body,
                    _meta = metadata
                };

                var json = _jsonSerializer.SerializeToString(entry);
                var path = GetPath(key);

                EnsureSizeInitialized();
                Directory.CreateDirectory(_directory);

                var gate = GetGate(key);
                gate.Wait();
                string tempPath = null;

                try
                {
                    tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tempPath, json, Utf8NoBom);

                    var newLength = new FileInfo(tempPath).Length;
                    _sizes.TryGetValue(key, out var oldLength);

                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tempPath, path);
                    tempPath = null;

                    _sizes[key] = newLength;
                    Interlocked.Add(ref _totalSize, newLength - oldLength);
                }
                finally
                {
                    gate.Release();

                    if (tempPath != null)
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // Best effort cleanup only.
                        }
                    }
                }

                EnforceSizeLimit();
            }
            catch (Exception e)
            {
                LogDebug("MovieDbCache - Write failed: " + e.Message);
            }
        }

        internal static string SanitizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            try
            {
                return SecretParamRegex.Replace(url, "$1***");
            }
            catch
            {
                return url;
            }
        }

        private static MetadataEnhanceOptions GetOptions()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            }
            catch
            {
                return null;
            }
        }

        private static TimeSpan GetTtl()
        {
            var days = GetOptions()?.MovieDbCacheTtlDays ?? 30;
            days = Math.Max(1, Math.Min(365, days));
            return TimeSpan.FromDays(days);
        }

        private static long GetMaxSizeBytes()
        {
            var megabytes = GetOptions()?.MovieDbCacheMaxSizeMb ?? 256;
            megabytes = Math.Max(16, Math.Min(4096, megabytes));
            return megabytes * 1024L * 1024L;
        }

        private static string GetCacheDirectory()
        {
            try
            {
                var dataFolder = Plugin.Instance?.DataFolderPath;

                if (string.IsNullOrEmpty(dataFolder))
                {
                    dataFolder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                        "StrmAssistant");
                }

                return Path.Combine(dataFolder, CacheDirectoryName);
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "StrmAssistant", CacheDirectoryName);
            }
        }

        private string GetPath(string key)
        {
            return Path.Combine(_directory, key + ".json");
        }

        private SemaphoreSlim GetGate(string key)
        {
            var hash = 17;

            unchecked
            {
                foreach (var c in key)
                {
                    hash = hash * 31 + c;
                }
            }

            var index = (hash & int.MaxValue) % _keyLocks.Length;
            return _keyLocks[index];
        }

        private void DeleteEntry(string key, string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore, the entry will be retried on the next request.
            }

            ForgetSize(key);
        }

        private void ForgetSize(string key)
        {
            if (_sizes.TryRemove(key, out var length)) Interlocked.Add(ref _totalSize, -length);
        }

        private void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // LRU precision is not important enough to fail a cache hit.
            }
        }

        private void EnsureSizeInitialized()
        {
            if (_sizeInitialized) return;

            lock (_sizeLock)
            {
                if (_sizeInitialized) return;

                long total = 0;

                try
                {
                    if (Directory.Exists(_directory))
                    {
                        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                        {
                            try
                            {
                                var length = new FileInfo(file).Length;
                                _sizes[Path.GetFileNameWithoutExtension(file)] = length;
                                total += length;
                            }
                            catch
                            {
                                // Ignore files that disappear during the scan.
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    LogDebug("MovieDbCache - Size scan failed: " + e.Message);
                }

                Interlocked.Exchange(ref _totalSize, total);
                _sizeInitialized = true;
            }
        }

        private void EnforceSizeLimit()
        {
            var maxSize = GetMaxSizeBytes();

            if (Interlocked.Read(ref _totalSize) <= maxSize) return;

            EnsureSizeInitialized();

            lock (_evictionLock)
            {
                if (Interlocked.Read(ref _totalSize) <= maxSize) return;

                List<FileInfo> files;

                try
                {
                    files = Directory.EnumerateFiles(_directory, "*.json")
                        .Select(path =>
                        {
                            try
                            {
                                return new FileInfo(path);
                            }
                            catch
                            {
                                return null;
                            }
                        })
                        .Where(file => file != null)
                        .OrderBy(file => file.LastWriteTimeUtc)
                        .ToList();
                }
                catch (Exception e)
                {
                    LogDebug("MovieDbCache - Eviction scan failed: " + e.Message);
                    return;
                }

                var evicted = 0;

                foreach (var file in files)
                {
                    if (Interlocked.Read(ref _totalSize) <= maxSize) break;

                    var key = Path.GetFileNameWithoutExtension(file.Name);
                    long length;

                    try
                    {
                        length = file.Length;
                        file.Delete();
                    }
                    catch
                    {
                        continue;
                    }

                    if (_sizes.TryRemove(key, out var trackedLength)) length = trackedLength;

                    Interlocked.Add(ref _totalSize, -length);
                    evicted++;
                }

                if (evicted > 0)
                {
                    LogDebug("MovieDbCache - Evicted " + evicted + " least recently used entries");
                }
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

            var builder = new StringBuilder();
            builder.Append(uri.Scheme.ToLowerInvariant());
            builder.Append("://");
            builder.Append(uri.Host.ToLowerInvariant());

            if (!uri.IsDefaultPort)
            {
                builder.Append(':');
                builder.Append(uri.Port);
            }

            builder.Append(string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath);

            var query = new List<KeyValuePair<string, string>>();

            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;

                var separator = pair.IndexOf('=');
                var key = separator >= 0 ? pair.Substring(0, separator) : pair;
                var value = separator >= 0 ? pair.Substring(separator + 1) : string.Empty;

                query.Add(new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
            }

            if (query.Count > 0)
            {
                query.Sort((left, right) =>
                {
                    var compare = StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key);
                    return compare != 0 ? compare : StringComparer.Ordinal.Compare(left.Value, right.Value);
                });

                builder.Append('?');
                builder.Append(string.Join("&", query.Select(pair =>
                    Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            }

            return builder.ToString();
        }

        private static void LogDebug(string message)
        {
            try
            {
                Plugin.Instance?.Logger?.Debug(message);
            }
            catch
            {
                // Logging must never break cache handling.
            }
        }
    }

    internal sealed class MovieDbCacheEntry
    {
        public string Url { get; set; }
        public DateTime CachedAtUtc { get; set; }
        public string ContentType { get; set; }
        public string Body { get; set; }
        public MovieDbCacheMeta _meta { get; set; }
    }

    public sealed class MovieDbCacheMeta
    {
        public string source { get; set; }
        public string request { get; set; }
        public string url { get; set; }
        public string language { get; set; }
        public bool converted { get; set; }
        public bool rewritten { get; set; }
        public DateTime capturedAtUtc { get; set; }
    }
}
