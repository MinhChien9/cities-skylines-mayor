<#
.SYNOPSIS
    Dot-source helper: starts Cities: Skylines with the Epic login, so owned DLC is loaded.

.DESCRIPTION
    On the Epic install the game loads owned expansions only when it is started with
    Epic's login arguments (-AUTH_PASSWORD=<one-time exchange code>, -epicapp=..., ...).
    Started from Epic, the Epic Games Launcher passes them to the Paradox launcher (Epic
    runs dowser.exe, which starts "Paradox Launcher.exe" with the same arguments), which
    passes them on to Cities.exe when the player clicks Play. A bare `Cities.exe` has none
    of them and comes up without any DLC (#84).

    Start-CitiesSkylines -Launch Epic (the default) does what the player does, minus the
    click:

      1. opens the game through the Epic URI (com.epicgames.launcher://apps/...)
      2. waits for the Paradox launcher to receive the Epic arguments
      3. closes the Paradox launcher and starts Cities.exe with those arguments plus the
         caller's own (for example --continuelastsave)

    The exchange code is single use and expires within minutes; the Paradox launcher only
    hands it on, so the game redeems it. -Launch Direct starts Cities.exe without the
    Epic login (no DLC), as before.

.EXAMPLE
    . (Join-Path $PSScriptRoot "game-launch.ps1")
    Start-CitiesSkylines -GamePath "D:\Epic Games\CitiesSkylines" -GameArgs @("--continuelastsave")
#>

# Cities: Skylines on the Epic store: <namespace>:<catalog item>:<app name>. The app name is
# the -epicapp argument the game receives. Override with $env:CS1_EPIC_APP.
$script:DefaultEpicApp = "6009be9994c2409099588cde6f3a5ed0:ee3c9b2831cf4e8c9a6dc407bcb2890a:bcbc03d8812a44c18f41cf7d5f849265"

function Get-ParadoxLauncherProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'Paradox Launcher.exe'" -ErrorAction SilentlyContinue
}

function Stop-ParadoxLauncher {
    $procs = @(Get-ParadoxLauncherProcesses)
    if ($procs.Count -eq 0) { return }
    foreach ($p in $procs) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @(Get-ParadoxLauncherProcesses).Count -gt 0) {
        Start-Sleep -Milliseconds 500
    }
}

# Picks the Epic login arguments out of a command line. Arguments are split at a space that
# starts a new "-option", so a value with spaces (a display name) stays whole.
function Get-EpicLoginArguments([string]$CommandLine) {
    $tokens = $CommandLine -split '\s+(?=-)'
    $keep = @()
    foreach ($t in $tokens) {
        $t = $t.Trim() -replace '^"(.*)"$', '$1'
        $t = $t -replace '^(-[^=]+=)"(.*)"$', '$1$2'   # -epicusername="name" -> -epicusername=name
        if ($t -match '^-(AUTH_[A-Za-z_]+=|epic[A-Za-z]+=|EpicPortal$)') { $keep += $t }
    }
    return $keep
}

function ConvertTo-ArgumentString([string[]]$Arguments) {
    ($Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' '
}

function Start-CitiesSkylines {
    param(
        [Parameter(Mandatory)][string]$GamePath,
        [string[]]$GameArgs = @(),
        [ValidateSet("Epic", "Direct")][string]$Launch = "Epic",
        [string]$EpicApp = "",
        [int]$LauncherTimeoutSeconds = 180
    )

    $exe = Join-Path $GamePath "Cities.exe"
    if (!(Test-Path $exe)) { throw "Cities.exe was not found at $exe. Pass -GamePath." }

    if ($Launch -eq "Direct") {
        $argString = ConvertTo-ArgumentString $GameArgs
        if ($argString) {
            Start-Process -FilePath $exe -ArgumentList $argString -WorkingDirectory $GamePath
        } else {
            Start-Process -FilePath $exe -WorkingDirectory $GamePath
        }
        Write-Host "Launched Cities.exe directly (no Epic login: expansions will not load)."
        return
    }

    if ([string]::IsNullOrWhiteSpace($EpicApp)) {
        $EpicApp = if ($env:CS1_EPIC_APP) { $env:CS1_EPIC_APP } else { $script:DefaultEpicApp }
    }

    # A Paradox launcher left open from an earlier start holds an old, spent exchange code.
    Stop-ParadoxLauncher

    $uri = "com.epicgames.launcher://apps/" + [uri]::EscapeDataString($EpicApp) + "?action=launch&silent=true"
    Start-Process $uri
    Write-Host "Asked the Epic Games Launcher to start the game; waiting for the Paradox launcher..."

    $loginArgs = $null
    $deadline = (Get-Date).AddSeconds($LauncherTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        $withLogin = Get-ParadoxLauncherProcesses |
            Where-Object { $_.CommandLine -match '-AUTH_PASSWORD=' } |
            Select-Object -First 1
        if ($withLogin) {
            $loginArgs = Get-EpicLoginArguments $withLogin.CommandLine
            break
        }
        if (Get-Process -Name "Cities" -ErrorAction SilentlyContinue) {
            # Epic or the player started the game some other way; it has the login already.
            Write-Host "Cities.exe started without the Paradox launcher; leaving it as it is." -ForegroundColor Yellow
            return
        }
    }
    if (!$loginArgs -or !($loginArgs -match '^-AUTH_PASSWORD=')) {
        throw ("The Paradox launcher did not start with the Epic login within $LauncherTimeoutSeconds s. " +
            "Is the Epic Games Launcher signed in? Start the game from Epic once by hand, or pass -Launch Direct (no DLC).")
    }

    # Close the Paradox launcher before it can start a second copy, then start the game the
    # way it would have (same arguments, same working directory), plus ours.
    Stop-ParadoxLauncher
    $argString = ConvertTo-ArgumentString (@($loginArgs) + @($GameArgs))
    Start-Process -FilePath $exe -ArgumentList $argString -WorkingDirectory $GamePath
    $shown = (@($GameArgs) + @("<Epic login>")) -join ' '
    Write-Host "Launched Cities.exe with the Epic login ($shown)."
}
