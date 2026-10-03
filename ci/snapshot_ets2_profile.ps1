<#
.SYNOPSIS
    Takes a snapshot of the Euro Truck Simulator 2 profile folders
    (relative path + size + last write time + SHA1) and, optionally, of the
    Steam Cloud manifest for app 227300.

.DESCRIPTION
    Goal: determine exactly which profile the player has loaded, what changes
    when the game starts / the profile is switched / a new profile is created,
    and what Steam Cloud actually syncs.

    Heavy or irrelevant folders (mod, cache, screenshot, *.bak, *.rar) are
    excluded by default, so a snapshot stays small and fast.

.PARAMETER GameRoot
    ETS2 "My Documents" folder. Default: E:\Users\Docs\Euro Truck Simulator 2
    (falls back to %USERPROFILE%\Documents\Euro Truck Simulator 2).

.PARAMETER Name
    Snapshot name, used as file name. Examples: baseline, after-launch.

.PARAMETER Include
    Sub-folders of GameRoot scanned recursively.
    Default: profiles, steam_profiles, preview_profiles

.PARAMETER Full
    Scan the whole GameRoot recursively (including mod, cache, *.bak, ...).

.PARAMETER IncludeSteamCloud
    Also snapshot the Steam Cloud manifest (remotecache.vdf) for appid 227300.

.PARAMETER OutputDir
    Where snapshots are stored. Default: <repo>\.ci-state\ets2-snapshots

.PARAMETER SteamUserData
    Steam userdata folder. Default: E:\Steam\userdata

.PARAMETER AppId
    ETS2 Steam app id. Default: 227300
#>
[CmdletBinding()]
param(
    [string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$Name,
    [string[]]$Include = @('profiles', 'steam_profiles', 'preview_profiles'),
    [switch]$Full,
    [switch]$IncludeSteamCloud,
    [string]$OutputDir,
    [string]$SteamUserData = 'E:\Steam\userdata',
    [int]$AppId = 227300
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot '.ci-state\ets2-snapshots' }

# Names skipped unconditionally: game caches, screenshots and backup archives.
$excludeNames = @('mod', 'cache', 'screenshot', 'music', 'plugins', '*.bak', '*.rar', '*.zip', '*.tmp')

if (-not $GameRoot) {
    $candidates = @(
        'E:\Users\Docs\Euro Truck Simulator 2',
        (Join-Path $env:USERPROFILE 'Documents\Euro Truck Simulator 2')
    )
    $GameRoot = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $GameRoot -or -not (Test-Path -LiteralPath $GameRoot)) {
    throw "ETS2 documents folder not found. Pass -GameRoot <path>."
}
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

function Test-ExcludedName {
    param([string]$ChildName)
    foreach ($pattern in $excludeNames) {
        if ($ChildName -like $pattern) { return $true }
    }
    return $false
}

# Profile folders are hex-encoded ASCII of the profile name (e.g.
# 4D696B68616C7963685F484558 -> "Mikhalych_HEX"). Decode it for human eyes.
function ConvertFrom-HexProfileName {
    param([string]$Value)
    if ($Value -match '^[0-9A-Fa-f]+$' -and ($Value.Length % 2) -eq 0 -and $Value.Length -ge 4) {
        try {
            $bytes = for ($i = 0; $i -lt $Value.Length; $i += 2) {
                [Convert]::ToByte($Value.Substring($i, 2), 16)
            }
            $text = [System.Text.Encoding]::ASCII.GetString([byte[]]$bytes)
            if ($text -match '^[\x20-\x7E]+$') { return $text }
        } catch { }
    }
    return $Value
}

$entries = New-Object System.Collections.Generic.List[object]

function Add-FileEntries {
    param([string]$ScanRoot, [string]$Prefix, [string]$Relative)

    $items = Get-ChildItem -LiteralPath $ScanRoot -Force -ErrorAction SilentlyContinue
    foreach ($item in $items) {
        if (Test-ExcludedName $item.Name) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }

        $rel = if ([string]::IsNullOrEmpty($Relative)) { $item.Name } else { "$Relative/$($item.Name)" }
        $full = if ([string]::IsNullOrEmpty($Prefix)) { $rel } else { "$Prefix/$rel" }

        if ($item.PSIsContainer) {
            $entries.Add([pscustomobject]@{
                RelPath          = $full
                Type             = 'D'
                Length           = ''
                LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
                Sha1             = ''
            })
            Add-FileEntries -ScanRoot $item.FullName -Prefix $Prefix -Relative $rel
        } else {
            $hash = ''
            try { $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA1 -ErrorAction Stop).Hash.ToLowerInvariant() } catch { }
            $entries.Add([pscustomobject]@{
                RelPath          = $full
                Type             = 'F'
                Length           = $item.Length
                LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
                Sha1             = $hash
            })
        }
    }
}

