# Resurrected VRMod 1.2.3 test build: install and test guide

This build is for the black screen with the Pimax OpenXR runtime. It is a fact-finding build: it tries the two things that can still be changed from inside the mod, and it writes down everything around the VR session that the earlier logs could not show. It has been compiled, packaged and tested against a pretend headset runtime on the build machine (17 situations). It has **not been run on a headset**.

- Source: branch `claude/vr-dlss-upscaling` (pull request #1), commit `ba4f5bb`.
- Native upscaling library: unchanged from 1.2.1 (built by GitHub Actions from commit `4a37fb7`).

**Result (2026-11-11):** the headset stayed black, and the report showed that the Pimax runtime fails inside its own software, for another game as well. The cause is outside the mod. See "Result of build 1.2.3" in the [Pimax check guide](../pimax-openxr/CHECK-GUIDE.md). One correction to the text below: Unity had already started the session before it asked for the eye textures, so the "session started first" step did not come into play.

**The test is two starts of the game, one right after the other, about two minutes each.** Section 3 has the steps.

## What your 1.2.2 logs showed

- The Pimax runtime refused the game's normal request for the eye textures, and then **every one of the 34 other ways of asking**: seven colour formats, four kinds of usage, every size down to 950x762, a two-layer texture, and a plain 512x512 texture. The answer was `XR_ERROR_FEATURE_UNSUPPORTED` each time.
- A runtime that refuses a 512x512 texture is not objecting to the request. Something about the session, or about what sits around it, is the cause.
- ReShade was not loaded in that run (no `dxgi.dll` from the game folder in the list of loaded libraries).
- Your run with the upscaling library switched off gave the same black screen, so the upscaling part of the mod is not the cause either.

What is left: another program's OpenXR layer sitting between the game and the runtime (Virtual Desktop's is installed and loaded), the order in which Unity starts the session, the graphics device the game hands over, or something inside the runtime that only its own log explains.

## What changed since 1.2.2

- **Session started first.** Unity asks for the eye textures before it starts the VR session. That order is allowed, but a runtime may want it the other way round. When the runtime has refused everything, the mod now starts the session itself and asks once more. If that works, the game carries on and you get a picture.
- **OpenXR layers.** A new setting, `OpenXR layers`, on `Auto` by default. Other programs (Virtual Desktop, overlays, tools) install add-ons called layers that sit between every VR game and the headset's runtime. After a start in which the runtime refused everything, the **next** start of the game runs without those layers, by itself. Nothing on the PC is changed; it applies to the game only, for that start.
- **Start-up report.** The mod records the OpenXR calls the game makes while the session starts, the graphics device it hands to the runtime and whether that device is on the graphics adapter the runtime asked for, the layers installed on the PC, and any diagnostic messages the runtime itself sends.
- **The runtime's own log.** When everything is refused, the mod looks for log files the Pimax software wrote in the last two days and copies their last lines into the report. The runtime knows why it refuses; its own log is the most direct place to read that.
- **One report file for several starts.** `LogOutput.log` is overwritten on every start. Everything under `[VR display]` now also goes to `BepInEx\VRMod-Display\report.txt`, which is added to instead.
- **Upscaling is the same as in 1.2.1.**

## 1. Get the files

- **Git:** in your clone of the repo, `git checkout test-builds` and then `git pull`.
- **Browser:** download <https://github.com/ethanw21/RoR2VRModDLSS/archive/refs/heads/test-builds.zip> and unzip it.

Everything for this build is in the folder `1.2.3`.

## 2. Install

**Close the game first.**

Open your profile's BepInEx folder by pasting this into the File Explorer address bar:

```
%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx
```

**With 1.2.2 installed, one file changes:**

