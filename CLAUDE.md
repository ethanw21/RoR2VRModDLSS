# CLAUDE.md — VRMod project notes

Persistent notes for AI sessions working on this codebase. Read this before making changes.

## Project identity

- **What:** Unofficial OpenXR rebuild of DrBibop's Risk of Rain 2 VR mod
- **Author:** Blowntobytes (GitHub: `Blowntobytes/RoR2VRMod`)
- **Current version:** 1.0.4 (first public release; version numbers restarted from the 2.22.x pre-releases)
- **Game target:** Risk of Rain 2 on Unity 2021.3.33f1 (current Steam build)
- **Framework:** BepInEx 5.4.x plugin + patcher, MonoMod On./IL. hooks, HookGen (MMHOOK_RoR2.dll)
- **VR stack:** Unity OpenXR 1.9.1, XR Management 4.4.0, XR CoreUtils 2.1.1, InputSystem 1.6.3
- **Plugin ID:** `com.DrBibop.VRMod` (kept for compatibility with mods that depend on it)

## Repository layout

```
RoR2VRMod/                  ← source code (C#)
  VRMod.cs                  ← plugin entry point (BepInPlugin)
  Inputs/
    Controllers.cs           ← VR controller polling loop (UpdateVRInputs), auto-sprint, recenter
    RewiredAddons.cs         ← Rewired CustomController definition, action-to-element maps
    ControllerGlyphs.cs      ← TMP sprite glyphs per controller type (standard/vive/wmr)
    LobbyNavigation.cs       ← character select lobby stick-navigation fix
    ActionAddons.cs           ← adds custom Rewired actions (UISubmitTertiary, etc.)
    SettingsTabs.cs           ← grip-driven settings tab switching
    MenuSubmitGuard.cs        ← one-press-per-activation for menu buttons
    InputTypes/               ← BaseInput subclasses: ButtonInput, HoldableButtonInput, etc.
  Camera/                    ← VR camera, stereo projection fix, recentering, vignette, eye texture guard and OpenXR start-up report (see "Eye texture guard" below)
  MotionControls/            ← hand tracking, per-survivor weapon animations
  Haptics/                   ← Shockwave / bHaptics suit support (optional DLLs)
  UI/                        ← HUD, UI pointer, UI fixes
  Settings/                  ← mod config (ModConfig.cs), settings panel addon
  Upscaling/                 ← DLSS / FSR upscaling, one upscaler per eye (see "Upscaling" below)
  LIV/                       ← LIV mixed-reality capture SDK (unchanged from original)
VREnabler/                  ← preloader patcher (VRPatcher.dll); UpscalingBootstrap.cs loads the native upscaling library
NativeUpscaling/            ← source of VRMod.Upscaling.Native.dll (from Risk of Rain Upscaling, MIT; changes listed in NOTICE.md)
```

## Build toolchain

Builds run in a Linux cloud environment. No Visual Studio required.

```bash
# Compile
cd /root/build
/opt/pwsh/pwsh -File /root/tools/csc.ps1 @out/VRMod.dll.rsp

# Copy DLL into Thunderstore package
cp /root/build/out/VRMod.dll /root/build/pkg/plugins/VRMod.dll

# Package zip
cd /root/build/pkg && zip -r /root/build/Blowntobytes-VRMod-<VERSION>.zip .
```

- Response file: `/root/build/out/VRMod.dll.rsp` (all references, defines, resource embeds)
- Asset bundle: embedded as a raw manifest resource via `/resource:` in the .rsp
- Reference assemblies: `/root/build/libs/`, `/root/build/mmhook/`, `/root/build/il/`
- Package template: `/root/build/pkg/` (manifest.json, icon.png, plugins/, patchers/)

## Version release checklist

**Every fix or feature gets its own version number. Never reuse or amend a version that has been built.**

