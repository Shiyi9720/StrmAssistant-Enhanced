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
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Plugin.Instance.Logger.Warn($"{PatchTracker.PatchType.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (PatchTracker.FallbackPatchApproach == PatchApproach.None) return;

            if (HarmonyMod is null) PatchTracker.FallbackPatchApproach = PatchApproach.None;
        }

        protected abstract void OnInitialize();

        protected abstract void Prepare(bool apply);

        public void Patch()
        {
            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony) return;

            Prepare(true);

            // A patch failure halfway leaves the earlier patches installed. Roll them back so
            // Emby keeps its native extraction path instead of running with a partial takeover.
            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                Plugin.Instance.Logger.Warn($"{PatchTracker.PatchType.Name} Patch Failed, rolled back");
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
