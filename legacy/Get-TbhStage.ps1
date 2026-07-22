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
    CSD_petKey      = 0x40
    CSD_heroKeys    = 0x48   # int[] of deployed hero keys
    # HeroInfoData
    HID_HeroKey     = 0x30
    HID_HeroNameKey = 0x38
    HID_ClassType   = 0x48   # EEquipClassType
    # PlayerSaveData
    PSD_heroSaves   = 0x60   # List<HeroSaveData>
    # HeroSaveData
    HSD_heroKey     = 0x10
    HSD_level       = 0x14
    HSD_unlocked    = 0x18
    HSD_exp         = 0x20
    # ux.uq static fields (live stage system)
    UU_currentCache = 0x88   # ux.StageCache bfan: the stage currently loaded
    # ux.StageCache
    SC_infoData     = 0x10   # StageInfoData
    # Il2CppClass
    KLASS_staticFields = 0xB8
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
# EEquipClassType: each hero maps 1:1 to a class, which doubles as its name
$HCLASS = @('All','Knight','Ranger','Sorcerer','Priest','Hunter','Slayer')
$CACHE_VERSION = 7

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

function Build-HeroTable($mem) {
    # heroKey -> class/display name, from HeroInfoData instances
    $hidKlass = $mem.FindClass('HeroInfoData', 'TaskbarHero.Data')
    $table = @{}
    if ($hidKlass -eq 0) { return $table }
    foreach ($r in $mem.FindInstances($hidKlass, 4096)) {
        $key = $mem.ReadInt($r + $OFF.HID_HeroKey)
        if ($key -le 0 -or $key -gt 99999) { continue }
        # real rows have a HeroNameKey string; false positives don't
        $nameKey = $mem.ReadIl2CppString($mem.ReadPtr($r + $OFF.HID_HeroNameKey), 64)
        if (-not $nameKey) { continue }
        $ct = $mem.ReadInt($r + $OFF.HID_ClassType)
        $name = if ($ct -ge 1 -and $ct -lt $HCLASS.Count) { $HCLASS[$ct] } else { $nameKey }
        if (-not $table.ContainsKey([string]$key)) { $table[[string]$key] = $name }
    }
    return $table
}

function Find-LiveStageStatics($mem) {
    # Locates the static-field block of ux.uq (the live stage system) and the
    # StageCache class pointer. Self-validating: the block is only accepted if
    # its +0x88 slot points at a StageCache instance.
    # Returns @{ Statics; ScKlass } or $null (non-fatal; save data is the fallback).
    # NOTE: 'uq' is an obfuscated class name (uu -> up @1.00.27 -> uq @1.01.01);
    # try current and recent names so a minor rename still resolves.
    $scKlass = $mem.FindClass('StageCache', $null)
    if ($scKlass -eq 0) { return $null }
    $namePats = @(
        [byte[]](0x00, 0x75, 0x71, 0x00),  # "\0uq\0" 1.01.01
        [byte[]](0x00, 0x75, 0x70, 0x00),  # "\0up\0" 1.00.27
        [byte[]](0x00, 0x75, 0x75, 0x00)   # "\0uu\0" older
    )
    foreach ($pat in $namePats) {
        $strHits = $mem.FindBytes($pat, 256)
        if ($strHits.Count -eq 0) { continue }
        $targets = New-Object 'System.Collections.Generic.HashSet[long]'
        foreach ($s in $strHits) { [void]$targets.Add($s + 1) }
        foreach ($r in $mem.FindQwordRefs($targets, 512)) {
            $klass = $r - 0x10
            $statics = $mem.ReadPtr($klass + $OFF.KLASS_staticFields)
            if ($statics -eq 0) { continue }
            $obj = $mem.ReadPtr($statics + $OFF.UU_currentCache)
            if ($obj -ne 0 -and $mem.ReadPtr($obj) -eq $scKlass) {
                return @{ Statics = $statics; ScKlass = $scKlass }
            }
        }
    }
    return $null
}