if ($Full) {
    Add-FileEntries -ScanRoot $GameRoot -Prefix '' -Relative ''
} else {
    # Top-level files only (config.cfg, mods_info.sii, saved_session_list.sii, ...).
    foreach ($item in (Get-ChildItem -LiteralPath $GameRoot -File -Force -ErrorAction SilentlyContinue)) {
        if (Test-ExcludedName $item.Name) { continue }
        $hash = ''
        try { $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA1 -ErrorAction Stop).Hash.ToLowerInvariant() } catch { }
        $entries.Add([pscustomobject]@{
            RelPath          = $item.Name
            Type             = 'F'
            Length           = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            Sha1             = $hash
        })
    }
    foreach ($inc in $Include) {
        $scanPath = Join-Path $GameRoot $inc
        if (Test-Path -LiteralPath $scanPath) {
            Add-FileEntries -ScanRoot $scanPath -Prefix $inc -Relative ''
        }
    }
}

$snapshotPath = Join-Path $OutputDir "$Name.csv"
$entries | Sort-Object RelPath | Export-Csv -LiteralPath $snapshotPath -NoTypeInformation -Encoding UTF8

# Human-readable profile index: folder name -> decoded name.
$profileIndexPath = Join-Path $OutputDir "$Name-profiles.txt"
$indexLines = New-Object System.Collections.Generic.List[string]
$indexLines.Add("Snapshot : $Name")
$indexLines.Add("GameRoot : $GameRoot")
$indexLines.Add("Created  : $((Get-Date).ToString('o'))")
$indexLines.Add('')
foreach ($area in $Include) {
    $areaPath = Join-Path $GameRoot $area
    if (-not (Test-Path -LiteralPath $areaPath)) { continue }
    $indexLines.Add("[$area]")
    foreach ($dir in (Get-ChildItem -LiteralPath $areaPath -Directory -Force -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $decoded = ConvertFrom-HexProfileName $dir.Name
        $fileCount = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Force -ErrorAction SilentlyContinue).Count
        $indexLines.Add(("  {0,-34} = {1,-22} files={2}" -f $dir.Name, $decoded, $fileCount))
    }
    $indexLines.Add('')
}
$indexLines | Set-Content -LiteralPath $profileIndexPath -Encoding UTF8

Write-Host "Snapshot '$Name': $($entries.Count) entries -> $snapshotPath" -ForegroundColor Green
Write-Host "Profile index -> $profileIndexPath" -ForegroundColor Green

# --- Optional: Steam Cloud manifest snapshot ---------------------------------
if ($IncludeSteamCloud) {
    $manifest = $null
    if (Test-Path -LiteralPath $SteamUserData) {
        foreach ($userDir in (Get-ChildItem -LiteralPath $SteamUserData -Directory -ErrorAction SilentlyContinue)) {
            $candidate = Join-Path $userDir.FullName "$AppId\remotecache.vdf"
            if (Test-Path -LiteralPath $candidate) { $manifest = $candidate; break }
        }
    }
    if (-not $manifest) {
        Write-Host "Steam Cloud manifest not found (appid $AppId under $SteamUserData)." -ForegroundColor Yellow
    } else {
        $cloudEntries = New-Object System.Collections.Generic.List[object]
        $current = $null
        foreach ($line in (Get-Content -LiteralPath $manifest)) {
            if ($line -match '^\s+"([^"]+/[^"]+)"\s*$') {
                $current = $Matches[1]
                continue
            }
            if ($current -and $line -match '^\s+"sha"\s+"([0-9a-fA-F]*)"') {
                $sha = $Matches[1].ToLowerInvariant()
                $size = ''; $sync = ''
                $cloudEntries.Add([pscustomobject]@{ CloudPath = $current; Sha1 = $sha; Size = $size; SyncState = $sync })
                $current = $null
            }
        }
        $cloudPath = Join-Path $OutputDir "$Name-cloud.csv"
        $cloudEntries | Sort-Object CloudPath | Export-Csv -LiteralPath $cloudPath -NoTypeInformation -Encoding UTF8
        Write-Host "Steam Cloud manifest: $($cloudEntries.Count) entries -> $cloudPath (source: $manifest)" -ForegroundColor Green
    }
}
