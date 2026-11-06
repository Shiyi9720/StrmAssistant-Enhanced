using System;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public abstract class PatchBase<T> where T : PatchBase<T>
    {
        public PatchTracker PatchTracker;

        public static T Instance { get; private set; }

        protected PatchBase()
        {
            Instance = (T)this;
            PatchTracker = new PatchTracker(typeof(T), PatchApproach.Harmony);
        }

        protected void Initialize()
        {
            try
            {
                OnInitialize();
            }
            catch (Exception e)
            {
                // Locating the target methods is the first thing that can go wrong after an Emby
                // update; report the exception even outside DebugMode so the reason is in the log.
                var reason = e.GetType().Name + (string.IsNullOrEmpty(e.Message) ? string.Empty : ": " + e.Message);
                PatchTracker.FailureReason = reason;
                PatchTracker.FallbackPatchApproach = PatchApproach.None;

                Plugin.Instance.Logger.Warn($"{PatchTracker.PatchType.Name} Init Failed: {reason}");

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            if (PatchTracker.FallbackPatchApproach == PatchApproach.None) return;

            if (HarmonyMod is null) PatchTracker.FallbackPatchApproach = PatchApproach.None;
        }

        protected abstract void OnInitialize();

        protected abstract void Prepare(bool apply);

        public void Patch()
        {
            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony) return;

            if (!IsModOperational)
            {
                Plugin.Instance.Logger.Warn(
                    $"{PatchTracker.PatchType.Name} Patch skipped: the mod engine is unavailable " +
                    $"({ModFailureReason})");
                return;
            }

            Prepare(true);

            // Prepare() applies the whole set atomically and already rolled back the
            // patches that were installed before the failure; this is a second, harmless
            // pass that makes sure nothing is left behind.
            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                // A rolled back patch set means the exclusive feature is not running. Report it
                // as unavailable so the settings page disables it instead of showing it as ON.
                MarkUnavailable($"{PatchTracker.PatchType.Name} patch set failed: " +
                                PatchTracker.FailureReason);
                Plugin.Instance.Logger.Warn(
                    $"{PatchTracker.PatchType.Name} Patch Failed: individual target(s) failed and " +
                    $"the set was rolled back ({PatchTracker.FailureReason})");
                Prepare(false);
            }
        }

        public void Unpatch()
        {
            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony) return;

            Prepare(false);
        }
    }
}
