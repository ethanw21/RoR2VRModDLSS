# Resurrected VRMod 1.2.4 test build: install, clean-up and upscaling test

Build 1.2.4 is the upscaling of 1.2.1 and nothing else. Everything that builds 1.2.2 and 1.2.3 added to look into the black screen has been taken out again: the eye texture guard, the start-up report, the `OpenXR layers` switch and the four settings under `[Headset compatibility]`. Apart from the version number, the source is the same as 1.2.1.

It has been compiled and packaged, but it has **not been run in a headset**. The upscaling fixes in it have been waiting for a headset test since 1.2.1. That is what section 4 is for.

- Source: branch `claude/vr-dlss-upscaling` (pull request #1), commit `9f41bb9`.
- Native upscaling library: unchanged since 1.2.1 (built by GitHub Actions from commit `4a37fb7`).

## 1. Get the files

- **Git:** in your clone of the repo, `git checkout test-builds` and then `git pull`.
- **Browser:** download <https://github.com/ethanw21/RoR2VRModDLSS/archive/refs/heads/test-builds.zip> and unzip it.

Everything for this build is in the folder `1.2.4`.

## 2. Install

**Close the game first.**

Open your profile's BepInEx folder by pasting this into the File Explorer address bar:

```
%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx
```

**With 1.2.1, 1.2.2 or 1.2.3 installed, one file changes:**

| From the `1.2.4` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\VRMod.dll` | `plugins\Blowntobytes-Resurrected_VRMod\` (replace the existing file) |

The other files in the `1.2.4` folder are byte for byte the same as in every build since 1.2.1. They are there so the folder is complete on its own. Copying all of them does no harm:

| From the `1.2.4` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\upscaling\` (the whole folder) | `plugins\Blowntobytes-Resurrected_VRMod\` (merges into the existing `upscaling` folder) |
| `copy-to-patchers\VRPatcher.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |
| `copy-to-patchers\VRMod.Upscaling.Native.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |

Leave alone, as before: the `upscaling\D3D11\` folder with its six NVIDIA files, `VRMod.dll.bak` and `VRPatcher.dll.bak` (your 1.1.1 backups), and every other file in those two folders. Neither folder may hold a second `VRMod*.dll` or `VRPatcher*.dll` ending in `.dll`.

Optional check that the right files are in place (PowerShell, `Get-FileHash <file>`):

| File | SHA-256 |
| --- | --- |
| `VRMod.dll` | `9709EB9044CA1631AB464E38B2C8320ABFA289C2A9C7DB1D1DEC84760E2CD99D` |
| `VRPatcher.dll` (same since 1.2.1) | `A06FAE4EC9F7C2538F75EBB920DDA4E2B8AF2BC433BA43D14CC697175F5C170B` |
| `VRMod.Upscaling.Native.dll` (same since 1.2.1) | `4169BC798B50457DDADA553D1BE943CD3F5D090A4987883F3242F2E39077DFEF` |

## 3. Clean up what the black screen tests left on the PC

Builds 1.2.2 and 1.2.3 wrote to two places on the PC, both inside the BepInEx folder. Build 1.2.4 does not read either of them. With the game closed:

| What | Where | Do |
| --- | --- | --- |
| The report folder | `VRMod-Display\` (holds `report.txt` and `state.txt`) | Delete the folder. |
| Four settings | `config\VRMod.cfg`, the section `[Headset compatibility]` | Delete the section: the line `[Headset compatibility]` and everything under it up to the next line that starts with `[`. Leaving it does no harm either; nothing reads it. |

Those builds changed nothing else on the PC. They did not touch the registry, the OpenXR settings of Windows or any other program; the `OpenXR layers` switch only applied inside the game while it ran.

**One setting to put back.** For one of the black screen tests you switched the upscaling library off. In `config\VRMod.cfg`, section `[Upscaling]`, set:

```
Load upscaling library = true
```

Without it there is no upscaling to test.

**If you did any of these while troubleshooting,** they were steps from the earlier guides and can be undone:

- `dxgi.dll` in the game folder renamed to `dxgi.dll.off` (ReShade): rename it back.
- `VRMod.Upscaling.Native.dll` in the patchers folder renamed to `VRMod.Upscaling.Native.dll.off`: delete the `.off` file once the `.dll` is there again.
- Another program's OpenXR layer switched off in that program's own app: switch it back on.
- **OpenXR Quad Views** switched off in the Pimax software: set it however you normally have it. The mod asks the headset for two views, one per eye, and has not been tested with that option on.

## 4. Upscaling test (about ten minutes)

Before starting, check that the standalone **Risk of Rain Upscaling** mod is still disabled in the mod manager.

Use whichever OpenXR runtime you normally play on, and say which one it was when you send the logs. The eye texture size differs between runtimes, and the cost of upscaling depends on it.

The first headset test (build 1.2.0, 4940x4136 per eye) had four problems. Three have a fix that needs confirming, and the fourth needs numbers:

| Seen in 1.2.0 | What changed | What to look for now |
| --- | --- | --- |
| Right eye black during runs | The last step, handing each eye's finished image to the headset, was rebuilt | Both eyes show the world |
| Freeze after flicking through presets | A setting is applied about a second after you stop changing it, and the old upscalers are released first | No freeze |
| DLSS switched itself off | NVIDIA's "over the video memory budget" warning is logged and no longer treated as a failure. A guard pauses upscaling if the game stays over budget for five seconds | DLSS stays on, or the chat box says "VR upscaling paused" |
| No speedup | Nothing yet. The log now measures frame times and the GPU time of every step | The numbers in the logs you send back |

The log takes a reading every minute and the GPU profile records the first minute after each change, so **stay about 75 seconds in each step** before moving on. All settings are in the pause menu, VR tab.

1. **Baseline.** Before starting a run, set **DLSS / FSR upscaling** to off. Start a run and stay in the first stage for a minute. This records the frame time at full resolution.
2. **Same settings as last time.** Turn upscaling on with **Upscaling quality** Performance, **Upscaler** DLSS, **DLSS preset** M. Look for:
   - Does the right eye show the world now?
   - Do both eyes agree (normal depth, nothing doubled, nothing upside down)?
   - Does it feel faster or slower than step 1?
3. **DLSS preset K.** Same questions. K is the previous DLSS model and should cost less GPU time than M.
4. **Upscaler FSR 3.** Same questions.
5. **The freeze test.** Flick quickly back and forth through **Upscaling quality** and **DLSS preset** for ten seconds, then stop. Expected: the world drops to full resolution while you flick, and upscaling comes back about a second after you stop. No freeze.
6. **Optional: DLAA.** At a high headset resolution this may not fit in video memory. Expected in that case: after a few seconds the chat box says "VR upscaling paused" and the game carries on at full resolution. That is the guard working, not a crash.

When a setting changes, the picture going to full resolution for about a second is normal.

If the game does freeze, give it up to a minute before Alt+F4, and send the logs anyway.

If something goes wrong early, for example the right eye is still black in step 2, there is no need to finish the list. Do the steps you can and send the logs.

## 5. Send back

The logs are overwritten the next time the game starts, so copy them before you launch again. From the BepInEx folder:

- `LogOutput.log`
- `VRMod-Upscaling\native.log`
- `VRMod-Upscaling\profile.log`
- `VRMod-Upscaling\sl.log`

Upload them to the project thread with a line or two on what you saw in each step, and which OpenXR runtime you used.

To confirm the build that ran, `LogOutput.log` contains `Loading [Resurrected VRMod 1.2.4]`. The log lists library paths that include your Windows user name; that is fine for the project thread, but look it over before sharing it anywhere public.

## 6. Reading the numbers yourself

Search `LogOutput.log` for `[VR upscaling]`.

| Line contains | Meaning |
| --- | --- |
| `Before turning on, the game averaged ...` | The frame time at full resolution, from step 1. |
| `Status` | One reading a minute: average and slowest frame time with upscaling on, how many frames each eye was shown (`shown in the headset left N / right N`), and video memory in use against the budget. |
| `Image check` | A one-time brightness reading of each eye's images. Two similar values mean both eyes carry a picture. |
| `VR upscaling paused` | The video memory guard stepped in. |

`profile.log` lists the GPU time of every step for the first minute after each change. The rows that matter most are the ones for the upscaler itself (`upscale.feature`) and for drawing each eye.

## 7. Going back

- **Play without upscaling:** turn **DLSS / FSR upscaling** off in the VR tab. The game then renders at full resolution the normal way.
- **Keep the library from loading at all:** set `Load upscaling library = false` under `[Upscaling]` in `config\VRMod.cfg`.
- **Back to 1.2.1:** copy `1.2.1\copy-to-plugins\VRMod.dll` over the one in `plugins\Blowntobytes-Resurrected_VRMod`.
- **Back to 1.1.1:** with the game closed, delete `VRMod.dll` and rename `VRMod.dll.bak` to `VRMod.dll` in `plugins\Blowntobytes-Resurrected_VRMod`. In `patchers\Blowntobytes-Resurrected_VRMod`, delete `VRPatcher.dll` and `VRMod.Upscaling.Native.dll` and rename `VRPatcher.dll.bak` to `VRPatcher.dll`. The `upscaling` folder can stay; 1.1.1 ignores it.
