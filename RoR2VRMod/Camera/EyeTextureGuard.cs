using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;

namespace VRMod
{
    /// <summary>
    /// Sits between Unity's OpenXR plugin and the headset's runtime for the three calls that decide
    /// the eye textures: the recommended size, the list of formats and the creation itself.
    ///
    /// It records what Unity asked for and what the runtime answered. When the runtime refuses the
    /// request, it tries the same request with other usage flags, other formats and smaller sizes,
    /// and steers Unity's next attempt to the best combination the runtime accepts. Unity retries
    /// the creation every frame and asks for the size and the formats again each time, so the
    /// next attempt picks the change up without a restart.
    ///
    /// Why: with the Pimax OpenXR runtime (Pimax Dream Air, runtime 0.1.0) every xrCreateSwapchain
    /// came back XR_ERROR_FEATURE_UNSUPPORTED for the size and format the runtime itself had
    /// recommended, and the headset stayed black. Which part of the request that runtime objects
    /// to is not known; this class finds out on the machine that has the problem.
    ///
    /// When the runtime refuses every kind of request, the request is not the cause. The guard
    /// then tries once with the session started first (Unity asks for the eye textures before
    /// xrBeginSession), and the log gets the start-up record (OpenXRJournal) and the end of the
    /// runtime's own log files (OpenXRSurroundings).
    ///
    /// While the runtime accepts what Unity asks for, every call is passed through unchanged.
    /// xrCreateSwapchain and the two enumerations are called on Unity's render thread, so nothing
    /// here touches Unity objects from those calls, and messages are queued for the main thread.
    /// </summary>
    public sealed class EyeTextureGuard : OpenXRFeature
    {
        private const int XrSizeInsufficient = -11;
        private const ulong UsageColor = 0x1, UsageTransferSrc = 0x8, UsageTransferDst = 0x10, UsageSampled = 0x20, UsageMutable = 0x40;
        private const int CreateInfoSize = 64, ViewSize = 40;

        // Sizes tried, largest first, as a share of what Unity asked for.
        private static readonly float[] Ladder = { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f, 0.25f, 0.2f, 0.15f };

        // Direct3D formats that hold depth, and their typeless forms. Everything else is colour.
        private static readonly HashSet<long> DepthFormats = new HashSet<long> { 19, 20, 39, 40, 44, 45, 53, 55 };
        private static readonly HashSet<long> SrgbFormats = new HashSet<long> { 29, 91, 93 };

