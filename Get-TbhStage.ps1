<#
.SYNOPSIS
    Reads the current stage TaskBarHero is on, live, from the running game's memory.

.DESCRIPTION
    Read-only. Attaches to the TaskBarHero process, resolves the IL2CPP object
    graph, and reports the current stage. Two data sources are combined:

      1. PlayerSaveData.commonSaveData  -> currentStageKey / currentStageWave / maxCompletedStage
      2. The StageInfoData table (120 stage definitions) -> Act / StageNo / Level / WaveAmount / difficulty

    Resolved addresses are cached in cache.json next to this script:
      - the stage table is reused across game restarts (invalidated when the
        game binary changes),
      - the live object address is reused while the same game process is alive
        (validated by PID + process start time + class pointer), making
        subsequent script starts instant.

    In continuous mode the script survives game restarts: when the game exits
    it waits for a new process and re-resolves automatically.

.PARAMETER Once
    Print one reading as JSON and exit (for a presence app to invoke on a timer).

.PARAMETER IntervalSeconds
    Poll interval in continuous mode. Default 3.

.PARAMETER JsonOut
    Optional path; each reading is written here as JSON (for another process to consume).

.PARAMETER NoCache
    Ignore and rebuild the address cache.

.EXAMPLE
    .\Get-TbhStage.ps1                 # continuous console monitor
    .\Get-TbhStage.ps1 -Once           # single JSON reading
    .\Get-TbhStage.ps1 -JsonOut state.json
#>
[CmdletBinding()]
param(
    [switch]$Once,
    [int]$IntervalSeconds = 3,
    [string]$JsonOut,
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -Path (Join-Path $here 'TbhMemory.cs')
$CachePath = Join-Path $here 'cache.json'
$GameExe = 'TaskBarHero'

# Field offsets (from Il2CppDumper, metadata v31). Object fields start at +0x10.
$OFF = @{
    # PlayerSaveData
    PSD_common      = 0x10
    # CommonSaveData
    CSD_maxStage    = 0x54
    CSD_stageKey    = 0x58
    CSD_stageWave   = 0x5C
    CSD_playTime    = 0x20
    # StageInfoData
    SID_StageKey    = 0x30
    SID_StageNameKey= 0x38
    SID_StageType   = 0x40   # EStageType: 0 NORMAL, 1 ACTBOSS
    SID_Difficulty  = 0x44   # ESTAGEDIFFICULTY: 0 NORMAL,1 NIGHTMARE,2 HELL,3 TORMENT
    SID_Act         = 0x48
    SID_StageNo     = 0x4C
    SID_StageLevel  = 0x50
    SID_WaveAmount  = 0x54
}
$DIFF = @('NORMAL','NIGHTMARE','HELL','TORMENT')
$STYPE = @('NORMAL','ACTBOSS')

function Get-GameStamp($proc) {
    # Identifies the game build; invalidates the cached stage table on updates.
    $exe = $proc.MainModule.FileName
    $fi = Get-Item $exe
    return "$exe|$($fi.LastWriteTimeUtc.Ticks)|$($fi.Length)"
}

function Load-Cache {
    if ($NoCache -or -not (Test-Path $CachePath)) { return $null }
    try { return Get-Content $CachePath -Raw | ConvertFrom-Json } catch { return $null }
}

function Save-Cache($cache) {
    try { $cache | ConvertTo-Json -Depth 6 -Compress | Set-Content -Path $CachePath -Encoding utf8 } catch {}
}

function Build-StageTable($mem) {
    $sidKlass = $mem.FindClass('StageInfoData', 'TaskbarHero.Data')
    $table = @{}
    if ($sidKlass -eq 0) { return $table }
    foreach ($r in $mem.FindInstances($sidKlass, 4096)) {
        $key = $mem.ReadInt($r + $OFF.SID_StageKey)
        $act = $mem.ReadInt($r + $OFF.SID_Act)
        $no  = $mem.ReadInt($r + $OFF.SID_StageNo)
        $lvl = $mem.ReadInt($r + $OFF.SID_StageLevel)
        if ($key -gt 1000 -and $key -lt 99999 -and $act -ge 0 -and $act -le 50 -and $no -ge 0 -and $no -le 99 -and $lvl -gt 0 -and $lvl -lt 100000) {
            if (-not $table.ContainsKey([string]$key)) {
                $table[[string]$key] = [pscustomobject]@{
                    Act        = $act
                    StageNo    = $no
                    Level      = $lvl
                    WaveAmount = $mem.ReadInt($r + $OFF.SID_WaveAmount)
                    Difficulty = $DIFF[[Math]::Max(0,[Math]::Min(3,$mem.ReadInt($r + $OFF.SID_Difficulty)))]
                    StageType  = $STYPE[[Math]::Max(0,[Math]::Min(1,$mem.ReadInt($r + $OFF.SID_StageType)))]
                    NameKey    = $mem.ReadIl2CppString($mem.ReadPtr($r + $OFF.SID_StageNameKey), 64)
                }
            }
        }
    }
    return $table
}

function Find-LiveSaveData($mem) {
    # Returns @{ CsdAddr; CsdKlass } for the live CommonSaveData object.
    $psdKlass = $mem.FindClass('PlayerSaveData', 'TaskbarHero')
    $csdKlass = $mem.FindClass('CommonSaveData', 'TaskbarHero')
    if ($psdKlass -eq 0 -or $csdKlass -eq 0) { throw 'Could not resolve save-data classes (game not fully loaded yet?).' }
    foreach ($r in $mem.FindInstances($psdKlass, 4096)) {
        $c = $mem.ReadPtr($r + $OFF.PSD_common)
        if ($c -ne 0 -and $mem.ReadPtr($c) -eq $csdKlass) {
            return @{ CsdAddr = $c; CsdKlass = $csdKlass }
        }
    }
    throw 'Could not find CommonSaveData instance.'
}

function Resolve-Targets($mem, $proc) {
    $stamp = Get-GameStamp $proc
    $bootId = "$($proc.Id)|$($proc.StartTime.ToFileTimeUtc())"
    $cache = Load-Cache

    # Fast path: same game process still alive -> reuse addresses after validating.
    if ($cache -and $cache.bootId -eq $bootId -and $cache.gameStamp -eq $stamp) {
        $csd = [long]$cache.csdAddr
        if ($mem.ReadPtr($csd) -eq [long]$cache.csdKlass) {
            $key = $mem.ReadInt($csd + $OFF.CSD_stageKey)
            if ($key -ge 0 -and $key -lt 1000000) {
                Write-Host 'Address cache hit - skipping memory scan.' -ForegroundColor DarkGray
                $table = @{}
                foreach ($p in $cache.table.PSObject.Properties) { $table[$p.Name] = $p.Value }
                return [pscustomobject]@{ CsdAddr = $csd; Table = $table }
            }
        }
        Write-Host 'Cached address failed validation - rescanning.' -ForegroundColor DarkGray
    }

    # Stage table: reusable across restarts of the same game build.
    $table = $null
    if ($cache -and $cache.gameStamp -eq $stamp -and $cache.table) {
        $table = @{}
        foreach ($p in $cache.table.PSObject.Properties) { $table[$p.Name] = $p.Value }
        Write-Host "Stage table from cache ($($table.Count) entries) - scanning live object only..." -ForegroundColor DarkGray
    }

    $live = Find-LiveSaveData $mem
    if ($null -eq $table -or $table.Count -eq 0) {
        Write-Host 'Building stage table (one-time full scan)...' -ForegroundColor DarkGray
        $table = Build-StageTable $mem
    }

    Save-Cache ([pscustomobject]@{
        gameStamp = $stamp
        bootId    = $bootId
        csdAddr   = $live.CsdAddr
        csdKlass  = $live.CsdKlass
        table     = $table
    })
    return [pscustomobject]@{ CsdAddr = $live.CsdAddr; Table = $table }
}

function Read-Stage($mem, $ctx) {
    $key      = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageKey)
    $wave     = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageWave)
    $maxStage = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_maxStage)
    $info     = $ctx.Table[[string]$key]
    $label = if ($info) {
        "Act $($info.Act) - Stage $($info.StageNo)  (Lv $($info.Level), $($info.Difficulty), $($info.WaveAmount) waves)"
    } else { "StageKey $key" }
    return [pscustomobject]@{
        stageKey          = $key
        savedWave         = $wave
        maxCompletedStage = $maxStage
        act               = if ($info) { $info.Act } else { $null }
        stageNo           = if ($info) { $info.StageNo } else { $null }
        level             = if ($info) { $info.Level } else { $null }
        waveAmount        = if ($info) { $info.WaveAmount } else { $null }
        difficulty        = if ($info) { $info.Difficulty } else { $null }
        stageType         = if ($info) { $info.StageType } else { $null }
        nameKey           = if ($info) { $info.NameKey } else { $null }
        label             = $label
        timestamp         = (Get-Date).ToString('s')
    }
}

