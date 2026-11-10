<#
.SYNOPSIS
    Installs the CS1 Mayor bridge mod into the local Cities: Skylines user mods folder
    and verifies the installation.

.DESCRIPTION
    The mod auto-loads: Cities: Skylines picks up any DLL dropped into the Mods folder,
    so it does not have to be enabled in the content manager. It reports
    "levelLoaded:false" until a city is loaded.

.EXAMPLE
    .\scripts\install-mod.ps1
    .\scripts\install-mod.ps1 -GamePath "D:\Games\CitiesSkylines"
#>
param(
    [string]$GamePath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ($repo -like "*\scripts") { $repo = Split-Path -Parent $repo }

if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot "build-mod.ps1") -GamePath $GamePath
}

$dll = Join-Path $repo "bin\SkylinesAgentBridge.dll"
if (!(Test-Path $dll)) {
    throw "The mod DLL was not found at $dll. Run scripts\build-mod.ps1 first."
}

$modDir = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge"
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $modDir "SkylinesAgentBridge.dll") -Force

Write-Host ""
Write-Host "Installed to: $modDir"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Start Cities: Skylines"
Write-Host "  2. Load a city"
Write-Host "  3. Verify the thread proof:"
Write-Host "       Invoke-RestMethod http://127.0.0.1:32123/debug/threads"
Write-Host ""
Write-Host "  probeThreadId MUST equal simulationThreadId and MUST NOT equal mainThreadId."
Write-Host "  If it does not, stop - see docs/01-architecture.md."