        [StructLayout(LayoutKind.Sequential)]
        private struct CreateInfo
        {
            public int type;
            public IntPtr next;
            public ulong createFlags;
            public ulong usageFlags;
            public long format;
            public uint sampleCount, width, height, faceCount, arraySize, mipCount;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetProcAddrFn(ulong instance, IntPtr name, IntPtr function);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSwapchainFn(ulong session, IntPtr createInfo, IntPtr swapchain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DestroySwapchainFn(ulong swapchain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateFormatsFn(ulong session, uint capacity, IntPtr countOutput, IntPtr formats);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateViewsFn(ulong instance, ulong systemId, int viewConfigurationType, uint capacity, IntPtr countOutput, IntPtr views);

        // The delegates handed to native code are kept in static fields so they are never collected.
        private static readonly GetProcAddrFn procAddrHook = ProcAddrHook;
        private static readonly CreateSwapchainFn createHook = CreateHook;
        private static readonly EnumerateFormatsFn formatsHook = FormatsHook;
        private static readonly EnumerateViewsFn viewsHook = ViewsHook;
        private static IntPtr procAddrHookPtr, createHookPtr, formatsHookPtr, viewsHookPtr;

        private static GetProcAddrFn nextProcAddr;
        private static CreateSwapchainFn nextCreate;
        private static DestroySwapchainFn nextDestroy;
        private static EnumerateFormatsFn nextFormats;
        private static EnumerateViewsFn nextViews;

        private static readonly object gate = new object();
        private static readonly List<KeyValuePair<bool, string>> pending = new List<KeyValuePair<bool, string>>();

        private static bool fallbackAllowed = true;
        private static volatile float userScale = 1f;      // from the config file
        private static volatile float fallbackScale = 1f;  // found by probing
        private static long forcedFormat;                   // 0 = leave the runtime's list alone
        private static ulong usageOverride;                 // 0 = leave Unity's usage flags alone
        private static long[] runtimeFormats = new long[0];
        private static uint recommendedWidth, recommendedHeight, maximumWidth, maximumHeight;
        private static ulong lastInstance;

        // Refusals in a row before alternatives are tried, so a one-off hiccup changes nothing.
        private const int RefusalsBeforeProbe = 3;

        private static bool probed, loggedCreated, loggedStillRefused, loggedHookFault, loggedModules;
        private static bool refusedEverything, reportArmed, reportWritten, sessionSeen;
        private static DateTime reportDue = DateTime.MaxValue;
        private static string pendingOutcome;
        private static volatile int sessionState;
        private static readonly OpenXRJournal.Entry createEntry = OpenXRJournal.EntryFor("xrCreateSwapchain");
        private static readonly OpenXRJournal.Entry viewsEntry = OpenXRJournal.EntryFor("xrEnumerateViewConfigurationViews");
        private static readonly OpenXRJournal.Entry formatsEntry = OpenXRJournal.EntryFor("xrEnumerateSwapchainFormats");
        private static int refusals, lastRefusalReported, refusalsInARow, lastRefusalResult;
        private static string lastRequest = "";

        /// <summary>True once the runtime has accepted an eye texture in this session.</summary>
        internal static bool Created { get; private set; }

        /// <summary>Called on the main thread before the OpenXR loader starts.</summary>
        internal static void Configure(bool allowFallback, float scale)
        {
            fallbackAllowed = allowFallback;
            userScale = Mathf.Clamp(scale, 0.1f, 1f);
            if (procAddrHookPtr == IntPtr.Zero)
            {
                procAddrHookPtr = Marshal.GetFunctionPointerForDelegate(procAddrHook);
                createHookPtr = Marshal.GetFunctionPointerForDelegate(createHook);
                formatsHookPtr = Marshal.GetFunctionPointerForDelegate(formatsHook);
                viewsHookPtr = Marshal.GetFunctionPointerForDelegate(viewsHook);
            }
        }

        /// <summary>Turns the start-up record on or off. Main thread, before the OpenXR loader starts.</summary>
        internal static void ConfigureReport(bool report)
        {
            OpenXRJournal.Enabled = report;
        }

        /// <summary>
        /// Writes queued messages to the log and to the report file. Main thread only.
        /// final: the game is closing, so write the start-up record now if it is still owed.
        /// </summary>
        internal static void Flush(bool final = false)
        {
            KeyValuePair<bool, string>[] lines;
            string outcome;
            bool report, refused, modules;
            lock (gate)
            {
                if (refusals - lastRefusalReported >= 2000 && !Created)
                {
                    lastRefusalReported = refusals;
                    pending.Add(new KeyValuePair<bool, string>(true, $"The headset's runtime has now refused the eye textures {refusals} times; the headset stays black. Last request: {lastRequest}."));
                }
                outcome = pendingOutcome; pendingOutcome = null;
                report = !reportWritten && ((reportArmed && DateTime.UtcNow >= reportDue) || (final && sessionSeen));
                if (report) reportWritten = true;
                refused = refusedEverything;
                modules = probed && !loggedModules && report;
                if (modules) loggedModules = true;
                lines = pending.ToArray();
                pending.Clear();
            }
            Write(lines);
            if (outcome != null) OpenXRSurroundings.RecordOutcome(outcome);
            if (!report) return;

            List<KeyValuePair<bool, string>> more = new List<KeyValuePair<bool, string>>();
            string journal = OpenXRJournal.Report();
            if (journal.Length > 0) more.Add(new KeyValuePair<bool, string>(false, journal));
            if (refused && OpenXRJournal.Enabled)
            {
                string runtime = "";
                try { runtime = UnityEngine.XR.OpenXR.OpenXRRuntime.name; } catch { }
                more.Add(new KeyValuePair<bool, string>(false, OpenXRSurroundings.RuntimeLogExcerpts(runtime)));
            }
            if (modules) more.Add(new KeyValuePair<bool, string>(false, Modules()));
            Write(more.ToArray());
        }

        private static void Write(KeyValuePair<bool, string>[] lines)
        {
            if (lines.Length == 0) return;
            List<string> forReport = new List<string>();
            foreach (KeyValuePair<bool, string> line in lines)
            {
                if (line.Key) VRMod.StaticLogger.LogWarning("[VR display] " + line.Value);
                else VRMod.StaticLogger.LogInfo("[VR display] " + line.Value);
                forReport.Add((line.Key ? "! " : "  ") + line.Value);
            }
            OpenXRSurroundings.AppendReport(forReport);
        }

        // What is loaded into the game besides Windows itself: wrappers and injected libraries
        // show up here. Listed once, when the runtime has refused a request.
        private static string Modules()
        {
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                List<string> names = new List<string>();
                foreach (System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
                {
                    string path = module.FileName ?? "";
                    if (path.Length == 0 || path.StartsWith("[In Memory]") || (windows.Length > 0 && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))) continue;
                    names.Add(path);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return $"Libraries loaded from outside the Windows folder ({names.Count}):\n    " + string.Join("\n    ", names.ToArray());
            }
            catch (Exception e)
            {
                return "Could not list the loaded libraries: " + e.Message;
            }
        }

        internal static void Say(string message, bool warning = false)
        {
            lock (gate) pending.Add(new KeyValuePair<bool, string>(warning, message));
        }

        // The runtime's own function for a name, past this class's replacements.
        private static IntPtr Lookup(ulong instance, string function)
        {
            if (instance == 0 || nextProcAddr == null) return IntPtr.Zero;
            IntPtr name = Marshal.StringToHGlobalAnsi(function), slot = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(slot, IntPtr.Zero);
                return nextProcAddr(instance, name, slot) < 0 ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(slot);
            }
        }

        public override bool OnInstanceCreate(ulong xrInstance)
        {
            try
            {
                if (xrInstance != 0) lastInstance = xrInstance;
                OpenXRJournal.StartMessages(xrInstance, Lookup);
            }
            catch (Exception e)
            {
                Say("Could not ask the runtime for its own messages: " + e.Message);
            }
            return true;
        }

        public override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            try
            {
                if (func == IntPtr.Zero || procAddrHookPtr == IntPtr.Zero) return func;
                OpenXRJournal.Forget();
                nextProcAddr = (GetProcAddrFn)Marshal.GetDelegateForFunctionPointer(func, typeof(GetProcAddrFn));
                return procAddrHookPtr;
            }
            catch (Exception e)
            {
                Say("Could not watch the eye texture calls: " + e.Message, true);
                return func;
            }
        }

