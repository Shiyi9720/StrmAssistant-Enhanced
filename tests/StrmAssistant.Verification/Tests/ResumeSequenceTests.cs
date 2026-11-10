using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Multi-step resume sequences against the real VersionPlaybackApi: playback events are fed
    /// through its real session event handlers and the resume points are read back through
    /// GetVersionPlayback, exactly as the web service does. Every conversion goes through the
    /// real ResumePositionMath.
    /// </summary>
    internal static class ResumeSequenceTests
    {
        private const long TicksPerMinute = 600_000_000L;

        public static void Run(TestRunner runner)
        {
            CrossItemSequence(runner);
            MostRecentUnfinishedWins(runner);
            PlayedCandidateIsNotConverted(runner);
            MergedSourceItem(runner);
        }

        /// <summary>
        /// A 40% -> switch to B (different runtime, conversion) -> B plays to the end -> back to A.
        /// A and B are separate library items grouped by name+year (the cross-library shape the
        /// feature targets).
        /// </summary>
        private static void CrossItemSequence(TestRunner runner)
        {
            runner.Section("F1. cross-item sequence A 40% -> B -> B finished -> back to A");

            var harness = new Harness();

            var sourceA = Source("srcA", "1000", "/library/Sequence.Movie.2024.1080p.mkv",
                60 * TicksPerMinute, 1080);
            var sourceB = Source("srcB", "2000", "/library/Sequence.Movie.2024.2160p.mkv",
                80 * TicksPerMinute, 2160);

            var movieA = Movie(1000, "Sequence Movie", 2024, sourceA);
            var movieB = Movie(2000, "Sequence Movie", 2024, sourceB);

            // A is watched to 40% (24 of 60 minutes).
            harness.Progress(movieA, sourceA, 24 * TicksPerMinute);

            var readA = harness.Get(movieA, "srcA");
            runner.Check("F1 A has its own resume point", readA != null);
            runner.EqualLong("F1 A position is 24min", 24 * TicksPerMinute,
                readA?.PositionTicks ?? -1);
            runner.Check("F1 A is not marked played", readA != null && !readA.Played);

            // Switching to B converts once by runtime ratio: 24 * 80/60 = 32 minutes.
            var readB = harness.Get(movieB, "srcB");
            runner.Check("F1 B receives a converted resume point", readB != null);
            runner.EqualLong("F1 B position is 32min (40% of 80min)", 32 * TicksPerMinute,
                readB?.PositionTicks ?? -1);
            runner.Check("F1 B is not marked played", readB != null && !readB.Played);

            // The conversion is persisted as B's own record before anything else changes.
            CheckConvertedStore(runner, harness, "F1", 32 * TicksPerMinute);

            // Reading B again is an exact hit: no second conversion.
            var readBAgain = harness.Get(movieB, "srcB");
            runner.EqualLong("F1 second read of B is unchanged", 32 * TicksPerMinute,
                readBAgain?.PositionTicks ?? -1);

            // A moves to 50% (30min). If conversion ran again, B would become 40min; it must
            // not, because B now owns its own resume record.
            harness.Progress(movieA, sourceA, 30 * TicksPerMinute);

            var readBAfterAMoved = harness.Get(movieB, "srcB");
            runner.EqualLong("F1 B keeps its own record after A moves on", 32 * TicksPerMinute,
                readBAfterAMoved?.PositionTicks ?? -1);

            // B is watched to the end.
            harness.StopCompleted(movieB, sourceB);

            var readBFinished = harness.Get(movieB, "srcB");
            runner.EqualLong("F1 finished B position is 0", 0,
                readBFinished?.PositionTicks ?? -1);
            runner.Check("F1 finished B is marked played", readBFinished?.Played == true);

            // Back to A: its exact unfinished record wins, so the user resumes where A stopped.
            var backToA = harness.Get(movieA, "srcA");
            runner.EqualLong("F1 back to A resumes at A's 30min", 30 * TicksPerMinute,
                backToA?.PositionTicks ?? -1);
            runner.Check("F1 back to A is not marked played", backToA != null && !backToA.Played);

            // A version with no record at all follows the group's most recent state (played).
            var sourceC = Source("srcC", "3000", "/library/Sequence.Movie.2024.720p.mkv",
                100 * TicksPerMinute, 720);
            var movieC = Movie(3000, "Sequence Movie", 2024, sourceC);

            var readC = harness.Get(movieC, "srcC");
            runner.Check("F1 recordless version C is reported played (shared latest state)",
                readC != null && readC.Played, readC is null ? "no entry" : "played=" + readC.Played);
            runner.EqualLong("F1 recordless version C position is 0", 0,
                readC?.PositionTicks ?? -1);
        }

        /// <summary>
        /// The conversion candidate is the most recent unfinished record of the content group,
        /// proven by giving an older and a newer record different runtimes.
        /// </summary>
        private static void MostRecentUnfinishedWins(TestRunner runner)
        {
            runner.Section("F2. conversion uses the most recent unfinished record");

            var harness = new Harness();

            var sourceA = Source("srcA", "1000", "/library/Recent.Movie.2024.1080p.mkv",
                60 * TicksPerMinute, 1080);
            var sourceC = Source("srcC", "3000", "/library/Recent.Movie.2024.2160p.mkv",
                100 * TicksPerMinute, 2160);
            var sourceB = Source("srcB", "2000", "/library/Recent.Movie.2024.720p.mkv",
                80 * TicksPerMinute, 720);

            var movieA = Movie(1000, "Recent Movie", 2024, sourceA);
            var movieC = Movie(3000, "Recent Movie", 2024, sourceC);
            var movieB = Movie(2000, "Recent Movie", 2024, sourceB);

            harness.Progress(movieA, sourceA, 12 * TicksPerMinute);   // 20% of 60
            harness.Progress(movieC, sourceC, 60 * TicksPerMinute);   // 60% of 100, newer

            var readB = harness.Get(movieB, "srcB");
            runner.Check("F2 B receives a converted resume point", readB != null);
            runner.EqualLong("F2 B uses the newer C record (60 of 100 -> 48 of 80)",
                48 * TicksPerMinute, readB?.PositionTicks ?? -1);
        }

        /// <summary>A played record is never used as a conversion source.</summary>
        private static void PlayedCandidateIsNotConverted(TestRunner runner)
        {
            runner.Section("F3. a finished version makes an unrecorded version played, not converted");

            var harness = new Harness();

            var sourceA = Source("srcA", "1000", "/library/Played.Movie.2024.1080p.mkv",
                60 * TicksPerMinute, 1080);
            var sourceB = Source("srcB", "2000", "/library/Played.Movie.2024.2160p.mkv",
                80 * TicksPerMinute, 2160);

            var movieA = Movie(1000, "Played Movie", 2024, sourceA);
            var movieB = Movie(2000, "Played Movie", 2024, sourceB);

            harness.Progress(movieA, sourceA, 30 * TicksPerMinute);
            harness.StopCompleted(movieA, sourceA);

            var readB = harness.Get(movieB, "srcB");
            runner.Check("F3 B is reported played from the shared state",
                readB != null && readB.Played, readB is null ? "no entry" : "played=" + readB.Played);
            runner.EqualLong("F3 B position is 0, not a converted 40min", 0,
                readB?.PositionTicks ?? -1);
        }

        /// <summary>
        /// A single item carrying both versions (Emby merged media sources). The documented rule
        /// says conversion draws only from records that do not belong to any current version, so
        /// here B gets no plugin entry and the client keeps Emby's native position. Pinned so the
        /// behaviour is visible; see the batch-15 report for the resulting limitation.
        /// </summary>
        private static void MergedSourceItem(TestRunner runner)
        {
            runner.Section("F4. merged multi-source item (documented exclusion, no conversion)");

            var harness = new Harness();

            var sourceA = Source("srcA", "5000", "/library/Merged.Movie.2024.1080p.mkv",
                60 * TicksPerMinute, 1080);
            var sourceB = Source("srcB", "5000", "/library/Merged.Movie.2024.2160p.mkv",
                80 * TicksPerMinute, 2160);

            var movie = Movie(5000, "Merged Movie", 2024, sourceA);
            movie.MediaSources.Add(sourceB);

            harness.Progress(movie, sourceA, 24 * TicksPerMinute);

            var readA = harness.Get(movie, "srcA");
            runner.EqualLong("F4 A exact record stays at 24min", 24 * TicksPerMinute,
                readA?.PositionTicks ?? -1);

            var readB = harness.Get(movie, "srcB");
            runner.Check("F4 B has no plugin entry (A's record belongs to a current version)",
                readB is null,
                readB is null ? null : "position=" + readB.PositionTicks);
        }

        private static void CheckConvertedStore(TestRunner runner, Harness harness, string prefix,
            long expectedPosition)
        {
            var path = Path.Combine(harness.StoreDirectory, "StrmAssistant.VersionPlayback.json");

            runner.Check(prefix + " store file was written", File.Exists(path), path);

            if (!File.Exists(path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("Records", out var records))
            {
                runner.Check(prefix + " store has a Records array", false,
                    File.ReadAllText(path));
                return;
            }

            var found = false;
            var played = true;

            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("MediaSourceId", out var id) ||
                    id.GetString() != "srcB") continue;

                found = record.TryGetProperty("PositionTicks", out var position) &&
                        position.GetInt64() == expectedPosition;
                played = !record.TryGetProperty("Played", out var value) || value.GetBoolean();
            }

            runner.Check(prefix + " store contains B's converted record at " +
                         (expectedPosition / TicksPerMinute) + "min", found);
            runner.Check(prefix + " stored B record is unfinished", !played);
        }

        private static Movie Movie(long id, string name, int year, MediaSourceInfo source)
        {
            return new Movie
            {
                InternalId = id,
                Name = name,
                ProductionYear = year,
                RunTimeTicks = source.RunTimeTicks,
                MediaSources = new List<MediaSourceInfo> {source}
            };
        }

        private static MediaSourceInfo Source(string id, string itemId, string path, long runtimeTicks,
            int height)
        {
            return new MediaSourceInfo
            {
                Id = id,
                ItemId = itemId,
                Path = path,
                Name = Path.GetFileName(path),
                RunTimeTicks = runtimeTicks,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = "hevc",
                        Height = height
                    }
                }
            };
        }

        private sealed class Harness
        {
            private readonly FakeSessionManager _sessions = new FakeSessionManager();
            private readonly PlaybackUser _user = new PlaybackUser {InternalId = 7};
            private readonly VersionPlaybackApi _api;

            public Harness()
            {
                StoreDirectory = Path.Combine(Path.GetTempPath(),
                    "strmassistant-verify-sequence-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StoreDirectory);

                Plugin.Instance.ApplicationPaths.PluginConfigurationsPath = StoreDirectory;
                Plugin.Instance.Logger = Plugin.Instance.Logger ?? new ConsoleLogger("verify");
                Plugin.Instance.Options.ExperienceEnhanceOptions.VersionPlaybackOptions
                    .PersistPerVersionResume = true;

                _api = new VersionPlaybackApi(_sessions, Plugin.Instance.JsonSerializer);
                _api.Initialize();
            }

            public string StoreDirectory { get; }

            public void Progress(Movie movie, MediaSourceInfo source, long positionTicks)
            {
                _sessions.RaiseProgress(new PlaybackProgressEventArgs
                {
                    Item = movie,
                    Users = new List<PlaybackUser> {_user},
                    MediaSource = source,
                    MediaSourceId = source.Id,
                    PlaybackPositionTicks = positionTicks
                });

                // LastPlayedDate drives "most recent", and UtcNow resolution is coarse enough
                // that two events in the same millisecond could tie.
                Thread.Sleep(15);
            }

            public void StopCompleted(Movie movie, MediaSourceInfo source)
            {
                _sessions.RaiseStopped(new PlaybackStopEventArgs
                {
                    Item = movie,
                    Users = new List<PlaybackUser> {_user},
                    MediaSource = source,
                    MediaSourceId = source.Id,
                    PlayedToCompletion = true
                });

                Thread.Sleep(15);
            }

            public VersionPlaybackInfo Get(Movie movie, string mediaSourceId)
            {
                return _api.GetVersionPlayback(_user.InternalId, movie)
                    .FirstOrDefault(e => string.Equals(e.MediaSourceId, mediaSourceId,
                        StringComparison.Ordinal));
            }
        }

        private sealed class FakeSessionManager : ISessionManager
        {
            public event EventHandler<PlaybackProgressEventArgs> PlaybackStart;
            public event EventHandler<PlaybackProgressEventArgs> PlaybackProgress;
            public event EventHandler<PlaybackStopEventArgs> PlaybackStopped;

            public void RaiseProgress(PlaybackProgressEventArgs args)
            {
                PlaybackProgress?.Invoke(this, args);
            }

            public void RaiseStopped(PlaybackStopEventArgs args)
            {
                PlaybackStopped?.Invoke(this, args);
            }

            // Referenced by the interface; kept so the shape matches the real manager.
            public void RaiseStarted(PlaybackProgressEventArgs args)
            {
                PlaybackStart?.Invoke(this, args);
            }
        }
    }
}
