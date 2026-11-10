<#
    Starts Cities: Skylines and waits for the CS1 Mayor Bridge to answer.

    Usage:
        .\scripts\start-game.ps1
        .\scripts\start-game.ps1 -GamePath "D:\Epic Games\CitiesSkylines"
        .\scripts\start-game.ps1 -Resume      # resume the newest save
#>
param(
    [string]$GamePath = "D:\Epic Games\CitiesSkylines",
    [switch]$Resume,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $GamePath "Cities.exe"
if (!(Test-Path $exe)) {
    throw "Cities.exe was not found at $exe. Pass -GamePath."
}

if (Get-Process -Name "Cities" -ErrorAction SilentlyContinue) {
    Write-Host "Cities: Skylines is already running."
} else {
    Write-Host "Launching Cities: Skylines..."
    Start-Process -FilePath $exe
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
    Write-Host "Do not let the agent act until this is fixed. See docs\01-architecture.md."
    exit 1
}
