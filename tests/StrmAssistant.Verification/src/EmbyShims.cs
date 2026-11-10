// Emby/plugin type shims for the verification build only (never compiled into the plugin).
//
// Two consumers need these:
//   * CustomFfprobeApi.cs - HttpRequestOptions/IHttpClient, ProbeResult, MediaInfo,
//     MediaProtocol, ILocalizationManager, IFileSystem, IFfmpegManager.
//   * VersionPlaybackApi.cs - BaseItem/Video/Movie, MediaSourceInfo/MediaStream,
//     ISessionManager + playback event args, IJsonSerializer.
//
// Every member below is present only because a linked production file references it. When a
// production file starts using another member, add it here instead of editing the plugin.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Common.Net
{
    public sealed class HttpRequestOptions
    {
        public string Url { get; set; }
        public string DownloadFilePath { get; set; }
        public bool BufferContent { get; set; }
        public int TimeoutMs { get; set; }
        public bool ThrowOnErrorResponse { get; set; }
        public bool LogRequest { get; set; }
        public bool LogResponse { get; set; }
        public bool LogErrors { get; set; }
        public string UserAgent { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public string AcceptHeader { get; set; }
        public string Referer { get; set; }
    }

    /// <summary>Emby's HTTP client returns this shape: a status code plus a response stream.</summary>
    public sealed class HttpResponseInfo : IDisposable
    {
        public HttpStatusCode StatusCode { get; set; }
        public Stream Content { get; set; }

        public void Dispose()
        {
            Content?.Dispose();
        }
    }

    public interface IHttpClient
    {
        Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string method);
    }
}

namespace MediaBrowser.Model.IO
{
    public interface IFileSystem
    {
    }
}

namespace MediaBrowser.Model.Globalization
{
    public interface ILocalizationManager
    {
    }
}

namespace MediaBrowser.Model.MediaInfo
{
    public enum MediaProtocol
    {
        File,
        Http,
        Rtmp,
        Rtsp,
        Udp,
        Rtp,
        Ftp,
        Smb
    }
}

namespace MediaBrowser.Model.Dto
{
    public sealed class MediaInfo
    {
    }

    public sealed class MediaSourceInfo
    {
        public string Id { get; set; }
        public string ItemId { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string Container { get; set; }
        public long? RunTimeTicks { get; set; }
        public List<MediaBrowser.Model.Entities.MediaStream> MediaStreams { get; set; } =
            new List<MediaBrowser.Model.Entities.MediaStream>();
    }
}

namespace MediaBrowser.Model.Entities
{
    public enum MediaStreamType
    {
        Audio,
        Video,
        Subtitle,
        EmbeddedImage,
        Data
    }

    public sealed class MediaStream
    {
        public MediaStreamType Type { get; set; }
        public string Codec { get; set; }
        public int? Height { get; set; }
    }
}

namespace Emby.Media.Model.ProbeModel
{
    /// <summary>Only the two fields CustomFfprobeApi.NormalizeProbeJson checks.</summary>
    public sealed class ProbeResult
    {
        public object[] streams { get; set; }
        public object format { get; set; }
    }
}

namespace MediaBrowser.Controller.MediaEncoding
{
    public interface IFfmpegManager
    {
    }
}

namespace MediaBrowser.Controller.Library
{
    /// <summary>Placeholder: VersionPlaybackApi imports this namespace without using a member.</summary>
    public interface ILibraryManager
    {
    }
}

namespace MediaBrowser.Model.Serialization
{
    public interface IJsonSerializer
    {
        void SerializeToFile(object obj, string path);
        T DeserializeFromFile<T>(string path);
    }
}

namespace MediaBrowser.Controller.Entities
{
    public class BaseItem
    {
        public long InternalId { get; set; }
        public string Name { get; set; }
        public int? ProductionYear { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Path { get; set; }
    }

    public class Video : BaseItem
    {
        public List<MediaBrowser.Model.Dto.MediaSourceInfo> MediaSources { get; set; } =
            new List<MediaBrowser.Model.Dto.MediaSourceInfo>();

        public static string GetUserDataKey(Video video)
        {
            // Production uses provider ids here; returning null makes the content group fall
            // back to the name+year key, which is what the sequence tests exercise.
            return null;
        }

        public List<MediaBrowser.Model.Dto.MediaSourceInfo> GetMediaSources(bool includeExternal,
            bool includeStrm, object options)
        {
            return MediaSources;
        }
    }
}

namespace MediaBrowser.Controller.Entities.Movies
{
    public class Movie : Video
    {
    }
}

namespace MediaBrowser.Controller.Session
{
    public class PlaybackProgressEventArgs : EventArgs
    {
        public MediaBrowser.Controller.Entities.BaseItem Item { get; set; }
        public List<PlaybackUser> Users { get; set; } =
            new List<PlaybackUser>();
        public string MediaSourceId { get; set; }
        public MediaBrowser.Model.Dto.MediaSourceInfo MediaSource { get; set; }
        public long? PlaybackPositionTicks { get; set; }
    }

    public sealed class PlaybackStopEventArgs : PlaybackProgressEventArgs
    {
        public bool PlayedToCompletion { get; set; }
    }

    public sealed class PlaybackUser
    {
        public long InternalId { get; set; }
    }

    public interface ISessionManager
    {
        event EventHandler<PlaybackProgressEventArgs> PlaybackStart;
        event EventHandler<PlaybackProgressEventArgs> PlaybackProgress;
        event EventHandler<PlaybackStopEventArgs> PlaybackStopped;
    }
}
