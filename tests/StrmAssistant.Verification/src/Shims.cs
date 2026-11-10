// Shim types for the verification build only.
//
// This file is never compiled into StrmAssistant/StrmAssistant.csproj. The verification project
// links the real source files under test (SourceBackoff.cs, DvP7Api.cs, ResumePositionMath.cs)
// and supplies the very small slice of the Emby/plugin surface those files touch. Keep the
// signatures in sync with the real members; when they disagree, the real source file wins and
// this shim is adjusted, never the other way round.
//
// Covered references:
//   MediaBrowser.Model.Logging.ILogger        - SourceBackoff/DvP7Api logging
//   StrmAssistant.Plugin.Instance             - Logger / GetPluginOptions() / JsonSerializer
//   StrmAssistant.Options.PluginOptions       - MediaInfoExtractOptions.EnableDvP7LayerBadge
//   StrmAssistant.Common.CustomFfprobeApi     - RunProbeJsonAsync used by DvP7Api.DetectAsync
//
// The shim is always compiled in this project; there is no conditional symbol because the
// project itself is the condition (it is the only project that references this file).

using System;
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
    }

    public sealed class MediaInfoExtractOptions
    {
        public bool EnableDvP7LayerBadge { get; set; }
    }
}

namespace StrmAssistant
{
    public static class Plugin
    {
        public static PluginInstance Instance { get; set; } = new PluginInstance();
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

        public StrmAssistant.Options.PluginOptions GetPluginOptions()
        {
            return Options;
        }

        public sealed class JsonSerializerShim
        {
            public T DeserializeFromString<T>(string json)
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
        }
    }
}

namespace StrmAssistant.Common
{
    internal static class CustomFfprobeApi
    {
        // DetectAsync is not exercised by the verification cases; returning null keeps the
        // signature identical to the real helper without spawning ffprobe.
        internal static Task<string> RunProbeJsonAsync(string inputPath, string arguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string>(null);
        }
    }
}
