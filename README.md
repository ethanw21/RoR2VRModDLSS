# Test builds of the VR mod with DLSS / FSR upscaling

This branch holds ready-built test versions of the mod, one folder per version, each with a guide that says which file goes where, what to try and which logs to send back. The newest version is at the top of the table. The source is on the branch `claude/vr-dlss-upscaling`.

| Version | Guide | What it is for |
| --- | --- | --- |
| 1.2.2 | [1.2.2/TEST-GUIDE.md](1.2.2/TEST-GUIDE.md) | Black screen with the Pimax OpenXR runtime: the runtime refuses the eye textures the game asks for. This build records the request, tries alternatives the runtime may accept and continues with the best one. Upscaling is the same as in 1.2.1. |
| 1.2.1 | [1.2.1/TEST-GUIDE.md](1.2.1/TEST-GUIDE.md) | Fixes from the first headset test: right eye black, freeze when flicking through settings, DLSS switching itself off. Adds measurements to explain the missing speedup. |

Other guides:

- [pimax-openxr/CHECK-GUIDE.md](pimax-openxr/CHECK-GUIDE.md): the first check for the black screen with the Pimax OpenXR runtime, and what its logs showed. The test that follows from it is build 1.2.2.

A version number is never reused: every new build gets its own folder.

The NVIDIA DLSS runtime files are not kept in this repository. Each guide says where they come from.