        public override void OnSessionStateChange(int oldState, int newState)
        {
            sessionState = newState;
            Say($"OpenXR session state {StateName(oldState)} -> {StateName(newState)}.");
        }

        public override void OnSessionCreate(ulong xrSession)
        {
            // A new session starts from what Unity asks for again.
            lock (gate)
            {
                probed = false; loggedCreated = false; loggedStillRefused = false;
                refusals = 0; lastRefusalReported = 0; refusalsInARow = 0; lastRefusalResult = 0; Created = false;
                forcedFormat = 0; usageOverride = 0; fallbackScale = 1f;
                refusedEverything = false; reportArmed = false; reportWritten = false; reportDue = DateTime.MaxValue; sessionSeen = true;
            }
        }

        private static int ProcAddrHook(ulong instance, IntPtr name, IntPtr function)
        {
            int result = nextProcAddr(instance, name, function);
            try
            {
                if (instance != 0) lastInstance = instance;
                if (result < 0 || name == IntPtr.Zero || function == IntPtr.Zero) return result;
                IntPtr target = Marshal.ReadIntPtr(function);
                if (target == IntPtr.Zero) return result;
                string text = Marshal.PtrToStringAnsi(name);
                switch (text)
                {
                    case "xrCreateSwapchain":
                        if (target == createHookPtr) break;
                        nextCreate = (CreateSwapchainFn)Marshal.GetDelegateForFunctionPointer(target, typeof(CreateSwapchainFn));
                        Marshal.WriteIntPtr(function, createHookPtr);
                        break;
                    case "xrDestroySwapchain":
                        nextDestroy = (DestroySwapchainFn)Marshal.GetDelegateForFunctionPointer(target, typeof(DestroySwapchainFn));
                        break;
                    case "xrEnumerateSwapchainFormats":
                        if (target == formatsHookPtr) break;
                        nextFormats = (EnumerateFormatsFn)Marshal.GetDelegateForFunctionPointer(target, typeof(EnumerateFormatsFn));
                        Marshal.WriteIntPtr(function, formatsHookPtr);
                        break;
                    case "xrEnumerateViewConfigurationViews":
                        if (target == viewsHookPtr) break;
                        nextViews = (EnumerateViewsFn)Marshal.GetDelegateForFunctionPointer(target, typeof(EnumerateViewsFn));
                        Marshal.WriteIntPtr(function, viewsHookPtr);
                        break;
                    default:
                        // The calls that start the session and the frames are only counted.
                        IntPtr counted = OpenXRJournal.Wrap(instance, text, target);
                        if (counted != IntPtr.Zero) Marshal.WriteIntPtr(function, counted);
                        break;
                }
            }
            catch (Exception e)
            {
                Fault("looking up an OpenXR call", e);
            }
            return result;
        }

