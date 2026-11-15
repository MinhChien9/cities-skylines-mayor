<#
.SYNOPSIS
    Cuts a release: checks, builds the mod and the MCP server, tags, publishes on GitHub.

.DESCRIPTION
    Releases are cut locally because the mod compiles against Cities: Skylines' own
    assemblies, which a CI runner does not have. See docs/14-releasing.md.

      1. Refuses unless: on main, no local changes, not behind origin/main, -Version
         matches Directory.Build.props, CHANGELOG.md has "## [<Version>]", tag v<Version>
         does not exist.
      2. Runs the check suite: build-mod.ps1 -NoInstall (0 warnings), dotnet test, the MCP
         smoke test, the API doc check.
      3. Builds dist/cs1-mayor-mod-v<Version>.zip (SkylinesAgentBridge.dll + INSTALL.txt)
         and dist/cs1-mayor-mcp-v<Version>.zip (dotnet publish, framework-dependent).
      4. Unless -DryRun: annotated tag, push, `gh release create` with the CHANGELOG
         section as the notes and both zips attached.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 -Version 0.1.0 -DryRun
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$GamePath = "",
    [string]$Repo = "MinhChien9/cities-skylines-mayor",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Fail([string]$message) { Write-Host "release: $message" -ForegroundColor Red; exit 1 }
function Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Run([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit $LASTEXITCODE)" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must be X.Y.Z, got '$Version'" }
$tag = "v$Version"

# ---- 1. preconditions ----------------------------------------------------------
Step "preconditions"
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") { Fail "on '$branch'; releases are cut from main" }
if (git status --porcelain) { Fail "the working tree has changes" }
git fetch -q origin main
$behind = (git rev-list --count HEAD..origin/main).Trim()
if ($behind -ne "0") { Fail "main is $behind commit(s) behind origin/main; pull first" }
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$propsVersion = $props.Project.PropertyGroup.Version
if ($propsVersion -ne $Version) { Fail "Directory.Build.props says $propsVersion, not $Version" }
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding UTF8
$match = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|^\[[^\]]+\]: |\z)")
if (-not $match.Success -or -not $match.Groups[1].Value.Trim()) { Fail "CHANGELOG.md has no '## [$Version]' section with content" }
$notes = $match.Groups[1].Value.Trim()
if (git tag --list $tag) { Fail "tag $tag already exists" }

# ---- 2. checks -----------------------------------------------------------------
Step "mod build (0 warnings)"
$buildArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "build-mod.ps1"), "-NoInstall")
if ($GamePath) { $buildArgs += @("-GamePath", $GamePath) }
$modLog = & powershell @buildArgs 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Write-Host $modLog; Fail "build-mod.ps1 failed" }
if ($modLog -notmatch '\b0 Warning\(s\)') { Write-Host $modLog; Fail "the mod build has warnings" }

Step "tests"
Run "dotnet test" { dotnet test tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj --nologo -v q }
Step "MCP server (Release) and smoke test"
Run "dotnet build" { dotnet build mcp-server/Cs1Mayor.Mcp.csproj -c Release --nologo -v q }
Run "smoke test" { python scripts/smoke-test-mcp.py --server mcp-server/bin/Release/net10.0/Cs1Mayor.Mcp.dll }
Step "API doc check"
Run "check-api-docs" { python scripts/check-api-docs.py }

# ---- 3. assets -----------------------------------------------------------------
Step "assets"
$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force (Join-Path $dist "mod"), (Join-Path $dist "mcp") | Out-Null

Copy-Item (Join-Path $root "bin\SkylinesAgentBridge.dll") (Join-Path $dist "mod")
@"
CS1 Mayor bridge mod $tag
==========================

1. Copy SkylinesAgentBridge.dll to
   %LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge\
2. Start Cities: Skylines, enable "Skylines Agent Bridge" in Content Manager > Mods.
3. Load a city. The bridge answers on http://127.0.0.1:32123/health (localhost only).

Then wire the MCP server (cs1-mayor-mcp-$tag.zip) into your agent:
    dotnet Cs1Mayor.Mcp.dll          (needs the .NET 10 runtime)
Docs: https://github.com/$Repo/blob/$tag/docs/README.md
"@ | Set-Content -Encoding UTF8 (Join-Path $dist "mod\INSTALL.txt")
$modZip = Join-Path $dist "cs1-mayor-mod-$tag.zip"
Compress-Archive -Path (Join-Path $dist "mod\*") -DestinationPath $modZip

Run "dotnet publish" { dotnet publish mcp-server/Cs1Mayor.Mcp.csproj -c Release -o (Join-Path $dist "mcp") --nologo -v q }
$mcpZip = Join-Path $dist "cs1-mayor-mcp-$tag.zip"
Compress-Archive -Path (Join-Path $dist "mcp\*") -DestinationPath $mcpZip
$notesFile = Join-Path $dist "notes.md"
Set-Content -Encoding UTF8 $notesFile $notes
Get-Item $modZip, $mcpZip | ForEach-Object { Write-Host ("    {0} ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB)) }

if ($DryRun) {
    Write-Host "Dry run: checks passed and assets built in dist/. Nothing tagged or published." -ForegroundColor Green
    exit 0
}

# ---- 4. tag and publish --------------------------------------------------------
Step "tag $tag and publish"
Run "git tag" { git tag -a $tag -m "Release $tag" }
Run "git push tag" { git push origin $tag }
Run "gh release create" { gh release create $tag $modZip $mcpZip -R $Repo --title "CS1 Mayor $tag" --notes-file $notesFile --verify-tag }
Write-Host "Released ${tag}: https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
