using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VRMod
{
    /// <summary>
    /// Bindings for VRMod.Upscaling.Native.dll (the native library of Risk of Rain Upscaling by
    /// Xander Frangos, MIT, built from the NativeUpscaling folder). Adapted from that mod's
    /// Shared/NativeApi.cs; only the exports the VR mod uses are bound. The exported names keep
    /// their upstream RiskOfRainUpscaling_ prefix.
    /// </summary>
    internal static class UpscalingNative
    {
        internal const string FileName = "VRMod.Upscaling.Native.dll";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal delegate int InitializeDelegate(string logs, string runtimes, string startupStage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr EventGetter();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int IntQuery();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void IntAction(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int IntSetter(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int FloatSetter(float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ContextQuery(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ContextAction(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr SceneInputsCreateDelegate(ref SceneInputsDescription description);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr UpscalerCreateDelegate(ref UpscalerDescription description);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int UpscalerSubmitDelegate(IntPtr context, ref CameraFrame frame);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Matrix16
        {
            internal float A0, A1, A2, A3, B0, B1, B2, B3, C0, C1, C2, C3, D0, D1, D2, D3;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SceneInputsDescription
        {
            internal uint Size, Version;
            internal int Width, Height;
            internal IntPtr Depth;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UpscalerDescription
        {
            internal uint Size, Version;
            internal int Width, Height;
            internal IntPtr SceneInputs, Color, Depth, Motion, Output;
            // Quality: 0 DLAA, 1 Quality, 2 Balanced, 3 Performance, 4 Ultra Performance.
            // Placement: 0 end of effects, 1 before post-processing.
            internal int OutputWidth, OutputHeight, Quality, Placement;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CameraFrame
        {
            internal uint Size, Version;
            internal int Frame, Reset;
            internal Matrix16 Projection, InverseProjection, ClipToPrevious, PreviousToClip;
            internal float JitterX, JitterY, MotionScaleX, MotionScaleY, Near, Far, Fov, Aspect;
            internal float PosX, PosY, PosZ, UpX, UpY, UpZ, RightX, RightY, RightZ, ForwardX, ForwardY, ForwardZ;
            internal int ReversedDepth;
        }

        internal static InitializeDelegate Initialize;
        internal static IntAction SetLoggingLevel;
        internal static IntQuery GetObservedDeviceCount;
        internal static SceneInputsCreateDelegate SceneInputsCreate;
        internal static EventGetter SceneInputsGetEvent, SceneInputsGetFrameEvent;
        internal static ContextQuery SceneInputsState;
        internal static ContextAction SceneInputsDestroy;
        internal static UpscalerCreateDelegate UpscalerCreate;
        internal static UpscalerSubmitDelegate UpscalerSubmitFrame;
        internal static EventGetter UpscalerGetEvaluateEvent;
        internal static ContextQuery UpscalerState, UpscalerKind;
        internal static ContextAction UpscalerDestroy;
        internal static IntSetter SetUpscalerBackend, SetDlssPreset;
        internal static FloatSetter SetSharpness;

        private static IntPtr module;
        internal static bool Loaded => module != IntPtr.Zero;

        /// <summary>1 DLSS, 2 FSR 3/4 (D3D12), 3 FSR 3 (D3D11), 0 not chosen yet.</summary>
        internal static string UpscalerName(int kind)
        {
            switch (kind)
            {
                case 1: return "DLSS";
                case 2: return "FSR 3/4 (D3D12)";
                case 3: return "FSR 3";
                default: return "pending";
            }
        }

        internal static void Load(string path)
        {
            if (module != IntPtr.Zero) return;
            // The patcher loaded this module before the graphics device existed; this returns the same one.
            IntPtr handle = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 0x100 | 0x800);
            if (handle == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Native library load failed: " + path);
            try
            {
                Bind(handle, out Initialize, nameof(Initialize));
                Bind(handle, out SetLoggingLevel, nameof(SetLoggingLevel));
                Bind(handle, out GetObservedDeviceCount, nameof(GetObservedDeviceCount));
                Bind(handle, out SceneInputsCreate, nameof(SceneInputsCreate));
                Bind(handle, out SceneInputsGetEvent, nameof(SceneInputsGetEvent));
                Bind(handle, out SceneInputsGetFrameEvent, nameof(SceneInputsGetFrameEvent));
                Bind(handle, out SceneInputsState, nameof(SceneInputsState));
                Bind(handle, out SceneInputsDestroy, nameof(SceneInputsDestroy));
                Bind(handle, out UpscalerCreate, nameof(UpscalerCreate));
                Bind(handle, out UpscalerSubmitFrame, nameof(UpscalerSubmitFrame));
                Bind(handle, out UpscalerGetEvaluateEvent, nameof(UpscalerGetEvaluateEvent));
                Bind(handle, out UpscalerState, nameof(UpscalerState));
                Bind(handle, out UpscalerKind, nameof(UpscalerKind));
                Bind(handle, out UpscalerDestroy, nameof(UpscalerDestroy));
                Bind(handle, out SetUpscalerBackend, nameof(SetUpscalerBackend));
                Bind(handle, out SetDlssPreset, nameof(SetDlssPreset));
                Bind(handle, out SetSharpness, nameof(SetSharpness));
            }
            catch
            {
                // Never FreeLibrary: startup hooks and queued render events need this module.
                throw;
            }
            module = handle;
        }

        private static void Bind<T>(IntPtr handle, out T field, string name) where T : Delegate
        {
            IntPtr pointer = GetProcAddress(handle, "RiskOfRainUpscaling_" + name);
            if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException("RiskOfRainUpscaling_" + name);
            field = Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
    }
}
