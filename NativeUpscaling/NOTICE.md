# VR mod upscaling library: origin and changes

`VRMod.Upscaling.Native.dll` is built from the source in this folder. That source is a
copy of the native library of **Risk of Rain Upscaling** by Xander Frangos,
<https://github.com/xanderfrangos/RiskOfRainUpscaling>, taken at commit
`2bea5e1e1122751264ff2512ca66d646112e24eb` (release 1.1.0) and used under its MIT
license (see `LICENSE` in this folder, shipped as `RiskOfRainUpscaling-LICENSE.txt`).

All of the upscaling work, the Streamline and FidelityFX integration, the graphics
hooks and the render-thread design are the upstream author's. This copy exists only
because the VR mod has to run one upscaler per eye, which the upstream library does
not do.

## Changes made for the VR mod

| File | Change |
| --- | --- |
| `native/Upscaling/DlssUpscaler.cpp` | Each DLSS feature takes its own Streamline viewport id instead of the fixed viewport 1, so the left and right eye keep separate DLSS history. A separate Streamline project id is used. |
| `native/FrameGeneration/FrameBridge.cpp` | Frame generation is never enabled and the standalone mod's config file is not read. |
| `native/CMakeLists.txt` | The library is named `VRMod.Upscaling.Native.dll`. Upstream's native tests and their shader fixture are not built. |
| `build/Build-Native.ps1`, `build/Fetch-Runtime.ps1` | New. Build the library and gather the NVIDIA runtime files and license texts for the VR mod package. |

`build/Fetch-SDK.ps1`, `build/Build-Helpers.ps1`, `third_party/FidelityFX-FSR` and
`third_party/patches` are unchanged copies.

The exported function names keep their upstream `RiskOfRainUpscaling_` prefix.

## Third-party components

See `THIRD_PARTY_NOTICES.md` (upstream's notices, unchanged). In short: NVIDIA
Streamline (MIT), the NVIDIA DLSS runtime (NVIDIA DLSS SDK license), AMD FidelityFX
SDK and FSR 1 (MIT), and OptiScaler's FidelityFX DX11 port (MIT). The VR mod ships only
the D3D11 super-resolution runtime files: `sl.interposer.dll`, `sl.common.dll`,
`sl.dlss.dll`, `sl.reflex.dll`, `sl.pcl.dll` and `nvngx_dlss.dll`.

NVIDIA, RTX and DLSS are trademarks of NVIDIA Corporation. AMD, FidelityFX and FSR are
trademarks of Advanced Micro Devices, Inc. This project is not affiliated with or
endorsed by NVIDIA, AMD, Hopoo Games, Gearbox or the Risk of Rain Upscaling author.
