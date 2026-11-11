// Shim types for the verification build only.
//
// This file is never compiled into StrmAssistant/StrmAssistant.csproj. The verification project
// links the real source files under test (SourceBackoff.cs, DvP7Api.cs, ResumePositionMath.cs,
// CustomFfprobeApi.cs, StrmUrlHelper.cs, VersionPlaybackApi.cs) and supplies the very small slice
// of the Emby/plugin surface those files touch. Keep the signatures in sync with the real
// members; when they disagree, the real source file wins and this shim is adjusted, never the
// other way round.
//
// Covered here:
//   MediaBrowser.Model.Logging.ILogger        - SourceBackoff/DvP7Api/CustomFfprobeApi logging
//   StrmAssistant.Plugin.Instance             - logger / options / serializer / paths / http
//   StrmAssistant.Options.PluginOptions       - media info extract and experience options
//
// Emby type shims live in src/EmbyShims.cs, the ffmpeg runner shim in src/FfmpegApiShim.cs.

using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Model.Logging
{
    public interface ILogger
    {
        void Info(string m, params object[] a);
        void Debug(string m, params object[] a);
    }
}

namespace StrmAssistant.Options
{
    public sealed class PluginOptions
    {
        public MediaInfoExtractOptions MediaInfoExtractOptions { get; set; } =
            new MediaInfoExtractOptions();

        public ExperienceEnhanceOptions ExperienceEnhanceOptions { get; set; } =
            new ExperienceEnhanceOptions();
    }

    public sealed class MediaInfoExtractOptions
    {
        public bool EnableDvP7LayerBadge { get; set; }

        /// <summary>Production option used to point the static ffprobe at a concrete binary.</summary>
        public string CustomFfprobePath { get; set; }
    }

    public sealed class ExperienceEnhanceOptions
    {
        public VersionPlaybackOptions VersionPlaybackOptions { get; set; } =
            new VersionPlaybackOptions();
    }

    public sealed class VersionPlaybackOptions
    {
        public bool PersistPerVersionResume { get; set; }
    }
}

namespace StrmAssistant
{
    public static class Plugin
    {
        public static PluginInstance Instance { get; set; } = new PluginInstance();

        /// <summary>Production field read by SatelliteResourceManager (set in Plugin.Init).</summary>
        internal static string _satelliteRoot;
    }

    /// <summary>Application paths slice used by the linked files.</summary>
    public sealed class PluginPaths
    {
        public string PluginConfigurationsPath { get; set; }

        public string ProgramDataPath { get; set; }

        public string PluginsPath { get; set; }
    }

    public sealed class PluginInstance
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public MediaBrowser.Model.Logging.ILogger Logger { get; set; }

        public StrmAssistant.Options.PluginOptions Options { get; set; } =
            new StrmAssistant.Options.PluginOptions();

        public JsonSerializerShim JsonSerializer { get; set; } = new JsonSerializerShim();

        public PluginPaths ApplicationPaths { get; set; } = new PluginPaths();

        public string DataFolderPath { get; set; }

        public string UserAgent { get; set; }

        public MediaBrowser.Controller.MediaEncoding.IFfmpegManager FfmpegManager { get; set; }

        public MediaBrowser.Model.IO.IFileSystem FileSystem { get; set; }

        public MediaBrowser.Model.Globalization.ILocalizationManager LocalizationManager { get; set; }

        public MediaBrowser.Common.Net.IHttpClient HttpClient { get; set; }

        public StrmAssistant.Options.PluginOptions GetPluginOptions()
        {
            return Options;
        }

        /// <summary>
        /// Minimal serializer surface. Implements Emby's IJsonSerializer for VersionPlaybackApi
        /// and additionally exposes the DeserializeFromString helper CustomFfprobeApi/DvP7Api use.
        /// </summary>
        public sealed class JsonSerializerShim : MediaBrowser.Model.Serialization.IJsonSerializer
        {
            public T DeserializeFromString<T>(string json)
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonOptions);
            }

            public void SerializeToFile(object obj, string path)
            {
                var directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                    obj, obj.GetType(), JsonOptions));
            }

            public T DeserializeFromFile<T>(string path)
            {
                if (!File.Exists(path)) return default;

                return System.Text.Json.JsonSerializer.Deserialize<T>(
                    File.ReadAllText(path), JsonOptions);
            }
        }
    }
}

namespace StrmAssistant.Verification
{
    /// <summary>Writes plugin log lines to the console so DetectAsync traces are visible.</summary>
    internal sealed class ConsoleLogger : MediaBrowser.Model.Logging.ILogger
    {
        private readonly string _area;

        public ConsoleLogger(string area)
        {
            _area = area;
        }

        public void Info(string m, params object[] a)
        {
            Console.WriteLine("      [{0}/INFO] {1}", _area, Format(m, a));
        }

        public void Debug(string m, params object[] a)
        {
            Console.WriteLine("      [{0}/DEBUG] {1}", _area, Format(m, a));
        }

        private static string Format(string message, object[] args)
        {
            if (args is null || args.Length == 0) return message;

            try
            {
                return string.Format(message, args);
            }
            catch
            {
                return message;
            }
        }
    }
}
