<#
    Native click / key helper for the Cities: Skylines window.

    Why this exists: Unity renders its entire UI into a texture, so the Windows
    accessibility tree sees nothing useful - UIA automation cannot drive the game.
    Input must be injected at the OS level. This script handles the two hard parts:

      1. Windows refuses SetForegroundWindow from a background process. The standard
         bypass is to hold ALT while calling it.
      2. cua-driver screenshots are scaled. This maps screenshot pixel coordinates
         back to real screen coordinates using the window bounds.

    Usage:
        .\scripts\game-ui.ps1 -Click 783,705              # click at screenshot coords
        .\scripts\game-ui.ps1 -Click 783,705 -Double     # double click
        .\scripts\game-ui.ps1 -Type "Agentville"          # type text
        .\scripts\game-ui.ps1 -Key ctrl+a                 # hotkey
        .\scripts\game-ui.ps1 -Screenshot                 # save a screenshot
#>
param(
    [string]$ProcessName = "Cities",
    [string]$Click,          # "x,y" in screenshot pixel coordinates
    [switch]$Double,
    [string]$Type,
    [string]$Key,            # e.g. "ctrl+a", "enter", "escape"
    [string]$Screenshot,
    [int]$ShotW = 1568,      # width of the screenshot the coordinates refer to
    [int]$ShotH = 907,
    [int]$SettleMs = 600
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct GRect { public int Left; public int Top; public int Right; public int Bottom; }
public static class GameUi {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out GRect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@ -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (!$proc) { throw "No window found for process '$ProcessName'." }

$hwnd = $proc.MainWindowHandle

# --- bring the window to the foreground -------------------------------------
# Windows blocks SetForegroundWindow from a background process; holding ALT bypasses it.
[GameUi]::ShowWindow($hwnd, 9) | Out-Null       # SW_RESTORE
[GameUi]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)   # ALT down
[GameUi]::SetForegroundWindow($hwnd) | Out-Null
[GameUi]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)   # ALT up
[GameUi]::BringWindowToTop($hwnd) | Out-Null
Start-Sleep -Milliseconds 400

$fg = [GameUi]::GetForegroundWindow()
$fgPid = 0
[GameUi]::GetWindowThreadProcessId($fg, [ref]$fgPid) | Out-Null
if ($fgPid -ne $proc.Id) {
    Write-Warning "Could not focus the game window (foreground pid $fgPid, want $($proc.Id))."
}

function Convert-Screen([double]$sx, [double]$sy) {
    $r = New-Object GRect
    [GameUi]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    # Screenshot pixels -> window pixels -> screen pixels.
    # The capture is scaled to fit 1568px width (observed on a 2560x1440 display).
    $winW = $r.Right - $r.Left
    $winH = $r.Bottom - $r.Top
    $scaleX = $winW / [double]$ShotW
    $scaleY = $winH / [double]$ShotH
    return @([int]($r.Left + $sx * $scaleX), [int]($r.Top + $sy * $scaleY))
}

function Invoke-Click([double]$sx, [double]$sy, [bool]$dbl) {
    $p = Convert-Screen $sx $sy
    [GameUi]::SetCursorPos($p[0], $p[1]) | Out-Null
    Start-Sleep -Milliseconds 200
    $times = if ($dbl) { 2 } else { 1 }
    for ($i = 0; $i -lt $times; $i++) {
        [GameUi]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
        Start-Sleep -Milliseconds 60
        [GameUi]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
        if ($dbl) { Start-Sleep -Milliseconds 60 }
    }
    Write-Host "clicked screenshot($sx,$sy) -> screen($($p[0]),$($p[1]))"
}

if ($Click) {
    $parts = $Click -split ','
    Invoke-Click ([double]$parts[0]) ([double]$parts[1]) $Double.IsPresent
}

if ($Type) {
    foreach ($ch in $Type.ToCharArray()) {
        $vk = [System.Windows.Forms.SendKeys]::SendWait($ch) 2>$null
        if (-not $?) {
            # Fall back to a raw scan code for simple ASCII.
            $code = [int][char]$ch
            if ($code -ge 97 -and $code -le 122) { $scan = $code - 96 } else { $scan = $code - 48 }
            [GameUi]::keybd_event([byte]$code, [byte]$scan, 0, [UIntPtr]::Zero)
            [GameUi]::keybd_event([byte]$code, [byte]$scan, 2, [UIntPtr]::Zero)
        }
        Start-Sleep -Milliseconds 30
    }
    Write-Host "typed '$Type'"
}

if ($Key) {
    $map = @{ "enter" = 13; "escape" = 27; "tab" = 9; "backspace" = 8; "delete" = 46
              "up" = 38; "down" = 40; "left" = 37; "right" = 39 }
    $lower = $Key.ToLowerInvariant()
    if ($lower.StartsWith("ctrl+")) {
        $rest = $lower.Substring(5)
        if ($map.ContainsKey($rest)) {
            [GameUi]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
            [GameUi]::keybd_event([byte]$map[$rest], 0, 0, [UIntPtr]::Zero)
            [GameUi]::keybd_event([byte]$map[$rest], 0, 2, [UIntPtr]::Zero)
            [GameUi]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
        } else {
            [GameUi]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
            [GameUi]::keybd_event([byte][char]$rest[0], 0, 0, [UIntPtr]::Zero)
            [GameUi]::keybd_event([byte][char]$rest[0], 0, 2, [UIntPtr]::Zero)
            [GameUi]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
        }
        Write-Host "hotkey '$Key'"
    }
    elseif ($map.ContainsKey($lower)) {
        [GameUi]::keybd_event([byte]$map[$lower], 0, 0, [UIntPtr]::Zero)
        [GameUi]::keybd_event([byte]$map[$lower], 0, 2, [UIntPtr]::Zero)
        Write-Host "key '$Key'"
    }
}

if ($Screenshot) {
    $r = New-Object GRect
    [GameUi]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($Screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose(); $bmp.Dispose()
    Write-Host "screenshot -> $Screenshot"
}

Start-Sleep -Milliseconds $SettleMs