        // The runtime's recommended eye size, scaled by the config setting and by a size the
        // probe found. Unity multiplies whatever it reads here by its own render scale.
        private static int ViewsHook(ulong instance, ulong systemId, int viewConfigurationType, uint capacity, IntPtr countOutput, IntPtr views)
        {
            int result = nextViews(instance, systemId, viewConfigurationType, capacity, countOutput, views);
            OpenXRJournal.Note(viewsEntry, result);
            try
            {
                if (result < 0 || capacity == 0 || views == IntPtr.Zero || countOutput == IntPtr.Zero) return result;
                uint count = Math.Min((uint)Marshal.ReadInt32(countOutput), capacity);
                float scale = userScale * fallbackScale;
                for (int i = 0; i < count; i++)
                {
                    IntPtr view = views + i * ViewSize;
                    uint width = (uint)Marshal.ReadInt32(view, 0x10), height = (uint)Marshal.ReadInt32(view, 0x18);
                    if (i == 0)
                    {
                        recommendedWidth = width; recommendedHeight = height;
                        maximumWidth = (uint)Marshal.ReadInt32(view, 0x14); maximumHeight = (uint)Marshal.ReadInt32(view, 0x1c);
                    }
                    if (scale >= 0.999f) continue;
                    Marshal.WriteInt32(view, 0x10, (int)Even(width * scale));
                    Marshal.WriteInt32(view, 0x18, (int)Even(height * scale));
                }
            }
            catch (Exception e)
            {
                Fault("reading the recommended eye size", e);
            }
            return result;
        }

        // The runtime's format list. Once the probe has found that only another colour format is
        // accepted, the list Unity sees holds that one colour format, so Unity picks it.
        private static int FormatsHook(ulong session, uint capacity, IntPtr countOutput, IntPtr formats)
        {
            long forced = forcedFormat;
            long[] known = runtimeFormats;
            if (forced == 0 || known.Length == 0 || countOutput == IntPtr.Zero)
            {
                int result = nextFormats(session, capacity, countOutput, formats);
                OpenXRJournal.Note(formatsEntry, result);
                try
                {
                    if (result >= 0 && capacity > 0 && formats != IntPtr.Zero && countOutput != IntPtr.Zero)
                    {
                        uint count = Math.Min((uint)Marshal.ReadInt32(countOutput), capacity);
                        long[] list = new long[count];
                        Marshal.Copy(formats, list, 0, (int)count);
                        runtimeFormats = list;
                    }
                }
                catch (Exception e)
                {
                    Fault("reading the format list", e);
                }
                return result;
            }

            try
            {
                List<long> shown = new List<long> { forced };
                foreach (long format in known)
                    if (DepthFormats.Contains(format)) shown.Add(format);
                Marshal.WriteInt32(countOutput, shown.Count);
                if (capacity == 0 || formats == IntPtr.Zero) return 0;
                if (capacity < shown.Count) return XrSizeInsufficient;
                Marshal.Copy(shown.ToArray(), 0, formats, shown.Count);
                return 0;
            }
            catch (Exception e)
            {
                Fault("writing the format list", e);
                return nextFormats(session, capacity, countOutput, formats);
            }
        }

        private static int CreateHook(ulong session, IntPtr createInfo, IntPtr swapchain)
        {
            bool called = false;
            int result = 0;
            try
            {
                CreateInfo asked = (CreateInfo)Marshal.PtrToStructure(createInfo, typeof(CreateInfo));
                bool colour = (asked.usageFlags & UsageColor) != 0 && !DepthFormats.Contains(asked.format);
                ulong usage = colour ? usageOverride : 0;

                result = usage != 0 && usage != asked.usageFlags
                    ? CreateWith(session, asked, asked.format, usage, asked.width, asked.height, swapchain)
                    : nextCreate(session, createInfo, swapchain);
                called = true;
                if (!colour) return result;
                OpenXRJournal.Note(createEntry, result);

                if (result >= 0) OnCreated(asked, usage);
                else if (OnRefused(session, asked, result))
                {
                    // A change that needs nothing from Unity (other usage flags, or the session
                    // started first) was enough: this very call can still succeed.
                    ulong retryUsage = usageOverride != 0 ? usageOverride : asked.usageFlags;
                    int retry = CreateWith(session, asked, asked.format, retryUsage, asked.width, asked.height, swapchain);
                    result = retry;
                    if (retry >= 0) OnCreated(asked, retryUsage);
                }
                return result;
            }
            catch (Exception e)
            {
                Fault("handling an eye texture request", e);
                return called ? result : nextCreate(session, createInfo, swapchain);
            }
        }