function Wait-ForGame {
    $warned = $false
    while ($true) {
        $p = Get-Process -Name $GameExe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { return $p }
        if (-not $warned) { Write-Host 'Waiting for TaskBarHero to start...' -ForegroundColor Yellow; $warned = $true }
        Start-Sleep -Seconds 5
    }
}

# ---- main ----
if ($Once) {
    $proc = Get-Process -Name $GameExe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { throw 'TaskBarHero is not running.' }
    $mem = New-Object Tbh.Mem($proc.Id)
    try {
        $ctx = Resolve-Targets $mem $proc
        $r = Read-Stage $mem $ctx
        if ($JsonOut) { $r | ConvertTo-Json -Compress | Set-Content -Path $JsonOut -Encoding utf8 }
        $r | ConvertTo-Json -Compress
    } finally { $mem.Dispose() }
    return
}

while ($true) {
    $proc = Wait-ForGame
    Write-Host "Attached to TaskBarHero (PID $($proc.Id)). Resolving pointers..." -ForegroundColor Cyan
    $mem = New-Object Tbh.Mem($proc.Id)
    try {
        # Right after launch the save data may not exist yet; retry until it does.
        $ctx = $null
        while (-not $ctx) {
            try { $ctx = Resolve-Targets $mem $proc }
            catch {
                if ($proc.HasExited) { break }
                Write-Host "Not ready yet ($($_.Exception.Message)) - retrying in 10s..." -ForegroundColor Yellow
                Start-Sleep -Seconds 10
            }
        }
        if (-not $ctx) { continue }
        Write-Host "Resolved. Stage table entries: $($ctx.Table.Count)." -ForegroundColor Cyan

        $last = $null
        while (-not $proc.HasExited) {
            $r = Read-Stage $mem $ctx
            if ($JsonOut) { $r | ConvertTo-Json -Compress | Set-Content -Path $JsonOut -Encoding utf8 }
            if ($r.label -ne $last) {
                Write-Host ("[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $r.label) -ForegroundColor Green
                $last = $r.label
            }
            Start-Sleep -Seconds $IntervalSeconds
        }
        Write-Host 'Game closed - waiting for restart (Ctrl+C to quit)...' -ForegroundColor Yellow
    }
    finally { $mem.Dispose() }
}
