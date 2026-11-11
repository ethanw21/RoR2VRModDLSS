# DLSS / FSR upscaling test with build 1.2.3

This is the upscaling test that has been waiting since build 1.2.1. Build 1.2.3 contains the same upscaling as 1.2.1, so **there are no files to move if 1.2.3 is already installed**. If it is not, install it first with sections 1 and 2 of the [1.2.3 guide](TEST-GUIDE.md).

The upscaling fixes in this build have been compiled and packaged, but they have **not been run in a headset yet**. That is what this test is for.

## What this test is meant to show

The first headset test (build 1.2.0, 4940x4136 per eye) had four problems. Three have a fix that needs confirming, and the fourth needs numbers:

| Seen in 1.2.0 | What changed | What to look for now |
| --- | --- | --- |
| Right eye black during runs | The last step, handing each eye's finished image to the headset, was rebuilt | Both eyes show the world |
| Freeze after flicking through presets | A setting is applied about a second after you stop changing it, and the old upscalers are released first | No freeze |
| DLSS switched itself off | NVIDIA's "over the video memory budget" warning is logged and no longer treated as a failure. A guard pauses upscaling if the game stays over budget for five seconds | DLSS stays on, or the chat box says "VR upscaling paused" |
| No speedup | Nothing yet. The log now measures frame times and the GPU time of every step | The numbers in the logs you send back |

## 1. Before you start

With the game closed, check two things:

- In `config\VRMod.cfg` inside the BepInEx folder, section `[Upscaling]`: `Load upscaling library = true`. It was set to `false` for one of the black screen tests.
- The standalone **Risk of Rain Upscaling** mod is still disabled in the mod manager.

The BepInEx folder is:

```
%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx
```

Use whichever OpenXR runtime you normally play on, and say which one it was when you send the logs. The eye texture size differs between runtimes, and the cost of upscaling depends on it.

## 2. Test (about ten minutes)

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

## 3. Send back

The logs are overwritten the next time the game starts, so copy them before you launch again. From the BepInEx folder:

- `LogOutput.log`
- `VRMod-Upscaling\native.log`
- `VRMod-Upscaling\profile.log`
- `VRMod-Upscaling\sl.log`

Upload them to the project thread with a line or two on what you saw in each step, and which OpenXR runtime you used.

To confirm the build that ran, `LogOutput.log` contains `Loading [Resurrected VRMod 1.2.3]`.

## 4. Reading the numbers yourself

Search `LogOutput.log` for `[VR upscaling]`.

| Line contains | Meaning |
| --- | --- |
| `Before turning on, the game averaged ...` | The frame time at full resolution, from step 1. |
| `Status` | One reading a minute: average and slowest frame time with upscaling on, how many frames each eye was shown (`shown in the headset left N / right N`), and video memory in use against the budget. |
| `Image check` | A one-time brightness reading of each eye's images. Two similar values mean both eyes carry a picture. |
| `VR upscaling paused` | The video memory guard stepped in. |

`profile.log` lists the GPU time of every step for the first minute after each change. The rows that matter most are the ones for the upscaler itself (`upscale.feature`) and for drawing each eye.

## 5. If upscaling gets in the way

- **Play without it:** turn **DLSS / FSR upscaling** off in the VR tab. The game then renders at full resolution the normal way.
- **Keep the library from loading at all:** set `Load upscaling library = false` under `[Upscaling]` in `config\VRMod.cfg`.
- **Back to an older build:** section 9 of the [1.2.3 guide](TEST-GUIDE.md).
