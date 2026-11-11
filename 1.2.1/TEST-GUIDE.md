# Resurrected VRMod 1.2.1 test build: install and test guide

This build answers the first headset test of 1.2.0 (right eye black, no speedup, freeze after flicking through DLSS presets). It has been compiled and packaged, but it has **not been run in a headset yet**. That is what this guide is for.

- Source: branch `claude/vr-dlss-upscaling` (pull request #1), commit `34a51f4`.
- Native library: built by GitHub Actions from commit `4a37fb7` (its source is the same in `34a51f4`).

## What changed since 1.2.0

- **Right eye black:** both eyes were being upscaled, but the last step, handing each finished image to the headset, did not reach the right eye. That step was rebuilt.
- **Freeze when flicking through settings:** each stop on a carousel built a new pair of upscalers while the old pair still held its video memory. A setting is now applied about a second after you stop changing it, and the old upscalers are released first.
- **DLSS switching itself off:** NVIDIA's "over the video memory budget" warning was treated as a failure. It is now only logged.
- **New video memory guard:** if the game stays over its video memory budget for five seconds with upscaling on, upscaling pauses, the game returns to full resolution, and the chat box says "VR upscaling paused". Changing any upscaling setting tries again.
- **Measurements:** the log now records frame times with upscaling on and off, how often each eye was shown, video memory use, and the GPU time of every step. This is what will explain the missing speedup.

## 1. Get the files

Use either way.

- **Git:** in your clone of the repo, run `git fetch origin` and then `git checkout test-builds`. For later builds, `git pull` on that branch.
- **Browser:** download <https://github.com/ethanw21/RoR2VRModDLSS/archive/refs/heads/test-builds.zip> and unzip it.

Everything for this build is in the folder `1.2.1`.

## 2. Install

**Close the game first.**

Open your profile's BepInEx folder by pasting this into the File Explorer address bar:

```
%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx
```

Copy these files and let Windows replace the existing ones:

| From the `1.2.1` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\VRMod.dll` | `plugins\Blowntobytes-Resurrected_VRMod\` |
| `copy-to-plugins\upscaling\` (the whole folder) | `plugins\Blowntobytes-Resurrected_VRMod\` (merges into the existing `upscaling` folder) |
| `copy-to-patchers\VRPatcher.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |
| `copy-to-patchers\VRMod.Upscaling.Native.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |

The quick way is to select everything inside `copy-to-plugins` and drop it into `plugins\Blowntobytes-Resurrected_VRMod`, then everything inside `copy-to-patchers` into `patchers\Blowntobytes-Resurrected_VRMod`.

Leave these alone:

- `plugins\Blowntobytes-Resurrected_VRMod\upscaling\D3D11\` with its six NVIDIA files (`nvngx_dlss.dll` and five `sl.*.dll`). They are unchanged from 1.2.0 and are not in this repo. If that folder is ever missing, copy the six files from `plugins\XanderChaos-RiskOfRainUpscaling\RiskOfRainUpscaling\runtimes\D3D11\`.
- `VRMod.dll.bak` and `VRPatcher.dll.bak`. They are your 1.1.1 backups.
- Every other file in those two folders.

Two things to check:

- Neither folder may hold a second `VRMod*.dll` or `VRPatcher*.dll` ending in `.dll`. BepInEx would load both.
- The standalone **Risk of Rain Upscaling** mod stays disabled in the mod manager, as it was for the last test.

Optional, instead of copying by hand: open PowerShell inside the `1.2.1` folder and run

```powershell
$bep = "$env:APPDATA\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx"
Copy-Item ".\copy-to-plugins\*"  "$bep\plugins\Blowntobytes-Resurrected_VRMod\"  -Recurse -Force
Copy-Item ".\copy-to-patchers\*" "$bep\patchers\Blowntobytes-Resurrected_VRMod\" -Recurse -Force
```

Optional check that the right files are in place (PowerShell, `Get-FileHash <file>`):

| File | SHA-256 |
| --- | --- |
| `VRMod.dll` | `1727221BEEF76683367343CC3F7D9F413FE7CAFFCEA6BFF106619F54CFC624DA` |
| `VRPatcher.dll` | `A06FAE4EC9F7C2538F75EBB920DDA4E2B8AF2BC433BA43D14CC697175F5C170B` |
| `VRMod.Upscaling.Native.dll` | `4169BC798B50457DDADA553D1BE943CD3F5D090A4987883F3242F2E39077DFEF` |

## 3. Test (about ten minutes)

The log takes a reading every minute and the GPU profile records the first minute after each change, so **stay about 75 seconds in each step** before moving on. All settings are in the pause menu, VR tab.

1. **Baseline.** Before starting a run, set **DLSS / FSR upscaling** to off. Start a run and stay in the first stage for a minute. This records the frame time at full resolution.
2. **Same settings as last time.** Turn upscaling on with **Upscaling quality** Performance, **Upscaler** DLSS, **DLSS preset** M. Look for:
   - Does the right eye show the world now?
   - Do both eyes agree (normal depth, nothing doubled, nothing upside down)?
   - Does it feel faster or slower than step 1?
3. **DLSS preset K.** Same questions. K is the previous DLSS model and should cost less GPU time than M.
4. **Upscaler FSR 3.** Same questions.
5. **The freeze test.** Flick quickly back and forth through **Upscaling quality** and **DLSS preset** for ten seconds, then stop. Expected: the world drops to full resolution while you flick and upscaling comes back about a second after you stop. No freeze.
6. **Optional: DLAA.** At your headset's resolution this may not fit in video memory. Expected in that case: after a few seconds the chat box says "VR upscaling paused" and the game carries on at full resolution. That is the guard working, not a crash.

When a setting changes, the picture going to full resolution for about a second is normal.

If the game does freeze, give it up to a minute before Alt+F4, and send the logs anyway.

## 4. Send back

The logs are overwritten the next time the game starts, so copy them before you launch again. From the BepInEx folder:

- `LogOutput.log`
- `VRMod-Upscaling\native.log`
- `VRMod-Upscaling\profile.log` (new in this build)
- `VRMod-Upscaling\sl.log`

Upload them to the project thread, with a line or two on what you saw in each step.

To confirm the build that ran, `LogOutput.log` contains `Loading [Resurrected VRMod 1.2.1]`.

## 5. Going back

- **Keep this build but play without upscaling:** turn **DLSS / FSR upscaling** off in the VR tab. The game then renders at full resolution the normal way. To also keep the upscaling library from loading, set `Load upscaling library = false` under `[Upscaling]` in `config\VRMod.cfg`.
- **Back to 1.1.1:** with the game closed, delete `VRMod.dll` and rename `VRMod.dll.bak` to `VRMod.dll` in `plugins\Blowntobytes-Resurrected_VRMod`. In `patchers\Blowntobytes-Resurrected_VRMod`, delete `VRPatcher.dll` and `VRMod.Upscaling.Native.dll` and rename `VRPatcher.dll.bak` to `VRPatcher.dll`. The `upscaling` folder can stay; 1.1.1 ignores it.