        private static void OnCreated(CreateInfo asked, ulong usage)
        {
            Created = true;
            refusalsInARow = 0;
            if (loggedCreated) return;
            loggedCreated = true;
            lock (gate)
            {
                pendingOutcome = "accepted";
                // A few seconds later, so the record also shows the first frames.
                if (!reportArmed || refusedEverything) { reportArmed = true; reportDue = DateTime.UtcNow.AddSeconds(4); }
            }
            float scale = userScale * fallbackScale;
            string changes = "";
            if (usage != 0 && usage != asked.usageFlags) changes += $" Usage flags changed from 0x{asked.usageFlags:X} to 0x{usage:X}.";
            if (forcedFormat != 0) changes += $" Format {FormatName(forcedFormat)} was chosen for Unity.";
            if (scale < 0.999f) changes += $" Size is {scale * 100f:0}% of the runtime's recommendation of {recommendedWidth}x{recommendedHeight}" + (fallbackScale < 0.999f ? " because the runtime refused larger ones." : " (config setting).");
            Say($"Eye texture accepted by the runtime: {Describe(asked)}.{changes}", changes.Length > 0 && (fallbackScale < 0.999f || forcedFormat != 0));
        }

        /// <returns>True when the caller can retry the same request at once.</returns>
        private static bool OnRefused(ulong session, CreateInfo asked, int result)
        {
            lock (gate)
            {
                ++refusals;
                lastRequest = Describe(asked);
            }
            if (probed)
            {
                if (!loggedStillRefused && (fallbackScale < 0.999f || forcedFormat != 0 || usageOverride != 0))
                {
                    loggedStillRefused = true;
                    Say($"The runtime still refuses the eye texture after the change ({ResultName(result)}): {Describe(asked)}.", true);
                }
                return false;
            }
            if (result != lastRefusalResult)
            {
                lastRefusalResult = result;
                Say($"The headset's runtime refused the eye texture Unity asked for ({ResultName(result)}): {Describe(asked)}. " +
                    $"Runtime's recommendation {recommendedWidth}x{recommendedHeight}, maximum {maximumWidth}x{maximumHeight}, formats {FormatList(runtimeFormats)}.", true);
            }
            // Only answers about the request itself can be helped by asking differently; a lost
            // or stopped session cannot.
            if (!AboutTheRequest(result) || ++refusalsInARow < RefusalsBeforeProbe) return false;
            probed = true;
            lock (gate)
            {
                pendingOutcome = "refused";
                reportArmed = true; reportDue = DateTime.UtcNow.AddSeconds(1);
            }

            if (!fallbackAllowed)
            {
                Say("Trying alternatives is turned off (Eye texture fallback in VRMod.cfg).", true);
                return false;
            }
            if (nextDestroy == null) FindDestroy();
            if (nextDestroy == null)
            {
                Say("Cannot try alternatives: the call that releases a test texture was not found.", true);
                return false;
            }
            return Probe(session, asked, result);
        }

