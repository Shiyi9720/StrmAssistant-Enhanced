using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistant.Options;
using System;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Applies the configurable intro marker bounds (minimum/maximum duration and padding) to the
    /// markers written by intro detection and by IntroDb. The defaults match the previous
    /// hardcoded values, so an unchanged configuration keeps the existing behavior.
    /// </summary>
    internal static class IntroMarkerHelper
    {
        internal static bool TryNormalizeRange(BaseItem item, IntroSkipOptions options, long startTicks,
            long endTicks, out long normalizedStartTicks, out long normalizedEndTicks)
        {
            var minTicks = TimeSpan.FromSeconds(Math.Max(1, options?.IntroMarkerMinDurationSeconds ?? 5)).Ticks;
            var maxTicks = TimeSpan.FromSeconds(Math.Max(1, options?.IntroMarkerMaxDurationSeconds ?? 600)).Ticks;
            var paddingTicks = TimeSpan.FromSeconds(Math.Max(0, options?.IntroMarkerPaddingSeconds ?? 0)).Ticks;

            normalizedStartTicks = Math.Max(0, startTicks - paddingTicks);
            normalizedEndTicks = endTicks + paddingTicks;

            var runtimeTicks = item?.RunTimeTicks;
            if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
            {
                normalizedEndTicks = Math.Min(normalizedEndTicks, runtimeTicks.Value);
            }

            if (normalizedEndTicks <= normalizedStartTicks) return false;

            var durationTicks = normalizedEndTicks - normalizedStartTicks;
            if (durationTicks < minTicks) return false;

            if (durationTicks > maxTicks)
            {
                normalizedEndTicks = normalizedStartTicks + maxTicks;
            }

            return true;
        }

        /// <summary>
        /// Reads the intro markers of an episode, discards them when they are out of the configured
        /// bounds and trims or pads them otherwise. Nothing is saved when the markers are already
        /// within bounds, so the detection results are left untouched for the default options.
        /// </summary>
        internal static bool NormalizeEpisodeMarkers(Episode episode, IItemRepository itemRepository,
            IntroSkipOptions options, ILogger logger)
        {
            var chapters = itemRepository.GetChapters(episode);

            var introStart = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
            var introEnd = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

            if (introStart == null && introEnd == null) return false;

            if (introStart == null || introEnd == null ||
                !TryNormalizeRange(episode, options, introStart.StartPositionTicks,
                    introEnd.StartPositionTicks, out var startTicks, out var endTicks))
            {
                chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart ||
                                        c.MarkerType == MarkerType.IntroEnd);
                itemRepository.SaveChapters(episode.InternalId, chapters);
                logger?.Debug("IntroMarkerFilter - Discarded out-of-range intro marker: " + episode.Name +
                              " - " + episode.Path);
                return true;
            }

            if (startTicks == introStart.StartPositionTicks && endTicks == introEnd.StartPositionTicks)
            {
                return false;
            }

            introStart.StartPositionTicks = startTicks;
            introEnd.StartPositionTicks = endTicks;
            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
            itemRepository.SaveChapters(episode.InternalId, chapters);
            logger?.Debug("IntroMarkerFilter - Adjusted intro marker: " + episode.Name + " - " + episode.Path);

            return true;
        }
    }
}
