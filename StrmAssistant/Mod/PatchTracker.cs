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

        public Type PatchType { get; }

        public PatchApproach DefaultPatchApproach { get; }

        public PatchApproach FallbackPatchApproach { get; set; }

        public bool IsSupported { get; set; } = true;

        /// <summary>
        /// Name(s) of the patch point(s) that failed, kept for logging and for the
        /// settings page so a broken mod is never presented as a working one.
        /// </summary>
        public string FailureReason { get; set; } = string.Empty;
    }
}
