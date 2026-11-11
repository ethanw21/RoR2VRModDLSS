# Resurrected VRMod 1.2.2 test build: install and test guide

This build is for the black screen with the Pimax OpenXR runtime. It has been compiled, packaged and tested against a pretend headset runtime on the build machine. It has **not been run on a headset**, and nobody knows yet which answer the real Pimax runtime will give. Either the picture comes back, or the log says exactly what the runtime accepts, which is what the proper fix needs.

- Source: branch `claude/vr-dlss-upscaling` (pull request #1), commit `6dcc1d9`.
- Native upscaling library: unchanged from 1.2.1 (built by GitHub Actions from commit `4a37fb7`).

## What your logs showed

With the Pimax runtime as default, the game reached the runtime ("Pimax OpenXR" 0.1.0, Pimax Dream Air) and the session started. The game then asked the runtime for the two eye textures at the size and in the format the runtime itself had recommended (6464x5044 per eye, RGBA8 sRGB). The runtime answered `XR_ERROR_FEATURE_UNSUPPORTED`. Unity asked again every frame, 2,586 times in 30 seconds, and got the same answer each time. Without eye textures nothing can be shown, so the headset stayed black.

The request is an ordinary one, and the logs do not say which part of it the runtime objects to. Through SteamVR the same game gets 4940x4136 per eye without complaint.

## What changed since 1.2.1

- **Eye texture fallback.** The mod now watches the request for the eye textures. While the runtime accepts it, nothing is changed. After three refusals in a row the mod asks the runtime for the same textures in other ways (other usage flags, other colour formats, smaller sizes), and continues with the best one the runtime accepts.
- **Everything is written to the log.** Lines starting with `[VR display]` in `LogOutput.log` hold the request, the runtime's answer, the list of what it accepted instead, and what the mod continued with.
- **Eye texture scale.** A new setting in the config file renders the eye textures smaller than the runtime recommends.
- **Upscaling is the same as in 1.2.1.**

## 1. Get the files

- **Git:** in your clone of the repo, `git checkout test-builds` and then `git pull`.
- **Browser:** download <https://github.com/ethanw21/RoR2VRModDLSS/archive/refs/heads/test-builds.zip> and unzip it.

Everything for this build is in the folder `1.2.2`.

## 2. Install

**Close the game first.**

Open your profile's BepInEx folder by pasting this into the File Explorer address bar:

```
%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx
```

**With 1.2.1 installed (your last log shows it is), one file changes:**

| From the `1.2.2` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\VRMod.dll` | `plugins\Blowntobytes-Resurrected_VRMod\` (replace the existing file) |

The other files in the `1.2.2` folder are byte for byte the same as in `1.2.1`. They are there so the folder is complete on its own. Copying all of them, as in the 1.2.1 guide, does no harm:

| From the `1.2.2` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\upscaling\` (the whole folder) | `plugins\Blowntobytes-Resurrected_VRMod\` (merges into the existing `upscaling` folder) |
| `copy-to-patchers\VRPatcher.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |
| `copy-to-patchers\VRMod.Upscaling.Native.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |

Leave alone, as before: the `upscaling\D3D11\` folder with its six NVIDIA files, `VRMod.dll.bak` and `VRPatcher.dll.bak` (your 1.1.1 backups), and every other file in those two folders. Neither folder may hold a second `VRMod*.dll` or `VRPatcher*.dll` ending in `.dll`.

Optional, instead of copying by hand: open PowerShell inside the `1.2.2` folder and run

```powershell
$bep = "$env:APPDATA\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx"
Copy-Item ".\copy-to-plugins\*"  "$bep\plugins\Blowntobytes-Resurrected_VRMod\"  -Recurse -Force
Copy-Item ".\copy-to-patchers\*" "$bep\patchers\Blowntobytes-Resurrected_VRMod\" -Recurse -Force
```

Optional check that the right files are in place (PowerShell, `Get-FileHash <file>`):

| File | SHA-256 |
| --- | --- |
| `VRMod.dll` | `7C4B796AC619D21FB8E16F6F74692033270FC8AD9B1287E109A5251C26365B08` |
| `VRPatcher.dll` (same as 1.2.1) | `A06FAE4EC9F7C2538F75EBB920DDA4E2B8AF2BC433BA43D14CC697175F5C170B` |
| `VRMod.Upscaling.Native.dll` (same as 1.2.1) | `4169BC798B50457DDADA553D1BE943CD3F5D090A4987883F3242F2E39077DFEF` |

## 3. Test on the Pimax runtime (about five minutes)

1. Close SteamVR completely. In Pimax Play, keep the Pimax runtime as the default OpenXR runtime, with Quad Views off as it was.
2. Start the game from the mod manager as usual.
3. **Wait a full minute at the main menu**, with the headset on. The mod lets the runtime refuse three times before it tries anything else, and trying the alternatives makes the runtime create and release some large textures, so a short stall right after the menu loads is expected.
4. Note what you see:
   - **Is there a picture in the headset?**
   - If there is: does it look right? Check sharpness (it may be rendered smaller than usual), brightness and colours, that both eyes agree, and that the picture follows your head.
   - If there is: do the controllers show up and work in the menu? Your last log had no controllers after ten seconds, which may only have been a result of the missing picture.
5. Quit with the game's own Quit button if you can reach it. Use Alt+F4 only if you have to. In both cases the lines needed are already in the log.
6. Copy the logs before starting the game again (section 4).

If there is a picture and you feel like going further, start a run with **DLSS / FSR upscaling** turned off in the pause menu's VR tab first. Upscaling has not been confirmed in a headset on any runtime yet, so testing it on the Pimax runtime at the same time would mix two unknowns.

## 4. Send back

- `LogOutput.log` from the BepInEx folder. This is the one that matters.
- `Player.log`, normally in `%USERPROFILE%\AppData\LocalLow\Hopoo Games, LLC\Risk of Rain 2\`.
- A line or two on what you saw in step 4.

To confirm the build that ran, `LogOutput.log` contains `Loading [Resurrected VRMod 1.2.2]`.

One thing to know before sharing the log anywhere public: when the runtime refuses a request, the mod lists the libraries loaded into the game, with their full paths. Those paths include your Windows user name.

## 5. Reading the `[VR display]` lines yourself

Search `LogOutput.log` for `[VR display]`.

| Line starts with | Meaning |
| --- | --- |
| `OpenXR session state ... -> ...` | The steps of the VR session starting. A working session ends at `FOCUSED`. |
| `Eye texture accepted by the runtime:` | The runtime handed out the eye textures. The rest of the line says what was changed to get there, if anything. With nothing changed, the runtime accepted the game's normal request. |
| `The headset's runtime refused the eye texture Unity asked for` | The refusal, with the exact request, the runtime's recommended and maximum sizes and its list of formats. |
| `What the runtime accepts instead:` | One row per combination tried, each with `accepted at N%` or `refused`. This table is what tells us which part of the request the runtime objects to. |
| `Continuing with format ..., usage ..., N% size` | What the mod chose. The game's next attempt uses it. |
| `The runtime still refuses the eye texture after the change` | The runtime accepted a test texture but refused the same thing when the game asked for it. |
| `The runtime refused every combination tried` | The request is not the problem. See section 7. |
| `Libraries loaded from outside the Windows folder` | What else is loaded into the game (ReShade, overlays and so on), in case one of them turns out to be involved. |

If none of these lines appear except the session states and `Eye texture accepted`, the runtime took the normal request this time.

## 6. The two new settings

They are in `config\VRMod.cfg` inside the BepInEx folder, in the section `[Headset compatibility]`. The section appears after the game has started once with 1.2.2. Both apply after a restart.

| Setting | Default | What it does |
| --- | --- | --- |
| `Eye texture fallback` | `true` | Trying alternatives when the runtime refuses. With `false` the mod only records the refusal. |
| `Eye texture scale` | `1` | Size of the eye textures as a share of the runtime's recommendation, from 0.25 to 1. For example `0.75` turns 6464x5044 into 4848x3784. |

There is no need to change either for the first test. The fallback tries smaller sizes on its own.

## 7. If the headset is still black

Send the logs anyway. They now hold the answer to "what does the runtime accept", which the last ones could not.

If the log says `The runtime refused every combination tried`, the runtime refuses any texture in this session, however it is asked. Then something around the request is the cause, and these steps from the [Pimax check guide](../pimax-openxr/CHECK-GUIDE.md) become worth a try, one at a time, with the logs from each:

- **Step C:** rename `dxgi.dll` in the game folder to `dxgi.dll.off` (switches off ReShade and what loads through it).
- **Step D:** rename `VRMod.Upscaling.Native.dll` in the patchers folder to `VRMod.Upscaling.Native.dll.off` (the mod then starts without the upscaling library).

## 8. The upscaling test under SteamVR

This build contains the same upscaling as 1.2.1. If the 1.2.1 test under SteamVR is still to do, do it with this build: switch the default runtime back to SteamVR and follow sections 3 and 4 of the [1.2.1 guide](../1.2.1/TEST-GUIDE.md). The only difference is the version line in the log.

## 9. Going back

- **Back to 1.2.1:** copy `1.2.1\copy-to-plugins\VRMod.dll` over the one in `plugins\Blowntobytes-Resurrected_VRMod`.
- **Keep 1.2.2 but stop it from changing anything:** set `Eye texture fallback = false` and `Eye texture scale = 1`.
- **Back to 1.1.1:** as in section 5 of the 1.2.1 guide.
