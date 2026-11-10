using BepInEx;
using BepInEx.Logging;
using RoR2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using Path = System.IO.Path;

namespace VRMod
{
    /// <summary>
    /// DLSS / FSR upscaling for the VR scene camera, one upscaler per eye.
    ///
    /// While it is on, each frame runs like this:
    ///   1. Before rendering, the stereo scene camera is turned into a "presenter": it draws no
    ///      world geometry and runs no post-processing of its own.
    ///   2. When the scene camera is about to render its first eye, both eye cameras
    ///      (UpscalingEye) render the world at the reduced size from the headset's per-eye pose
    ///      and projection, and the native library upscales each one to the full eye size.
    ///   3. For each eye pass, the scene camera copies that eye's upscaled image into its target.
    ///      Its remaining image effects (outlines, UI blur source) and the HUD camera then run at
    ///      full resolution on top, exactly as before.
    ///   4. After the last eye pass the scene camera gets its own settings back.
    ///
    /// The native library is the one from Risk of Rain Upscaling by Xander Frangos (MIT), built
    /// from the NativeUpscaling folder with per-eye support. VRPatcher loads it before the game
    /// creates its graphics device (UpscalingBootstrap); this class binds the same library.
    /// </summary>
    internal static class Upscaling
    {
        private const string Tag = "[VR upscaling] ";
        private const int SettleFrames = 10;
        private const float MipBiasOffset = -1f; // DLSS guide 3.5
        private const string StandalonePreloader = "RiskOfRainUpscaling.Preloader.dll";
        // Where the game's own post-processing writes its finished image; the upscaled eye takes its place.
        private const CameraEvent PresentEvent = CameraEvent.BeforeImageEffects;
        internal const string LogFolderName = "VRMod-Upscaling";

        private static ManualLogSource Log => VRMod.StaticLogger;

        /// <summary>The native library is loaded and this graphics setup can run it.</summary>
        internal static bool Available { get; private set; }
        private static string unavailableReason = "the upscaling library is not loaded";
        private static bool earlyHooks, warnedUnavailable;

        // One attachment: the rig's scene camera and its two eyes.
        private static UpscalingEye[] eyes;
        private static CameraRigController rig;
        private static Camera real;
        private static PostProcessLayer realLayer;
        private static SceneCamera realSceneCamera;
        private static StereoProjectionFix projectionFix;
        private static CommandBuffer presenter;
        private static UpscalingQuality attachedQuality;
        private static UpscalingBackend attachedBackend;
        private static UpscalingDlssPreset attachedPreset;
        private static readonly List<UpscalingEye> retiring = new List<UpscalingEye>();

        // Frame state.
        private static int presentingFrame = -1, renderedFrame = -1, skippedFrames;
        private static bool stateApplied, realLayerCleared, loggedFirstFrame;
        private static int savedCullingMask;
        private static CameraClearFlags savedClearFlags;
        private static Color savedBackground;
        private static RenderingPath savedPath;
        private static DepthTextureMode savedDepthMode;
        private static bool savedMsaa;

        // Eligibility and failure state.
        private static CameraRigController candidate;
        private static int waitFrames, autoKind;
        private static bool suspended;
        private static volatile string failure;
        private static string lastBlocked;
        private static float nextStats;

        // ---------------------------------------------------------------- startup

        internal static void Init()
        {
            try { Available = LoadNative(); }
            catch (Exception e)
            {
                unavailableReason = "the upscaling library failed to start (" + e.Message + ")";
                Log.LogError(Tag + "Startup failed; the game continues without upscaling. " + e);
            }
            if (!Available)
            {
                if (ModConfig.UpscalingEnabled.Value) WarnUnavailable();
                return;
            }
            try
            {
                UpscalingHooks.Install();
                Camera.onPreCull += OnPreCull;
                Camera.onPreRender += OnPreRender;
                Camera.onPostRender += OnPostRender;
                Application.onBeforeRender += OnBeforeRender;
                var driver = new GameObject("VR Upscaling Driver") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(driver);
                driver.AddComponent<UpscalingDriver>();
            }
            catch (Exception e)
            {
                Available = false;
                unavailableReason = "the upscaling hooks could not be installed (" + e.Message + ")";
                Log.LogError(Tag + "Hook setup failed; the game continues without upscaling. " + e);
            }
        }

