<#
.SYNOPSIS
    Sets up OBS Studio for recording the city: obs-websocket on, a "CS1 Mayor" scene with
    a Game Capture of Cities.exe, and NVENC hybrid-MP4 recording to D:\cs1-mayor-recordings.

.DESCRIPTION
    The studio tools in the MCP server drive OBS through obs-websocket v5. This script
    prepares the portable OBS install for that, and is safe to run again:

      1. obs-websocket: enabled on port 4455 with authentication. The password comes from
         the git-ignored studio.local.json at the repo root (generated on the first run,
         or with -RotatePassword). OBS reads this file only at start-up and may rewrite it
         on exit, so when it has to change OBS is closed first, the file is written, and
         OBS is started again with the same portable flags.
      2. Over the websocket (so OBS stays the owner of its own scene and profile files):
         - canvas and output 1920x1080 at 60 fps
         - Simple output mode, NVENC H.264, hybrid MP4 (the format that carries chapter
           markers), recordings in -RecordDirectory, file names prefixed "cs1-mayor"
         - scene "CS1 Mayor" with a Game Capture input matched on Cities.exe, scaled to
           fit the canvas, cursor hidden; microphones muted (the voice-over is added later)
      3. Checks: a preview frame of the scene saved as setup-check.png in the recording
         folder (proves the capture hooked the game), and a three-second test recording
         checked with ffprobe and then deleted (skip it with -SkipTestRecording).
      4. studio.local.json is written with everything the MCP server needs to connect.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-obs.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-obs.ps1 -RotatePassword
#>
param(
    [string]$ObsDir = "D:\tools\obs-studio",
    [string]$FfprobePath = "D:\tools\ffmpeg\bin\ffprobe.exe",
    [string]$RecordDirectory = "D:\cs1-mayor-recordings",
    [int]$Port = 4455,
    [string]$SceneName = "CS1 Mayor",
    [string]$CaptureName = "Game Capture: Cities",
    [string]$GameExecutable = "Cities.exe",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Fps = 60,
    [switch]$RotatePassword,
    [switch]$SkipTestRecording
)

$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

$repoRoot = Split-Path $PSScriptRoot -Parent
$localConfigPath = Join-Path $repoRoot "studio.local.json"
$obsExe = Join-Path $ObsDir "bin\64bit\obs64.exe"
$obsArgs = "--portable --minimize-to-tray --disable-shutdown-check --disable-updater"
$wsConfigPath = Join-Path $ObsDir "config\obs-studio\plugin_config\obs-websocket\config.json"

if (!(Test-Path $obsExe)) { throw "OBS not found at $obsExe (pass -ObsDir)." }