function Find-LiveSaveData($mem) {
    # Returns @{ PsdAddr; CsdAddr; CsdKlass } for the live save-data objects.
    $psdKlass = $mem.FindClass('PlayerSaveData', 'TaskbarHero')
    $csdKlass = $mem.FindClass('CommonSaveData', 'TaskbarHero')
    if ($psdKlass -eq 0 -or $csdKlass -eq 0) { throw 'Could not resolve save-data classes (game not fully loaded yet?).' }
    foreach ($r in $mem.FindInstances($psdKlass, 4096)) {
        $c = $mem.ReadPtr($r + $OFF.PSD_common)
        if ($c -ne 0 -and $mem.ReadPtr($c) -eq $csdKlass) {
            return @{ PsdAddr = $r; CsdAddr = $c; CsdKlass = $csdKlass }
        }
    }
    throw 'Could not find CommonSaveData instance.'
}

function Resolve-Targets($mem, $proc) {
    $stamp = Get-GameStamp $proc
    $bootId = "$($proc.Id)|$($proc.StartTime.ToFileTimeUtc())"
    $cache = Load-Cache

    if ($cache -and $cache.version -ne $CACHE_VERSION) { $cache = $null }

    function ConvertTo-Hashtable($psobj) {
        $h = @{}
        if ($psobj) { foreach ($p in $psobj.PSObject.Properties) { $h[$p.Name] = $p.Value } }
        return $h
    }

    # Fast path: same game process still alive -> reuse addresses after validating.
    if ($cache -and $cache.bootId -eq $bootId -and $cache.gameStamp -eq $stamp) {
        $csd = [long]$cache.csdAddr
        $psd = [long]$cache.psdAddr
        if ($mem.ReadPtr($csd) -eq [long]$cache.csdKlass -and $mem.ReadPtr($psd + $OFF.PSD_common) -eq $csd) {
            $key = $mem.ReadInt($csd + $OFF.CSD_stageKey)
            if ($key -ge 0 -and $key -lt 1000000) {
                Write-Host 'Address cache hit - skipping memory scan.' -ForegroundColor DarkGray
                $uuStatics = [long]$cache.uuStatics
                if ($uuStatics -ne 0) {
                    # validate the live-stage static block still points at a StageCache
                    $obj = $mem.ReadPtr($uuStatics + $OFF.UU_currentCache)
                    if ($obj -eq 0 -or $mem.ReadPtr($obj) -ne [long]$cache.scKlass) { $uuStatics = 0 }
                }
                return [pscustomobject]@{
                    PsdAddr   = $psd
                    CsdAddr   = $csd
                    UuStatics = $uuStatics
                    Table     = ConvertTo-Hashtable $cache.table
                    Heroes    = ConvertTo-Hashtable $cache.heroTable
                }
            }
        }
        Write-Host 'Cached address failed validation - rescanning.' -ForegroundColor DarkGray
    }

    # Static tables: reusable across restarts of the same game build.
    $table = $null; $heroTable = $null
    if ($cache -and $cache.gameStamp -eq $stamp -and $cache.table) {
        $table = ConvertTo-Hashtable $cache.table
        $heroTable = ConvertTo-Hashtable $cache.heroTable
        Write-Host "Static tables from cache - scanning live object only..." -ForegroundColor DarkGray
    }

    $live = Find-LiveSaveData $mem
    if ($null -eq $table -or $table.Count -eq 0) {
        Write-Host 'Building stage table (one-time full scan)...' -ForegroundColor DarkGray
        $table = Build-StageTable $mem
    }
    if ($null -eq $heroTable -or $heroTable.Count -eq 0) {
        Write-Host 'Building hero table...' -ForegroundColor DarkGray
        $heroTable = Build-HeroTable $mem
    }
    Write-Host 'Locating live stage statics...' -ForegroundColor DarkGray
    $uu = Find-LiveStageStatics $mem
    if (-not $uu) { Write-Host 'Live stage statics not found - falling back to save data for stage.' -ForegroundColor Yellow }

    Save-Cache ([pscustomobject]@{
        version   = $CACHE_VERSION
        gameStamp = $stamp
        bootId    = $bootId
        psdAddr   = $live.PsdAddr
        csdAddr   = $live.CsdAddr
        csdKlass  = $live.CsdKlass
        uuStatics = if ($uu) { $uu.Statics } else { 0 }
        scKlass   = if ($uu) { $uu.ScKlass } else { 0 }
        table     = $table
        heroTable = $heroTable
    })
    return [pscustomobject]@{
        PsdAddr   = $live.PsdAddr
        CsdAddr   = $live.CsdAddr
        UuStatics = if ($uu) { $uu.Statics } else { 0 }
        Table     = $table
        Heroes    = $heroTable
    }
}

