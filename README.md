# Test builds of the VR mod with DLSS / FSR upscaling

This branch holds ready-built test versions of the mod, one folder per version, each with a guide that says which file goes where, what to try and which logs to send back. The newest version is at the top of the table. The source is on the branch `claude/vr-dlss-upscaling`.

| Version | Guide | What it is for |
| --- | --- | --- |
| 1.2.3 | [1.2.3/TEST-GUIDE.md](1.2.3/TEST-GUIDE.md) | Black screen with the Pimax OpenXR runtime, second round: the runtime refused every kind of eye texture, so the request is not the cause. This build starts the session first, can start without other programs' OpenXR layers, and writes a report with the start-up calls, the graphics device and the runtime's own log lines. Two starts of the game. Upscaling is the same as in 1.2.1. **Result:** still black; the report shows the Pimax runtime failing inside its own software, for another game too, so the cause is outside the mod. |
| 1.2.2 | [1.2.2/TEST-GUIDE.md](1.2.2/TEST-GUIDE.md) | Black screen with the Pimax OpenXR runtime: the runtime refuses the eye textures the game asks for. This build records the request, tries alternatives the runtime may accept and continues with the best one. Upscaling is the same as in 1.2.1. |
| 1.2.1 | [1.2.1/TEST-GUIDE.md](1.2.1/TEST-GUIDE.md) | Fixes from the first headset test: right eye black, freeze when flicking through settings, DLSS switching itself off. Adds measurements to explain the missing speedup. |

Other guides:

- [pimax-openxr/CHECK-GUIDE.md](pimax-openxr/CHECK-GUIDE.md): the first check for the black screen with the Pimax OpenXR runtime, and what its logs showed. The tests that follow from it are builds 1.2.2 and 1.2.3, and their results are at the top of that page.

A version number is never reused: every new build gets its own folder.

The NVIDIA DLSS runtime files are not kept in this repository. Each guide says where they come from.