# ---- password ----------------------------------------------------------------
function New-Password([int]$length = 24) {
    $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
    $bytes = New-Object byte[] $length
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $rng.GetBytes($bytes)
    $rng.Dispose()
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

$password = $null
if (!$RotatePassword -and (Test-Path $localConfigPath)) {
    $existing = Get-Content $localConfigPath -Raw | ConvertFrom-Json
    if ($existing.obs -and $existing.obs.password) { $password = [string]$existing.obs.password }
}
if (!$password) { $password = New-Password }

# ---- 1. obs-websocket config -------------------------------------------------
$wsWanted = [ordered]@{
    alerts_enabled  = $false
    auth_required   = $true
    first_load      = $false
    server_enabled  = $true
    server_password = $password
    server_port     = $Port
}
$wsCurrent = $null
if (Test-Path $wsConfigPath) { $wsCurrent = Get-Content $wsConfigPath -Raw | ConvertFrom-Json }
$wsOk = $wsCurrent -and $wsCurrent.server_enabled -and $wsCurrent.auth_required `
    -and ($wsCurrent.server_port -eq $Port) -and ($wsCurrent.server_password -ceq $password)

function Stop-Obs {
    $procs = @(Get-Process -Name "obs64" -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    foreach ($p in $procs) { [void]$p.CloseMainWindow() }
    foreach ($p in $procs) {
        if (!$p.WaitForExit(15000)) {
            # Minimised to the tray there is no main window to close; nothing is recording
            # through our tools yet (the websocket was off), so a hard stop loses nothing.
            Stop-Process -Id $p.Id -Force
            $p.WaitForExit(10000) | Out-Null
        }
    }
    Start-Sleep -Seconds 1
    Write-Host "Stopped OBS."
}

function Start-Obs {
    Start-Process -FilePath $obsExe -ArgumentList $obsArgs -WorkingDirectory (Split-Path $obsExe)
    Write-Host "Started OBS ($obsArgs)."
}

if (!$wsOk) {
    Stop-Obs
    New-Item -ItemType Directory -Force (Split-Path $wsConfigPath) | Out-Null
    [IO.File]::WriteAllText($wsConfigPath, ($wsWanted | ConvertTo-Json), $utf8NoBom)
    Write-Host "obs-websocket enabled on port $Port with authentication."
}
if (!(Get-Process -Name "obs64" -ErrorAction SilentlyContinue)) { Start-Obs }

# ---- obs-websocket v5 client -------------------------------------------------
$script:ws = $null
$script:requestCounter = 0

function Receive-ObsMessage {
    $buffer = New-Object byte[] 65536
    $stream = New-Object System.IO.MemoryStream
    do {
        $task = $script:ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None)
        if (!$task.Wait(20000)) { throw "Timed out waiting for obs-websocket." }
        $result = $task.Result
        if ($result.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) {
            throw "obs-websocket closed the connection ($($result.CloseStatus) $($result.CloseStatusDescription))."
        }
        $stream.Write($buffer, 0, $result.Count)
    } while (!$result.EndOfMessage)
    $utf8NoBom.GetString($stream.ToArray()) | ConvertFrom-Json
}

function Send-ObsMessage($message) {
    $bytes = $utf8NoBom.GetBytes(($message | ConvertTo-Json -Depth 20 -Compress))
    $script:ws.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true,
        [Threading.CancellationToken]::None).Wait()
}

function Get-Sha256Base64([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToBase64String($sha.ComputeHash($utf8NoBom.GetBytes($text))) } finally { $sha.Dispose() }
}

function Connect-Obs([int]$timeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ($true) {
        $socket = New-Object System.Net.WebSockets.ClientWebSocket
        try {
            $socket.ConnectAsync([Uri]"ws://127.0.0.1:$Port", [Threading.CancellationToken]::None).Wait()
            break
        } catch {
            $socket.Dispose()
            if ((Get-Date) -gt $deadline) { throw "obs-websocket did not answer on port $Port within $timeoutSeconds s." }
            Start-Sleep -Seconds 2
        }
    }
    $script:ws = $socket
    $hello = Receive-ObsMessage
    if ($hello.op -ne 0) { throw "Expected Hello (op 0), got op $($hello.op)." }
    $identify = @{ rpcVersion = 1; eventSubscriptions = 0 }
    if ($hello.d.authentication) {
        $secret = Get-Sha256Base64 ($password + $hello.d.authentication.salt)
        $identify.authentication = Get-Sha256Base64 ($secret + $hello.d.authentication.challenge)
    }
    Send-ObsMessage @{ op = 1; d = $identify }
    $identified = Receive-ObsMessage
    if ($identified.op -ne 2) { throw "obs-websocket did not identify us (op $($identified.op))." }
}

function Invoke-Obs([string]$requestType, $requestData = $null, [switch]$AllowFailure) {
    $script:requestCounter++
    $id = "setup-$($script:requestCounter)"
    $d = @{ requestType = $requestType; requestId = $id }
    if ($null -ne $requestData) { $d.requestData = $requestData }
    Send-ObsMessage @{ op = 6; d = $d }
    while ($true) {
        $message = Receive-ObsMessage
        if ($message.op -ne 7 -or $message.d.requestId -ne $id) { continue }
        $status = $message.d.requestStatus
        if (!$status.result) {
            if ($AllowFailure) { return $null }
            throw "$requestType failed: code $($status.code) $($status.comment)"
        }
        return $message.d.responseData
    }
}

# ---- 2. configure over the websocket -----------------------------------------
Connect-Obs
$version = Invoke-Obs "GetVersion"
Write-Host ("Connected: OBS {0}, obs-websocket {1}." -f $version.obsVersion, $version.obsWebSocketVersion)

$recording = (Invoke-Obs "GetRecordStatus").outputActive
if ($recording) { throw "OBS is recording; stop the recording and run this again." }

New-Item -ItemType Directory -Force $RecordDirectory | Out-Null

Invoke-Obs "SetVideoSettings" @{
    baseWidth = $Width; baseHeight = $Height; outputWidth = $Width; outputHeight = $Height
    fpsNumerator = $Fps; fpsDenominator = 1
} | Out-Null

$profileParameters = @(
    @("Output", "Mode", "Simple"),
    @("Output", "FilenameFormatting", "cs1-mayor %CCYY-%MM-%DD %hh-%mm-%ss"),
    @("SimpleOutput", "RecQuality", "Small"),
    @("SimpleOutput", "RecEncoder", "nvenc"),
    @("SimpleOutput", "RecFormat2", "hybrid_mp4")
)
foreach ($p in $profileParameters) {
    Invoke-Obs "SetProfileParameter" @{ parameterCategory = $p[0]; parameterName = $p[1]; parameterValue = $p[2] } | Out-Null
}
Invoke-Obs "SetRecordDirectory" @{ recordDirectory = $RecordDirectory } | Out-Null

$scenes = (Invoke-Obs "GetSceneList").scenes | ForEach-Object { $_.sceneName }
if ($scenes -notcontains $SceneName) {
    Invoke-Obs "CreateScene" @{ sceneName = $SceneName } | Out-Null
    Write-Host "Created scene '$SceneName'."
}
Invoke-Obs "SetCurrentProgramScene" @{ sceneName = $SceneName } | Out-Null

# Game Capture matched on the executable, so a changed window title still captures.
# OBS stores the window as "title:class:exe" with ':' in the title escaped as "#3A".
$gameTitle = "Cities#3A Skylines"
$captureSettings = @{
    capture_mode       = "window"
    window             = "${gameTitle}:UnityWndClass:$GameExecutable"
    priority           = 2          # WINDOW_PRIORITY_EXE: match the executable first
    capture_cursor     = $false     # the mayor has no cursor; keep B-roll clean
    allow_transparency = $false
}
$inputs = (Invoke-Obs "GetInputList" @{ inputKind = "game_capture" }).inputs | ForEach-Object { $_.inputName }
if ($inputs -notcontains $CaptureName) {
    Invoke-Obs "CreateInput" @{
        sceneName = $SceneName; inputName = $CaptureName; inputKind = "game_capture"
        inputSettings = $captureSettings; sceneItemEnabled = $true
    } | Out-Null
    Write-Host "Created input '$CaptureName'."
} else {
    Invoke-Obs "SetInputSettings" @{ inputName = $CaptureName; inputSettings = $captureSettings; overlay = $true } | Out-Null
}
# Prefer the exact window string OBS lists for the running game, when it is running.
$windows = Invoke-Obs "GetInputPropertiesListPropertyItems" @{ inputName = $CaptureName; propertyName = "window" } -AllowFailure
$match = $null
if ($windows) { $match = $windows.propertyItems | Where-Object { $_.itemValue -like "*:$GameExecutable" } | Select-Object -First 1 }
if ($match -and $match.itemValue -ne $captureSettings.window) {
    Invoke-Obs "SetInputSettings" @{ inputName = $CaptureName; inputSettings = @{ window = $match.itemValue }; overlay = $true } | Out-Null
}

$itemId = (Invoke-Obs "GetSceneItemId" @{ sceneName = $SceneName; sourceName = $CaptureName }).sceneItemId
Invoke-Obs "SetSceneItemTransform" @{
    sceneName = $SceneName; sceneItemId = $itemId
    sceneItemTransform = @{
        positionX = 0; positionY = 0; alignment = 5           # top-left
        boundsType = "OBS_BOUNDS_SCALE_INNER"; boundsAlignment = 0
        boundsWidth = $Width; boundsHeight = $Height
    }
} | Out-Null

# Mute every microphone input; the game's own sound (desktop audio) stays.
$mics = @((Invoke-Obs "GetInputList" @{ inputKind = "wasapi_input_capture" }).inputs | ForEach-Object { $_.inputName })
foreach ($mic in $mics) {
    Invoke-Obs "SetInputMute" @{ inputName = $mic; inputMuted = $true } | Out-Null
}
$micState = "none found"
if ($mics.Count -gt 0) { $micState = "muted: " + ($mics -join ", ") }

# ---- preview frame: proves the capture hooked the game (not a black frame) ---
$previewPath = Join-Path $RecordDirectory "setup-check.png"
$previewResult = "not captured"
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $active = Invoke-Obs "GetSourceActive" @{ sourceName = $CaptureName }
    $shot = Invoke-Obs "GetSourceScreenshot" @{ sourceName = $SceneName; imageFormat = "png"; imageWidth = 640 }
    $bytes = [Convert]::FromBase64String(($shot.imageData -replace "^data:image/png;base64,", ""))
    # A black 640x360 PNG compresses to well under 5 KB; a real frame is far larger.
    if ($active.videoActive -and $bytes.Length -gt 5000) {
        [IO.File]::WriteAllBytes($previewPath, $bytes)
        $previewResult = "$previewPath ({0:N0} KB)" -f ($bytes.Length / 1KB)
        break
    }
    Start-Sleep -Seconds 1
}
if ($previewResult -eq "not captured") {
    Write-Host "The game capture shows no picture yet. Is $GameExecutable running and not minimised?" -ForegroundColor Yellow
}

# ---- 3. test recording -------------------------------------------------------
$testResult = "skipped"
if (!$SkipTestRecording) {
    if (!(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExecutable)) -ErrorAction SilentlyContinue)) {
        Write-Host "$GameExecutable is not running; the test recording will be black." -ForegroundColor Yellow
    }
    Invoke-Obs "StartRecord" | Out-Null
    Start-Sleep -Seconds 3
    $outputPath = (Invoke-Obs "StopRecord").outputPath
    Start-Sleep -Seconds 1
    if (!(Test-Path $outputPath)) { throw "The test recording was not written ($outputPath)." }
    $probe = "no ffprobe"
    if (Test-Path $FfprobePath) {
        $probe = (& $FfprobePath -v error -select_streams v:0 `
            -show_entries stream=codec_name,width,height,avg_frame_rate -of csv=p=0 $outputPath) -join " "
        if ($probe -notmatch "^h264,$Width,$Height,") { throw "Unexpected test recording stream: $probe ($outputPath kept)." }
    }
    $testResult = "{0} ({1}, {2:N0} KB) - deleted" -f [IO.Path]::GetFileName($outputPath), $probe, ((Get-Item $outputPath).Length / 1KB)
    Remove-Item $outputPath
}

$hasChapters = @($version.availableRequests) -contains "CreateRecordChapter"
$script:ws.Dispose()

# ---- 4. studio.local.json ----------------------------------------------------
$local = [ordered]@{
    obs = [ordered]@{
        url             = "ws://127.0.0.1:$Port"
        password        = $password
        scene           = $SceneName
        gameCapture     = $CaptureName
        recordDirectory = $RecordDirectory
        executable      = $obsExe
        arguments       = $obsArgs
    }
}
[IO.File]::WriteAllText($localConfigPath, ($local | ConvertTo-Json -Depth 5), $utf8NoBom)

Write-Host ""
Write-Host "OBS is ready."
Write-Host "  websocket       ws://127.0.0.1:$Port (password in studio.local.json)"
Write-Host "  scene           $SceneName -> $CaptureName"
Write-Host "  video           ${Width}x${Height} @ $Fps fps, NVENC H.264, hybrid MP4"
Write-Host "  recordings      $RecordDirectory"
Write-Host "  microphones     $micState"
Write-Host "  preview frame   $previewResult"
Write-Host "  chapter markers $hasChapters (CreateRecordChapter)"
Write-Host "  test recording  $testResult"