1. **Bump version** in `/root/build/pkg/manifest.json`
2. **Build** the DLL, copy to pkg, create the zip
3. **Deliver zip** to user's machine at `C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\VRMod-unofficial-build\`
4. **Commit** the source changes with a message like `2.XX.Y: short description`
5. **Tag** the commit: `git tag v2.XX.Y`
6. **Create git bundle** (incremental from upstream): `git bundle create /root/build/RoR2VRMod-history-<VERSION>.bundle upstream/main..main v2.19.3 v2.19.4 ... v<VERSION>`
7. **Update** `Publish-ToGitHub.ps1` — set `$Version` to the new version
8. **Write release notes** in `release-notes-<VERSION>.md`
9. **Deliver** the bundle, publish script, and release notes to the user's VRMod-unofficial-build folder
10. User runs `Publish-ToGitHub.cmd` on their Windows machine to push to GitHub

**Never amend a commit that has already been tagged or shipped.** Create a new commit and a new version instead.

## Key technical patterns

### Rewired input system

The mod creates a Rewired `CustomController` with 33 elements (0–32). Each element maps to a physical button or axis via `BaseInput` subclasses in the `inputs` array. The input loop in `Controllers.UpdateVRInputs()` calls `UpdateValues()` on each input every frame, which calls `vrControllers.SetButtonValueById(elementId, value)`.

**Critical:** Rewired latches button-down edges the instant `SetButtonValueById` is called with `true`. You cannot set an element to true and then clear it in the same frame — the game will already have seen the press. To suppress an input conditionally, you must **skip the `UpdateValues()` call entirely** for that element, not clear it after the fact.

Action-to-element bindings are in `RewiredAddons.cs` (CreateUIMap / CreateGameplayMap). Glyph sprites are indexed by element ID in `ControllerGlyphs.cs`.

### Element ID reference (important ones)

| Element | Name | Physical button | Action |
|---------|------|----------------|--------|
| 15 | Info | Y (hold) | Scoreboard / profile (HoldableButtonInput) |
| 24 | Pause | Y (press) | Pause menu (ButtonInput) |
| 32 | Load | — (virtual) | UISubmitTertiary / ProperSave Load (lobby remap only) |

Y button (left hand, SecondaryButton) drives **both** elements 15 and 24 via separate input entries. When remapping Y in the lobby, both must be suppressed.

### TypeLoadException isolation

The CLR throws `TypeLoadException` before a method body executes if that method references types from a missing assembly. A try-catch inside the method cannot catch it. To safely reference optional types (e.g. ShockwaveController from ShockwaveManager.dll), isolate the reference into a separate factory method — the JIT only resolves the type when that specific method is called.

```csharp
// WRONG — TypeLoadException kills Init() before try-catch can fire
void Init() { try { new ShockwaveController(); } catch { } }

// RIGHT — JIT only resolves ShockwaveController when CreateShockwave() is called
void Init() { try { CreateShockwave(); } catch { } }
private static GenericHapticsController CreateShockwave() => new ShockwaveController();
```

### Upscaling (1.2.0)

`RoR2VRMod/Upscaling/` renders each eye at a reduced size and upscales it with DLSS or FSR 3. Nothing GPU or VR related can be run in the cloud build environment, so every change here has to be tested in a headset.

