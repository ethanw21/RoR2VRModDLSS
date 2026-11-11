# Pimax OpenXR runtime: black screen check

The mod works when SteamVR is the OpenXR runtime. With the Pimax OpenXR runtime (the one inside Pimax Play) set as default, the picture is entirely black. This guide collects what is needed to find out why. Nothing has to be installed for it. Use whichever version of the mod is in the profile now (1.2.1 if you have it).

Part 1 takes about five minutes and is the part that matters. Part 2 is optional and only saves a round trip if one of its steps happens to bring the picture back.

## What is known so far

- The mod asks the runtime for nothing unusual: Direct3D 11, a normal two-eye view, two eye textures (one per eye), and no depth submission. The open source PimaxXR runtime supports all of that, and its author says the runtime in Pimax Play is largely based on it.
- No report was found of Unity games in general failing on the Pimax runtime. The black screens and failed starts that are reported come from things around the game: the OpenXR Toolkit layer, the Quad Views setting, and SteamVR starting on its own.
- Unlike SteamVR, the Pimax runtime (going by the PimaxXR source) asks the game's own Direct3D 11 device for newer interfaces and opens shared textures on it. Anything that wraps that device can get in the way. In this game folder that means ReShade (`dxgi.dll`), RenoDX and the DLSS bridge from your own experiments, and possibly this mod's upscaling library.

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

If the logs show a refused call or a missing feature, the next test build changes how the mod starts OpenXR for this runtime. If the logs look healthy and the picture is still black, the next build adds a display report and switches to try in the headset (for example drawing without the runtime's hidden area mask).