        private static bool LoadNative()
        {
            if (!ModConfig.UpscalingLoadLibrary.Value)
            {
                unavailableReason = "'Load upscaling library' is off in VRMod.cfg";
                Log.LogInfo(Tag + "'Load upscaling library' is off in VRMod.cfg; upscaling is unavailable.");
                return false;
            }
            string standalone = FindFile(Paths.PatcherPluginPath, StandalonePreloader);
            if (standalone != null)
            {
                unavailableReason = "the standalone Risk of Rain Upscaling mod is installed; disable it to use the VR mod's upscaling";
                Log.LogWarning(Tag + "The standalone Risk of Rain Upscaling mod is installed (" + standalone + "). Its graphics hooks and the VR mod's cannot run together, so VR upscaling stays off. Disable that mod to use the upscaling settings in the VR tab.");
                return false;
            }
            string native = FindFile(Paths.PatcherPluginPath, UpscalingNative.FileName);
            if (native == null)
            {
                unavailableReason = UpscalingNative.FileName + " is missing from the BepInEx patchers folder";
                Log.LogWarning(Tag + UpscalingNative.FileName + " was not found under " + Paths.PatcherPluginPath + "; upscaling is unavailable. Reinstall the mod so both its plugins and patchers folders are updated.");
                return false;
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || GraphicsSettings.currentRenderPipeline ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf) ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) || QualitySettings.activeColorSpace != ColorSpace.Linear)
            {
                unavailableReason = "upscaling needs Direct3D 11 (the game is running on " + SystemInfo.graphicsDeviceType + ")";
                Log.LogWarning(Tag + "Upscaling needs Direct3D 11 with float render textures and linear color; this setup (" + SystemInfo.graphicsDeviceType + ") is not supported.");
                return false;
            }

