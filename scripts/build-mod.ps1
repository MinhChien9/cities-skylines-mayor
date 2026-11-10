<#
    Builds the CS1 Mayor Bridge mod DLL and installs it into the local
    Cities: Skylines user mods folder.

    The game ships its own Mono runtime and a full set of reference assemblies in
    Cities_Data\Managed. We compile against those exact assemblies so the mod links
    against the same BCL the game loads at runtime.

    NOTE: reference Cities_Data\Managed\System.Core.dll (NOT Mono\lib\mono\unity\).
    The "unity" compile profile does not contain System.IO.Pipes, while the runtime
    Managed\ folder does.
#>
param(
    [string]$GamePath = "D:\Epic Games\CitiesSkylines",
    [string]$Configuration = "Release",
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ($repo -like "*\scripts") { $repo = Split-Path -Parent $repo }
$src = Join-Path $repo "src"
$out = Join-Path $repo "bin"

$managed = Join-Path $GamePath "Cities_Data\Managed"
if (!(Test-Path (Join-Path $managed "ICities.dll"))) {
    throw "Cities: Skylines managed assemblies were not found at $managed. Pass -GamePath."
}

# The game ships its own C# compiler (Unity's smcs, C# 3.0 era) which cannot build
# modern language features, so we use the installed .NET SDK's Roslyn compiler and
# target the game's own reference assemblies explicitly.
$csc = Get-ChildItem "C:\Program Files\dotnet\sdk" -Recurse -Filter csc.dll -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "Roslyn\\bincore\\csc.dll$" } |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if (!$csc) {
    throw "Roslyn csc.dll was not found under C:\Program Files\dotnet\sdk. Install the .NET SDK."
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

$target = Join-Path $out "SkylinesAgentBridge.dll"
if (Test-Path $target) { Remove-Item $target -Force }

# Reference set: the game's OWN runtime assemblies.
$refs = @(
    (Join-Path $managed "mscorlib.dll"),
    (Join-Path $managed "System.dll"),
    (Join-Path $managed "System.Core.dll"),
    (Join-Path $managed "ICities.dll"),
    (Join-Path $managed "Assembly-CSharp.dll"),
    (Join-Path $managed "Assembly-CSharp-firstpass.dll"),
    (Join-Path $managed "ColossalManaged.dll"),
    (Join-Path $managed "UnityEngine.dll")
)

$missing = $refs | Where-Object { !(Test-Path $_) }
if ($missing) {
    throw "Missing reference assemblies: $($missing -join ', ')"
}

$sources = Get-ChildItem -LiteralPath $src -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName }

$argList = @(
    "exec", $csc,
    "-noconfig", "-nostdlib", "-nologo",
    "-target:library",
    "-optimize+",
    "-define:TRACE",
    "-langversion:5",
    "-out:$target"
)
foreach ($r in $refs) { $argList += "-r:$r" }
$argList += $sources

Write-Host "Building $target"
& dotnet @argList
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

if (!$NoInstall) {
    $modDir = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge"
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null
    Copy-Item -LiteralPath $target -Destination (Join-Path $modDir "SkylinesAgentBridge.dll") -Force
    Write-Host "Installed to $modDir"
}

Write-Host "OK: $target"