| From the `1.2.3` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\VRMod.dll` | `plugins\Blowntobytes-Resurrected_VRMod\` (replace the existing file) |

The other files in the `1.2.3` folder are byte for byte the same as in `1.2.1` and `1.2.2`. They are there so the folder is complete on its own. Copying all of them does no harm:

| From the `1.2.3` folder | Into the BepInEx folder |
| --- | --- |
| `copy-to-plugins\upscaling\` (the whole folder) | `plugins\Blowntobytes-Resurrected_VRMod\` (merges into the existing `upscaling` folder) |
| `copy-to-patchers\VRPatcher.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |
| `copy-to-patchers\VRMod.Upscaling.Native.dll` | `patchers\Blowntobytes-Resurrected_VRMod\` |

Leave alone, as before: the `upscaling\D3D11\` folder with its six NVIDIA files, `VRMod.dll.bak` and `VRPatcher.dll.bak` (your 1.1.1 backups), and every other file in those two folders. Neither folder may hold a second `VRMod*.dll` or `VRPatcher*.dll` ending in `.dll`.

If you set `Load upscaling library = false` for the last test, you can leave it that way or set it back to `true`. It makes no difference to this test.

Optional check that the right file is in place (PowerShell, `Get-FileHash <file>`):

| File | SHA-256 |
| --- | --- |
| `VRMod.dll` | `382AE46F99C4B1D2B1050571EA7B4D4C17B60890B182718D4E86D7E0746675EF` |
| `VRPatcher.dll` (same as 1.2.1) | `A06FAE4EC9F7C2538F75EBB920DDA4E2B8AF2BC433BA43D14CC697175F5C170B` |
| `VRMod.Upscaling.Native.dll` (same as 1.2.1) | `4169BC798B50457DDADA553D1BE943CD3F5D090A4987883F3242F2E39077DFEF` |

## 3. Test on the Pimax runtime: two starts

Keep the Pimax runtime as the default OpenXR runtime for both starts, and do not start the game through SteamVR in between. The mod remembers how the last start went per runtime, and a start on another runtime in between resets that.

**First start**

1. Close SteamVR completely. Quad Views off in Pimax EVO, as before.
2. Start the game from the mod manager as usual.
3. **Wait about a minute at the main menu**, with the headset on.
4. Note whether there is a picture in the headset.
   - **If there is a picture:** the "session started first" change did it. Look at whether it is sharp, the right brightness, the same in both eyes, and follows your head, and whether the controllers work in the menu. Quit with the game's Quit button. **Skip the second start** and go to section 4.
   - **If it is black:** close the game (Alt+F4 is fine).

**Second start (only if the first was black)**

5. Start the game again from the mod manager. Because the first start was refused, this one runs without the other programs' OpenXR layers.
6. Wait about a minute at the main menu again, and note whether there is a picture this time.
7. Close the game.

A third start would run with the layers on again; the mod does not keep switching back and forth.

## 4. Send back

- **`report.txt`** from `BepInEx\VRMod-Display\`. This is the one that matters; it covers both starts.
- `LogOutput.log` from the BepInEx folder and `Player.log` from `%USERPROFILE%\AppData\LocalLow\Hopoo Games, LLC\Risk of Rain 2\` (both are from the last start only).
- A line on what you saw in each start.

**Please look through `report.txt` before sending it, or before sharing it anywhere else.** It contains:

- the full paths of the libraries loaded into the game, which include your Windows user name;
- the last lines of log files written by the Pimax software. I cannot know in advance what Pimax writes there. Such logs can hold things like a headset serial number or an account name. Remove anything you do not want to share; the lines about OpenXR, the session and the eye textures ("swapchain") are the ones that matter.

Nothing is sent anywhere by the mod. The file stays on your PC until you attach it.

## 5. Two questions

These would help to read the result, and neither needs the mod:

1. **Does any other OpenXR game or app show a picture on the Pimax runtime with SteamVR closed?** If nothing does, the cause is in the Pimax software or its settings, not in this game.
2. **The trail of cursors you saw on the black screen: was that inside the headset, or in the game window on the monitor?**

## 6. Reading `report.txt` yourself

Each start begins with a line `==== Game started <time>, Resurrected VRMod 1.2.3 ====`. Lines that start with `!` are warnings.

| Line starts with | Meaning |
| --- | --- |
| `Default OpenXR runtime on this PC:` | Which runtime Windows hands to games. Should name the Pimax one. |
| `OpenXR layers other programs have installed` | One row per layer: its name, whether it is on, and `SWITCHED OFF FOR THIS START` when the mod left it out. |
| `This start runs without those layers, because ...` | The second start. |
| `What the runtime accepts instead:` | The table from 1.2.2, with two more rows (static image, depth texture). |
| `The runtime refused every combination tried ...` | Followed by what happened when the mod started the session first: `WITH THE SESSION STARTED FIRST, THE RUNTIME ACCEPTS THE EYE TEXTURE`, or that it was refused as well. |
| `How the VR session was started` | The OpenXR calls in order, with how often each was made and what the runtime answered; then the graphics device and adapter, the controller profiles the runtime refused, and the runtime's own messages if it sent any. `THE GAME'S DEVICE IS ON A DIFFERENT ADAPTER` would be a finding. |
| `The runtime's own log files` | Which files were found and their last lines. A line repeated many times is shown once with a count. |
| `Eye texture accepted by the runtime:` | The runtime handed out the eye textures: there should be a picture. |

## 7. The new settings

They are in `config\VRMod.cfg` inside the BepInEx folder, section `[Headset compatibility]`, next to the two from 1.2.2. They appear after the game has started once with 1.2.3 and apply after a restart. There is no need to change any of them for this test.

| Setting | Default | What it does |
| --- | --- | --- |
| `OpenXR layers` | `Auto` | `Keep`: leave other programs' layers on. `Skip`: always start this game without them. `Auto`: leave them on, but after a start in which the runtime refused every eye texture, run the next start without them, and keep doing so while that works. If the runtime refuses without them as well, they stay on. |
| `OpenXR start-up report` | `true` | The record of how the session starts, the attempt with the session started first, and the lines from the runtime's own log files. `false` turns all three off. |

Through SteamVR's runtime, where the eye textures are accepted, `Auto` never switches anything off.

## 8. The upscaling test under SteamVR

This build contains the same upscaling as 1.2.1. If the 1.2.1 test under SteamVR is still to do, do it with this build after the two Pimax starts: switch the default runtime back to SteamVR and follow sections 3 and 4 of the [1.2.1 guide](../1.2.1/TEST-GUIDE.md). The only difference is the version line in the log.

## 9. Going back

- **Back to 1.2.2 or 1.2.1:** copy that folder's `copy-to-plugins\VRMod.dll` over the one in `plugins\Blowntobytes-Resurrected_VRMod`.
- **Keep 1.2.3 but stop it from changing anything:** set `OpenXR layers = Keep`, `OpenXR start-up report = false`, `Eye texture fallback = false` and `Eye texture scale = 1`.
- **Remove what it wrote:** delete the folder `BepInEx\VRMod-Display`.
- **Back to 1.1.1:** as in section 5 of the 1.2.1 guide.
