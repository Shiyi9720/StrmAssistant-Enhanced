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
        public PatchTracker(Type patchType, PatchApproach defaultApproach)
        {
            PatchType = patchType;
            DefaultPatchApproach = defaultApproach;
            FallbackPatchApproach = defaultApproach;

            PatchManager.PatchTrackerList.Add(this);
        }

        public Type PatchType { get; set; }

        public PatchApproach DefaultPatchApproach { get; }

        public PatchApproach FallbackPatchApproach { get; set; }

        public bool IsSupported { get; set; } = true;
    }
}
