using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VRMod
{
    /// <summary>
    /// A record of how Unity starts the VR session: which OpenXR calls it makes, in which order,
    /// how often and with which results, and which graphics device it hands to the runtime.
    ///
    /// Why: a runtime that refuses every eye texture is refusing the session or the device, not
    /// the request, and neither Unity's log nor the runtime's error code says which. The record
    /// shows whether the session had been started, whether frames were running, and whether the
    /// device sits on the graphics adapter the runtime asked for.
    ///
    /// Each watched call is passed straight through; only a counter and the result are kept.
    /// Some of these calls arrive on Unity's render thread, so nothing here touches Unity objects.
    /// </summary>
    internal static class OpenXRJournal
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call1(ulong a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Call2(ulong a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call3(ulong a, IntPtr b, IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call2Handles(ulong a, ulong b, IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call6(ulong a, IntPtr b, IntPtr c, uint d, IntPtr e, IntPtr f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PathToStringFn(ulong instance, ulong path, uint capacity, IntPtr count, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateMessengerFn(ulong instance, IntPtr createInfo, IntPtr messenger);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint MessageFn(ulong severity, ulong types, IntPtr data, IntPtr userData);

        internal sealed class Entry
        {
            public string name;
            public int order, calls, failures, firstResult, lastFailure;
            public string detail = "";
        }

        // The watched calls and the shape of their arguments.
        private static readonly Dictionary<string, int> Shapes = new Dictionary<string, int>
        {
            { "xrGetD3D11GraphicsRequirementsKHR", 4 },
            { "xrCreateSession", 3 }, { "xrBeginSession", 2 }, { "xrEndSession", 1 }, { "xrRequestExitSession", 1 }, { "xrDestroySession", 1 },
            { "xrWaitFrame", 3 }, { "xrBeginFrame", 2 }, { "xrEndFrame", 2 }, { "xrLocateViews", 6 },
            { "xrCreateReferenceSpace", 3 }, { "xrAttachSessionActionSets", 2 }, { "xrSyncActions", 2 },
            { "xrSuggestInteractionProfileBindings", 2 },
        };

        // Printed in this order when they were never called.
        private static readonly string[] Expected =
        {
            "xrGetD3D11GraphicsRequirementsKHR", "xrCreateSession", "xrEnumerateViewConfigurationViews", "xrEnumerateSwapchainFormats",
            "xrCreateSwapchain", "xrBeginSession", "xrWaitFrame", "xrBeginFrame", "xrLocateViews", "xrEndFrame",
        };

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, KeyValuePair<IntPtr, IntPtr>> wrappers = new Dictionary<string, KeyValuePair<IntPtr, IntPtr>>();
        private static readonly List<Delegate> keepAlive = new List<Delegate>();
        private static readonly Dictionary<string, int> messages = new Dictionary<string, int>();
        private static readonly List<string> messageOrder = new List<string>();
        private static readonly List<string> refusedProfiles = new List<string>();
        private static readonly MessageFn messageHook = OnMessage;
        private static int orderCounter;
        private static Call2 runtimeBegin;
        private static PathToStringFn pathToString;
        private static ulong lastInstance;
        private static string deviceText = "", requirementText = "";
        private static long requiredLuid = -1, deviceLuid = -1;

        /// <summary>False leaves every call alone: nothing is wrapped and nothing is recorded.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>True once Unity, or the guard on its behalf, has started the session.</summary>
        internal static volatile bool SessionBegun;

        /// <summary>True when the guard started the session itself; Unity's own call is then answered without reaching the runtime.</summary>
        internal static volatile bool BegunByTheGuard;

        internal static bool CanBeginSession { get { return runtimeBegin != null; } }

        /// <summary>A new or ended session has not been started by anyone. The counts run on.</summary>
        internal static void SessionReset()
        {
            SessionBegun = false;
            BegunByTheGuard = false;
        }

        /// <summary>Starts the record afresh. Called when the OpenXR loader is set up, before the instance exists.</summary>
        internal static void Forget()
        {
            lock (gate)
            {
                foreach (Entry entry in entries.Values)
                {
                    entry.calls = entry.failures = entry.order = entry.firstResult = entry.lastFailure = 0;
                    entry.detail = "";
                }
                orderCounter = 0;
                refusedProfiles.Clear();
                messages.Clear();
                messageOrder.Clear();
                messagesAsked = MessagesNotAsked;
                messagesSeen = 0;
                deviceText = requirementText = "";
                requiredLuid = deviceLuid = -1;
            }
            SessionReset();
        }

        internal static Entry EntryFor(string name)
        {
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(name, out entry)) entries[name] = entry = new Entry { name = name };
                return entry;
            }
        }

        internal static void Note(Entry entry, int result)
        {
            if (Interlocked.Increment(ref entry.calls) == 1)
            {
                entry.firstResult = result;
                entry.order = Interlocked.Increment(ref orderCounter);
            }
            if (result < 0)
            {
                Interlocked.Increment(ref entry.failures);
                entry.lastFailure = result;
            }
        }

        /// <summary>
        /// Returns a replacement for one OpenXR function that records the call and passes it on,
        /// or zero when the function is not one of the watched ones.
        /// </summary>
        internal static IntPtr Wrap(ulong instance, string name, IntPtr target)
        {
            if (instance != 0) lastInstance = instance;
            if (name == "xrPathToString" && target != IntPtr.Zero)
                pathToString = (PathToStringFn)Marshal.GetDelegateForFunctionPointer(target, typeof(PathToStringFn));
            int shape;
            if (!Enabled || name == null || target == IntPtr.Zero || !Shapes.TryGetValue(name, out shape)) return IntPtr.Zero;

            lock (gate)
            {
                KeyValuePair<IntPtr, IntPtr> known;
                if (wrappers.TryGetValue(name, out known))
                {
                    if (known.Key == target) return known.Value;   // looked up again: same replacement
                    if (known.Value == target) return IntPtr.Zero; // already ours
                }
            }

            Entry entry = EntryFor(name);
            Delegate hook;
            switch (shape)
            {
                case 1:
                {
                    Call1 next = (Call1)Marshal.GetDelegateForFunctionPointer(target, typeof(Call1));
                    bool ends = name == "xrEndSession" || name == "xrDestroySession";
                    hook = new Call1(a => { int r = next(a); Note(entry, r); if (ends && r >= 0) SessionReset(); return r; });
                    break;
                }
                case 2:
                {
                    Call2 next = (Call2)Marshal.GetDelegateForFunctionPointer(target, typeof(Call2));
                    if (name == "xrBeginSession")
                    {
                        runtimeBegin = next;
                        hook = new Call2((a, b) =>
                        {
                            if (BegunByTheGuard)
                            {
                                // The guard has started this session already. A second start would be
                                // refused as "already running", so Unity is told its start went through.
                                BegunByTheGuard = false;
                                Note(entry, 0);
                                entry.detail = "Unity's own call was answered by the mod, which had started the session already";
                                return 0;
                            }
                            int r = next(a, b);
                            Note(entry, r);
                            if (r >= 0) SessionBegun = true;
                            return r;
                        });
                    }
                    else if (name == "xrSuggestInteractionProfileBindings")
                    {
                        hook = new Call2((a, b) =>
                        {
                            int r = next(a, b);
                            Note(entry, r);
                            if (r < 0) Safely(() => NoteRefusedProfile(a, b, r));
                            return r;
                        });
                    }
                    else if (name == "xrEndFrame")
                    {
                        hook = new Call2((a, b) =>
                        {
                            int r = next(a, b);
                            bool first = entry.calls == 0;
                            Note(entry, r);
                            if (first) NoteFirstFrame(entry, b); // no lambda here: this runs every frame
                            return r;
                        });
                    }
                    else hook = new Call2((a, b) => { int r = next(a, b); Note(entry, r); return r; });
                    break;
                }
                case 3:
                {
                    Call3 next = (Call3)Marshal.GetDelegateForFunctionPointer(target, typeof(Call3));
                    if (name == "xrCreateSession")
                    {
                        hook = new Call3((a, b, c) =>
                        {
                            Safely(() => DescribeSessionRequest(b));
                            int r = next(a, b, c);
                            Note(entry, r);
                            if (r >= 0)
                            {
                                // A new session: nobody has started it, and what was said about the last start no longer holds.
                                SessionReset();
                                EntryFor("xrBeginSession").detail = "";
                            }
                            return r;
                        });
                    }
                    else hook = new Call3((a, b, c) => { int r = next(a, b, c); Note(entry, r); return r; });
                    break;
                }
                case 4:
                {
                    Call2Handles next = (Call2Handles)Marshal.GetDelegateForFunctionPointer(target, typeof(Call2Handles));
                    hook = new Call2Handles((a, b, c) =>
                    {
                        int r = next(a, b, c);
                        Note(entry, r);
                        if (r >= 0) Safely(() => NoteRequirements(c));
                        return r;
                    });
                    break;
                }
                default:
                {
                    Call6 next = (Call6)Marshal.GetDelegateForFunctionPointer(target, typeof(Call6));
                    hook = new Call6((a, b, c, d, e, f) => { int r = next(a, b, c, d, e, f); Note(entry, r); return r; });
                    break;
                }
            }

            IntPtr pointer = Marshal.GetFunctionPointerForDelegate(hook);
            lock (gate)
            {
                keepAlive.Add(hook); // handed to native code: must never be collected
                wrappers[name] = new KeyValuePair<IntPtr, IntPtr>(target, pointer);
            }
            return pointer;
        }

        /// <summary>Starts the session on the guard's behalf. Returns the runtime's answer, or null when it cannot be done.</summary>
        internal static int? BeginSession(ulong session)
        {
            Call2 begin = runtimeBegin;
            if (begin == null || SessionBegun) return null;
            IntPtr info = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteInt32(info, 0, 10);  // XR_TYPE_SESSION_BEGIN_INFO
                Marshal.WriteInt32(info, 16, 2);  // XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO
                int result = begin(session, info);
                Entry entry = EntryFor("xrBeginSession");
                Note(entry, result);
                entry.detail = "the first one was made by the mod, before Unity's own";
                if (result >= 0) { SessionBegun = true; BegunByTheGuard = true; }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        // XrFrameEndInfo: type, next, display time at 16, blend mode at 24, layer count at 28.
        private static void NoteFirstFrame(Entry entry, IntPtr frameEndInfo)
        {
            try
            {
                if (frameEndInfo != IntPtr.Zero) entry.detail = "first frame had " + Marshal.ReadInt32(frameEndInfo, 28) + " layer(s)";
            }
            catch { }
        }

        private static void Safely(Action action)
        {
            try { action(); } catch { }
        }

        // XrGraphicsRequirementsD3D11KHR: type, next, adapter LUID at 16, minimum feature level at 24.
        private static void NoteRequirements(IntPtr requirements)
        {
            if (requirements == IntPtr.Zero) return;
            requiredLuid = Marshal.ReadInt64(requirements, 16);
            requirementText = $"the runtime asks for the graphics adapter with id {Luid(requiredLuid)} and Direct3D feature level {Level(Marshal.ReadInt32(requirements, 24))} or higher";
        }

        // XrSessionCreateInfo: type, next at 8, create flags at 16, system id at 24. The graphics
        // binding hangs off "next"; for Direct3D 11 it is type 1000027000 with the device at 16.
        private static void DescribeSessionRequest(IntPtr createInfo)
        {
            if (createInfo == IntPtr.Zero) return;
            StringBuilder text = new StringBuilder();
            IntPtr entry = Marshal.ReadIntPtr(createInfo, 8);
            bool found = false;
            for (int i = 0; entry != IntPtr.Zero && i < 8; i++)
            {
                int type = Marshal.ReadInt32(entry);
                if (type == 1000027000)
                {
                    found = true;
                    text.Append(DescribeDevice(Marshal.ReadIntPtr(entry, 16)));
                }
                else text.Append($"[also attached: structure type {type}] ");
                entry = Marshal.ReadIntPtr(entry, 8);
            }
            if (!found) text.Append("no Direct3D 11 device was attached to the session request");
            ulong flags = (ulong)Marshal.ReadInt64(createInfo, 16);
            if (flags != 0) text.Append($"; session create flags 0x{flags:X}");
            deviceText = text.ToString();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetUIntFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObjectFn(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescFn(IntPtr self, IntPtr description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdaptersFn(IntPtr self, uint index, out IntPtr adapter);

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

        private static T Slot<T>(IntPtr comObject, int index) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(comObject);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, index * IntPtr.Size), typeof(T));
        }

        private static void Release(IntPtr comObject)
        {
            if (comObject != IntPtr.Zero) Slot<ReleaseFn>(comObject, 2)(comObject);
        }

        // What Unity's Direct3D 11 device is: its adapter, feature level and creation flags.
        // ID3D11Device slots: 37 GetFeatureLevel, 38 GetCreationFlags, 39 GetDeviceRemovedReason.
        // IDXGIDevice slot 7 GetAdapter; IDXGIAdapter slot 8 GetDesc.
        private static string DescribeDevice(IntPtr device)
        {
            if (device == IntPtr.Zero) return "the Direct3D 11 device in the session request is null";
            StringBuilder text = new StringBuilder("Direct3D 11 device");
            try
            {
                int level = Slot<GetIntFn>(device, 37)(device);
                uint flags = Slot<GetUIntFn>(device, 38)(device);
                int removed = Slot<GetIntFn>(device, 39)(device);
                text.Append($" at feature level {Level(level)}, creation flags 0x{flags:X}");
                if ((flags & 0x1) != 0) text.Append(" (single-threaded)");
                if (removed != 0) text.Append($", DEVICE REMOVED (reason 0x{removed:X8})");

                Guid dxgiDeviceId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                IntPtr dxgiDevice;
                if (Slot<QueryInterfaceFn>(device, 0)(device, ref dxgiDeviceId, out dxgiDevice) >= 0 && dxgiDevice != IntPtr.Zero)
                {
                    IntPtr adapter;
                    if (Slot<GetObjectFn>(dxgiDevice, 7)(dxgiDevice, out adapter) >= 0 && adapter != IntPtr.Zero)
                    {
                        string name; long luid;
                        if (AdapterDescription(adapter, out name, out luid))
                        {
                            deviceLuid = luid;
                            text.Append($", on adapter '{name}' (id {Luid(luid)})");
                        }
                        Release(adapter);
                    }
                    Release(dxgiDevice);
                }
                else text.Append(", which does not answer as a DXGI device");

                Guid multithreadId = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
                IntPtr multithread;
                if (Slot<QueryInterfaceFn>(device, 0)(device, ref multithreadId, out multithread) >= 0 && multithread != IntPtr.Zero)
                {
                    text.Append(Slot<GetIntFn>(multithread, 6)(multithread) != 0 ? ", multithread protection on" : ", multithread protection off");
                    Release(multithread);
                }
            }
            catch (Exception e)
            {
                text.Append($" (could not be examined further: {e.GetType().Name}: {e.Message})");
            }
            return text.ToString();
        }

        // DXGI_ADAPTER_DESC: 128 UTF-16 characters of name, four ids, three memory sizes at 272, LUID at 296.
        private static bool AdapterDescription(IntPtr adapter, out string name, out long luid)
        {
            name = ""; luid = -1;
            IntPtr description = Marshal.AllocHGlobal(320);
            try
            {
                for (int i = 0; i < 320; i += 8) Marshal.WriteInt64(description, i, 0);
                if (Slot<GetDescFn>(adapter, 8)(adapter, description) < 0) return false;
                name = Marshal.PtrToStringUni(description, 128) ?? "";
                int end = name.IndexOf('\0');
                name = (end >= 0 ? name.Substring(0, end) : name).Trim();
                luid = Marshal.ReadInt64(description, 296);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(description);
            }
        }

        // Every graphics adapter Windows lists, so a mismatch between the device and what the
        // runtime asked for can be read. IDXGIFactory1 slot 12 is EnumAdapters1.
        private static string Adapters()
        {
            StringBuilder text = new StringBuilder();
            try
            {
                Guid factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                IntPtr factory;
                if (CreateDXGIFactory1(ref factoryId, out factory) < 0 || factory == IntPtr.Zero) return "";
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr adapter;
                    if (Slot<EnumAdaptersFn>(factory, 12)(factory, i, out adapter) < 0 || adapter == IntPtr.Zero) break;
                    string name; long luid;
                    if (AdapterDescription(adapter, out name, out luid))
                        text.Append($"\n        adapter {i}: '{name}' (id {Luid(luid)})" + (luid == requiredLuid ? ", the one the runtime asks for" : "") + (luid == deviceLuid ? ", the one the game's device is on" : ""));
                    Release(adapter);
                }
                Release(factory);
            }
            catch (Exception)
            {
                return "";
            }
            return text.ToString();
        }

        // XrInteractionProfileSuggestedBinding: type, next, interaction profile path at 16.
        private static void NoteRefusedProfile(ulong instance, IntPtr suggestion, int result)
        {
            PathToStringFn convert = pathToString;
            string profile = "(profile name not available)";
            if (convert != null && suggestion != IntPtr.Zero)
            {
                IntPtr count = Marshal.AllocHGlobal(4), buffer = Marshal.AllocHGlobal(256);
                try
                {
                    Marshal.WriteInt32(count, 0);
                    if (convert(instance, (ulong)Marshal.ReadInt64(suggestion, 16), 256, count, buffer) >= 0)
                        profile = Marshal.PtrToStringAnsi(buffer) ?? profile;
                }
                finally
                {
                    Marshal.FreeHGlobal(count); Marshal.FreeHGlobal(buffer);
                }
            }
            string entry = profile + " (" + EyeTextureGuard.ResultName(result) + ")";
            lock (gate)
                if (refusedProfiles.Count < 16 && !refusedProfiles.Contains(entry)) refusedProfiles.Add(entry);
        }

        /// <summary>
        /// Asks the runtime to send its own diagnostic messages here, when it offers
        /// XR_EXT_debug_utils and Unity enabled it for the guard. Main thread.
        /// </summary>
        internal static void StartMessages(ulong instance, Func<ulong, string, IntPtr> lookup)
        {
            if (!Enabled || instance == 0 || lookup == null) return;
            try
            {
                IntPtr create = lookup(instance, "xrCreateDebugUtilsMessengerEXT");
                if (create == IntPtr.Zero) return;
                IntPtr info = Marshal.AllocHGlobal(48), messenger = Marshal.AllocHGlobal(8);
                try
                {
                    for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(info, i, 0);
                    Marshal.WriteInt64(messenger, 0);
                    Marshal.WriteInt32(info, 0, 1000019002);  // XR_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT
                    Marshal.WriteInt64(info, 16, 0x1111);     // verbose, info, warning, error
                    Marshal.WriteInt64(info, 24, 0xF);        // general, validation, performance, conformance
                    Marshal.WriteIntPtr(info, 32, Marshal.GetFunctionPointerForDelegate(messageHook));
                    int result = ((CreateMessengerFn)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateMessengerFn)))(instance, info, messenger);
                    lock (gate) messagesAsked = result >= 0 ? "on" : "refused (" + EyeTextureGuard.ResultName(result) + ")";
                }
                finally
                {
                    Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(messenger);
                }
            }
            catch (Exception e)
            {
                lock (gate) messagesAsked = "failed (" + e.GetType().Name + ")";
            }
        }

        private static int messagesSeen;
        private const string MessagesNotAsked = "not offered by the runtime, or not enabled";
        private static string messagesAsked = MessagesNotAsked;

        // XrDebugUtilsMessengerCallbackDataEXT: type, next, message id at 16, function name at 24, message at 32.
        // Called by the runtime on any thread; only stores text.
        private static uint OnMessage(ulong severity, ulong types, IntPtr data, IntPtr userData)
        {
            try
            {
                // Enough is enough: a runtime that talks on every call must not cost the game anything for long.
                if (data == IntPtr.Zero || Interlocked.Increment(ref messagesSeen) > 20000) return 0;
                string id = Text(Marshal.ReadIntPtr(data, 16)), function = Text(Marshal.ReadIntPtr(data, 24)), message = Text(Marshal.ReadIntPtr(data, 32));
                string level = (severity & 0x1000) != 0 ? "error" : (severity & 0x100) != 0 ? "warning" : (severity & 0x10) != 0 ? "info" : "verbose";
                string line = $"{level}: {(function.Length > 0 ? function + ": " : "")}{message}{(id.Length > 0 ? " [" + id + "]" : "")}";
                if (line.Length > 600) line = line.Substring(0, 600) + "...";
                lock (gate)
                {
                    int count;
                    if (messages.TryGetValue(line, out count)) messages[line] = count + 1;
                    else if (messageOrder.Count < 60) { messages[line] = 1; messageOrder.Add(line); }
                }
            }
            catch { }
            return 0; // XR_FALSE: do not abort the call that produced the message
        }

        private static string Text(IntPtr text)
        {
            return text == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(text) ?? "").Trim();
        }

        /// <summary>The whole record as text, for the log. Main thread (it lists the adapters).</summary>
        internal static string Report()
        {
            if (!Enabled) return "";
            StringBuilder text = new StringBuilder();
            List<Entry> called = new List<Entry>();
            List<string> never = new List<string>();
            string[] profiles, lines; int[] counts; string asked;
            lock (gate)
            {
                foreach (Entry entry in entries.Values) if (entry.calls > 0) called.Add(entry);
                foreach (string name in Expected)
                {
                    Entry entry;
                    if (!entries.TryGetValue(name, out entry) || entry.calls == 0) never.Add(name);
                }
                profiles = refusedProfiles.ToArray();
                lines = messageOrder.ToArray();
                counts = new int[lines.Length];
                for (int i = 0; i < lines.Length; i++) counts[i] = messages[lines[i]];
                asked = messagesAsked;
            }
            called.Sort((a, b) => a.order.CompareTo(b.order));

            text.Append("How the VR session was started (OpenXR calls since the game started, in the order they were first made):");
            foreach (Entry entry in called)
            {
                text.Append($"\n    {entry.name}: {entry.calls} call(s), ");
                if (entry.failures == 0) text.Append(entry.firstResult == 0 ? "all fine" : "all fine (" + EyeTextureGuard.ResultName(entry.firstResult) + ")");
                else if (entry.failures == entry.calls) text.Append("all refused with " + EyeTextureGuard.ResultName(entry.lastFailure));
                else text.Append($"{entry.failures} refused, last with {EyeTextureGuard.ResultName(entry.lastFailure)}");
                if (entry.detail.Length > 0) text.Append("; " + entry.detail);
            }
            if (never.Count > 0) text.Append("\n    not called so far: " + string.Join(", ", never.ToArray()));
            if (deviceText.Length > 0) text.Append("\n    Handed to the runtime: " + deviceText);
            if (requirementText.Length > 0)
            {
                text.Append("\n    Before that, " + requirementText + ".");
                if (requiredLuid != -1 && deviceLuid != -1)
                    text.Append(requiredLuid == deviceLuid ? " The game's device is on that adapter." : " THE GAME'S DEVICE IS ON A DIFFERENT ADAPTER.");
            }
            string adapters = Adapters();
            if (adapters.Length > 0) text.Append("\n    Graphics adapters on this PC:" + adapters);
            if (profiles.Length > 0) text.Append("\n    Controller profiles the runtime refused: " + string.Join(", ", profiles));
            text.Append("\n    The runtime's own messages (XR_EXT_debug_utils): " + asked + (lines.Length == 0 ? "; none received" : ""));
            for (int i = 0; i < lines.Length; i++) text.Append("\n        " + lines[i] + (counts[i] > 1 ? $"  (x{counts[i]})" : ""));
            return text.ToString();
        }

        private static string Luid(long luid)
        {
            return $"{(uint)(luid >> 32):X8}:{(uint)luid:X8}";
        }

        private static string Level(int level)
        {
            switch (level)
            {
                case 0x9100: return "9.1";
                case 0x9200: return "9.2";
                case 0x9300: return "9.3";
                case 0xa000: return "10.0";
                case 0xa100: return "10.1";
                case 0xb000: return "11.0";
                case 0xb100: return "11.1";
                case 0xc000: return "12.0";
                case 0xc100: return "12.1";
                case 0xc200: return "12.2";
                default: return "0x" + level.ToString("X");
            }
        }
    }
}