- **Native library:** `VRMod.Upscaling.Native.dll` is built from `NativeUpscaling/` on Windows only (`.github/workflows/native-upscaling.yml`). The workflow keeps the newest build on the single-commit branch `ci/native-upscaling-build`, which can be fetched with plain git. The NVIDIA runtime DLLs are never committed; `NativeUpscaling/build/Fetch-Runtime.ps1` fetches them for packaging.
- **Load order:** `VRPatcher` (`UpscalingBootstrap`) loads the library before the game creates its graphics device, because DLSS needs to see the device and swap chain being created. The plugin (`UpscalingNative`) binds the already loaded library. Both stand down when the standalone Risk of Rain Upscaling mod is installed.
- **Frame flow (`Upscaling.cs`):** `Application.onBeforeRender` turns the stereo scene camera into a presenter that draws nothing. At its first pre-cull, two mono eye cameras (`UpscalingEye`) render with `Camera.Render()` from `GetStereoViewMatrix` / the stereo reference camera's `GetStereoProjectionMatrix`, each with its own native depth/motion capture and upscaler. For each eye pass, a hook on the scene camera's `PostProcessLayer.OnRenderImage` (its first image effect) blits that eye's upscaled image to the destination instead of the presenter's empty picture (`Upscaling.Present`). The scene camera is restored at the end of the frame (`WaitForEndOfFrame`), never from a camera callback. 1.2.0 copied the eyes in with a command buffer at `BeforeImageEffects` and restored from `OnPostRender`; the right eye came out black in the headset, most likely because `OnPostRender` runs before a pass's image effects and the restore emptied that buffer first (not confirmed on a headset).
- **One set of upscalers at a time:** a settings change detaches at once but re-attaches only after the setting has rested (`SettleSeconds`) and the old native contexts are collected (`ReadyToAttach`). At headset resolutions two sets do not fit in video memory together.
- **Diagnostics:** `Status` lines every 60 s (frame time, frames shown per eye, video memory), a one-time brightness probe of each eye's textures, and the native GPU profiler (`UpscalingProfiler` marks, `profile.log`). The video memory guard (`CheckVideoMemory`) pauses upscaling after 5 s over the DXGI budget.
- **Package layout:** `patchers/VRMod.Upscaling.Native.dll`; `plugins/upscaling/D3D11/` (six NVIDIA runtime DLLs), `plugins/upscaling/licenses/`, `plugins/upscaling/native-manifest.json`.
- **Private post-processing members** (`PostProcessLayer.m_LegacyCmdBuffer*`, `m_Resources`, `PostProcessManager.m_Volumes`) are reached through reflection in `UpscalingHooks.cs`, because `VRMod.csproj` references `Unity.Postprocessing.Runtime` unpublicized.
- **Settings:** section `[Upscaling]` in `VRMod.cfg`. String settings with a `choices` array show as carousels in the VR tab (`ConfigSetting.choices`, `SettingsAddon.cs`).

### Eye texture guard (1.2.2) and start-up report (1.2.3)

`RoR2VRMod/Camera/EyeTextureGuard.cs` is an `OpenXRFeature` created at runtime next to the controller profiles (`SetupControllerProfiles`). Through `HookGetInstanceProcAddr` it wraps three OpenXR calls: `xrEnumerateViewConfigurationViews` (recommended eye size), `xrEnumerateSwapchainFormats` and `xrCreateSwapchain`.