        // Tries the request with other usage flags and other colour formats, each from full size
        // downwards, and applies the best combination. Refused attempts cost nothing; an accepted
        // one allocates the textures for a moment and releases them again.
        private static bool Probe(ulong session, CreateInfo asked, int result)
        {
            ulong[] usages =
            {
                asked.usageFlags,
                UsageColor,
                asked.usageFlags | UsageMutable,
                asked.usageFlags | UsageTransferSrc | UsageTransferDst,
            };
            List<long> formats = new List<long> { asked.format };
            foreach (long format in runtimeFormats)
                if (!DepthFormats.Contains(format) && !formats.Contains(format)) formats.Add(format);

            bool wantSrgb = SrgbFormats.Contains(asked.format);
            // Best combination in Unity's colour encoding, and best in the other encoding.
            long[] bestFormat = new long[2]; ulong[] bestUsage = new ulong[2]; float[] bestScale = new float[2];
            StringBuilder table = new StringBuilder();
            foreach (long format in formats)
            {
                int group = SrgbFormats.Contains(format) == wantSrgb ? 0 : 1;
                foreach (ulong usage in usages)
                {
                    bool same = format == asked.format && usage == asked.usageFlags;
                    if (format != asked.format && usage != asked.usageFlags && usage != UsageColor) continue;
                    // Only sizes above the best found so far are worth a try: each accepted one
                    // makes the runtime allocate the textures for a moment.
                    float floor = bestScale[group], accepted = 0f; int lastResult = result;
                    foreach (float step in Ladder)
                    {
                        if (step <= floor + 0.001f) break;
                        if (same && step >= 0.999f) continue; // the request that was just refused
                        lastResult = Try(session, asked, format, usage, Even(asked.width * step), Even(asked.height * step));
                        if (lastResult >= 0) { accepted = step; break; }
                    }
                    table.Append($"\n    format {FormatName(format)}, usage 0x{usage:X}: ");
                    if (accepted > 0f) table.Append($"accepted at {accepted * 100f:0}% ({Even(asked.width * accepted)}x{Even(asked.height * accepted)})");
                    else if (floor > 0f) table.Append($"refused above {floor * 100f:0}% ({ResultName(lastResult)}); smaller sizes not tried");
                    else table.Append($"refused at every size down to {Even(asked.width * Ladder[Ladder.Length - 1])}x{Even(asked.height * Ladder[Ladder.Length - 1])} ({ResultName(lastResult)})");
                    // Largest size wins; at equal size the first one tried stays (Unity's own format and flags come first).
                    if (accepted > bestScale[group] + 0.001f)
                    {
                        bestScale[group] = accepted; bestFormat[group] = format; bestUsage[group] = usage;
                    }
                }
            }

            // Two more answers that help to read the result: a texture array and a small plain texture.
            int array = Try(session, asked, asked.format, asked.usageFlags, asked.width, asked.height, 2);
            int small = Try(session, asked, asked.format, asked.usageFlags, 512, 512);
            int still = Try(session, asked, asked.format, asked.usageFlags, asked.width, asked.height, 0, 0x2);
            table.Append($"\n    same request as a two-layer array: {ResultName(array)}; 512x512 in the same format: {ResultName(small)}; as a static image: {ResultName(still)}");
            if (Array.IndexOf(runtimeFormats, 40L) >= 0)
                table.Append($"; as a depth texture (format 40, usage 0x22): {ResultName(Try(session, asked, 40, 0x22, asked.width, asked.height))}");
            Say("What the runtime accepts instead:" + table, true);

            // Keep Unity's colour encoding unless the other one allows more than twice the size.
            int pick = bestScale[0] > 0f && bestScale[0] * 2f >= bestScale[1] ? 0 : 1;
            float scale = bestScale[pick];
            if (scale <= 0f)
            {
                lock (gate) refusedEverything = true;
                return AfterEverythingRefused(session, asked);
            }
            long chosenFormat = bestFormat[pick];
            ulong chosenUsage = bestUsage[pick];

            // Close in on the largest size the runtime takes, between the accepted step and the refused one above it.
            if (scale < 0.999f)
            {
                float upper = 1f;
                foreach (float step in Ladder)
                    if (step > scale + 0.001f) upper = step;
                for (int i = 0; i < 3; i++)
                {
                    float middle = (scale + upper) * 0.5f;
                    if (Try(session, asked, chosenFormat, chosenUsage, Even(asked.width * middle), Even(asked.height * middle)) >= 0) scale = middle;
                    else upper = middle;
                }
            }

            bool usageOnly = chosenFormat == asked.format && scale >= 0.999f;
            usageOverride = chosenUsage != asked.usageFlags ? chosenUsage : 0;
            if (chosenFormat != asked.format) forcedFormat = chosenFormat;
            if (scale < 0.999f) fallbackScale = scale;
            Say($"Continuing with format {FormatName(chosenFormat)}, usage 0x{chosenUsage:X}, {scale * 100f:0.#}% size ({Even(asked.width * scale)}x{Even(asked.height * scale)}). " +
                (usageOnly ? "Only the usage flags differ from what Unity asked for." : "Unity's next attempt picks this up.") +
                (pick == 1 ? " The colour encoding differs from Unity's choice, so brightness may look wrong." : ""), true);
            return usageOnly && usageOverride != 0;
        }

        // Every kind of request was refused, so the request is not what the runtime objects to.
        // One thing about the session can still be changed from here. Unity asks for the eye
        // textures before it starts the session (xrBeginSession); it only starts the session once
        // the textures exist. OpenXR allows that order, but a runtime may want the session started
        // first. Try that once: start the session, ask again.
        private static bool AfterEverythingRefused(ulong session, CreateInfo asked)
        {
            StringBuilder text = new StringBuilder("The runtime refused every combination tried, so the request itself is not what it objects to.");
            bool helped = false;
            if (!OpenXRJournal.Enabled || !OpenXRJournal.CanBeginSession)
                text.Append(" Starting the session first was not tried (the start-up report is off).");
            else if (OpenXRJournal.SessionBegun)
                text.Append(" The session had already been started when Unity asked, so the order of the calls is not the cause.");
            else if (sessionState != 2)
                text.Append($" Starting the session first was not tried: the session is in state {StateName(sessionState)}, not READY.");
            else
            {
                int? begun = OpenXRJournal.BeginSession(session);
                if (begun == null) text.Append(" Starting the session first was not possible.");
                else if (begun.Value < 0) text.Append($" Starting the session before asking (Unity asks first, then starts it) was refused as well: {ResultName(begun.Value)}.");
                else
                {
                    int again = Try(session, asked, asked.format, asked.usageFlags, asked.width, asked.height);
                    if (again >= 0)
                    {
                        helped = true;
                        text.Append(" WITH THE SESSION STARTED FIRST, THE RUNTIME ACCEPTS THE EYE TEXTURE. Unity asks for the eye textures before it starts the session; this runtime wants the session started first. The mod has started it and carries on.");
                    }
                    else
                    {
                        int small = Try(session, asked, asked.format, asked.usageFlags, 512, 512);
                        text.Append($" Starting the session before asking (Unity asks first, then starts it) went through, but the runtime still refuses: {ResultName(again)} at full size, {ResultName(small)} at 512x512.");
                    }
                }
            }
            if (!helped) text.Append(" The start-up record and the runtime's own log lines follow.");
            Say(text.ToString(), true);
            return helped;
        }

