using System;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace VRMod
{
    /// <summary>
    /// Marks for the native library's GPU profiler. While profiling is on, the library times every
    /// stretch of GPU work between two marks of a frame, next to its own steps, and writes the
    /// averages to BepInEx\VRMod-Upscaling\profile.log. While it is off a mark does nothing.
    /// </summary>
    internal static class UpscalingProfiler
    {
        private const int Instant = 2; // the library's event kinds: 0 begin, 1 end, 2 instant
        private static readonly Dictionary<string, int> labels = new Dictionary<string, int>();

        /// <summary>The loaded native library has the profiler.</summary>
        internal static bool Available => UpscalingNative.ProfilerSetEnabled != null && UpscalingNative.ProfilerSetContext != null &&
                                          UpscalingNative.ProfilerRegister != null && UpscalingNative.ProfilerGetEvent != null;

        /// <summary>Profiling is switched on right now (Upscaling.SetProfiling).</summary>
        internal static bool Active;

        /// <summary>Adds a mark to a command buffer. The label is fixed when the buffer is built.</summary>
        internal static void Mark(CommandBuffer commands, string label)
        {
            if (!Available) return;
            if (!labels.TryGetValue(label, out int id))
            {
                id = UpscalingNative.ProfilerRegister(label);
                labels[label] = id;
            }
            if (id < 0) return;
            commands.IssuePluginEventAndData(UpscalingNative.ProfilerGetEvent(), (id << 2) | Instant, IntPtr.Zero);
        }
    }
}
