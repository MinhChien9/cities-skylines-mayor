<#
    Installs the CS1 Mayor Bridge mod into the local Cities: Skylines user mods folder
    and verifies the installation.

    The mod is NOT enabled automatically: Cities: Skylines keeps a content-manager
    database (playsets_mods) that must be updated by the launcher. Enable it once in
    the game's content manager, or via the Content Manager UI.

    Usage:
        .\scripts\install-mod.ps1
        .\scripts\install-mod.ps1 -GamePath "D:\Epic Games\CitiesSkylines"
#>
param(
    [string]$GamePath = "D:\Epic Games\CitiesSkylines",
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
Write-Host "  2. Content Manager -> enable 'Skylines Agent Bridge'"
Write-Host "  3. Load a city"
Write-Host "  4. Verify the thread proof:"
Write-Host "       Invoke-RestMethod http://127.0.0.1:32123/debug/threads"
Write-Host ""
Write-Host "  probeThreadId MUST equal simulationThreadId and MUST NOT equal mainThreadId."
Write-Host "  If it does not, stop - see docs\ARCHITECTURE.md."
