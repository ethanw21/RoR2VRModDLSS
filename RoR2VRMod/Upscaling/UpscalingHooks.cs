using MonoMod.RuntimeDetour;
using RoR2;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace VRMod
{
    /// <summary>
    /// The game-side hooks upscaling needs. All of them pass straight through unless the camera
    /// involved belongs to the active upscaling attachment.
    /// </summary>
    internal static class UpscalingHooks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<PostProcessVolume> retargeted = new List<PostProcessVolume>();
        private static readonly List<Hook> hooks = new List<Hook>(); // kept referenced for as long as the game runs
        private static bool installed;

        // Private members of the post-processing package. The project references that assembly
        // as shipped with the game (not publicized), so they are reached through reflection.
        private static readonly FieldInfo resourcesField = typeof(PostProcessLayer).GetField("m_Resources", Any);
        private static readonly FieldInfo volumesField = typeof(PostProcessManager).GetField("m_Volumes", Any);
        private static readonly FieldInfo[] legacyBufferFields =
        {
            typeof(PostProcessLayer).GetField("m_LegacyCmdBufferBeforeReflections", Any),
            typeof(PostProcessLayer).GetField("m_LegacyCmdBufferBeforeLighting", Any),
            typeof(PostProcessLayer).GetField("m_LegacyCmdBufferOpaque", Any),
            typeof(PostProcessLayer).GetField("m_LegacyCmdBuffer", Any)
        };
        private static PostProcessManager volumesOwner;
        private static List<PostProcessVolume> volumes;

        /// <summary>Gives a new layer the shader resources of an existing one when the copy lacks them.</summary>
        internal static void ShareResources(PostProcessLayer from, PostProcessLayer to)
        {
            if (resourcesField == null) return;
            object existing = resourcesField.GetValue(to);
            if (existing == null || (existing is UnityEngine.Object unityObject && !unityObject)) resourcesField.SetValue(to, resourcesField.GetValue(from));
        }

        /// <summary>Empties the command buffers a layer keeps on its camera. False when one could not be reached.</summary>
        internal static bool ClearCommandBuffers(PostProcessLayer layer)
        {
            bool complete = true;
            foreach (FieldInfo field in legacyBufferFields)
            {
                if (field == null) { complete = false; continue; }
                (field.GetValue(layer) as UnityEngine.Rendering.CommandBuffer)?.Clear();
            }
            return complete;
        }

        internal static void Install()
        {
            if (installed) return;
            installed = true;

            MethodInfo preCull = typeof(PostProcessLayer).GetMethod("OnPreCull", Any);
            MethodInfo preRender = typeof(PostProcessLayer).GetMethod("OnPreRender", Any);
            MethodInfo renderImage = typeof(PostProcessLayer).GetMethod("OnRenderImage", Any, null, new[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
            if (preCull == null || preRender == null || renderImage == null) throw new MissingMethodException("PostProcessLayer.OnPreCull/OnPreRender/OnRenderImage");
            foreach (FieldInfo field in legacyBufferFields) if (field == null) throw new MissingFieldException("PostProcessLayer legacy command buffers");
            hooks.Add(new Hook(preCull, (Action<Action<PostProcessLayer>, PostProcessLayer>)LayerPreCull));
            hooks.Add(new Hook(preRender, (Action<Action<PostProcessLayer>, PostProcessLayer>)LayerPreRender));
            hooks.Add(new Hook(renderImage, (Action<Action<PostProcessLayer, RenderTexture, RenderTexture>, PostProcessLayer, RenderTexture, RenderTexture>)LayerRenderImage));

            // Optional: without it, effects aimed at the player's own camera (for example the
            // low-health screen tint) do not show while upscaling.
            MethodInfo updateSettings = typeof(PostProcessManager).GetMethod("UpdateSettings", Any, null, new[] { typeof(PostProcessLayer), typeof(Camera) }, null);
            if (updateSettings != null)
                hooks.Add(new Hook(updateSettings, (Action<Action<PostProcessManager, PostProcessLayer, Camera>, PostProcessManager, PostProcessLayer, Camera>)ManagerUpdateSettings));
            else
                VRMod.StaticLogger.LogWarning("[VR upscaling] PostProcessManager.UpdateSettings was not found; camera-specific screen effects will not show while upscaling.");

            On.RoR2.SceneCamera.OnPreCull += SceneCameraPreCull;
            On.RoR2.SceneCamera.OnPreRender += SceneCameraPreRender;
            On.RoR2.SceneCamera.OnPostRender += SceneCameraPostRender;
        }

        // The eye's layer: build the post-processing for this frame, then jitter the projection.
        // The scene camera's own layer: stays out while the eyes are being presented.
        private static void LayerPreCull(Action<PostProcessLayer> orig, PostProcessLayer self)
        {
            UpscalingEye eye = Upscaling.EyeFor(self);
            if (eye != null)
            {
                eye.BeforeLayer();
                orig(self);
                try { eye.Prepare(); }
                catch (Exception e) { Upscaling.Fail("preparing the " + eye.Name + " eye failed: " + e.Message); }
                return;
            }
            if (Upscaling.SkipRealLayer(self)) return;
            orig(self);
        }

        private static void LayerPreRender(Action<PostProcessLayer> orig, PostProcessLayer self)
        {
            if (Upscaling.EyeFor(self) == null && Upscaling.SkipRealLayer(self)) return;
            orig(self);
        }

        // The layer is the first image effect of its camera. On the scene camera, while the eyes are
        // being presented, this is where each eye pass gets its upscaled image (Upscaling.Present).
        private static void LayerRenderImage(Action<PostProcessLayer, RenderTexture, RenderTexture> orig, PostProcessLayer self, RenderTexture source, RenderTexture destination)
        {
            bool presented = false;
            try { presented = Upscaling.Present(self, destination); }
            catch (Exception e) { Upscaling.Fail("showing an upscaled eye failed: " + e.Message); }
            if (!presented) orig(self, source, destination);
        }

        // The game aims some post-processing volumes at one camera's layer (PostProcessVolume.targetLayer,
        // set by CameraRigController.SetPostProcessVolumeExclusive). The eyes render in place of the
        // scene camera, so while an eye's layer gathers its settings those volumes count for it.
        private static void ManagerUpdateSettings(Action<PostProcessManager, PostProcessLayer, Camera> orig, PostProcessManager self, PostProcessLayer layer, Camera camera)
        {
            PostProcessLayer realLayer = Upscaling.RealLayerFor(layer);
            if (ReferenceEquals(realLayer, null)) { orig(self, layer, camera); return; }
            if (!ReferenceEquals(volumesOwner, self)) { volumesOwner = self; volumes = volumesField?.GetValue(self) as List<PostProcessVolume>; }
            if (volumes == null) { orig(self, layer, camera); return; }
            retargeted.Clear();
            foreach (PostProcessVolume volume in volumes)
            {
                if (ReferenceEquals(volume, null) || !ReferenceEquals(volume.targetLayer, realLayer)) continue;
                volume.targetLayer = layer;
                retargeted.Add(volume);
            }
            try { orig(self, layer, camera); }
            finally
            {
                foreach (PostProcessVolume volume in retargeted) volume.targetLayer = realLayer;
                retargeted.Clear();
            }
        }

        // The game's per-camera events (character fading, weather, sprint particles, ...) already ran
        // for both eye cameras; the scene camera draws no world on a presented frame.
        private static void SceneCameraPreCull(On.RoR2.SceneCamera.orig_OnPreCull orig, SceneCamera self)
        {
            if (Upscaling.SkipRealSceneCamera(self, true)) return;
            orig(self);
        }

        private static void SceneCameraPreRender(On.RoR2.SceneCamera.orig_OnPreRender orig, SceneCamera self)
        {
            if (Upscaling.SkipRealSceneCamera(self, false)) return;
            orig(self);
        }

        private static void SceneCameraPostRender(On.RoR2.SceneCamera.orig_OnPostRender orig, SceneCamera self)
        {
            if (Upscaling.SkipRealSceneCamera(self, false)) return;
            orig(self);
        }
    }
}
