using System;

namespace StrmAssistant.Mod
{
    public enum PatchApproach
    {
        None,
        Reflection,
        Harmony
    }

    public class PatchTracker
    {
        public PatchTracker(Type patchType)
        {
            PatchType = patchType;

            PatchManager.PatchTrackerList.Add(this);
        }

        public Type PatchType { get; set; }

        public PatchApproach FallbackPatchApproach { get; set; } = PatchApproach.Harmony;

        public bool IsSupported { get; set; } = true;
    }
}