- **Why:** on a Pimax Dream Air with the Pimax OpenXR runtime 0.1.0, every `xrCreateSwapchain` returned `XR_ERROR_FEATURE_UNSUPPORTED` for the size (6464x5044) and format (29, RGBA8 sRGB) the runtime itself had recommended; the headset stayed black. The 1.2.2 log from that headset showed it refuses every combination (all formats and usage flags, every size down to 512x512, array, static, depth), so the request is not what it objects to. The 1.2.3 report then showed where it fails: inside Pimax's own client library (`pvr_createTextureSwapChainDX` returns result 1 at `PiOpenXR\swapchain.cpp:412`, after "createTexture failed with error:1" in `%LOCALAPPDATA%\Pimax\runtime\pvr_client_<game>_log.txt`), and the same lines appear in that runtime's log for another Unity game on the same PC. Nothing the game asks for changes it. **Closed 2026-11-11:** the tester found that the Pimax software was being misused on that PC; it was not a bug in the runtime, the game or the mod. The guard and the report stay as tools for the next runtime that refuses.
- **What Unity asks for** (from disassembling `UnityOpenXR.dll` 1.9.1): type 9, no extension data, create flags 0, usage 0x21 (colour attachment + sampled), the first runtime format it can use, the recommended sample count, width and height = recommended size x render scale rounded to even, 1 face, 1 layer in MultiPass, 1 mip. It re-reads the recommended size and the format list before every attempt and retries every frame, which is what lets the guard steer the next attempt.
- **Behaviour:** pass-through while the runtime accepts. After three refusals in a row with a request-type error it probes usage flags x colour formats x sizes (each accepted probe is destroyed at once), then applies the best: usage override inside the create call, a format list reduced to one colour format, or a scale on the recommended size.
- **Threads:** the three calls arrive on Unity's render thread. Nothing in the hooks touches Unity objects; messages are queued and written by `EyeTextureGuardDriver.Update`.
- **When everything is refused (1.2.3):** `AfterEverythingRefused` starts the session itself (`OpenXRJournal.BeginSession`) and asks once more, but only if nobody has started it yet and the session is in state READY. If that helps, Unity's own later `xrBeginSession` is answered with success without reaching the runtime. **Correction from the first headset report:** this was written on the belief that Unity creates the eye textures before `xrBeginSession`. The call record shows the opposite for plugin 1.9.1: `xrBeginSession` is made once, successfully, before the first `xrCreateSwapchain`. So the path did not apply there ("The session had already been started when Unity asked"). Comments in `EyeTextureGuard.cs` and one of its log messages still state the old belief; correct them with the next build.
- **Start-up record (`OpenXRJournal.cs`, 1.2.3):** every other session and frame call Unity looks up is wrapped with a closure delegate that counts and passes through (`Wrap`, shapes in `Shapes`). Also read: the D3D11 device in `XrGraphicsBindingD3D11KHR` (COM vtable calls: feature level, creation flags, adapter name and LUID) against the LUID from `xrGetD3D11GraphicsRequirementsKHR`, the refused interaction profiles, and the runtime's `XR_EXT_debug_utils` messages (the extension is requested by setting the guard's `openxrExtensionStrings` through reflection before the loader starts). `Forget` clears the record when the loader is set up. `Report()` is written once per session by `EyeTextureGuard.Flush`: 1 s after a probe, 4 s after acceptance, or at shutdown.
- **Surroundings (`OpenXRSurroundings.cs`, 1.2.3):** lists the implicit API layers from the registry (`HKLM` and `HKCU` `SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit`) and switches them off for this process by setting each manifest's `disable_environment` variable before `InitializeLoaderSync` (setting `OpenXR layers`: Auto / Keep / Skip). Auto remembers the last start in `BepInEx/VRMod-Display/state.txt` (layers kept or skipped, outcome, runtime, and whether going without layers was no better) and never alternates forever. `RuntimeLogExcerpts` looks for the runtime's own recent `.log` / `.txt` files in the data folders and the runtime's program folder, ranks them (mentions of swapchains, OpenXR, the game), and prints the end of each with repeats collapsed. Everything under `[VR display]` is mirrored into `BepInEx/VRMod-Display/report.txt`, which is appended to across starts.
- **Test without a headset:** `tests/eyeguard` in the build environment (`/mnt/project-files/vrmod-dlss/build-env/`) runs the guard against a pretend runtime written in C with different acceptance rules.

### Lobby navigation

The game's character select screen ships buttons with `Navigation.Mode = None`, which makes them unreachable by stick when the input source is Gamepad. `LobbyNavigation.cs` periodically sweeps for these and sets them to `Automatic`. The `IsInLobby` property exposes whether the character select screen is active for use by the input remap logic.

### Menu input source

In `ControllerGlyphs.ChangedToCustom`, when the active controller switches to Custom (our VR controller), we set `currentInputSource = Gamepad`. This makes all menus navigable with the left stick and A/B buttons — no laser pointer needed. This applies everywhere: title screen, settings, and pause menu.

## User's environment