function Read-Stage($mem, $ctx) {
    # stage identity: prefer the live loaded stage (ux.uq.bfan -> StageInfoData),
    # which flips the moment a new stage loads; save data lags until autosave.
    $key = 0
    $source = 'save'
    if ($ctx.UuStatics -ne 0) {
        $sc = $mem.ReadPtr($ctx.UuStatics + $OFF.UU_currentCache)
        if ($sc -ne 0) {
            $sid = $mem.ReadPtr($sc + $OFF.SC_infoData)
            if ($sid -ne 0) {
                $k = $mem.ReadInt($sid + $OFF.SID_StageKey)
                if ($k -gt 0 -and $k -lt 1000000) { $key = $k; $source = 'live' }
            }
        }
    }
    if ($key -eq 0) { $key = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageKey) }
    $wave     = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_stageWave)
    $maxStage = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_maxStage)
    $info     = $ctx.Table[[string]$key]

    # hero levels: List<HeroSaveData> re-read every poll (small: one entry per hero)
    $levels = @{}
    $hlist = $mem.ReadPtr($ctx.PsdAddr + $OFF.PSD_heroSaves)
    if ($hlist -ne 0) {
        $items = $mem.ReadPtr($hlist + 0x10)
        $n = $mem.ReadInt($hlist + 0x18)
        if ($items -ne 0 -and $n -gt 0 -and $n -le 64) {
            for ($i = 0; $i -lt $n; $i++) {
                $h = $mem.ReadPtr($items + 0x20 + 8 * $i)
                if ($h -eq 0) { continue }
                $levels[$mem.ReadInt($h + $OFF.HSD_heroKey)] = $mem.ReadInt($h + $OFF.HSD_level)
            }
        }
    }

    # deployed heroes: int[] pointer re-read every poll (array is replaced on re-arrange)
    $heroes = @()
    $arr = $mem.ReadPtr($ctx.CsdAddr + $OFF.CSD_heroKeys)
    if ($arr -ne 0) {
        $len = $mem.ReadInt($arr + 0x18)
        if ($len -gt 0 -and $len -le 16) {
            for ($i = 0; $i -lt $len; $i++) {
                $hk = $mem.ReadInt($arr + 0x20 + 4 * $i)
                if ($hk -le 0) { continue }
                $hn = $ctx.Heroes[[string]$hk]
                $heroes += [pscustomobject]@{
                    key   = $hk
                    name  = if ($hn) { $hn } else { "Hero_$hk" }
                    level = if ($levels.ContainsKey($hk)) { $levels[$hk] } else { $null }
                }
            }
        }
    }
    $petKey = $mem.ReadInt($ctx.CsdAddr + $OFF.CSD_petKey)

    $label = if ($info) {
        "Act $($info.Act) - Stage $($info.StageNo)  (Lv $($info.Level), $($info.Difficulty), $($info.WaveAmount) waves)"
    } else { "StageKey $key" }
    if ($heroes.Count -gt 0) {
        $label += "  |  " + (($heroes | ForEach-Object { if ($_.level) { "$($_.name) Lv$($_.level)" } else { $_.name } }) -join ', ')
    }
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
        heroes            = $heroes
        petKey            = $petKey
        stageSource       = $source
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
