<#
.SYNOPSIS
    Builds the CS1 Mayor bridge mod and installs it into the local
    Cities: Skylines user mods folder.

.DESCRIPTION
    The game ships its own Mono runtime and a full set of reference assemblies in
    Cities_Data\Managed. The mod compiles against those exact assemblies so it links
    against the same BCL the game loads at runtime.

    The build is a normal MSBuild build of src\SkylinesAgentBridge.csproj. That project
    file disables the default framework references and points at the game's own
    assemblies instead, which is why the output can be dropped straight into the game.

.PARAMETER GamePath
    Path to the Cities: Skylines installation. Defaults to $env:CITIES_SKYLINES_PATH,
    then to "D:\Epic Games\CitiesSkylines".

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoInstall
    Build only. Do not copy the DLL into the game's mods folder.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1 -GamePath "D:\Games\CitiesSkylines"
#>
param(
    [string]$GamePath = "",
    [string]$Configuration = "Release",
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
if ($repo -like "*\scripts") { $repo = Split-Path -Parent $repo }

$project = Join-Path $repo "src\SkylinesAgentBridge.csproj"
$out = Join-Path $repo "bin"

# ---- Locate the game --------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CITIES_SKYLINES_PATH)) {
        $GamePath = $env:CITIES_SKYLINES_PATH
    }
    else {
        $GamePath = "D:\Epic Games\CitiesSkylines"
    }
}

$managed = Join-Path $GamePath "Cities_Data\Managed"
if (!(Test-Path (Join-Path $managed "ICities.dll"))) {
    throw @"
Cities: Skylines managed assemblies were not found at '$managed'.

Pass the install path explicitly:
    .\scripts\build-mod.ps1 -GamePath "D:\Games\CitiesSkylines"

or set it for the session:
    `$env:CITIES_SKYLINES_PATH = "D:\Games\CitiesSkylines"
"@
}

# ---- Build ------------------------------------------------------------------
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found on PATH. Install it from https://dotnet.microsoft.com/download"
}

Write-Host "Game path : $GamePath"
Write-Host "Building  : $project ($Configuration)"

& dotnet build $project -c $Configuration --nologo -p:GamePath="$GamePath"
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

$target = Join-Path $out "SkylinesAgentBridge.dll"
if (!(Test-Path $target)) {
    throw "The build succeeded but '$target' was not produced."
}

# ---- Install ----------------------------------------------------------------
if (!$NoInstall) {
    $modDir = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge"
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null
    Copy-Item -LiteralPath $target -Destination (Join-Path $modDir "SkylinesAgentBridge.dll") -Force
    Write-Host "Installed to $modDir"
}

Write-Host "OK: $target"
