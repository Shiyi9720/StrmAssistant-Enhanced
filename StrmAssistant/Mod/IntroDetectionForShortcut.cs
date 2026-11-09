using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby 4.10's <c>AudioFingerprintManager.IsIntroDetectionSupported()</c> returns false for
    /// every .strm episode, because <c>BaseItem.IsShortcut</c> is true for a path whose extension
    /// is .strm. As a result the per-season fingerprint list stays empty, the season pipeline has
    /// nothing to compare and no IntroStart/IntroEnd marker is ever written — even though this
    /// plugin can create the .fp files itself.
    ///
    /// This patch only overrides that single predicate, and only for episodes that live in a
    /// library the plugin's IntroSkip feature is scoped to. All other conditions of the original
    /// method (ffmpeg chromaprint support, file protocol, placeholder, runtime, completeness) are
    /// evaluated again by the prefix, so only the shortcut gate is bypassed.
    /// </summary>
    public class IntroDetectionForShortcut : PatchBase<IntroDetectionForShortcut>
    {
        private static MethodInfo _isIntroDetectionSupported;

        public IntroDetectionForShortcut()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                Plugin.Instance.Logger.Warn(
                    $"{nameof(IntroDetectionForShortcut)} is unavailable: {PatchTracker.FailureReason}");
                return;
            }

            if (!IsModOperational)
            {
                Plugin.Instance.Logger.Warn(
                    $"{nameof(IntroDetectionForShortcut)} patch skipped: {ModFailureReason}");
                return;
            }

            // Deliberately not Patch(): Prepare(true) applies the hook and rolls it back on
            // failure, but a broken optional hook must not call MarkUnavailable() and disable
            // the unrelated features that do work.
            Prepare(true);

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                Plugin.Instance.Logger.Warn(
                    $"{nameof(IntroDetectionForShortcut)} patch failed: {PatchTracker.FailureReason}");
            }
            else
            {
                Plugin.Instance.Logger.Info(
                    "Intro detection for .strm episodes is enabled within the IntroSkip library scope");
            }
        }

        protected override void OnInitialize()
        {
            var audioFingerprintManager = Assembly.Load("Emby.Providers")
                .GetType("Emby.Providers.Markers.AudioFingerprintManager");

            _isIntroDetectionSupported = audioFingerprintManager?.GetMethod(
                "IsIntroDetectionSupported", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(Episode), typeof(LibraryOptions) }, null);

            if (_isIntroDetectionSupported is null)
            {
                throw new MissingMethodException(
                    "Emby.Providers.Markers.AudioFingerprintManager.IsIntroDetectionSupported");
            }
        }

        protected override void Prepare(bool apply)
        {
            var spec = new PatchSpec("IsIntroDetectionSupported", _isIntroDetectionSupported,
                prefix: nameof(IsIntroDetectionSupportedPrefix));

            if (apply)
            {
                PatchAll(PatchTracker, new[] { spec });
                return;
            }

            if (spec.Target != null)
            {
                PatchUnpatch(PatchTracker, false, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler,
                    spec.Finalizer, true);
            }
        }

        /// <summary>
        /// Harmony prefix. Returns false (and sets <c>__result</c>) only for a .strm episode that
        /// is in the plugin's IntroSkip scope; every other call falls through to Emby unchanged.
        /// </summary>
        private static bool IsIntroDetectionSupportedPrefix(Episode item, LibraryOptions libraryOptions,
            ref bool __result)
        {
            if (!ShouldTreatShortcutAsSupported(item)) return true;

            // Emby throws for a null LibraryOptions, but a shortcut returns false before reaching
            // that check; letting the original run keeps that exact behaviour.
            if (libraryOptions is null) return true;

            // If chromaprint is unavailable Emby returns false itself; defer to the original.
            if (Plugin.Instance?.FfmpegManager?.FfmpegCapabilities?.SupportsChromaprint != true) return true;

            // The remaining checks of IsIntroDetectionSupported, in the same order, minus
            // IsShortcut. Repeating them means only the shortcut gate is bypassed.
            if (!item.IsFileProtocol || item.IsPlaceHolder || !item.RunTimeTicks.HasValue ||
                !item.IsCompleteMedia ||
                item.RunTimeTicks.Value < TimeSpan.FromMinutes(
                    GetTitleFingerprintDurationMinutes(libraryOptions)).Ticks)
            {
                __result = false;
                return false;
            }

            __result = true;
            return false;
        }

        private static bool ShouldTreatShortcutAsSupported(BaseItem item)
        {
            if (item is null || !item.IsShortcut) return false;

            var libraryPaths = FingerprintApi.LibraryPathsInScope;

            return libraryPaths != null && libraryPaths.Count > 0 &&
                   !string.IsNullOrEmpty(item.Path) &&
                   libraryPaths.Any(path => !string.IsNullOrEmpty(path) && item.Path.StartsWith(path));
        }

        /// <summary>
        /// Mirrors Emby's private AudioFingerprintManager.GetTitleFingerprintDurationMinutes():
        /// a configured value outside [2, 30) means the default of 10 minutes.
        /// </summary>
        private static int GetTitleFingerprintDurationMinutes(LibraryOptions libraryOptions)
        {
            var minutes = libraryOptions.IntroDetectionFingerprintLength;

            return minutes < 2 || minutes >= 30 ? 10 : minutes;
        }
    }
}