- **Headset:** Meta Quest 3 via Virtual Desktop (VDXR OpenXR runtime)
- **Friend's headset:** Oculus Rift CV1 (Meta/Oculus runtime)
- **Game install:** two Steam library paths (E: and C: drives)
- **Mod manager:** r2modman
- **Build output folder:** `C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\VRMod-unofficial-build\`
- **r2modman profile:** `BitsySeekers1.1` (at `C:\Users\atyou\AppData\Roaming\r2modmanPlus-local\RiskOfRain2\profiles\`)
- **Active mods alongside VRMod:** ProperSave 3.0.7, ProperSave_NoDeathDelete, SeekersPatcher, MiscFixes, RoR2BepInExPack

## Git conventions

- Remote `origin`: `https://github.com/Blowntobytes/RoR2VRMod.git` (user's fork)
- Remote `upstream`: `https://github.com/DrBibop/RoR2VRMod.git` (original mod)
- Branch: `main` only
- Commit message format: `<version>: <short description>` (e.g. `2.19.5: fix Y button firing both Load and Scoreboard in lobby`)
- Non-version commits: `chore: <description>` for cleanup
- Git bundles for GitHub publishing are incremental from `upstream/main` (not from previous versions), keeping them ~100 KB
- User's git identity: `Blowntobytes` / `147319775+Blowntobytes@users.noreply.github.com`

## Version history (2.18.1+)

| Version | Summary |
|---------|---------|
| 2.18.1 | One press = one menu activation; UISubmitAlt moved from A to X |
| 2.18.2 | Lobby grips jump between sides; recenter re-parks open menus |
| 2.18.3 | UI camera on Camera Offset node; lobby right grip geometric select |
| 2.18.4 | Revert to 2.18.1 base; lobby automatic navigation; grips jump sides |
| 2.18.5 | Submit is A only; lobby jump verification and shortcut logging |
| 2.18.6 | Bindings match glyph artwork (A=Submit, triggers=tabs, grips=submenus) |
| 2.18.7 | UIPageLeft/Right on triggers for character select navigation |
| 2.18.8 | Triggers drive page nav; grips unbound in menus; lobby helper nav-mode only |
| 2.18.9 | Settings-tab actions (UISubmenuLeft/Right) back on grips |
| 2.19.0 | Settings tabs via MoveHeaderLeft/Right from grip poll |
| 2.19.1 | Auto Sprint fix — send fresh press edges instead of holding |
| 2.19.2 | Cross-headset OpenXR support (Rift CV1, Index, Bigscreen Beyond) |
| 2.19.3 | Fix TypeLoadException hang when haptics DLLs are missing |
| 2.19.4 | ProperSave compatibility — Y triggers Load in lobby |
| 2.19.5 | Fix Y button flickering (skip elements in loop, don't clear after) |
| 2.19.6 | Constrain joystick navigation in lobby panels (superseded by 2.19.8) |
| 2.19.7 | Withdrawn build — did not start VR |
| 2.19.8 | Confine the left stick to one side of the character select screen |
| 2.19.9 | Stop the stick walking the highlight off the right edge of the character select screen; renamed to "Resurrected VRMod" |
| 2.19.10 | Character-select off-screen fix v2: keep only controls that project inside the visible screen (UI camera) |
| 2.19.11 | Character-select off-screen fix v3 (anchor kept; canvas world-bounds); removed right-stick-click recenter so ping works |
| 2.19.12 | Character-select off-screen fix v4: bound navigation to the UI camera's view frustum |
| 2.19.13 | Diagnostic build: log the lobby canvas/camera/control layout to find the real off-screen cause |
| 2.19.14 | Character-select off-screen fix (root-caused): build navigation only from controls inside the canvas's own local rect |
| 2.19.15 | Reverted the 2.19.14 filter (it did nothing); shrink the character-select canvas to ~53° so the rules panel fits in view |
| 2.19.17 | Diagnostic build: trace the actual selection (Lobby select: lines) - geometry fixes failed, so log exactly which control the highlight lands on |
