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
    [string]$Cli = "ccl",
    [string]$BridgeDll = "bridge\bin\Debug\net10.0\OwnAudioSharp.Contract.dll",
    [string]$Main = "samples\main.ct"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "D:\git\OwnAudioSharp.Contract"
$bridgeAbs = Join-Path $root ($BridgeDll -replace '\\','/')

if (-not $NoBuild) {
    Write-Host "== Building OwnAudioSharp.Contract bridge ==" -ForegroundColor Cyan
    Push-Location (Join-Path $root "bridge")
    try { dotnet build -c Debug 2>&1 | ForEach-Object { $_.ToString() } | Write-Host }
    finally { Pop-Location }
}

if (-not (Test-Path $bridgeAbs)) { throw "Bridge DLL not found: $bridgeAbs (run without -NoBuild)" }

Write-Host "`n== Running self-test: $Main ==" -ForegroundColor Cyan
Push-Location $root
try { & $Cli --bind $bridgeAbs $Main }
finally { Pop-Location }
exit $LASTEXITCODE