            UpscalingNative.Load(native);
            UpscalingNative.SetLoggingLevel(ParseLogLevel(ModConfig.UpscalingLogLevel.Value));
            string logs = Path.Combine(Paths.BepInExRootPath, LogFolderName);
            string runtimes = Path.Combine(Path.GetDirectoryName(typeof(VRMod).Assembly.Location), "upscaling");
            int startup = UpscalingNative.Initialize(logs, runtimes, "plugin-awake");
            int devices = UpscalingNative.GetObservedDeviceCount();
            earlyHooks = startup >= 0 && devices > 0;
            ApplySharpness();
            bool nvidia = SystemInfo.graphicsDeviceVendorID == UpscalingPolicy.NvidiaVendorId;
            Log.LogInfo(Tag + "Native library ready. GPU: " + SystemInfo.graphicsDeviceName + ". Early graphics hooks: " + (earlyHooks ? "installed" : "missing") +
                " (observed devices " + devices + "). " +
                (earlyHooks ? (nvidia ? "DLSS can be used." : "This is not an NVIDIA card, so Auto uses FSR 3.")
                            : "DLSS needs those hooks, so only FSR 3 is available until the patcher part of the mod loads the library at startup.") +
                " Native log: " + Path.Combine(logs, "native.log"));
            return true;
        }

        // 0 Off, 1 Error, 2 Warning, 3 Info, 4 Debug (the native library's own values).
        private static int ParseLogLevel(string value)
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

        private static void WarnUnavailable()
        {
            if (warnedUnavailable) return;
            warnedUnavailable = true;
            Log.LogWarning(Tag + "Upscaling is turned on in the VR settings but cannot run: " + unavailableReason + ".");
        }

        // ---------------------------------------------------------------- settings

        /// <summary>Any upscaling setting changed (VR settings tab or config file).</summary>
        internal static void OnSettingChanged(object sender, EventArgs e)
        {
            if (!Available)
            {
                if (ModConfig.UpscalingEnabled != null && ModConfig.UpscalingEnabled.Value) WarnUnavailable();
                return;
            }
            // A failure pauses upscaling without saving Off over the player's setting; changing a setting retries.
            suspended = false;
            autoKind = 0;
            lastBlocked = null;
            ApplySharpness();
        }

        private static void ApplySharpness()
        {
            try { UpscalingNative.SetSharpness(Mathf.Clamp01(ModConfig.UpscalingSharpness.Value)); }
            catch (Exception e) { Log.LogWarning(Tag + "Could not apply sharpening: " + e.Message); }
        }

        // ---------------------------------------------------------------- per-frame control (Update)

        internal static void Tick()
        {
            try { TickInternal(); }
            catch (Exception e) { Suspend("unexpected error: " + e); }
        }

        private static void TickInternal()
        {
            RestoreRealCamera(); // normally already done after the last eye pass
            CollectRetired();
            string failed = failure;
            if (failed != null) { failure = null; Suspend(failed); return; }
            if (!ModConfig.UpscalingEnabled.Value) { Detach("turned off"); candidate = null; return; }
            if (suspended) return;

            CameraRigController found = FindRig(out string blocked);
            if (found == null) { Block(blocked); candidate = null; return; }
            if (eyes != null && (found != rig || found.sceneCam != real)) Detach("the scene camera changed");
            if (eyes == null)
            {
                // Give a new camera rig a few frames to finish its own setup first.
                if (found != candidate) { candidate = found; waitFrames = 0; }
                if (++waitFrames < SettleFrames) return;
            }

            int outputWidth = XRSettings.eyeTextureWidth, outputHeight = XRSettings.eyeTextureHeight;
            if (outputWidth < 64 || outputHeight < 64) { Block("the headset has not reported its eye texture size yet"); return; }

            UpscalingQuality quality = UpscalingPolicy.ParseQuality(ModConfig.UpscalingQuality.Value);
            UpscalingBackend backend = UpscalingPolicy.ParseBackend(ModConfig.UpscalingBackend.Value);
            UpscalingDlssPreset preset = UpscalingPolicy.ParsePreset(ModConfig.UpscalingPreset.Value);
            // FSR's Balanced ratio differs from DLSS. Once the native library has made its Auto choice, size for it.
            if (eyes != null && backend == UpscalingBackend.Auto)
            {
                int kind = UpscalingNative.UpscalerKind(eyes[0].Context);
                if (kind > 0) autoKind = kind;
            }
            bool nvidia = SystemInfo.graphicsDeviceVendorID == UpscalingPolicy.NvidiaVendorId;
            UpscalingBackend sizing = UpscalingPolicy.Expected(backend, autoKind, nvidia, earlyHooks);
            int renderWidth = UpscalingPolicy.Dimension(outputWidth, quality, sizing);
            int renderHeight = UpscalingPolicy.Dimension(outputHeight, quality, sizing);

            if (eyes != null && (eyes[0].OutputWidth != outputWidth || eyes[0].OutputHeight != outputHeight ||
                                 eyes[0].RenderWidth != renderWidth || eyes[0].RenderHeight != renderHeight ||
                                 attachedQuality != quality || attachedBackend != backend || attachedPreset != preset))
                Detach("the settings or the eye texture size changed");

            if (eyes == null)
            {
                Attach(found, outputWidth, outputHeight, renderWidth, renderHeight, quality, backend, preset);
                return;
            }

            if (eyes[0].Failed || eyes[1].Failed)
            {
                Suspend("the native upscaler reported a failure (see " + Path.Combine(Paths.BepInExRootPath, LogFolderName, "native.log") + ")");
                return;
            }
            if (!eyes[0].Alive || !eyes[1].Alive) { Detach("an eye camera was destroyed"); return; }

            UpscalingSceneFixups.Tick();
            if (Time.unscaledTime >= nextStats) LogStats();
        }

        /// <summary>The one camera rig upscaling can attach to right now, or null and why not.</summary>
        private static CameraRigController FindRig(out string blocked)
        {
            blocked = null;
            if (!XRSettings.enabled || !XRSettings.isDeviceActive) { blocked = "the headset is not active"; return null; }
            if (!Run.instance) { blocked = "not in a run (menus render at full resolution)"; return null; }
            CameraRigController found = null;
            int count = 0;
            string reason = "no gameplay scene camera";
            foreach (CameraRigController current in CameraRigController.readOnlyInstancesList)
            {
                if (!current || !current.sceneCam || !current.sceneCam.isActiveAndEnabled) continue;
                Camera camera = current.sceneCam;
                Rect rect = camera.rect;
                if (camera.stereoTargetEye != StereoTargetEyeMask.Both) { reason = "the scene camera is not a stereo camera"; continue; }
                if (camera.targetTexture) { reason = "the scene camera renders into a texture"; continue; }
                if (camera.orthographic) { reason = "the scene camera is orthographic"; continue; }
                if (Mathf.Abs(rect.x) > 1e-4f || Mathf.Abs(rect.y) > 1e-4f || Mathf.Abs(rect.width - 1) > 1e-4f || Mathf.Abs(rect.height - 1) > 1e-4f) { reason = "the scene camera does not cover the whole view"; continue; }
                // The attached camera was last rendered as the presenter (forward, nothing drawn); it was checked when attaching.
                bool attached = eyes != null && ReferenceEquals(camera, real);
                if (!attached && camera.actualRenderingPath != RenderingPath.DeferredShading) { reason = "the scene camera is not using deferred shading"; continue; }
                if (current.isCutscene) { reason = "a cutscene is playing"; continue; }
                if (current.skyboxCam && current.skyboxCam.isActiveAndEnabled) { reason = "the rig uses a separate skybox camera"; continue; }
                ++count; found = current;
            }
            if (count == 1) return found;
            blocked = count == 0 ? reason : "more than one gameplay scene camera is active";
            return null;
        }

        private static void Block(string reason)
        {
            Detach(reason);
            if (reason == lastBlocked) return;
            lastBlocked = reason;
            Log.LogInfo(Tag + "Waiting: " + reason + ".");
        }

        private static void Attach(CameraRigController newRig, int outputWidth, int outputHeight, int renderWidth, int renderHeight,
            UpscalingQuality quality, UpscalingBackend backend, UpscalingDlssPreset preset)
        {
            Camera source = newRig.sceneCam;
            var created = new UpscalingEye[2];
            try
            {
                ApplySharpness();
                for (int i = 0; i < 2; i++)
                {
                    created[i] = new UpscalingEye(i == 0 ? "left" : "right", renderWidth, renderHeight, outputWidth, outputHeight);
                    created[i].Create(source, newRig, quality, backend, preset);
                }
            }
            catch (Exception e)
            {
                foreach (UpscalingEye eye in created)
                {
                    if (eye == null) continue;
                    try { eye.Detach(); eye.Retire(); } catch (Exception inner) { Log.LogWarning(Tag + "Cleanup after a failed start: " + inner.Message); }
                    retiring.Add(eye);
                }
                Suspend("could not start (" + e.Message + ")");
                Log.LogDebug(Tag + e);
                return;
            }

            eyes = created; rig = newRig; real = source;
            realLayer = source.GetComponent<PostProcessLayer>();
            realSceneCamera = source.GetComponent<SceneCamera>();
            projectionFix = source.GetComponent<StereoProjectionFix>();
            attachedQuality = quality; attachedBackend = backend; attachedPreset = preset;
            presenter = new CommandBuffer { name = "VRMod.Upscaling.Present" };
            real.AddCommandBuffer(PresentEvent, presenter);
            presentingFrame = renderedFrame = -1; skippedFrames = 0; loggedFirstFrame = false; lastBlocked = null;
            nextStats = Time.unscaledTime + 5f;

            float mipBias = UpscalingPolicy.MipBias(renderWidth, outputWidth, MipBiasOffset);
            UpscalingSceneFixups.Begin(mipBias);

            var components = new List<string>();
            foreach (MonoBehaviour behaviour in source.GetComponents<MonoBehaviour>()) if (behaviour) components.Add(behaviour.GetType().Name + (behaviour.enabled ? "" : "(off)"));
            Log.LogInfo(Tag + "On: " + UpscalingPolicy.QualityNames[(int)quality] + ", each eye renders " + renderWidth + "x" + renderHeight + " and is upscaled to " + outputWidth + "x" + outputHeight +
                " (" + (100.0 * renderWidth / outputWidth).ToString("0", CultureInfo.InvariantCulture) + "% per axis). Upscaler: " + UpscalingPolicy.BackendNames[BackendIndex(backend)] +
                ", DLSS preset: " + UpscalingPolicy.PresetNames[(int)preset] + ", sharpening: " + ModConfig.UpscalingSharpness.Value.ToString("0.00", CultureInfo.InvariantCulture) +
                ", texture mip bias: " + mipBias.ToString("0.00", CultureInfo.InvariantCulture) + ". Scene: " + source.gameObject.scene.name +
                ". Scene camera pixels: " + source.pixelWidth + "x" + source.pixelHeight + ", HDR " + source.allowHDR + ". Scene camera components: " + string.Join(", ", components.ToArray()) + ".");
        }

        private static int BackendIndex(UpscalingBackend backend) => backend == UpscalingBackend.DLSS ? 1 : backend == UpscalingBackend.FSR3 ? 2 : 0;

        private static void Detach(string reason)
        {
            if (eyes == null) return;
            UpscalingEye[] old = eyes;
            LogStats();
            RestoreRealCamera();
            eyes = null;
            if (real && presenter != null) real.RemoveCommandBuffer(PresentEvent, presenter);
            presenter?.Release(); presenter = null;
            // The scene camera's own post-processing was emptied while presenting; make it rebuild.
            if (realLayerCleared && realLayer) realLayer.OnGlobalVolumesDirty();
            realLayerCleared = false;
            foreach (UpscalingEye eye in old)
            {
                try { eye.Detach(); eye.Retire(); }
                catch (Exception e) { Log.LogWarning(Tag + "Cleanup: " + e.Message); }
                retiring.Add(eye);
            }
            rig = null; real = null; realLayer = null; realSceneCamera = null; projectionFix = null;
            presentingFrame = renderedFrame = -1; waitFrames = 0;
            UpscalingSceneFixups.End();
            Log.LogInfo(Tag + "Off: " + reason + ". The scene camera renders normally again.");
        }

        private static void Suspend(string reason)
        {
            Detach("stopped after a problem");
            suspended = true;
            Log.LogError(Tag + "Stopped: " + reason + ". The game keeps rendering at full resolution and the saved settings are kept. Changing any upscaling setting in the VR tab tries again.");
        }

        /// <summary>Called from render callbacks, where nothing may be torn down; Tick handles it next frame.</summary>
        internal static void Fail(string reason)
        {
            if (failure == null) failure = reason;
        }

        private static void CollectRetired()
        {
            for (int i = retiring.Count - 1; i >= 0; i--)
            {
                try { if (retiring[i].Collect()) retiring.RemoveAt(i); }
                catch (Exception e) { Log.LogWarning(Tag + "Could not release an upscaler: " + e.Message); retiring.RemoveAt(i); }
            }
        }

        private static void LogStats()
        {
            nextStats = Time.unscaledTime + 60f;
            if (eyes == null) return;
            UpscalingEye left = eyes[0], right = eyes[1];
            int renders = left.Renders + right.Renders;
            if (renders == 0) return;
            double perFrame = (left.RenderMilliseconds + right.RenderMilliseconds) / Math.Max(1, left.Renders);
            Log.LogInfo(Tag + "Status: " + UpscalingNative.UpscalerName(UpscalingNative.UpscalerKind(left.Context)) +
                ", native state left " + left.State + " / right " + right.State + " (2 = upscaling), frames " + left.Renders +
                ", upscaled left " + left.Evaluations + " / right " + right.Evaluations +
                ", history resets left " + left.Resets + " / right " + right.Resets +
                ", CPU time for both eye renders " + perFrame.ToString("0.00", CultureInfo.InvariantCulture) + " ms per frame.");
            left.RenderMilliseconds = right.RenderMilliseconds = 0;
            left.Renders = right.Renders = 0;
            left.Evaluations = right.Evaluations = 0;
            left.Resets = right.Resets = 0;
        }

        // ---------------------------------------------------------------- frame: presenter state

        /// <summary>After every LateUpdate and before any camera renders: turn the scene camera into the presenter.</summary>
        [BeforeRenderOrder(10000)]
        private static void OnBeforeRender()
        {
            if (eyes == null) return;
            try
            {
                RestoreRealCamera();
                if (failure != null || !real || !real.isActiveAndEnabled || !real.stereoEnabled || !XRSettings.isDeviceActive) return;
                if (!eyes[0].Alive || !eyes[1].Alive) return;

                savedCullingMask = real.cullingMask;
                savedClearFlags = real.clearFlags;
                savedBackground = real.backgroundColor;
                savedPath = real.renderingPath;
                savedDepthMode = real.depthTextureMode;
                savedMsaa = real.allowMSAA;
                eyes[0].Sync(real, savedCullingMask, savedClearFlags, savedBackground, savedPath, savedDepthMode);
                eyes[1].Sync(real, savedCullingMask, savedClearFlags, savedBackground, savedPath, savedDepthMode);

                real.cullingMask = 0;
                real.clearFlags = CameraClearFlags.SolidColor;
                real.backgroundColor = Color.black;
                real.renderingPath = RenderingPath.Forward;
                real.depthTextureMode = DepthTextureMode.None;
                real.allowMSAA = false;
                stateApplied = true;
                presentingFrame = Time.frameCount;

                // The eye cameras already ran the game's post-processing; the scene camera must not run it again.
                if (realLayer && !realLayerCleared)
                {
                    UpscalingHooks.ClearCommandBuffers(realLayer);
                    realLayerCleared = true;
                }
            }
            catch (Exception e) { Fail("could not prepare the frame: " + e.Message); }
        }

        private static void RestoreRealCamera()
        {
            if (!stateApplied) return;
            stateApplied = false;
            presenter?.Clear();
            if (!real) return;
            real.cullingMask = savedCullingMask;
            real.clearFlags = savedClearFlags;
            real.backgroundColor = savedBackground;
            real.renderingPath = savedPath;
            real.depthTextureMode = savedDepthMode;
            real.allowMSAA = savedMsaa;
        }

        /// <summary>True while this frame shows the upscaled eyes through the scene camera.</summary>
        private static bool Presenting => presentingFrame == Time.frameCount && eyes != null;

        // ---------------------------------------------------------------- frame: rendering

        private static void OnPreCull(Camera camera)
        {
            if (presentingFrame != Time.frameCount || eyes == null || !ReferenceEquals(camera, real)) return;
            EnsureEyesRendered();
        }

        private static void OnPreRender(Camera camera)
        {
            if (presentingFrame != Time.frameCount || eyes == null || presenter == null || !ReferenceEquals(camera, real)) return;
            EnsureEyesRendered();
            UpscalingEye eye = eyes[camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0];
            presenter.Clear();
            if (ModConfig.UpscalingFlip.Value) presenter.Blit(eye.Output, BuiltinRenderTextureType.CameraTarget, new Vector2(1, -1), new Vector2(0, 1));
            else presenter.Blit(eye.Output, BuiltinRenderTextureType.CameraTarget);
            presenter.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        private static void OnPostRender(Camera camera)
        {
            if (!stateApplied || !ReferenceEquals(camera, real)) return;
            if (camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) return; // the right eye still follows
            RestoreRealCamera();
        }

        /// <summary>Renders both eyes once per frame, from whichever scene camera callback comes first.</summary>
        internal static void EnsureEyesRendered()
        {
            int frame = Time.frameCount;
            if (renderedFrame == frame || presentingFrame != frame || eyes == null) return;
            renderedFrame = frame;
            try { RenderEyes(); }
            catch (Exception e) { Fail("rendering an eye failed: " + e); }
        }

        private static void RenderEyes()
        {
            // The hidden reference camera holds the projection the headset asked for (StereoProjectionFix).
            Camera reference = projectionFix ? projectionFix.ReferenceCamera : null;
            if (!reference || !reference.isActiveAndEnabled) reference = real;

            Matrix4x4 leftView = real.GetStereoViewMatrix(Camera.StereoscopicEye.Left), rightView = real.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
            Matrix4x4 leftProjection = reference.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), rightProjection = reference.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            if (!Pose(leftView, out Vector3 leftPosition, out Quaternion leftRotation, out float scale) || !Pose(rightView, out Vector3 rightPosition, out Quaternion rightRotation, out _) ||
                !Perspective(leftProjection) || !Perspective(rightProjection))
            {
                // Tracking can drop out for a moment; the previous image stays up meanwhile.
                if (++skippedFrames > 300) Fail("the headset did not provide usable eye poses for 300 frames");
                return;
            }
            skippedFrames = 0;
            if (!loggedFirstFrame)
            {
                loggedFirstFrame = true;
                Log.LogInfo(Tag + "First frame: eye distance " + Vector3.Distance(leftPosition, rightPosition).ToString("0.0000", CultureInfo.InvariantCulture) +
                    " (world units), view scale " + scale.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", projection from " + (ReferenceEquals(reference, real) ? "the scene camera" : "the stereo reference camera") +
                    ", left eye m00 " + leftProjection.m00.ToString("0.0000", CultureInfo.InvariantCulture) + " m11 " + leftProjection.m11.ToString("0.0000", CultureInfo.InvariantCulture) +
                    " m02 " + leftProjection.m02.ToString("0.0000", CultureInfo.InvariantCulture) + " m12 " + leftProjection.m12.ToString("0.0000", CultureInfo.InvariantCulture) + ".");
            }
            eyes[0].Render(leftPosition, leftRotation, leftProjection);
            eyes[1].Render(rightPosition, rightRotation, rightProjection);
        }

        /// <summary>World pose of an eye from its view matrix; scale in the matrix is dropped.</summary>
        private static bool Pose(Matrix4x4 view, out Vector3 position, out Quaternion rotation, out float scale)
        {
            position = default; rotation = Quaternion.identity; scale = 1;
            for (int i = 0; i < 16; i++) if (float.IsNaN(view[i]) || float.IsInfinity(view[i])) return false;
            Matrix4x4 world = view.inverse;
            for (int i = 0; i < 16; i++) if (float.IsNaN(world[i]) || float.IsInfinity(world[i])) return false;
            Vector3 up = world.GetColumn(1), back = world.GetColumn(2);
            scale = up.magnitude;
            if (scale < 1e-6f || back.sqrMagnitude < 1e-12f) return false;
            position = world.GetColumn(3);
            rotation = Quaternion.LookRotation(-back.normalized, up.normalized); // view space looks down -Z
            return true;
        }

        private static bool Perspective(Matrix4x4 projection)
        {
            for (int i = 0; i < 16; i++) if (float.IsNaN(projection[i]) || float.IsInfinity(projection[i])) return false;
            return projection.m00 > 1e-4f && projection.m11 > 1e-4f && Mathf.Abs(projection.m32 + 1f) < 1e-3f;
        }

        // ---------------------------------------------------------------- hook entry points (UpscalingHooks)

        /// <summary>The eye whose post-processing layer this is, or null.</summary>
        internal static UpscalingEye EyeFor(PostProcessLayer layer)
        {
            UpscalingEye[] current = eyes;
            if (current == null) return null;
            if (ReferenceEquals(current[0].Layer, layer)) return current[0];
            if (ReferenceEquals(current[1].Layer, layer)) return current[1];
            return null;
        }

        /// <summary>The scene camera's layer when the given layer belongs to one of its eyes, else null.</summary>
        internal static PostProcessLayer RealLayerFor(PostProcessLayer eyeLayer)
        {
            return EyeFor(eyeLayer) != null ? realLayer : null;
        }

        /// <summary>
        /// True when the scene camera's own post-processing must stay out of this frame. On a frame
        /// that is not presented (headset inactive), its emptied command buffers are rebuilt first.
        /// </summary>
        internal static bool SkipRealLayer(PostProcessLayer layer)
        {
            if (eyes == null || !ReferenceEquals(layer, realLayer)) return false;
            if (presentingFrame == Time.frameCount) { EnsureEyesRendered(); return true; }
            if (realLayerCleared) { layer.OnGlobalVolumesDirty(); realLayerCleared = false; }
            return false;
        }

        /// <summary>True for the scene camera's per-camera game events on a presented frame (the eyes already ran them).</summary>
        internal static bool SkipRealSceneCamera(SceneCamera sceneCamera, bool preCull)
        {
            if (!Presenting || !ReferenceEquals(sceneCamera, realSceneCamera)) return false;
            if (preCull) EnsureEyesRendered();
            return true;
        }
    }

    /// <summary>Runs the upscaling state machine once per frame.</summary>
    internal sealed class UpscalingDriver : MonoBehaviour
    {
        private void Update()
        {
            Upscaling.Tick();
        }
    }
}
