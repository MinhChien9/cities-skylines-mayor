<#
.SYNOPSIS
    Saves the running city, restarts Cities: Skylines with a freshly built bridge mod, and
    resumes the same city.

.DESCRIPTION
    Every bridge change needs a game restart: the game loads mod assemblies once. Doing
    that by hand means saving, quitting, copying the DLL, relaunching and clicking
    Continue. This script does all of it:

      1. POST /commands/save with a timestamped name and wait until the file is on disk
         (skipped when the bridge is not answering, e.g. the game is at the main menu)
      2. stop Cities.exe
      3. build and install the mod (scripts/build-mod.ps1)
      4. relaunch with --continuelastsave, which loads the save from step 1. By default the
         game is started through the Epic Games Launcher, so it gets the Epic login and
         loads the owned DLC; the Paradox launcher is skipped, no click on Play needed
         (scripts/game-launch.ps1). -Launch Direct starts Cities.exe bare: no DLC
      5. wait for /health to report levelLoaded, then print /debug/threads

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\restart-game.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\restart-game.ps1 -NoBuild
#>
param(
    [string]$GamePath = "",
    [string]$Bridge = "http://127.0.0.1:32123",
    [switch]$NoBuild,
    [ValidateSet("Epic", "Direct")][string]$Launch = "Epic",
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "game-launch.ps1")

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = if ($env:CITIES_SKYLINES_PATH) { $env:CITIES_SKYLINES_PATH } else { "D:\Epic Games\CitiesSkylines" }
}
$saves = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Saves"

# ---- 1. save -----------------------------------------------------------------
$saveName = "restart-" + (Get-Date -Format "yyyyMMdd-HHmmss")
$saved = $false
try {
    $health = Invoke-RestMethod -Uri "$Bridge/health" -TimeoutSec 3
    if ($health.levelLoaded) {
        Invoke-RestMethod -Method Post -Uri "$Bridge/commands/save" -ContentType "application/json" `
            -Body ("{""name"":""$saveName""}") | Out-Null
        $file = Join-Path $saves "$saveName.crp"
        $deadline = (Get-Date).AddSeconds(90)
        $lastSize = -1
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            if (Test-Path $file) {
                $size = (Get-Item $file).Length
                if ($size -gt 0 -and $size -eq $lastSize) { $saved = $true; break }
                $lastSize = $size
            }
        }
        if (!$saved) { throw "The save '$saveName' did not finish writing within 90 seconds. Not restarting." }
        Write-Host "Saved city as $saveName ($lastSize bytes)."
    }
} catch [System.Net.WebException] {
    Write-Host "Bridge not answering - restarting without saving."
}

# ---- 2. stop -----------------------------------------------------------------
$proc = Get-Process -Name "Cities" -ErrorAction SilentlyContinue
if ($proc) {
    Stop-Process -Name "Cities" -Force
    $proc | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "Stopped Cities: Skylines."
}
Stop-ParadoxLauncher

# ---- 3. build + install ------------------------------------------------------
if (!$NoBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-mod.ps1") -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) { throw "build-mod.ps1 failed." }
}

# ---- 4. relaunch -------------------------------------------------------------
Start-CitiesSkylines -GamePath $GamePath -GameArgs @("--continuelastsave") -Launch $Launch
Write-Host "Waiting for the city to load..."

# ---- 5. wait -----------------------------------------------------------------
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    try {
        $health = Invoke-RestMethod -Uri "$Bridge/health" -TimeoutSec 3
        if ($health.levelLoaded) {
            $threads = Invoke-RestMethod -Uri "$Bridge/debug/threads"
            Write-Host ("City loaded. runsOnSimulationThread=" + $threads.runsOnSimulationThread + ", gameTime=" + $threads.gameTime)
            if (!$threads.runsOnSimulationThread) { exit 2 }
            exit 0
        }
    } catch {
        # still loading
    }
}
Write-Host "The city did not load within $TimeoutSeconds seconds." -ForegroundColor Yellow
exit 1
