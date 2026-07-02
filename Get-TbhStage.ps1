<#
.SYNOPSIS
    Reads the current stage TaskBarHero is on, live, from the running game's memory.

.DESCRIPTION
    Read-only. Attaches to the TaskBarHero process, resolves the IL2CPP object
    graph, and reports the current stage. Two data sources are combined:

      1. PlayerSaveData.commonSaveData  -> currentStageKey / currentStageWave / maxCompletedStage
      2. The StageInfoData table (120 stage definitions) -> Act / StageNo / Level / WaveAmount / difficulty

    The stage table is enumerated once and cached; the live currentStageKey is
    re-read every poll from a fixed address, so polling is cheap.

.PARAMETER Once
    Print one reading as JSON and exit (for a presence app to invoke on a timer).

.PARAMETER IntervalSeconds
    Poll interval in continuous mode. Default 3.

.PARAMETER JsonOut
    Optional path; each reading is written here as JSON (for another process to consume).

.EXAMPLE
    .\Get-TbhStage.ps1                 # continuous console monitor
    .\Get-TbhStage.ps1 -Once           # single JSON reading
    .\Get-TbhStage.ps1 -JsonOut state.json
#>
[CmdletBinding()]
param(
    [switch]$Once,
    [int]$IntervalSeconds = 3,
    [string]$JsonOut
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -Path (Join-Path $here 'TbhMemory.cs')

# StageInfoData field offsets (from Il2CppDumper, metadata v31). Object fields start at +0x10.
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

function Get-GameProcess {
    $p = Get-Process -Name 'TaskBarHero' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw 'TaskBarHero is not running.' }
    return $p
}

function Resolve-Targets($mem) {
    # 1. Locate the live CommonSaveData object via PlayerSaveData.
    $psdKlass = $mem.FindClass('PlayerSaveData', 'TaskbarHero')
    $csdKlass = $mem.FindClass('CommonSaveData', 'TaskbarHero')
    if ($psdKlass -eq 0 -or $csdKlass -eq 0) { throw 'Could not resolve save-data classes (game not fully loaded yet?).' }

    $csdAddr = 0
    foreach ($r in $mem.FindInstances($psdKlass, 4096)) {
        $c = $mem.ReadPtr($r + $OFF.PSD_common)
        if ($c -ne 0 -and $mem.ReadPtr($c) -eq $csdKlass) { $csdAddr = $c; break }
    }
    if ($csdAddr -eq 0) { throw 'Could not find CommonSaveData instance.' }

    # 2. Build stageKey -> details table from all StageInfoData instances (once).
    $sidKlass = $mem.FindClass('StageInfoData', 'TaskbarHero.Data')
    $table = @{}
    if ($sidKlass -ne 0) {
        foreach ($r in $mem.FindInstances($sidKlass, 4096)) {
            $key = $mem.ReadInt($r + $OFF.SID_StageKey)
            $act = $mem.ReadInt($r + $OFF.SID_Act)
            $no  = $mem.ReadInt($r + $OFF.SID_StageNo)
            $lvl = $mem.ReadInt($r + $OFF.SID_StageLevel)
            if ($key -gt 1000 -and $key -lt 99999 -and $act -ge 0 -and $act -le 50 -and $no -ge 0 -and $no -le 99 -and $lvl -gt 0 -and $lvl -lt 100000) {
                if (-not $table.ContainsKey($key)) {
                    $table[$key] = [pscustomobject]@{
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
    }
    return [pscustomobject]@{ CsdAddr = $csdAddr; Table = $table }
}

function Read-Stage($mem, $ctx) {
    $key      = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageKey)
    $wave     = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageWave)
    $maxStage = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_maxStage)
    $info     = $ctx.Table[$key]
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

$proc = Get-GameProcess
$mem  = New-Object Tbh.Mem($proc.Id)
try {
    Write-Host "Attached to TaskBarHero (PID $($proc.Id)). Resolving pointers..." -ForegroundColor Cyan
    $ctx = Resolve-Targets $mem
    Write-Host "Resolved. Stage table entries: $($ctx.Table.Count)." -ForegroundColor Cyan

    if ($Once) {
        $r = Read-Stage $mem $ctx
        if ($JsonOut) { $r | ConvertTo-Json -Compress | Set-Content -Path $JsonOut -Encoding utf8 }
        $r | ConvertTo-Json -Compress
        return
    }

    $last = $null
    while ($true) {
        if ($proc.HasExited) { Write-Host 'Game closed. Exiting.' -ForegroundColor Yellow; break }
        $r = Read-Stage $mem $ctx
        if ($JsonOut) { $r | ConvertTo-Json -Compress | Set-Content -Path $JsonOut -Encoding utf8 }
        if ($r.label -ne $last) {
            Write-Host ("[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $r.label) -ForegroundColor Green
            $last = $r.label
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    $mem.Dispose()
}
