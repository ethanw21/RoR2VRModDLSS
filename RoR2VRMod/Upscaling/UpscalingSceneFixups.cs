using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VRMod
{
    /// <summary>
    /// Scene changes that only apply while upscaling: sharper texture mips for the reduced render
    /// size, and usable motion vectors for particles. Both are undone when upscaling stops.
    /// Ported from UpscalerSession.SceneFixups.cs of Risk of Rain Upscaling (Xander Frangos, MIT).
    /// </summary>
    internal static class UpscalingSceneFixups
    {
        // Seconds after attaching for the first rescans; afterwards one every RepeatSeconds.
        // Each scan walks every loaded texture and particle renderer, so they are kept rare.
        private static readonly float[] Schedule = { 2f, 5f, 10f, 20f };
        private const float RepeatSeconds = 30f;

        private static ManualLogSource Log => VRMod.StaticLogger;

        private static bool active;
        private static float mipBias, startTime, nextScan;
        private static int scans;

        // Built-in Unity has no global LOD bias, so each mipmapped texture is biased and its
        // original value remembered. Textures loaded later are picked up by the rescans.
        private static readonly Dictionary<Texture2D, float> biased = new Dictionary<Texture2D, float>();
        // The HUD samples sprite and RawImage textures at full resolution: they keep their own bias.
        private static readonly HashSet<Texture> interfaceTextures = new HashSet<Texture>();
        // The game's particle shaders have no MotionVectors pass, and Unity's per-object fallback
        // writes garbage for world-space particle geometry. Camera motion reprojects their depth
        // instead, which matches world-anchored particles. Loaded prefabs are included, so new
        // clones inherit the setting.
        private static readonly Dictionary<Renderer, MotionVectorGenerationMode> particleMotion = new Dictionary<Renderer, MotionVectorGenerationMode>();
        private static readonly Dictionary<Shader, bool> motionVectorShaders = new Dictionary<Shader, bool>();
        private static readonly List<Material> rendererMaterials = new List<Material>();

        internal static void Begin(float bias)
        {
            End();
            active = true; mipBias = bias; scans = 0;
            startTime = Time.unscaledTime;
            Scan();
        }

        internal static void Tick()
        {
            if (!active || Time.unscaledTime < nextScan) return;
            Scan();
        }

        internal static void End()
        {
            if (!active) return;
            active = false;
            foreach (KeyValuePair<Texture2D, float> pair in biased)
                if (pair.Key && Math.Abs(pair.Key.mipMapBias - (pair.Value + mipBias)) < 0.0001f) pair.Key.mipMapBias = pair.Value;
            biased.Clear();
            interfaceTextures.Clear();
            foreach (KeyValuePair<Renderer, MotionVectorGenerationMode> pair in particleMotion)
                if (pair.Key && pair.Key.motionVectorGenerationMode == MotionVectorGenerationMode.Camera) pair.Key.motionVectorGenerationMode = pair.Value;
            particleMotion.Clear();
        }

        private static void Scan()
        {
            float sinceStart = Time.unscaledTime - startTime;
            float next = RepeatSeconds;
            foreach (float second in Schedule) if (second > sinceStart + 0.5f) { next = second - sinceStart; break; }
            nextScan = Time.unscaledTime + next;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int textures = mipBias != 0 ? ApplyMipBias() : 0;
                int particles = ApplyParticleMotion();
                if (scans++ == 0 || textures > 0 || particles > 0)
                    Log.LogInfo("[VR upscaling] Scene scan: mip bias on " + textures + " new texture(s) (" + biased.Count + " total), camera motion on " + particles +
                        " new particle renderer(s) (" + particleMotion.Count + " total), " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
            }
            catch (Exception e) { Log.LogWarning("[VR upscaling] Scene scan failed: " + e.Message); }
        }

        private static int ApplyMipBias()
        {
            interfaceTextures.Clear();
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>()) if (sprite && sprite.texture) interfaceTextures.Add(sprite.texture);
            foreach (UnityEngine.UI.RawImage image in Resources.FindObjectsOfTypeAll<UnityEngine.UI.RawImage>()) if (image && image.texture) interfaceTextures.Add(image.texture);
            int added = 0;
            foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (!texture || texture.mipmapCount <= 1 || interfaceTextures.Contains(texture) || biased.ContainsKey(texture)) continue;
                biased.Add(texture, texture.mipMapBias);
                texture.mipMapBias += mipBias;
                ++added;
            }
            // Biased before the HUD first showed it (a RawImage assigned later): give it back.
            foreach (Texture texture in interfaceTextures)
            {
                if (texture is Texture2D texture2D && biased.TryGetValue(texture2D, out float original))
                {
                    if (Math.Abs(texture2D.mipMapBias - (original + mipBias)) < 0.0001f) texture2D.mipMapBias = original;
                    biased.Remove(texture2D);
                }
            }
            return added;
        }

        private static int ApplyParticleMotion()
        {
            int added = 0;
            foreach (ParticleSystemRenderer renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>()) if (UseCameraMotion(renderer)) ++added;
            foreach (TrailRenderer renderer in Resources.FindObjectsOfTypeAll<TrailRenderer>()) if (UseCameraMotion(renderer)) ++added;
            foreach (LineRenderer renderer in Resources.FindObjectsOfTypeAll<LineRenderer>()) if (UseCameraMotion(renderer)) ++added;
            return added;
        }

        private static bool UseCameraMotion(Renderer renderer)
        {
            if (!renderer || renderer.motionVectorGenerationMode != MotionVectorGenerationMode.Object || particleMotion.ContainsKey(renderer)) return false;
            renderer.GetSharedMaterials(rendererMaterials);
            foreach (Material material in rendererMaterials) if (material && HasMotionVectorsPass(material)) return false;
            particleMotion.Add(renderer, renderer.motionVectorGenerationMode);
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            return true;
        }

        private static bool HasMotionVectorsPass(Material material)
        {
            Shader shader = material.shader;
            if (shader && motionVectorShaders.TryGetValue(shader, out bool cached)) return cached;
            bool found = false;
            for (int i = 0; i < material.passCount && !found; ++i) found = string.Equals(material.GetPassName(i), "MotionVectors", StringComparison.OrdinalIgnoreCase);
            if (shader) motionVectorShaders[shader] = found;
            return found;
        }
    }
}
