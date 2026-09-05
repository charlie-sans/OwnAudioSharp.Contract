<#
Builds the OwnAudioSharp.Contract bridge host and runs the library self-test.

  .\run.ps1            # build bridge + run self-test
  .\run.ps1 -NoBuild   # skip the bridge build, just run from the last build

The sample project (samples/contract.ctproj) declares ImportRoots pointing at this
library's src/ and the stdlib's Memory/ dir, so `import OwnAudioSharp;` resolves
src/OwnAudio.ct by its DECLARED NAMESPACE (compiler content-based resolution), no
copying needed. This is the same C#/Java "classpath" a consumer app would use to
discover the library.

Uses:  ccl --bind bridge/bin/Debug/net10.0/OwnAudioSharp.Contract.dll samples/main.ct
Because `ccl build` does not forward --bind, we drive the CLI directly.
#>
param(
    [switch]$NoBuild,
    [switch]$RealDevice,
    [string]$Cli = "ccl",
    [string]$BridgeDll = "bridge\bin\Debug\net10.0\OwnAudioSharp.Contract.dll",
    [string]$Main = "samples\main.ct"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path ".\"
$bridgeAbs = Join-Path $root ($BridgeDll -replace '\\','/')
$mainAbs = Join-Path $root ($Main -replace '\\','/')

if (-not $NoBuild) {
    Write-Host "== Building OwnAudioSharp.Contract bridge ==" -ForegroundColor Cyan
    Push-Location (Join-Path $root "bridge")
    try { dotnet build -c Debug 2>&1 | ForEach-Object { $_.ToString() } | Write-Host }
    finally { Pop-Location }
}

if (-not (Test-Path $bridgeAbs)) { throw "Bridge DLL not found: $bridgeAbs (run without -NoBuild)" }
if (-not (Test-Path $mainAbs)) { throw "Main file not found: $mainAbs" }

# -RealDevice: play through the system DEFAULT output device (real audio) so
# the demo actually makes sound; otherwise the software mock engine is used.
$env:OWNAUDIO_REAL = if ($RealDevice) { "1" } else { "0" }

Write-Host "`n== Running self-test: $mainAbs ==" -ForegroundColor Cyan
Push-Location $root
try { & $Cli --bind $bridgeAbs $mainAbs }
finally { Pop-Location }
exit $LASTEXITCODE