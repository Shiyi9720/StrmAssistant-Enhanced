using System;
using System.Globalization;
using System.IO;
using System.Linq;
using StrmAssistant.Properties;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// 复现插件在 Emby 下加载本地化资源的场景：主程序集没有物理路径，中文资源来自
    /// &lt;pluginsRoot&gt;/&lt;cultureDir&gt;/StrmAssistantLite.resources.dll。
    /// 这里链接真实的 SatelliteResourceManager.cs，用构建产物里的卫星程序集验证它确实能取到中文。
    /// </summary>
    internal static class SatelliteResourceTests
    {
        private const string BaseName = "StrmAssistant.Properties.Resources";
        private const string Key = "AreYouSureToContinue";
        private const string English = "Are you sure you wish to continue?";

        public static void Run(TestRunner runner)
        {
            runner.Section("SatelliteResourceManager");

            var pluginOutput = FindPluginOutput();
            if (pluginOutput == null)
            {
                runner.Skip("satellite resource loading",
                    "StrmAssistant/bin/Release/netstandard2.1 not found; build the plugin first");
                return;
            }

            // SatResourceManager 的根目录优先级：SAT_ROOT > Plugin._satelliteRoot > PluginsPath。
            Environment.SetEnvironmentVariable("SAT_ROOT", null);
            Plugin._satelliteRoot = pluginOutput;

            CheckCulture(runner, pluginOutput, "zh-CN", "zh",
                value => value.Contains("您") && value.Contains("继续"));
            CheckCulture(runner, pluginOutput, "zh-Hant", "zh-hant",
                value => value.Contains("繼續"));
        }

        private static void CheckCulture(TestRunner runner, string pluginOutput, string cultureName,
            string expectedDir, Func<string, bool> isExpectedChinese)
        {
            var satelliteFile = Path.Combine(pluginOutput, expectedDir,
                "StrmAssistantLite.resources.dll");
            runner.Check("satellite exists: " + expectedDir, File.Exists(satelliteFile),
                satelliteFile);

            // 直接验证清单流：资源名本身带文化后缀，ResourceSet 应读出中文。
            var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(satelliteFile));
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.StartsWith(BaseName, StringComparison.OrdinalIgnoreCase) &&
                                     n.EndsWith(".resources", StringComparison.OrdinalIgnoreCase));
            runner.Check("manifest resource: " + expectedDir, resourceName != null);
            if (resourceName == null)
            {
                return;
            }

            using (var stream = asm.GetManifestResourceStream(resourceName))
            using (var set = new System.Resources.ResourceSet(stream))
            {
                var value = set.GetString(Key);
                runner.Check(resourceName + " -> " + Key, value != null && value != English,
                    "actual=" + (value ?? "<null>"));
            }

            // 端到端：真实的 SatelliteResourceManager 在 zh / zh-hant 文化下应返回中文。
            var manager = new SatelliteResourceManager(BaseName, typeof(SatelliteResourceManager).Assembly);
            var culture = new CultureInfo(cultureName);
            var actual = manager.GetString(Key, culture);
            Console.WriteLine("  [" + cultureName + "] " + Key + " = " + (actual ?? "<null>"));
            runner.Check("SatelliteResourceManager " + cultureName,
                actual != null && actual != English && isExpectedChinese(actual),
                "actual=" + (actual ?? "<null>"));
        }

        private static string FindPluginOutput()
        {
            var overrideRoot = Environment.GetEnvironmentVariable("STRMASSISTANT_SATELLITE_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot) && HasSatellites(overrideRoot))
            {
                return overrideRoot;
            }

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "StrmAssistant.sln")))
                {
                    var output = Path.Combine(dir.FullName, "StrmAssistant", "bin", "Release",
                        "netstandard2.1");
                    return HasSatellites(output) ? output : null;
                }
            }

            return null;
        }

        private static bool HasSatellites(string root)
        {
            return File.Exists(Path.Combine(root, "zh", "StrmAssistantLite.resources.dll")) &&
                   File.Exists(Path.Combine(root, "zh-hant", "StrmAssistantLite.resources.dll"));
        }
    }
}