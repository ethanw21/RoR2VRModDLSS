using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace VRMod
{
    /// <summary>
    /// One eye of the upscaled scene.
    ///
    /// The game's stereo scene camera cannot render at a reduced size: its targets are the
    /// headset's eye textures. So each eye gets its own mono copy of that camera, rendering the
    /// world (with the game's post-processing) into a smaller texture. The native library then
    /// upscales that texture to the full eye size with DLSS or FSR, using this eye's own depth,
    /// motion vectors, jitter and history. Nothing is shared between the eyes.
    ///
    /// The jitter, camera packet and history handling follow UpscalerSession.cs of Risk of Rain
    /// Upscaling (Xander Frangos, MIT).
    /// </summary>
    internal sealed class UpscalingEye
    {
        /// <summary>True while an eye camera object is being activated (its components run Awake).</summary>
        internal static bool Activating;

        private static readonly System.Reflection.FieldInfo SceneCameraStatic = typeof(SceneCamera).GetField("_camera",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        internal readonly string Name;
        internal readonly int RenderWidth, RenderHeight, OutputWidth, OutputHeight;

        internal GameObject Object;
        internal Camera Camera;
        internal PostProcessLayer Layer;
        internal RenderTexture World, Color, Depth, Motion, Output;
        internal IntPtr Inputs, Context;
        internal double RenderMilliseconds;
        internal int Renders, Evaluations, Resets;

        private readonly List<RenderTexture> textures = new List<RenderTexture>();
        private CommandBuffer geometry, inputs, tail, started, lit;
        private UpscalingEyeTail tailComponent;
        private bool layerActive;
        private int jitterPhases;

        private Matrix4x4 projection, previousVP;
        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private float previousTime;
        private int previousFrame, preparedFrame = -1, jitterIndex;

        internal UpscalingEye(string name, int renderWidth, int renderHeight, int outputWidth, int outputHeight)
        {
            Name = name;
            RenderWidth = renderWidth; RenderHeight = renderHeight;
            OutputWidth = outputWidth; OutputHeight = outputHeight;
        }

        internal bool Alive => Object && Camera;
        internal int State => Context == IntPtr.Zero ? -1 : UpscalingNative.UpscalerState(Context);
        internal bool Failed => (Context != IntPtr.Zero && UpscalingNative.UpscalerState(Context) == -2) ||
                                (Inputs != IntPtr.Zero && UpscalingNative.SceneInputsState(Inputs) == -2);
        internal bool Prepared => preparedFrame == Time.frameCount;

        // ---------------------------------------------------------------- creation

        internal void Create(Camera source, CameraRigController rig, UpscalingQuality quality, UpscalingBackend backend, UpscalingDlssPreset preset)
        {
            jitterPhases = UpscalingPolicy.JitterPhases(RenderWidth, OutputWidth);

            World = Texture("World", RenderWidth, RenderHeight, RenderTextureFormat.ARGBHalf, 24);
            Color = Texture("Color", RenderWidth, RenderHeight, RenderTextureFormat.ARGBHalf, 0);
            // Single-channel depth and Unity's own two-channel motion format; the native library reads them directly.
            Depth = Texture("Depth", RenderWidth, RenderHeight, RenderTextureFormat.RFloat, 0);
            Motion = Texture("Motion", RenderWidth, RenderHeight, RenderTextureFormat.RGHalf, 0);
            Output = Texture("Output", OutputWidth, OutputHeight, RenderTextureFormat.ARGBHalf, 0);

            var inputsDescription = new UpscalingNative.SceneInputsDescription
            {
                Size = (uint)Marshal.SizeOf<UpscalingNative.SceneInputsDescription>(), Version = 4,
                Width = RenderWidth, Height = RenderHeight, Depth = Depth.GetNativeTexturePtr()
            };
            Inputs = UpscalingNative.SceneInputsCreate(ref inputsDescription);
            if (Inputs == IntPtr.Zero) throw new InvalidOperationException("the native library could not create the depth and motion capture");

            var description = new UpscalingNative.UpscalerDescription
            {
                Size = (uint)Marshal.SizeOf<UpscalingNative.UpscalerDescription>(), Version = 2,
                Width = RenderWidth, Height = RenderHeight, SceneInputs = Inputs,
                Color = Color.GetNativeTexturePtr(), Depth = Depth.GetNativeTexturePtr(), Motion = Motion.GetNativeTexturePtr(),
                Output = Output.GetNativeTexturePtr(), OutputWidth = OutputWidth, OutputHeight = OutputHeight,
                Quality = (int)quality, Placement = 0
            };
            // The native context captures the backend and preset at creation.
            if (UpscalingNative.SetUpscalerBackend((int)backend) != 0) throw new InvalidOperationException("the native library rejected the upscaler choice");
            if (UpscalingNative.SetDlssPreset(UpscalingPolicy.NativePreset(preset)) != 0) throw new InvalidOperationException("the native library rejected the DLSS preset");
            Context = UpscalingNative.UpscalerCreate(ref description);
            if (Context == IntPtr.Zero) throw new InvalidOperationException("the native library could not create the upscaler (see native.log)");

            CreateCamera(source, rig);

            var start = new CommandBuffer { name = "VRMod.Upscaling.Initialize" };
            try { start.IssuePluginEventAndData(UpscalingNative.UpscalerGetEvaluateEvent(), -2, Context); Graphics.ExecuteCommandBuffer(start); }
            finally { start.Release(); }
        }

        private RenderTexture Texture(string name, int width, int height, RenderTextureFormat format, int depth)
        {
            var texture = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear)
            {
                name = "VRMod.Upscaling." + Name + "." + name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1, useMipMap = false, autoGenerateMips = false
            };
            textures.Add(texture);
            if (!texture.Create()) throw new InvalidOperationException("could not allocate the " + name + " texture (" + width + "x" + height + ")");
            RenderTexture previous = RenderTexture.active;
            try { RenderTexture.active = texture; GL.Clear(true, true, UnityEngine.Color.clear); }
            finally { RenderTexture.active = previous; }
            return texture;
        }

        private void CreateCamera(Camera source, CameraRigController rig)
        {
            // Built inactive, so every component is configured before its Awake/OnEnable runs.
            Object = new GameObject("VR Upscaling Eye (" + Name + ")");
            Object.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(Object);

            Camera = Object.AddComponent<Camera>();
            Camera.CopyFrom(source);
            Camera.enabled = false; // rendered by hand, once per frame, from the scene camera's pre-cull
            Camera.stereoTargetEye = StereoTargetEyeMask.None;
            Camera.targetTexture = World;
            Camera.rect = new Rect(0, 0, 1, 1);
            Camera.ResetStereoProjectionMatrices();
            Camera.ResetStereoViewMatrices();
            Camera.ResetWorldToCameraMatrix();
            Camera.ResetProjectionMatrix();
            Camera.ResetCullingMatrix();
            // The post-processing layer resets the projection of non-physical cameras every frame;
            // this one always gets the headset's own per-eye projection assigned.
            Camera.usePhysicalProperties = true;
            Camera.allowMSAA = false;
            Camera.allowDynamicResolution = false;
            Camera.useOcclusionCulling = false;
            Camera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            try { UnityEngine.XR.XRDevice.DisableAutoXRCameraTracking(Camera, true); } catch { }

            if (source.GetComponent<FlareLayer>()) Object.AddComponent<FlareLayer>();

            PostProcessLayer sourceLayer = source.GetComponent<PostProcessLayer>();
            if (sourceLayer && sourceLayer.enabled)
            {
                Layer = Object.AddComponent<PostProcessLayer>();
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(sourceLayer), Layer);
                UpscalingHooks.ShareResources(sourceLayer, Layer);
                if (Layer.volumeTrigger == null) Layer.volumeTrigger = sourceLayer.volumeTrigger;
                Layer.volumeLayer = sourceLayer.volumeLayer;
                Layer.antialiasingMode = PostProcessLayer.Antialiasing.None; // the upscaler is the anti-aliasing
                Layer.finalBlitToCameraTarget = false; // the tail below needs the finished image as its source
            }
            SceneCamera sceneCamera = source.GetComponent<SceneCamera>() ? Object.AddComponent<SceneCamera>() : null;
            Copy<RoR2.PostProcess.SobelCommandBuffer>(source);
            Copy<ThreeEyedGames.DecaliciousRenderer>(source);
            // Last component: its OnRenderImage receives the image after every effect above.
            tailComponent = Object.AddComponent<UpscalingEyeTail>();
            tailComponent.Eye = this;

            // SceneCamera.Awake points a static at the newest scene camera (newer game builds) and
            // looks for the rig in its parents; put both right afterwards.
            object previousStatic = SceneCameraStatic?.GetValue(null);
            Activating = true;
            try { Object.SetActive(true); }
            finally { Activating = false; }
            if (SceneCameraStatic != null && ReferenceEquals(SceneCameraStatic.GetValue(null), Camera))
                SceneCameraStatic.SetValue(null, previousStatic as Camera ? previousStatic : source);
            if (sceneCamera) sceneCamera.cameraRigController = rig;

            layerActive = Layer && Layer.isActiveAndEnabled;
            // The game's post-processing keeps its command buffers until something marks them dirty.
            if (layerActive) Layer.OnGlobalVolumesDirty();

            // Profiler marks (UpscalingProfiler) split this eye's GPU time into its stages.
            started = new CommandBuffer { name = "VRMod.Upscaling.BeforeGBuffer" };
            UpscalingProfiler.Mark(started, Name + " eye: start");
            Camera.AddCommandBuffer(CameraEvent.BeforeGBuffer, started);

            geometry = new CommandBuffer { name = "VRMod.Upscaling.AfterGBuffer" };
            geometry.IssuePluginEventAndData(UpscalingNative.SceneInputsGetEvent(), 90, Inputs);
            Camera.AddCommandBuffer(CameraEvent.AfterGBuffer, geometry);

            lit = new CommandBuffer { name = "VRMod.Upscaling.AfterLighting" };
            UpscalingProfiler.Mark(lit, Name + " eye: shadows and lighting drawn");
            Camera.AddCommandBuffer(CameraEvent.AfterLighting, lit);

            inputs = new CommandBuffer { name = "VRMod.Upscaling.AfterForwardAlpha" };
            UpscalingProfiler.Mark(inputs, Name + " eye: sky and transparent objects drawn");
            inputs.IssuePluginEventAndData(UpscalingNative.SceneInputsGetEvent(), 100, Inputs);
            inputs.Blit(BuiltinRenderTextureType.MotionVectors, Motion);
            // Stage 102 reads the blit's bound source, so it follows the blit directly.
            inputs.IssuePluginEventAndData(UpscalingNative.SceneInputsGetEvent(), 102, Inputs);
            inputs.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            Camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, inputs);

            tail = new CommandBuffer { name = "VRMod.Upscaling.Tail" };
        }

        private void Copy<T>(Camera source) where T : MonoBehaviour
        {
            T original = source.GetComponent<T>();
            if (!original || !original.enabled) return;
            T copy = Object.AddComponent<T>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(original), copy);
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Settings the game changes while playing, taken from the scene camera's own (unmodified) state.</summary>
        internal void Sync(Camera source, int cullingMask, CameraClearFlags clearFlags, Color background, RenderingPath path, DepthTextureMode depthMode)
        {
            Camera.cullingMask = cullingMask;
            if (clearFlags == CameraClearFlags.Depth || clearFlags == CameraClearFlags.Nothing)
            {
                // The eye texture starts empty; there is no earlier camera's image to keep.
                clearFlags = CameraClearFlags.SolidColor; background = UnityEngine.Color.black;
            }
            Camera.clearFlags = clearFlags;
            Camera.backgroundColor = background;
            Camera.renderingPath = path;
            Camera.nearClipPlane = source.nearClipPlane;
            Camera.farClipPlane = source.farClipPlane;
            Camera.allowHDR = source.allowHDR;
            Camera.opaqueSortMode = source.opaqueSortMode;
            Camera.transparencySortMode = source.transparencySortMode;
            Camera.layerCullSpherical = source.layerCullSpherical;
            Camera.depthTextureMode = depthMode | DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        /// <summary>Renders this eye from the headset's pose and projection for it.</summary>
        internal void Render(Vector3 position, Quaternion rotation, Matrix4x4 eyeProjection)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Object.transform.SetPositionAndRotation(position, rotation);
            projection = eyeProjection;
            Camera.fieldOfView = 2f * Mathf.Atan(1f / eyeProjection.m11) * Mathf.Rad2Deg;
            Camera.aspect = eyeProjection.m11 / eyeProjection.m00;
            Camera.projectionMatrix = eyeProjection;
            Camera.nonJitteredProjectionMatrix = eyeProjection;
            Camera.useJitteredProjectionMatrixForTransparentRendering = false;
            preparedFrame = -1;

            // Event ids are copied into Unity's queued command stream, so the frame number is rebuilt every frame.
            geometry.Clear();
            geometry.IssuePluginEventAndData(UpscalingNative.SceneInputsGetFrameEvent(), Time.frameCount, Inputs);
            UpscalingProfiler.Mark(geometry, Name + " eye: G-buffer drawn");
            geometry.IssuePluginEventAndData(UpscalingNative.SceneInputsGetEvent(), 90, Inputs);

            // With a post-processing layer the jitter is applied from its pre-cull (see UpscalingHooks).
            if (!layerActive) Prepare();
            Camera.Render();
            ++Renders;
            RenderMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Before the post-processing layer of this eye builds its frame.</summary>
        internal void BeforeLayer()
        {
            if (Layer.antialiasingMode != PostProcessLayer.Antialiasing.None) Layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
        }

        /// <summary>
        /// Jitters this frame's projection and hands the native library the camera data it needs to
        /// reproject the previous frame. Runs after the post-processing layer captured the
        /// unjittered projection.
        /// </summary>
        internal void Prepare()
        {
            preparedFrame = -1;
            if (Context == IntPtr.Zero || UpscalingNative.UpscalerState(Context) < 1) return;
            int frame = Time.frameCount;
            if (frame == previousFrame) return; // a second render in one frame is not another temporal frame

            Matrix4x4 original = projection;
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(original, true);
            Matrix4x4 currentVP = gpu * Camera.worldToCameraMatrix;
            Transform transform = Object.transform;
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            float now = Time.realtimeSinceStartup;
            float elapsed = previousFrame == 0 ? 0 : now - previousTime;
            float distance = previousFrame == 0 ? 0 : Vector3.Distance(position, previousPosition);
            float angle = previousFrame == 0 ? 0 : Quaternion.Angle(rotation, previousRotation);
            bool reset = UpscalingPolicy.ResetReasons(previousFrame, frame, elapsed, distance, angle) != UpscalingResetReason.None;
            if (reset) { jitterIndex = 0; previousVP = currentVP; ++Resets; }

            // Jitter is one render pixel wide; upscaling needs more phases (DLSS guide 3.7).
            int sample = (jitterIndex++ % jitterPhases) + 1;
            float x = UpscalingPolicy.Halton(sample, 2) - 0.5f, y = UpscalingPolicy.Halton(sample, 3) - 0.5f;
            Matrix4x4 applied = original;
            applied.m02 -= 2 * x / RenderWidth;
            applied.m12 -= 2 * y / RenderHeight;
            // Derive the SDK offset from the actual GPU projection, including Unity's render-texture Y flip.
            Matrix4x4 gpuJittered = GL.GetGPUProjectionMatrix(applied, true);
            Vector4 anchor = new Vector4(0, 0, -1, 1);
            Vector4 plain = gpu * anchor, jittered = gpuJittered * anchor;
            float jitterX = (jittered.x / jittered.w - plain.x / plain.w) * RenderWidth * 0.5f;
            float jitterY = -(jittered.y / jittered.w - plain.y / plain.w) * RenderHeight * 0.5f;
            Matrix4x4 toPrevious = previousVP * currentVP.inverse;

            Vector3 up = transform.up, right = transform.right, forward = transform.forward;
            var packet = new UpscalingNative.CameraFrame
            {
                Size = (uint)Marshal.SizeOf<UpscalingNative.CameraFrame>(), Version = 1, Frame = frame, Reset = reset ? 1 : 0,
                Projection = Rows(gpu), InverseProjection = Rows(gpu.inverse), ClipToPrevious = Rows(toPrevious), PreviousToClip = Rows(toPrevious.inverse),
                JitterX = jitterX, JitterY = jitterY, MotionScaleX = 1, MotionScaleY = 1,
                Near = Camera.nearClipPlane, Far = Camera.farClipPlane,
                Fov = 2f * Mathf.Atan(1f / original.m11), Aspect = original.m11 / original.m00,
                PosX = position.x, PosY = position.y, PosZ = position.z, UpX = up.x, UpY = up.y, UpZ = up.z,
                RightX = right.x, RightY = right.y, RightZ = right.z, ForwardX = forward.x, ForwardY = forward.y, ForwardZ = forward.z,
                ReversedDepth = SystemInfo.usesReversedZBuffer ? 1 : 0
            };
            if (UpscalingNative.UpscalerSubmitFrame(Context, ref packet) != 0)
            {
                // A context that failed on the render thread since the check above refuses new frames;
                // the manager reports that failure. Anything else is a bad packet.
                if (UpscalingNative.UpscalerState(Context) < 0) return;
                throw new InvalidOperationException("the native library rejected the camera data for the " + Name + " eye");
            }

            Camera.nonJitteredProjectionMatrix = original;
            Camera.useJitteredProjectionMatrixForTransparentRendering = true;
            Camera.projectionMatrix = applied;
            preparedFrame = frame;
            previousVP = currentVP; previousPosition = position; previousRotation = rotation; previousTime = now; previousFrame = frame;
        }

        /// <summary>
        /// End of this eye's image effects: source is the finished, post-processed image at render
        /// size. It is copied for the upscaler, which writes the full-size result to Output. When
        /// the upscaler is not ready this frame, Output gets a plain bilinear copy instead.
        /// </summary>
        internal void Finish(RenderTexture source, RenderTexture destination)
        {
            if (tail == null || !source) return;
            if (source.width != RenderWidth || source.height != RenderHeight)
                throw new InvalidOperationException("the " + Name + " eye rendered at " + source.width + "x" + source.height + " instead of " + RenderWidth + "x" + RenderHeight);
            tail.Clear();
            if (UpscalingProfiler.Active) UpscalingProfiler.Mark(tail, Name + " eye: post-processing done");
            tail.SetRenderTarget(source);
            tail.Blit(source, Color);
            bool evaluate = Prepared && Context != IntPtr.Zero && UpscalingNative.UpscalerState(Context) > 0;
            if (evaluate) { tail.IssuePluginEventAndData(UpscalingNative.UpscalerGetEvaluateEvent(), Time.frameCount, Context); ++Evaluations; }
            else tail.Blit(source, Output);
            if (UpscalingProfiler.Active) UpscalingProfiler.Mark(tail, Name + " eye: upscaled");
            tail.SetRenderTarget(destination ? new RenderTargetIdentifier(destination) : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
            Graphics.ExecuteCommandBuffer(tail);
        }

        // Unity uses column-vector transforms. Transpose them for Streamline's row-vector transforms.
        private static UpscalingNative.Matrix16 Rows(Matrix4x4 m) => new UpscalingNative.Matrix16
        {
            A0 = m.m00, A1 = m.m10, A2 = m.m20, A3 = m.m30, B0 = m.m01, B1 = m.m11, B2 = m.m21, B3 = m.m31,
            C0 = m.m02, C1 = m.m12, C2 = m.m22, C3 = m.m32, D0 = m.m03, D1 = m.m13, D2 = m.m23, D3 = m.m33
        };

        // ---------------------------------------------------------------- teardown

        /// <summary>Removes the eye camera. The native contexts and textures stay until Retire/Collect.</summary>
        internal void Detach()
        {
            if (tailComponent) tailComponent.Eye = null;
            if (Camera)
            {
                if (started != null) Camera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, started);
                if (geometry != null) Camera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, geometry);
                if (lit != null) Camera.RemoveCommandBuffer(CameraEvent.AfterLighting, lit);
                if (inputs != null) Camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, inputs);
                Camera.targetTexture = null;
            }
            geometry?.Release(); inputs?.Release(); tail?.Release(); started?.Release(); lit?.Release();
            geometry = inputs = tail = started = lit = null;
            if (Object) UnityEngine.Object.Destroy(Object);
            Object = null; Camera = null; Layer = null; tailComponent = null;
        }

        /// <summary>Asks the render thread to let go of the native contexts.</summary>
        internal void Retire()
        {
            if (Inputs == IntPtr.Zero && Context == IntPtr.Zero) return;
            var commands = new CommandBuffer { name = "VRMod.Upscaling.Retire" };
            try
            {
                if (Context != IntPtr.Zero) commands.IssuePluginEventAndData(UpscalingNative.UpscalerGetEvaluateEvent(), -1, Context);
                if (Inputs != IntPtr.Zero) commands.IssuePluginEventAndData(UpscalingNative.SceneInputsGetEvent(), 199, Inputs);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally { commands.Release(); }
        }

        /// <summary>Frees the native contexts and textures once the render thread has retired them. True when done.</summary>
        internal bool Collect()
        {
            if (Inputs != IntPtr.Zero && UpscalingNative.SceneInputsState(Inputs) != -1) return false;
            if (Context != IntPtr.Zero)
            {
                if (UpscalingNative.UpscalerState(Context) != -1) return false;
                UpscalingNative.UpscalerDestroy(Context); Context = IntPtr.Zero;
            }
            if (Inputs != IntPtr.Zero) { UpscalingNative.SceneInputsDestroy(Inputs); Inputs = IntPtr.Zero; }
            foreach (RenderTexture texture in textures)
            {
                if (!texture) continue;
                texture.Release(); UnityEngine.Object.Destroy(texture);
            }
            textures.Clear();
            return true;
        }
    }

    /// <summary>Last image effect of an eye camera; see UpscalingEye.Finish.</summary>
    internal sealed class UpscalingEyeTail : MonoBehaviour
    {
        internal UpscalingEye Eye;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            try { Eye?.Finish(source, destination); }
            catch (Exception e) { Upscaling.Fail("the upscaling step failed: " + e.Message); }
            finally { Graphics.Blit(source, destination); }
        }
    }
}
