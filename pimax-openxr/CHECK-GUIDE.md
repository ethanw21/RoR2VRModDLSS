# Pimax OpenXR runtime: black screen check

The mod works when SteamVR is the OpenXR runtime. With the Pimax OpenXR runtime (the one inside Pimax Play) set as default, the picture is entirely black. This guide collects what is needed to find out why. Nothing has to be installed for it. Use whichever version of the mod is in the profile now (1.2.1 if you have it).

## Result of Part 1 (2026-11-11)

The logs from Part 1 gave the cause. The game reaches the Pimax runtime ("Pimax OpenXR" 0.1.0) and the session starts. The runtime then refuses every request for an eye texture with `XR_ERROR_FEATURE_UNSUPPORTED`, at the size and in the format it had itself recommended (6464x5044 per eye, RGBA8 sRGB). Unity asked 2,586 times in 30 seconds. Without eye textures the headset stays black.

OpenXR Toolkit, PimaxXR and Quad Views are ruled out: the first two are not installed and Quad Views was off. The only OpenXR layer on the machine is Virtual Desktop's compatibility layer, which is also there when the game works through SteamVR.

Why the runtime refuses is not known yet. **The next step is build 1.2.2: [1.2.2/TEST-GUIDE.md](../1.2.2/TEST-GUIDE.md).** It asks the runtime in other ways, continues with what the runtime accepts, and writes down every answer.

## Result of build 1.2.2 (2026-11-11)

The runtime refused every kind of request: seven colour formats, four kinds of usage, every size down to 950x762, a two-layer texture and a plain 512x512 texture. So the request is not what it objects to. ReShade was not loaded in that run, and a run with the mod's upscaling library switched off gave the same black screen, which settles steps C and D of Part 2 below.

**The next step is build 1.2.3: [1.2.3/TEST-GUIDE.md](../1.2.3/TEST-GUIDE.md).** It starts the session before asking, can start the game without other programs' OpenXR layers, and writes a report with the start-up calls, the graphics device and the last lines of the runtime's own log files.

## Result of build 1.2.3 (2026-11-11)

The headset stayed black, and the report says why. **The cause is not in the mod or the game.**

- The Pimax software's own log for the game (`%LOCALAPPDATA%\Pimax\runtime\pvr_client_Risk of Rain 2_log.txt`) shows its client library failing whenever it creates a texture for the game: `createTexture failed with error:1`, then `pvr_createTextureSwapChainDX ... pvrResult failure [1]` at `PiOpenXR\swapchain.cpp:412`. The runtime passes that on to the game as `XR_ERROR_FEATURE_UNSUPPORTED`.
- The same lines are in that folder's log for another game (Nuclear Option) from the same evening. The failure is not specific to Risk of Rain 2 or to this mod.
- On the game's side everything was in order: the session had already been started when the eye textures were requested, the game's graphics device was on the adapter the runtime asked for, and no other program's OpenXR layer was installed.

No request the mod can make changes this.

**Closed (2026-11-11).** The tester found the cause: the Pimax software was not being used the way it is meant to be on the test PC. It was not a bug in the Pimax runtime, the game or the mod, and there is nothing left to fix here. Work is back on the upscaling test: [1.2.3/UPSCALING-TEST.md](../1.2.3/UPSCALING-TEST.md).

The rest of this page is the original check, kept for reference.

Part 1 takes about five minutes and is the part that matters. Part 2 is optional and only saves a round trip if one of its steps happens to bring the picture back.

## What is known so far

- The mod asks the runtime for nothing unusual: Direct3D 11, a normal two-eye view, two eye textures (one per eye), and no depth submission. The open source PimaxXR runtime supports all of that, and its author says the runtime in Pimax Play is largely based on it.
- No report was found of Unity games in general failing on the Pimax runtime. The black screens and failed starts that are reported come from things around the game: the OpenXR Toolkit layer, the Quad Views setting, and SteamVR starting on its own.
- Going by the PimaxXR source, the Pimax runtime asks the game's own Direct3D 11 device for newer interfaces and opens shared textures on it. Anything that wraps that device can get in the way. How SteamVR handles the same device is not public, so this is a lead, not a finding. In this game folder that means ReShade (`dxgi.dll`), RenoDX and the DLSS bridge from your own experiments, and possibly this mod's upscaling library.

None of these has been confirmed as the cause. The logs from Part 1 decide.

## Part 1: one run, three notes, the logs

1. Close SteamVR completely.
2. In Pimax Play, make the Pimax runtime the default (the button is called **Switch to Pimax OpenXR**).
3. Start the game from the mod manager as usual and wait about 30 seconds at the main menu.
4. Note three things:
   - **Headset:** is it black at the main menu too, or only inside a run? Is it pure black, or do you still see the Pimax home environment?
   - **Desktop:** does the game window on the monitor show the menu, or is it black as well?
   - **SteamVR:** did it start by itself?
5. Quit with the game's own Quit button if you can reach it on the monitor. A normal quit lets the OpenXR plugin write its closing report. Use Alt+F4 only if you have to.
6. Send these to the thread, together with the three notes, the Pimax Play version and the current **OpenXR Quad Views** setting (Pimax Play, Games tab):
   - `LogOutput.log` from `%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx\`
   - `Player.log`, normally in `%USERPROFILE%\AppData\LocalLow\Hopoo Games, LLC\Risk of Rain 2\`
   - With 1.2.0 or 1.2.1 installed, also `VRMod-Upscaling\native.log` and `VRMod-Upscaling\sl.log` from the same `BepInEx` folder as `LogOutput.log`
   - The output of the command in "List the OpenXR layers" below

Copy the logs before starting the game again, because each start overwrites them.

What the logs answer: which runtime the game really talked to (the line `[VR input] OpenXR runtime:`), whether the session was created and reached the focused state, which eye texture size and format the runtime handed out, and any call the runtime refused.

### List the OpenXR layers

Layers sit between a game and the runtime, and Pimax's own guide says to turn OpenXR Toolkit off when using their runtime. This PowerShell command only reads the registry and prints the default runtime and the installed layers (a value of 0 means the layer is on):

```powershell
foreach ($k in 'HKLM:\SOFTWARE\Khronos\OpenXR\1',
               'HKLM:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit',
               'HKCU:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit') {
  "== $k"
  if (Test-Path $k) { $i = Get-Item $k; foreach ($n in $i.Property) { "$n = $($i.GetValue($n))" } }
  else { "(not present)" }
}
```

## Part 2: optional, one change at a time

Change one thing, start the game, look in the headset, then undo the change before trying the next. If a step brings the picture back, stop there and say which one it was.

| Step | What to change | How to undo |
| --- | --- | --- |
| A | In Pimax Play, Games tab, turn **OpenXR Quad Views** off if it offers that, otherwise note the choices it has. | Set it back. |
| B | Turn off OpenXR Toolkit (and any other layer the command above lists with value 0) in that tool's own app. | Turn it back on. |
| C | In the game folder (Steam: right-click the game, Manage, Browse local files), rename `dxgi.dll` to `dxgi.dll.off`. This switches off ReShade and what loads through it. | Rename it back. |
| D | In `%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Modded VR\BepInEx\patchers\Blowntobytes-Resurrected_VRMod\`, rename `VRMod.Upscaling.Native.dll` to `VRMod.Upscaling.Native.dll.off`. The mod then starts without upscaling, as 1.1.1 did. | Rename it back. |

Step D matters most for the mod itself: if the picture returns only with the upscaling library out of the way, the fix belongs in this mod.

## After this

Done: see "Result of Part 1" at the top. The logs showed a refused call, and build 1.2.2 is the answer to it.
