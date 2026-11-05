using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Resources;

namespace StrmAssistant.Properties
{
    /// <summary>
    /// Emby 通过 Assembly.Load(bytes) 载入插件主程序集（无物理位置），
    /// 这种程序集的卫星程序集（culture 子目录）探测不会触发 AssemblyResolve，
    /// 因此 ResourceManager 的默认文化回退找不到 zh/zh-hant 资源。
    /// 此类在 zh* 文化下直接从插件目录 &lt;culture&gt;/StrmAssistantLite.resources.dll
    /// 读取卫星程序集并返回其资源集。
    /// </summary>
    public class SatelliteResourceManager : ResourceManager
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, Assembly> SatelliteCache =
            new Dictionary<string, Assembly>();

        private readonly string _baseName;

        public SatelliteResourceManager(string baseName, Assembly assembly)
            : base(baseName, assembly)
        {
            _baseName = baseName;
        }

        private static string EnvRoot => Environment.GetEnvironmentVariable("SAT_ROOT");

        private static string PluginRoot
        {
            get
            {
                try
                {
                    return Plugin.Instance?.ApplicationPaths.PluginsPath;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static string SatelliteRoot => EnvRoot ?? Plugin._satelliteRoot ?? PluginRoot;

        private static Assembly TryLoadSatellite(string cultureDir)
        {
            var root = SatelliteRoot;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var file = Path.Combine(root, cultureDir, "StrmAssistantLite.resources.dll");
            if (!File.Exists(file))
            {
                return null;
            }

            lock (SyncRoot)
            {
                if (SatelliteCache.TryGetValue(cultureDir, out var cached) && cached != null)
                {
                    return cached;
                }

                Assembly assembly = null;
                try
                {
                    assembly = Assembly.Load(File.ReadAllBytes(file));
                }
                catch
                {
                    assembly = null;
                }

                SatelliteCache[cultureDir] = assembly;
                return assembly;
            }
        }

        protected override ResourceSet InternalGetResourceSet(CultureInfo culture,
            bool createIfNotExists, bool tryParents)
        {
            Plugin.Instance?.Logger.Info("[SatRM] IGRES culture={0} root={1} instance={2}",
                culture?.Name, SatelliteRoot ?? "(null)", Plugin.Instance == null ? "null" : "set");
            for (var c = culture; c != null && !string.IsNullOrEmpty(c.Name); c = c.Parent)
            {
                if (!c.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidates = new List<string> { c.Name, c.Name.ToLowerInvariant() };
                foreach (var dir in candidates)
                {
                    var satellite = TryLoadSatellite(dir);
                    if (satellite == null)
                    {
                        continue;
                    }

                    // 卫星程序集里的资源名带文化后缀（…Properties.Resources.zh.resources），
                    // 枚举出与基础名匹配的那个，用它的“去掉 .resources 后缀”的名字建 RM
                    var satName = satellite.GetManifestResourceNames()
                        .FirstOrDefault(n => n.StartsWith(_baseName, StringComparison.OrdinalIgnoreCase) &&
                                             n.EndsWith(".resources", StringComparison.OrdinalIgnoreCase));
                    if (satName == null)
                    {
                        continue;
                    }

                    var satBaseName = satName.Substring(0, satName.Length - ".resources".Length);
                    var satelliteManager = new ResourceManager(satBaseName, satellite);
                    var set = satelliteManager.GetResourceSet(
                        CultureInfo.InvariantCulture, createIfNotExists, true);
                    if (set != null)
                    {
                        return set;
                    }
                }
            }

            return base.InternalGetResourceSet(culture, createIfNotExists, tryParents);
        }
    }
}
