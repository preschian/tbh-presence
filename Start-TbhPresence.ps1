<#
.SYNOPSIS
    Discord Rich Presence for TaskBarHero: shows your current stage and party.

.DESCRIPTION
    Polls the game via Get-TbhStage.ps1 (read-only memory reading) and pushes
    the result to Discord over its local IPC named pipe (discord-ipc-N).
    No dependencies - pure PowerShell/.NET.

    Shows e.g.:
        Playing TaskBarHero
        Act 3 - Stage 3 (HELL, Lv 72)
        Ranger Lv80, Sorcerer Lv23, Priest Lv35

    Survives game restarts and Discord restarts; clears the presence while the
    game is closed. Ctrl+C to quit.

.PARAMETER IntervalSeconds
    How often to poll the game and (on change) update Discord. Default 15
    (Discord rate-limits presence updates; don't go much lower).

.PARAMETER ClientId
    Discord application id. Default: the TaskbarHero presence app.
#>
[CmdletBinding()]
param(
    [int]$IntervalSeconds = 15,
    [string]$ClientId = '1522386796078432429'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Reader = Join-Path $here 'Get-TbhStage.ps1'

# ---------- Discord IPC ----------

function Send-Frame($pipe, [int]$op, [string]$json) {
    $payload = [System.Text.Encoding]::UTF8.GetBytes($json)
    $buf = New-Object byte[] (8 + $payload.Length)
    [BitConverter]::GetBytes($op).CopyTo($buf, 0)
    [BitConverter]::GetBytes($payload.Length).CopyTo($buf, 4)
    $payload.CopyTo($buf, 8)
    $pipe.Write($buf, 0, $buf.Length)
    $pipe.Flush()
}

function Read-Frame($pipe) {
    $hdr = New-Object byte[] 8
    $got = 0
    while ($got -lt 8) {
        $n = $pipe.Read($hdr, $got, 8 - $got)
        if ($n -le 0) { throw 'Discord pipe closed.' }
        $got += $n
    }
    $op = [BitConverter]::ToInt32($hdr, 0)
    $len = [BitConverter]::ToInt32($hdr, 4)
    $payload = New-Object byte[] $len
    $got = 0
    while ($got -lt $len) {
        $n = $pipe.Read($payload, $got, $len - $got)
        if ($n -le 0) { throw 'Discord pipe closed.' }
        $got += $n
    }
    return @{ Op = $op; Json = [System.Text.Encoding]::UTF8.GetString($payload) }
}

function Connect-Discord {
    for ($i = 0; $i -lt 10; $i++) {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "discord-ipc-$i", [System.IO.Pipes.PipeDirection]::InOut)
        try { $pipe.Connect(500) } catch { $pipe.Dispose(); continue }
        try {
            Send-Frame $pipe 0 (@{ v = 1; client_id = $ClientId } | ConvertTo-Json -Compress)
            $resp = Read-Frame $pipe   # DISPATCH/READY
            if ($resp.Json -match '"evt"\s*:\s*"READY"') {
                Write-Host "Connected to Discord (discord-ipc-$i)." -ForegroundColor Cyan
                return $pipe
            }
            Write-Host "Unexpected handshake reply: $($resp.Json)" -ForegroundColor Yellow
            $pipe.Dispose()
        } catch { $pipe.Dispose() }
    }
    return $null
}

function Set-Activity($pipe, $activity) {
    # $activity = $null clears the presence
    $args = @{ pid = $global:PID }
    if ($activity) { $args.activity = $activity }
    $cmd = @{ cmd = 'SET_ACTIVITY'; args = $args; nonce = [guid]::NewGuid().ToString() }
    Send-Frame $pipe 1 ($cmd | ConvertTo-Json -Compress -Depth 6)
    $resp = Read-Frame $pipe
    if ($resp.Json -match '"evt"\s*:\s*"ERROR"') { Write-Host "Discord error: $($resp.Json)" -ForegroundColor Red }
}

# ---------- game state ----------

function Get-GameState {
    # Returns the parsed JSON state, or $null when the game isn't running/ready.
    $proc = Get-Process -Name 'TaskBarHero' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return $null }
    try {
        $out = & $Reader -Once 2>$null
        $json = $out | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
        if (-not $json) { return $null }
        $state = $json | ConvertFrom-Json
        $state | Add-Member -NotePropertyName gameStartTime -NotePropertyValue $proc.StartTime -Force
        return $state
    } catch { return $null }
}

function Build-Activity($state) {
    $details = if ($state.act -ne $null) {
        "Act $($state.act) - Stage $($state.stageNo)  ($($state.difficulty), Lv $($state.level))"
    } else { "Stage $($state.stageKey)" }
    $partyState = if ($state.heroes -and $state.heroes.Count -gt 0) {
        ($state.heroes | ForEach-Object { if ($_.level) { "$($_.name) Lv$($_.level)" } else { $_.name } }) -join ', '
    } else { $null }
    $start = [long]([DateTimeOffset](Get-Date $state.gameStartTime)).ToUnixTimeSeconds()
    $activity = @{
        details    = $details
        timestamps = @{ start = $start }
    }
    if ($partyState) { $activity.state = $partyState }
    return $activity
}

# ---------- main loop ----------

Write-Host "TaskBarHero Rich Presence - client id $ClientId. Ctrl+C to quit." -ForegroundColor Cyan
$pipe = $null
$lastSent = $null   # signature of last activity ('' = cleared)

try {
    while ($true) {
        # ensure Discord connection
        if (-not $pipe) {
            $pipe = Connect-Discord
            if (-not $pipe) {
                Write-Host 'Discord not running - retrying in 30s...' -ForegroundColor Yellow
                Start-Sleep -Seconds 30
                continue
            }
            $lastSent = $null   # force re-send after (re)connect
        }

        $state = Get-GameState
        try {
            if ($state) {
                $activity = Build-Activity $state
                $sig = "$($activity.details)|$($activity.state)"
                if ($sig -ne $lastSent) {
                    Set-Activity $pipe $activity
                    Write-Host ("[{0}] presence: {1} - {2}" -f (Get-Date -Format HH:mm:ss), $activity.details, $activity.state) -ForegroundColor Green
                    $lastSent = $sig
                }
            } elseif ($lastSent -ne '') {
                Set-Activity $pipe $null
                Write-Host ("[{0}] game closed - presence cleared" -f (Get-Date -Format HH:mm:ss)) -ForegroundColor Yellow
                $lastSent = ''
            }
        } catch {
            Write-Host "Discord connection lost ($($_.Exception.Message)) - reconnecting..." -ForegroundColor Yellow
            try { $pipe.Dispose() } catch {}
            $pipe = $null
            continue
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    if ($pipe) {
        try { Set-Activity $pipe $null } catch {}
        try { $pipe.Dispose() } catch {}
    }
}
