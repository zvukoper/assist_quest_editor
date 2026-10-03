<#
.SYNOPSIS
    Compares two ETS2 profile snapshots produced by snapshot_ets2_profile.ps1.

.DESCRIPTION
    Prints:
      * top-level "hot" files whose content changed (these point at the active
        profile: config.cfg, mods_info.sii, saved_session_list.sii, ...);
      * every added / removed / changed file grouped by top-level area;
      * a summary of profile folders that were touched;
      * a Steam Cloud manifest diff when -Cloud is passed.

.PARAMETER Base
    Name of the older snapshot (e.g. baseline).

.PARAMETER Target
    Name of the newer snapshot (e.g. after-launch).

.PARAMETER ListPath
    Print the full path of each changed file instead of the relative one.

.PARAMETER Cloud
    Also diff <Base>-cloud.csv against <Target>-cloud.csv.

.PARAMETER SnapshotDir
    Folder with snapshots. Default: <repo>\.ci-state\ets2-snapshots
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$Target,
    [switch]$ListPath,
    [switch]$Cloud,
    [string]$SnapshotDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SnapshotDir) { $SnapshotDir = Join-Path $repoRoot '.ci-state\ets2-snapshots' }

function Read-Snapshot {
    param([string]$Name)
    $path = Join-Path $SnapshotDir "$Name.csv"
    if (-not (Test-Path -LiteralPath $path)) { throw "Snapshot not found: $path" }
    $map = @{}
    foreach ($row in (Import-Csv -LiteralPath $path)) { $map[$row.RelPath] = $row }
    return $map
}

$baseMap = Read-Snapshot $Base
$targetMap = Read-Snapshot $Target

$added = New-Object System.Collections.Generic.List[object]
$removed = New-Object System.Collections.Generic.List[object]
$changed = New-Object System.Collections.Generic.List[object]

foreach ($key in $targetMap.Keys) {
    if (-not $baseMap.ContainsKey($key)) {
        $added.Add($targetMap[$key]); continue
    }
    $b = $baseMap[$key]; $t = $targetMap[$key]
    if ($t.Type -eq 'F' -and ($b.Sha1 -ne $t.Sha1)) {
        $changed.Add([pscustomobject]@{ RelPath = $key; Base = $b; Target = $t })
    }
}
foreach ($key in $baseMap.Keys) {
    if (-not $targetMap.ContainsKey($key)) { $removed.Add($baseMap[$key]) }
}

function Get-TopSegment { param([string]$RelPath) ($RelPath -split '/')[0] }

Write-Host "=== Snapshot diff: $Base -> $Target ===" -ForegroundColor Cyan
Write-Host ("Added: {0}   Removed: {1}   Changed: {2}" -f $added.Count, $removed.Count, $changed.Count)

# --- Hot top-level files ------------------------------------------------------
$hotNames = @('config.cfg', 'mods_info.sii', 'saved_session_list.sii', 'inventory_item_data.sii',
    'bcg_data_list.sii', '.history.sii', 'game.log.txt', 'net.log', 'backups.txt',
    'news.sii', 'server_config.sii', 'crash_detection', 'game.crash.txt')
$hotChanged = $changed | Where-Object { $hotNames -contains $_.RelPath }
Write-Host ''
Write-Host '--- Top-level files that changed (active-profile hints) ---' -ForegroundColor Yellow
if ($hotChanged.Count -eq 0) {
    Write-Host '  (none)'
} else {
    foreach ($h in ($hotChanged | Sort-Object RelPath)) {
        Write-Host ("  {0,-28} {1} -> {2} bytes" -f $h.RelPath, $h.Base.Length, $h.Target.Length)
    }
}

# --- Group everything by top area ---------------------------------------------
function Show-Group {
    param([System.Collections.Generic.List[object]]$Items, [string]$Title, [string]$Color)
    Write-Host ''
    Write-Host "--- $Title (" -NoNewline -ForegroundColor $Color
    Write-Host $Items.Count -NoNewline
    Write-Host ") ---" -ForegroundColor $Color
    if ($Items.Count -eq 0) { Write-Host '  (none)'; return }
    $grouped = $Items | Group-Object -Property @{ Expression = { ($_.RelPath -split '/')[0] } } | Sort-Object Name
    foreach ($g in $grouped) {
        Write-Host ("  [{0}] {1} item(s)" -f $g.Name, $g.Count)
        $shown = $g.Group | Select-Object -First 40
        foreach ($it in $shown) {
            Write-Host ("      {0}" -f $it.RelPath)
        }
        if ($g.Count -gt 40) { Write-Host ("      ... and {0} more" -f ($g.Count - 40)) }
    }
}

Show-Group $changed 'Changed files' 'Yellow'
Show-Group $added 'Added files' 'Green'
Show-Group $removed 'Removed files' 'Red'

# --- Which profile folders were touched ---------------------------------------
Write-Host ''
Write-Host '--- Profile folders touched ---' -ForegroundColor Magenta
$all = @()
foreach ($x in $added) { $all += $x }
foreach ($x in $changed) { $all += $x }
foreach ($x in $removed) { $all += $x }
$areaNames = @('profiles', 'steam_profiles', 'preview_profiles')
$touched = @()
foreach ($it in $all) {
    $rel = [string]$it.RelPath
    if ([string]::IsNullOrEmpty($rel)) { continue }
    $parts = $rel -split '/'
    if ($parts.Length -ge 2 -and ($areaNames -contains $parts[0])) {
        $touched += "$($parts[0])/$($parts[1])"
    }
}
$touched = $touched | Sort-Object -Unique
if (-not $touched) { Write-Host '  (none)' } else { $touched | ForEach-Object { Write-Host "  $_" } }

# --- Steam Cloud diff ---------------------------------------------------------
if ($Cloud) {
    Write-Host ''
    Write-Host '=== Steam Cloud manifest diff ===' -ForegroundColor Cyan
    $baseCloudPath = Join-Path $SnapshotDir "$Base-cloud.csv"
    $targetCloudPath = Join-Path $SnapshotDir "$Target-cloud.csv"
    if (-not (Test-Path $baseCloudPath) -or -not (Test-Path $targetCloudPath)) {
        Write-Host '  Cloud snapshot missing; run snapshots with -IncludeSteamCloud.' -ForegroundColor Yellow
    } else {
        $baseCloud = @{}; foreach ($r in (Import-Csv $baseCloudPath)) { $baseCloud[$r.CloudPath] = $r }
        $targetCloud = @{}; foreach ($r in (Import-Csv $targetCloudPath)) { $targetCloud[$r.CloudPath] = $r }
        $cloudAdded = @($targetCloud.Keys | Where-Object { -not $baseCloud.ContainsKey($_) })
        $cloudRemoved = @($baseCloud.Keys | Where-Object { -not $targetCloud.ContainsKey($_) })
        $cloudChanged = @($targetCloud.Keys | Where-Object { $baseCloud.ContainsKey($_) -and $baseCloud[$_].Sha1 -ne $targetCloud[$_].Sha1 })
        Write-Host ("  Cloud added: {0}   removed: {1}   changed: {2}" -f $cloudAdded.Count, $cloudRemoved.Count, $cloudChanged.Count)
        foreach ($p in ($cloudChanged | Sort-Object)) { Write-Host "    [changed] $p" -ForegroundColor Yellow }
        foreach ($p in ($cloudAdded | Sort-Object)) { Write-Host "    [added]   $p" -ForegroundColor Green }
        foreach ($p in ($cloudRemoved | Sort-Object)) { Write-Host "    [removed] $p" -ForegroundColor Red }
    }
}
