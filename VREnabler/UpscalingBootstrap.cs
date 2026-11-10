using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VRPatcher
{
    /// <summary>
    /// Loads VRMod.Upscaling.Native.dll before the game creates its graphics device.
    ///
    /// DLSS runs through NVIDIA Streamline, which has to see the Direct3D device and the swap
    /// chain being created and every Present call. Those hooks can only be installed this early,
    /// so the patcher does it; the plugin later binds the same, already loaded library.
    /// The library is the native part of Risk of Rain Upscaling by Xander Frangos (MIT), built
    /// from the NativeUpscaling folder of this repository.
    /// </summary>
    internal static class UpscalingBootstrap
    {
        internal const string NativeFileName = "VRMod.Upscaling.Native.dll";
        internal const string LogFolderName = "VRMod-Upscaling";
        // The standalone mod installs the same graphics hooks. Two copies in one process would
        // both wrap the swap chain, so the VR mod stands down when it is present.
        internal const string StandalonePreloader = "RiskOfRainUpscaling.Preloader.dll";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int InitializeDelegate(string logs, string runtimes, string startupStage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void IntAction(int value);

        internal static void Run(ManualLogSource log)
        {
            try
            {
                string patcherFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string native = Path.Combine(patcherFolder, NativeFileName);
                if (!File.Exists(native))
                {
                    log.LogInfo($"[VR upscaling] {NativeFileName} is not installed next to the patcher; upscaling stays unavailable.");
                    return;
                }

                // Read only: the plugin owns VRMod.cfg and writes it later.
                var config = new ConfigFile(Path.Combine(Paths.ConfigPath, "VRMod.cfg"), false) { SaveOnConfigSet = false };
                bool load = config.Bind("Upscaling", "Load upscaling library", true, "").Value;
                string level = config.Bind("Upscaling", "Upscaling log level", "Info", "").Value;
                if (!load)
                {
                    log.LogInfo("[VR upscaling] 'Load upscaling library' is off in VRMod.cfg; the library is not loaded and upscaling stays unavailable.");
                    return;
                }

                string standalone = FindFile(Paths.PatcherPluginPath, StandalonePreloader);
                if (standalone != null)
                {
                    log.LogWarning("[VR upscaling] The standalone Risk of Rain Upscaling mod is installed (" + standalone +
                        "). Its graphics hooks and the VR mod's cannot run together, so VR upscaling stays off. Disable that mod to use the upscaling settings in the VR tab.");
                    return;
                }

                string runtimes = FindRuntimeFolder();
                if (runtimes == null)
                {
                    log.LogWarning("[VR upscaling] The NVIDIA runtime folder (upscaling\\D3D11 next to VRMod.dll) was not found; DLSS will be unavailable and FSR 3 is used instead.");
                    runtimes = Path.Combine(patcherFolder, "upscaling");
                }

                IntPtr module = LoadLibraryEx(Path.GetFullPath(native), IntPtr.Zero, 0x100 | 0x800);
                if (module == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Native library load failed: " + native);

                var setLevel = Bind<IntAction>(module, "SetLoggingLevel");
                var initialize = Bind<InitializeDelegate>(module, "Initialize");
                setLevel(ParseLevel(level));
                string logs = Path.Combine(Paths.BepInExRootPath, LogFolderName);
                int result = initialize(logs, runtimes, "preloader");
                if (result <= 0)
                    log.LogWarning("[VR upscaling] No early graphics hooks were installed (result " + result + "); DLSS will be unavailable. See " + Path.Combine(logs, "native.log"));
                else
                    log.LogInfo("[VR upscaling] Early graphics hooks installed (" + result + " import slots). Runtime folder: " + runtimes + ". Native log: " + Path.Combine(logs, "native.log"));
            }
            catch (Exception e)
            {
                log.LogError("[VR upscaling] Startup failed; the game continues without upscaling. " + e);
            }
        }

        private static T Bind<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr pointer = GetProcAddress(module, "RiskOfRainUpscaling_" + name);
            if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException("RiskOfRainUpscaling_" + name);
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        // 0 Off, 1 Error, 2 Warning, 3 Info, 4 Debug (the native library's own values).
        private static int ParseLevel(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "off": return 0;
                case "error": return 1;
                case "warning": return 2;
                case "debug": return 4;
                default: return 3;
            }
        }

        private static string FindFile(string root, string name)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                string[] matches = Directory.GetFiles(root, name, SearchOption.AllDirectories);
                return matches.Length > 0 ? matches[0] : null;
            }
            catch { return null; }
        }

        // The runtime folder ships in the plugin half of the package: <folder of VRMod.dll>\upscaling.
        // Mod managers rename the package folder, so find it by the assembly.
        private static string FindRuntimeFolder()
        {
            try
            {
                foreach (string plugin in Directory.GetFiles(Paths.PluginPath, "VRMod.dll", SearchOption.AllDirectories))
                {
                    string folder = Path.Combine(Path.GetDirectoryName(plugin), "upscaling");
                    if (File.Exists(Path.Combine(folder, "D3D11", "sl.interposer.dll"))) return folder;
                }
            }
            catch { }
            return null;
        }
    }
}