        // Unity normally looks xrDestroySwapchain up itself, which is where it is picked up. This
        // asks the runtime directly in case it has not.
        private static void FindDestroy()
        {
            IntPtr target = Lookup(lastInstance, "xrDestroySwapchain");
            if (target != IntPtr.Zero) nextDestroy = (DestroySwapchainFn)Marshal.GetDelegateForFunctionPointer(target, typeof(DestroySwapchainFn));
        }

        private static int Try(ulong session, CreateInfo asked, long format, ulong usage, uint width, uint height, uint arraySize = 0, ulong createFlags = 0)
        {
            IntPtr handle = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(handle, 0);
                if (arraySize != 0) asked.arraySize = arraySize;
                if (createFlags != 0) asked.createFlags = createFlags;
                int result = CreateWith(session, asked, format, usage, width, height, handle);
                if (result >= 0)
                {
                    ulong created = (ulong)Marshal.ReadInt64(handle);
                    if (created != 0) nextDestroy(created);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        private static int CreateWith(ulong session, CreateInfo asked, long format, ulong usage, uint width, uint height, IntPtr swapchain)
        {
            asked.format = format;
            asked.usageFlags = usage;
            asked.width = width;
            asked.height = height;
            IntPtr copy = Marshal.AllocHGlobal(CreateInfoSize);
            try
            {
                Marshal.StructureToPtr(asked, copy, false);
                return nextCreate(session, copy, swapchain);
            }
            finally
            {
                Marshal.FreeHGlobal(copy);
            }
        }

        private static uint Even(float value)
        {
            return Math.Max(2u, 2u * (uint)Mathf.RoundToInt(value * 0.5f));
        }

        private static void Fault(string what, Exception e)
        {
            if (loggedHookFault) return;
            loggedHookFault = true;
            Say($"Problem while {what}: {e.GetType().Name}: {e.Message}. The call was passed through unchanged.", true);
        }

        private static string Describe(CreateInfo info)
        {
            return $"{info.width}x{info.height}, format {FormatName(info.format)}, usage 0x{info.usageFlags:X}, create flags 0x{info.createFlags:X}, " +
                   $"{info.sampleCount} sample(s), {info.arraySize} layer(s), {info.mipCount} mip level(s), {info.faceCount} face(s)" + (info.next != IntPtr.Zero ? ", with extension data" : "");
        }

        private static string FormatList(long[] formats)
        {
            if (formats == null || formats.Length == 0) return "(not read)";
            StringBuilder list = new StringBuilder();
            foreach (long format in formats) list.Append(list.Length > 0 ? ", " : "").Append(FormatName(format));
            return list.ToString();
        }

        private static string FormatName(long format)
        {
            switch (format)
            {
                case 2: return "2 (RGBA32 float)";
                case 10: return "10 (RGBA16 float)";
                case 24: return "24 (RGB10 A2)";
                case 26: return "26 (RG11 B10 float)";
                case 28: return "28 (RGBA8)";
                case 29: return "29 (RGBA8 sRGB)";
                case 87: return "87 (BGRA8)";
                case 88: return "88 (BGRX8)";
                case 91: return "91 (BGRA8 sRGB)";
                case 93: return "93 (BGRX8 sRGB)";
                case 20: return "20 (depth 32 float + stencil)";
                case 40: return "40 (depth 32 float)";
                case 45: return "45 (depth 24 + stencil)";
                case 55: return "55 (depth 16)";
                default: return format.ToString();
            }
        }

        private static readonly Dictionary<int, string> ResultNames = new Dictionary<int, string>
        {
            { 9, "XR_FRAME_DISCARDED" },
            { 8, "XR_SESSION_NOT_FOCUSED" },
            { 4, "XR_EVENT_UNAVAILABLE" },
            { 3, "XR_SESSION_LOSS_PENDING" },
            { 1, "XR_TIMEOUT_EXPIRED" },
            { 0, "XR_SUCCESS" },
            { -1, "XR_ERROR_VALIDATION_FAILURE" },
            { -2, "XR_ERROR_RUNTIME_FAILURE" },
            { -3, "XR_ERROR_OUT_OF_MEMORY" },
            { -4, "XR_ERROR_API_VERSION_UNSUPPORTED" },
            { -6, "XR_ERROR_INITIALIZATION_FAILED" },
            { -7, "XR_ERROR_FUNCTION_UNSUPPORTED" },
            { -8, "XR_ERROR_FEATURE_UNSUPPORTED" },
            { -9, "XR_ERROR_EXTENSION_NOT_PRESENT" },
            { -10, "XR_ERROR_LIMIT_REACHED" },
            { -11, "XR_ERROR_SIZE_INSUFFICIENT" },
            { -12, "XR_ERROR_HANDLE_INVALID" },
            { -13, "XR_ERROR_INSTANCE_LOST" },
            { -14, "XR_ERROR_SESSION_RUNNING" },
            { -16, "XR_ERROR_SESSION_NOT_RUNNING" },
            { -17, "XR_ERROR_SESSION_LOST" },
            { -18, "XR_ERROR_SYSTEM_INVALID" },
            { -19, "XR_ERROR_PATH_INVALID" },
            { -20, "XR_ERROR_PATH_COUNT_EXCEEDED" },
            { -21, "XR_ERROR_PATH_FORMAT_INVALID" },
            { -22, "XR_ERROR_PATH_UNSUPPORTED" },
            { -23, "XR_ERROR_LAYER_INVALID" },
            { -24, "XR_ERROR_LAYER_LIMIT_EXCEEDED" },
            { -25, "XR_ERROR_SWAPCHAIN_RECT_INVALID" },
            { -26, "XR_ERROR_SWAPCHAIN_FORMAT_UNSUPPORTED" },
            { -27, "XR_ERROR_ACTION_TYPE_MISMATCH" },
            { -28, "XR_ERROR_SESSION_NOT_READY" },
            { -29, "XR_ERROR_SESSION_NOT_STOPPING" },
            { -30, "XR_ERROR_TIME_INVALID" },
            { -31, "XR_ERROR_REFERENCE_SPACE_UNSUPPORTED" },
            { -32, "XR_ERROR_FILE_ACCESS_ERROR" },
            { -33, "XR_ERROR_FILE_CONTENTS_INVALID" },
            { -34, "XR_ERROR_FORM_FACTOR_UNSUPPORTED" },
            { -35, "XR_ERROR_FORM_FACTOR_UNAVAILABLE" },
            { -36, "XR_ERROR_API_LAYER_NOT_PRESENT" },
            { -37, "XR_ERROR_CALL_ORDER_INVALID" },
            { -38, "XR_ERROR_GRAPHICS_DEVICE_INVALID" },
            { -39, "XR_ERROR_POSE_INVALID" },
            { -40, "XR_ERROR_INDEX_OUT_OF_RANGE" },
            { -41, "XR_ERROR_VIEW_CONFIGURATION_TYPE_UNSUPPORTED" },
            { -42, "XR_ERROR_ENVIRONMENT_BLEND_MODE_UNSUPPORTED" },
            { -44, "XR_ERROR_NAME_DUPLICATED" },
            { -45, "XR_ERROR_NAME_INVALID" },
            { -46, "XR_ERROR_ACTIONSET_NOT_ATTACHED" },
            { -47, "XR_ERROR_ACTIONSETS_ALREADY_ATTACHED" },
            { -48, "XR_ERROR_LOCALIZED_NAME_DUPLICATED" },
            { -49, "XR_ERROR_LOCALIZED_NAME_INVALID" },
            { -50, "XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING" },
            { -51, "XR_ERROR_RUNTIME_UNAVAILABLE" },
        };

        internal static string ResultName(int result)
        {
            string name;
            if (ResultNames.TryGetValue(result, out name)) return name;
            return result >= 0 ? "success code " + result : "error code " + result;
        }

        private static bool AboutTheRequest(int result)
        {
            return result == -1 || result == -2 || result == -3 || result == -8 || result == -10 || result == -26;
        }

        private static string StateName(int state)
        {
            switch (state)
            {
                case 0: return "UNKNOWN";
                case 1: return "IDLE";
                case 2: return "READY";
                case 3: return "SYNCHRONIZED";
                case 4: return "VISIBLE";
                case 5: return "FOCUSED";
                case 6: return "STOPPING";
                case 7: return "LOSS_PENDING";
                case 8: return "EXITING";
                default: return state.ToString();
            }
        }
    }

    /// <summary>Writes the guard's queued messages from the main thread.</summary>
    internal sealed class EyeTextureGuardDriver : MonoBehaviour
    {
        private void Update()
        {
            EyeTextureGuard.Flush();
        }
    }
}
