# Builds VRMod.Upscaling.Native.dll and gathers the files the VR mod ships with it.
# Runs on Windows x64 with Visual Studio (Desktop development with C++, Windows SDK,
# CMake tools) and Git. Used by .github/workflows/native-upscaling.yml and by hand:
#   pwsh -File NativeUpscaling\build\Build-Native.ps1
# Output: NativeUpscaling\build\package\
#   patchers\VRMod.Upscaling.Native.dll
#   plugins\upscaling\D3D11\*.dll     NVIDIA Streamline and DLSS runtime (signed by NVIDIA)
#   plugins\upscaling\licenses\*      license texts for everything above
param([string]$VisualStudioPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Build-Helpers.ps1')
# Load the signature commands before compiling so a packaging problem fails early.
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop

$git = Find-BuildTool 'git.exe' @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')) 'Install Git for Windows.'
if (-not $VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio or Build Tools with Desktop development with C++, the Windows SDK and CMake tools; or pass -VisualStudioPath.' }
    $VisualStudioPath = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
}
if (-not $VisualStudioPath) { throw 'Install Visual Studio or Build Tools with Desktop development with C++ and the Windows SDK.' }
$devShell = Join-Path $VisualStudioPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
if (-not (Test-Path -LiteralPath $devShell)) { throw "Visual Studio developer tools missing: $devShell" }
Import-Module $devShell
Enter-VsDevShell -VsInstallPath $VisualStudioPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
$nmake = Join-Path $env:VCToolsInstallDir 'bin\Hostx64\x64\nmake.exe'
if (-not (Test-Path -LiteralPath $nmake)) { throw "x64 nmake not found: $nmake. Install the Visual Studio C++ tools." }
$cmake = Find-BuildTool 'cmake.exe' @((Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe'), (Join-Path $VisualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')) 'Install CMake 3.24 or newer, or the Visual Studio CMake tools.'
$fxc = Find-BuildTool 'fxc.exe' @((Join-Path $env:WindowsSdkDir "bin\$($env:WindowsSDKVersion)\x64\fxc.exe")) 'Install the Windows SDK through the Visual Studio installer.'
Write-Host "Compiler: $VisualStudioPath"

# Streamline headers, FidelityFX SDK headers and the FSR 3 DX11 sources at pinned commits.
& (Join-Path $PSScriptRoot 'Fetch-SDK.ps1') -GitPath $git
# The NVIDIA runtime files at the pinned upstream commit (never committed here).
& (Join-Path $PSScriptRoot 'Fetch-Runtime.ps1') -GitPath $git

Push-Location $projectRoot
try {
    # An absolute NMake path also works when the host supplies duplicate Path/PATH variables.
    & $cmake -S native -B build/native-nmake -G 'NMake Makefiles' -DCMAKE_BUILD_TYPE=Release "-DCMAKE_MAKE_PROGRAM=$nmake" "-DFXC_EXECUTABLE=$fxc"
    if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
    & $cmake --build build/native-nmake
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }

    # Start from an empty tree so files from an earlier build never reach the package.
    $packageRoot = Join-Path $PSScriptRoot 'package'
    if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
    $patchers = Join-Path $packageRoot 'patchers'
    $runtime = Join-Path $packageRoot 'plugins\upscaling\D3D11'
    $licenses = Join-Path $packageRoot 'plugins\upscaling\licenses'
    New-Item -ItemType Directory -Path $patchers, $runtime, $licenses -Force | Out-Null
    Copy-Item -LiteralPath 'build\native-nmake\VRMod.Upscaling.Native.dll' -Destination $patchers -Force

    # Only the D3D11 super-resolution set: the VR mod never uses frame generation.
    $nvidia = Join-Path $projectRoot 'third_party\RiskOfRainUpscaling\third_party\NVIDIA-DLSS'
    foreach ($name in 'sl.interposer.dll', 'sl.common.dll', 'sl.dlss.dll', 'nvngx_dlss.dll', 'sl.reflex.dll', 'sl.pcl.dll') {
        $source = Join-Path $nvidia $name
        $signature = Get-AuthenticodeSignature -LiteralPath $source
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'NVIDIA') { throw "Invalid NVIDIA signature: $source" }
        Copy-Item -LiteralPath $source -Destination $runtime -Force
    }

    Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $licenses 'RiskOfRainUpscaling-LICENSE.txt') -Force
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination (Join-Path $licenses 'RiskOfRainUpscaling-THIRD_PARTY_NOTICES.md') -Force
    Copy-Item -LiteralPath 'NOTICE.md' -Destination (Join-Path $licenses 'VRMod-Upscaling-NOTICE.md') -Force
    Copy-Item -LiteralPath 'third_party\Streamline\license.txt' -Destination (Join-Path $licenses 'Streamline-LICENSE.txt') -Force
    # The license of the NVIDIA DLSS runtime (nvngx_dlss.dll); a copy of LICENSE.txt from github.com/NVIDIA/DLSS.
    Copy-Item -LiteralPath 'third_party\NVIDIA-RTX-SDKs-LICENSE.txt' -Destination (Join-Path $licenses 'NVIDIA-RTX-SDKs-LICENSE.txt') -Force
    Copy-Item -LiteralPath 'third_party\FidelityFX-SDK\Kits\FidelityFX\docs\license.md' -Destination (Join-Path $licenses 'FidelityFX-SDK-LICENSE.md') -Force
    Copy-Item -LiteralPath 'third_party\FidelityFX-FSR\LICENSE.txt' -Destination (Join-Path $licenses 'FidelityFX-FSR-LICENSE.txt') -Force
    Copy-Item -LiteralPath 'third_party\FidelityFX-SDK-DX11\LICENSE.txt' -Destination (Join-Path $licenses 'FidelityFX-SDK-DX11-LICENSE.txt') -Force

    $files = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = (Get-BuildRelativePath $packageRoot $_.FullName); length = $_.Length; version = $_.VersionInfo.FileVersion; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    $sourceCommit = "$(& $git -C $projectRoot rev-parse HEAD 2>$null)".Trim()
    $manifest = [ordered]@{
        builtUtc = [DateTime]::UtcNow.ToString('O'); sourceCommit = $sourceCommit
        upstream = 'xanderfrangos/RiskOfRainUpscaling 2bea5e1e1122751264ff2512ca66d646112e24eb (v1.1.0)'
        streamline = '2.14.1'; dlss = '310.9.1'; fidelityFxDx11 = 'FSR 3.1.2 (optiscaler/FidelityFX-SDK-DX11 9b04fa49abae5aa341c0007af13c8ba1b4ad9913)'
        files = @($files)
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageRoot 'plugins\upscaling\native-manifest.json') -Encoding utf8
    Write-Output "VR mod upscaling files ready: $packageRoot"
}
finally { Pop-Location }
