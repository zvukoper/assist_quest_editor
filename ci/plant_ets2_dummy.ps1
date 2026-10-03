<#
.SYNOPSIS
    Plants a small custom "dummy" file into the active ETS2 profile folder so
    that Steam Cloud picks it up, then reports whether it reached the cloud
    staging area.

.DESCRIPTION
    The file contains:
      * machine name of this computer,
      * active ETS2 profile name + hex folder,
      * Steam account name + persona nickname + SteamID64,
      * timestamp and a unique random tag.

    It is written to  <GameRoot>\steam_profiles\<hex>\aqe_dummy_<tag>.json .
    Because Steam Cloud for ETS2 (app 227300) syncs the whole "profiles/*"
    subtree, an arbitrary payload file under the profile folder is uploaded.

.PARAMETER GameRoot
    ETS2 documents folder. Default auto-detected.

.PARAMETER Tag
    Optional suffix for the file name. Default: UTC timestamp (yyyyMMdd-HHmmss).

.PARAMETER DryRun
    Print what would be written without touching the file system.

.PARAMETER VerifyOnly
    Do not write; only check whether the newest aqe_dummy_* file already exists
    in the Steam Cloud staging folder.
#>
[CmdletBinding()]
param(
    [string]$GameRoot,
    [string]$Tag,
    [string]$Extension = 'json',
    [string]$Subfolder,
    [switch]$DryRun,
    [switch]$VerifyOnly,
    [string]$VerifyPattern = 'aqe_dummy_*'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-CloudStagingPath {
    param([string]$UserDataId)
    if (-not $UserDataId) { return $null }
    $steamRoot = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamRoot) { $steamRoot = 'E:\Steam' }
    $path = Join-Path $steamRoot "userdata\$UserDataId\227300\remote"
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

# --- Resolve active profile + identity ---------------------------------------
$detector = Join-Path $PSScriptRoot 'detect_ets2_profile.ps1'
$json = & $detector -GameRoot $GameRoot -Json
$info = $json | ConvertFrom-Json

if (-not $info.HexFolder -or -not $info.ProfileArea) {
    throw "Could not resolve the active profile folder. Is the game/log available? Detected: $($info.ActiveProfile)"
}
if (-not $Tag) { $Tag = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') }

$profileDir = Join-Path (Join-Path $info.GameRoot $info.ProfileArea) $info.HexFolder
if ($Subfolder) { $profileDir = Join-Path $profileDir $Subfolder }
$fileName = "aqe_dummy_$Tag.$Extension"
$targetPath = Join-Path $profileDir $fileName

$payload = [ordered]@{
    tool            = 'AssistQuestEditor/ETS2-CloudProbe'
    purpose         = 'Verify custom files are synced by Steam Cloud'
    tag             = $Tag
    writtenUtc       = (Get-Date).ToUniversalTime().ToString('o')
    machineName     = $env:COMPUTERNAME
    profileName     = $info.ActiveProfile
    profileHex      = $info.HexFolder
    profileArea     = $info.ProfileArea
    profileType     = $info.ProfileType
    steamUserDataId = $info.SteamUserDataId
    steamId64       = $info.SteamId64
    steamAccount    = $info.SteamAccountName
    steamNick       = $info.SteamPersonaName
    nonce           = [guid]::NewGuid().ToString()
}
if ($Extension -eq 'sii') {
    # Text SII flavour, in case Steam whitelists *.sii files.
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('SiiNunit')
    $lines.Add('{')
    $lines.Add('aqe_probe : _nameless.aqe.probe {')
    foreach ($kv in $payload.GetEnumerator()) { $lines.Add((" " + $kv.Key + ": `"" + ([string]$kv.Value).Replace('\','/') + "`"")) }
    $lines.Add('}')
    $lines.Add('')
    $lines.Add('}')
    $content = ($lines -join "`r`n")
} else {
    $content = ($payload | ConvertTo-Json -Depth 5)
}

Write-Host '=== ETS2 cloud probe file ===' -ForegroundColor Cyan
Write-Host "  Machine      : $env:COMPUTERNAME"
Write-Host "  Profile      : $($info.ActiveProfile)  ($($info.HexFolder))"
Write-Host "  Steam nick   : $($info.SteamPersonaName)  ($($info.SteamAccountName))"
Write-Host "  Target file  : $targetPath"

if ($VerifyOnly) {
    $staging = Get-CloudStagingPath -UserDataId $info.SteamUserDataId
    if ($staging) {
        $relSub = if ($Subfolder) { "profiles\$($info.HexFolder)\$Subfolder" } else { "profiles\$($info.HexFolder)" }
        $searchDir = Join-Path $staging $relSub
        $hit = Get-ChildItem -LiteralPath $searchDir -Filter $VerifyPattern -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { Write-Host "  CLOUD: FOUND -> $($hit.FullName) ($($hit.Length) bytes, $($hit.LastWriteTime))" -ForegroundColor Green }
        else { Write-Host '  CLOUD: not found yet (Steam has not synced it).' -ForegroundColor Yellow }
    } else {
        Write-Host "  CLOUD: staging folder not found for user $($info.SteamUserDataId)." -ForegroundColor Yellow
    }
    return
}

if ($DryRun) {
    Write-Host '  (dry run, not writing)' -ForegroundColor Yellow
    Write-Host $content
    return
}

if (-not (Test-Path -LiteralPath $profileDir)) {
    throw "Profile folder does not exist: $profileDir"
}
Set-Content -LiteralPath $targetPath -Value $content -Encoding UTF8
Write-Host "  Written: $targetPath ($((Get-Item -LiteralPath $targetPath).Length) bytes)" -ForegroundColor Green
Write-Host '  Now: focus the game or exit it, let Steam sync, then run this script with -VerifyOnly.' -ForegroundColor Gray
