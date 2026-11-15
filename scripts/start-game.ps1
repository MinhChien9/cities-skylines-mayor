<#
    Starts Cities: Skylines and waits for the CS1 Mayor Bridge to answer.

    The game is started through the Epic Games Launcher by default, so it gets the Epic
    login and loads the owned DLC; the Paradox launcher is skipped (scripts/game-launch.ps1).

    Usage:
        .\scripts\start-game.ps1
        .\scripts\start-game.ps1 -GamePath "D:\Epic Games\CitiesSkylines"
        .\scripts\start-game.ps1 -Resume          # resume the newest save
        .\scripts\start-game.ps1 -Launch Direct   # Cities.exe bare: no Epic login, no DLC
#>
param(
    [string]$GamePath = "",
    [switch]$Resume,
    [ValidateSet("Epic", "Direct")][string]$Launch = "Epic",
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "game-launch.ps1")

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = if ($env:CITIES_SKYLINES_PATH) { $env:CITIES_SKYLINES_PATH } else { "D:\Epic Games\CitiesSkylines" }
}

if (Get-Process -Name "Cities" -ErrorAction SilentlyContinue) {
    Write-Host "Cities: Skylines is already running."
} else {
    Write-Host "Launching Cities: Skylines..."
    $gameArgs = if ($Resume) { @("--continuelastsave") } else { @() }
    Start-CitiesSkylines -GamePath $GamePath -GameArgs $gameArgs -Launch $Launch
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$bridgeUp = $false

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:32123/health" -TimeoutSec 3
        if ($health.ok) {
            $bridgeUp = $true
            break
        }
    } catch {
        # The bridge only starts once a city is loaded.
    }
}

if (!$bridgeUp) {
    Write-Host ""
    Write-Host "The bridge did not answer within $TimeoutSeconds seconds." -ForegroundColor Yellow
    Write-Host "That is expected if no city is loaded yet. Load a city in the game, then run:"
    Write-Host "    Invoke-RestMethod http://127.0.0.1:32123/debug/threads"
    exit 1
}

Write-Host ""
Write-Host "Bridge is up. Thread proof:" -ForegroundColor Green
$threads = Invoke-RestMethod -Uri "http://127.0.0.1:32123/debug/threads"
$threads | Format-List

if ($threads.runsOnSimulationThread -and !$threads.runsOnMainThread) {
    Write-Host "PASS - commands execute on the simulation thread." -ForegroundColor Green
} else {
    Write-Host "FAIL - commands are NOT running on the simulation thread." -ForegroundColor Red
    Write-Host "Do not let the agent act until this is fixed. See docs\architecture.md."
    exit 1
}
