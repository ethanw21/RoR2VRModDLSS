# Fetches the NVIDIA Streamline 2.14.1 and DLSS 310.9.1 runtime DLLs that the upstream
# Risk of Rain Upscaling repository commits under third_party/NVIDIA-DLSS, at the pinned
# upstream commit this folder was vendored from. They are large, NVIDIA-signed binaries
# under NVIDIA's own license, so they are fetched for packaging and never committed here.
# Build-Native.ps1 checks every file's NVIDIA signature before it is packaged.
param([string]$GitPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Build-Helpers.ps1')
if (-not $GitPath) { $GitPath = Find-BuildTool 'git.exe' @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')) 'Install Git for Windows.' }
$root = Join-Path $projectRoot 'third_party\RiskOfRainUpscaling'
$commit = '2bea5e1e1122751264ff2512ca66d646112e24eb'
if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    & $GitPath init -q $root
    if ($LASTEXITCODE -ne 0) { throw 'Runtime checkout failed.' }
    & $GitPath -C $root remote add origin https://github.com/xanderfrangos/RiskOfRainUpscaling.git
    if ($LASTEXITCODE -ne 0) { throw 'Runtime checkout failed.' }
}
& $GitPath -C $root sparse-checkout set --no-cone '/third_party/NVIDIA-DLSS/' '/LICENSE' '/THIRD_PARTY_NOTICES.md'
if ($LASTEXITCODE -ne 0) { throw 'Runtime checkout failed.' }
$actual = & $GitPath -C $root rev-parse -q --verify HEAD 2>$null
if ("$actual".Trim() -ne $commit) {
    # Fetched by hash, so later pushes upstream never change what is packaged.
    & $GitPath -c http.sslBackend=openssl -C $root fetch -q --depth 1 --filter=blob:none origin $commit
    if ($LASTEXITCODE -ne 0) { throw 'Runtime fetch failed.' }
    & $GitPath -c http.sslBackend=openssl -C $root checkout -q --detach $commit
    if ($LASTEXITCODE -ne 0) { throw 'Runtime checkout failed.' }
}
$actual = & $GitPath -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or "$actual".Trim() -ne $commit) { throw "Runtime checkout must be pinned to $commit; found $actual" }
foreach ($name in 'sl.interposer.dll', 'sl.common.dll', 'sl.dlss.dll', 'nvngx_dlss.dll', 'sl.reflex.dll', 'sl.pcl.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $root "third_party\NVIDIA-DLSS\$name") -PathType Leaf)) { throw "NVIDIA runtime file missing: $name" }
}
Write-Output "NVIDIA Streamline 2.14.1 and DLSS 310.9.1 runtime ready at $root ($commit)"
