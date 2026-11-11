# Test builds of the VR mod with DLSS / FSR upscaling

This branch holds ready-built test versions of the mod, one folder per version, each with a guide that says which file goes where, what to try and which logs to send back. The newest version is at the top of the table. The source is on the branch `claude/vr-dlss-upscaling`.

**The test to do now:** [1.2.4/TEST-GUIDE.md](1.2.4/TEST-GUIDE.md): install 1.2.4, clean up what the black screen tests left on the PC, and run the DLSS / FSR upscaling test.

| Version | Guide | What it is for |
| --- | --- | --- |
| 1.2.4 | [1.2.4/TEST-GUIDE.md](1.2.4/TEST-GUIDE.md) | The upscaling of 1.2.1 and nothing else. Everything builds 1.2.2 and 1.2.3 added to look into a black screen has been removed again. Its guide also covers the clean-up on the PC and the upscaling test. |
| 1.2.1 | [1.2.1/TEST-GUIDE.md](1.2.1/TEST-GUIDE.md) | Fixes from the first headset test: right eye black, freeze when flicking through settings, DLSS switching itself off. Adds measurements to explain the missing speedup. |

Builds 1.2.2 and 1.2.3 were made to look into a black screen with one headset runtime. The cause was a set-up problem on the test PC and not a fault in the mod, so those two builds and their guides have been taken off this branch. They remain in the branch's history (commit `cf921c8` and earlier) and their numbers are not reused.

A version number is never reused: every new build gets its own folder.

The NVIDIA DLSS runtime files are not kept in this repository. Each guide says where they come from.
