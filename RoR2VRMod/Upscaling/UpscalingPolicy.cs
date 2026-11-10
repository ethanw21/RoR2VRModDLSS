using System;

namespace VRMod
{
    // Native code maps these values to sl::DLSSMode; keep the order stable.
    internal enum UpscalingQuality { DLAA, Quality, Balanced, Performance, UltraPerformance }

    // Native code: 0 Auto, 1 DLSS, 2 FSR 3/4 on D3D12 (not used in VR), 3 FSR 3 on D3D11.
    internal enum UpscalingBackend { Auto = 0, DLSS = 1, FSR3 = 3 }

    // DLSS model for every quality mode. Default keeps NVIDIA's own choice per mode.
    internal enum UpscalingDlssPreset { Default, K, M, L, J, E }

    [Flags]
    internal enum UpscalingResetReason { None = 0, FirstFrame = 1, FrameGap = 2, TimeGap = 4, PositionJump = 8, RotationJump = 16 }

    /// <summary>
    /// Render sizes, jitter and history rules. Ported from Risk of Rain Upscaling
    /// (Xander Frangos, MIT): Upscaling/DlssPolicy.cs and Upscaling/DlaaHistoryPolicy.cs.
    /// </summary>
    internal static class UpscalingPolicy
    {
        internal const int NvidiaVendorId = 0x10DE;

        internal static readonly string[] QualityNames = { "DLAA", "Quality", "Balanced", "Performance", "Ultra Performance" };
        internal static readonly string[] BackendNames = { "Auto", "DLSS", "FSR 3" };
        internal static readonly string[] PresetNames = { "Default", "K", "M", "L", "J", "E" };

        internal static UpscalingQuality ParseQuality(string value)
        {
            for (int i = 0; i < QualityNames.Length; i++)
                if (string.Equals(QualityNames[i], value, StringComparison.OrdinalIgnoreCase)) return (UpscalingQuality)i;
            return UpscalingQuality.Quality;
        }

        internal static UpscalingBackend ParseBackend(string value)
        {
            if (string.Equals(value, "DLSS", StringComparison.OrdinalIgnoreCase)) return UpscalingBackend.DLSS;
            if (string.Equals(value, "FSR 3", StringComparison.OrdinalIgnoreCase)) return UpscalingBackend.FSR3;
            return UpscalingBackend.Auto;
        }

        internal static UpscalingDlssPreset ParsePreset(string value)
        {
            for (int i = 0; i < PresetNames.Length; i++)
                if (string.Equals(PresetNames[i], value, StringComparison.OrdinalIgnoreCase)) return (UpscalingDlssPreset)i;
            return UpscalingDlssPreset.Default;
        }

        // The runtime that sizes the render targets. The native library makes the real choice
        // for Auto (DLSS on NVIDIA cards when the early hooks are in and DLSS is supported,
        // FSR 3 otherwise); once it has chosen, autoKind carries that choice (1 DLSS, 3 FSR 3).
        internal static UpscalingBackend Expected(UpscalingBackend requested, int autoKind, bool nvidia, bool earlyHooks)
        {
            if (requested != UpscalingBackend.Auto) return requested;
            if (autoKind == 1) return UpscalingBackend.DLSS;
            if (autoKind == 2 || autoKind == 3) return UpscalingBackend.FSR3;
            return nvidia && earlyHooks ? UpscalingBackend.DLSS : UpscalingBackend.FSR3;
        }

        // NVIDIA's fixed per-axis render ratios. FSR's differ only at Balanced (1.7x).
        internal static float Scale(UpscalingQuality quality, UpscalingBackend backend)
        {
            switch (quality)
            {
                case UpscalingQuality.Quality: return 2f / 3f;
                case UpscalingQuality.Balanced: return backend == UpscalingBackend.FSR3 ? 1f / 1.7f : 0.58f;
                case UpscalingQuality.Performance: return 0.5f;
                case UpscalingQuality.UltraPerformance: return 1f / 3f;
                default: return 1f;
            }
        }

        internal static int Dimension(int output, UpscalingQuality quality, UpscalingBackend backend)
        {
            if (output < 1) throw new ArgumentOutOfRangeException(nameof(output));
            return Math.Max(1, (int)Math.Round(output * (double)Scale(quality, backend)));
        }

        // The sl::DLSSPreset value the native library passes for every quality mode.
        internal static int NativePreset(UpscalingDlssPreset preset)
        {
            switch (preset)
            {
                case UpscalingDlssPreset.K: return 11;
                case UpscalingDlssPreset.M: return 13;
                case UpscalingDlssPreset.L: return 12;
                case UpscalingDlssPreset.J: return 10;
                case UpscalingDlssPreset.E: return 5;
                default: return 0;
            }
        }

        // DLSS guide 3.7: 8 base phases scaled by the output/render pixel-area ratio.
        internal static int JitterPhases(int render, int output)
        {
            double ratio = (double)output / render;
            return Math.Max(8, (int)Math.Ceiling(8 * ratio * ratio - 1e-6));
        }

        // DLSS guide 3.5: log2(render/display) - 1 + epsilon; the caller supplies the offset.
        internal static float MipBias(int render, int output, float offset)
        {
            return render >= output ? 0 : (float)(Math.Log((double)render / output, 2) + offset);
        }

        // History is dropped when the previous frame cannot be reprojected onto this one: a
        // teleport, a camera cut, a pause, or a snap turn (30 degrees at the smallest setting, so
        // the limit sits under it). Head movement stays far inside these limits per frame.
        internal const float ResetDistance = 5f, ResetAngle = 25f, ResetSeconds = 0.5f;

        internal static UpscalingResetReason ResetReasons(int previousFrame, int frame, float elapsed, float distance, float angle)
        {
            if (previousFrame == 0) return UpscalingResetReason.FirstFrame;
            UpscalingResetReason reason = UpscalingResetReason.None;
            if (frame != previousFrame + 1) reason |= UpscalingResetReason.FrameGap;
            if (elapsed > ResetSeconds) reason |= UpscalingResetReason.TimeGap;
            if (distance > ResetDistance) reason |= UpscalingResetReason.PositionJump;
            if (angle > ResetAngle) reason |= UpscalingResetReason.RotationJump;
            return reason;
        }

        internal static float Halton(int index, int radix)
        {
            float result = 0, fraction = 1;
            while (index > 0) { fraction /= radix; result += fraction * (index % radix); index /= radix; }
            return result;
        }
    }
}
