<#
.SYNOPSIS
    Detects the currently / last loaded Euro Truck Simulator 2 profile and
    extracts its metadata (name, hex folder, type, mods, saves, settings).

.DESCRIPTION
    Sources, most reliable first:
      1) game.log.txt  -> "Set profile finished: '<name>'", "New profile selected: '<name>'",
                          "Profile type: <type>" (PC_steam_cloud / ...),
                          "Loading save. ... path: .../profiles/<hex>/save/..."
      2) preview_profiles\preview  -> timestamp of the last profile load
      3) steam_profiles\<hex>\last_session_config.sii -> written on exit
      4) mods_info.sii, saved_session_list.sii

    Profile folder names are hex-encoded ASCII of the profile name.

.PARAMETER GameRoot
    ETS2 documents folder. Default auto-detected.

.PARAMETER Json
    Emit the result as JSON (for machine consumption).
#>
[CmdletBinding()]
param(
    [string]$GameRoot,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

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

function ConvertFrom-HexName {
    param([string]$Value)
    if ($Value -match '^[0-9A-Fa-f]+$' -and ($Value.Length % 2) -eq 0 -and $Value.Length -ge 4) {
        try {
            $bytes = for ($i = 0; $i -lt $Value.Length; $i += 2) { [Convert]::ToByte($Value.Substring($i, 2), 16) }
            $text = [System.Text.Encoding]::ASCII.GetString([byte[]]$bytes)
            if ($text -match '^[\x20-\x7E]+$') { return $text }
        } catch { }
    }
    return $Value
}

function ConvertTo-HexName {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $null }
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Value)
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join '')
}

$result = [ordered]@{
    GameRoot          = $GameRoot
    ActiveProfile     = $null
    HexFolder         = $null
    ProfileArea       = $null
    ProfileType       = $null
    Source            = $null
    SavePath          = $null
    LastProfileLoad   = $null
    MachineName       = $env:COMPUTERNAME
    SteamUserDataId   = $null
    SteamId64         = $null
    SteamAccountName  = $null
    SteamPersonaName  = $null
    Mods              = @()
    Profiles          = @()
}

# --- 0) Steam identity (active user + persona nickname) ----------------------
function Get-SteamIdentity {
    $info = [ordered]@{
        SteamRoot       = $null
        UserDataId      = $null
        SteamId64       = $null
        AccountName     = $null
        PersonaName     = $null
    }

    # Steam install path (registry), fall back to common locations.
    $steamRoot = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamRoot) {
        $steamRoot = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
    }
    if (-not $steamRoot) {
        foreach ($c in @('E:\Steam', 'C:\Program Files (x86)\Steam')) {
            if (Test-Path -LiteralPath $c) { $steamRoot = $c; break }
        }
    }
    if (-not $steamRoot) { return [pscustomobject]$info }
    $info.SteamRoot = $steamRoot

    # Currently logged-in account id (userdata folder name).
    $active = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
    if ($active) { $info.UserDataId = [string]$active }

    # loginusers.vdf holds SteamID64 -> AccountName / PersonaName.
    $loginUsers = Join-Path $steamRoot 'config\loginusers.vdf'
    if (Test-Path -LiteralPath $loginUsers) {
        $lines = Get-Content -LiteralPath $loginUsers
        $currentSteamId = $null
        $accounts = @()
        foreach ($line in $lines) {
            if ($line -match '^\s+"(7656119\d+)"\s*$') {
                $currentSteamId = $Matches[1]
                $accounts += [pscustomobject]@{ SteamId64 = $currentSteamId; AccountName = $null; PersonaName = $null }
                continue
            }
            if ($currentSteamId) {
                if ($line -match '^\s+"AccountName"\s+"([^"]*)"') { $accounts[-1].AccountName = $Matches[1]; continue }
                if ($line -match '^\s+"PersonaName"\s+"([^"]*)"') { $accounts[-1].PersonaName = $Matches[1]; continue }
            }
        }
        $match = $null
        if ($info.UserDataId) {
            foreach ($a in $accounts) {
                $sid = [uint64]$a.SteamId64
                if (([uint64]($sid - 76561197960265728)) -eq [uint64]$info.UserDataId) { $match = $a; break }
            }
        }
        if (-not $match -and $accounts.Count -gt 0) { $match = $accounts[0] }
        if ($match) {
            $info.SteamId64 = $match.SteamId64
            $info.AccountName = $match.AccountName
            $info.PersonaName = $match.PersonaName
            if (-not $info.UserDataId) { $info.UserDataId = [string]([uint64]$match.SteamId64 - 76561197960265728) }
        }
    }
    return [pscustomobject]$info
}

$steam = Get-SteamIdentity
$result.SteamUserDataId  = $steam.UserDataId
$result.SteamId64        = $steam.SteamId64
$result.SteamAccountName = $steam.AccountName
$result.SteamPersonaName = $steam.PersonaName

# --- 1) game.log.txt ---------------------------------------------------------
$logPath = Join-Path $GameRoot 'game.log.txt'
if (Test-Path -LiteralPath $logPath) {
    $log = Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue
    if ($log) {
        $nameLine = $log | Select-String -Pattern "Set profile finished:\s*'([^']+)'" | Select-Object -Last 1
        if (-not $nameLine) { $nameLine = $log | Select-String -Pattern "New profile selected:\s*'([^']+)'" | Select-Object -Last 1 }
        if ($nameLine) {
            $result.ActiveProfile = $nameLine.Matches[0].Groups[1].Value
            $result.Source = 'game.log.txt'
        }
        $typeLine = $log | Select-String -Pattern 'Profile type:\s*(\S+)' | Select-Object -Last 1
        if ($typeLine) { $result.ProfileType = $typeLine.Matches[0].Groups[1].Value }

        $saveLine = $log | Select-String -Pattern 'Loading save\..*?path:\s*.*?/(steam_profiles|profiles)/([0-9A-Fa-f]+)/' | Select-Object -Last 1
        if ($saveLine) {
            $result.ProfileArea = $saveLine.Matches[0].Groups[1].Value
            $result.HexFolder = $saveLine.Matches[0].Groups[2].Value
            $result.SavePath = ($saveLine.Line -replace '^.*path:\s*', '').Trim()
            if (-not $result.ActiveProfile) { $result.ActiveProfile = ConvertFrom-HexName $result.HexFolder }
        }
    }
}

# --- 2) preview_profiles\<hex 'preview'> (last load time) --------------------
$previewRoot = Join-Path $GameRoot 'preview_profiles'
if (Test-Path -LiteralPath $previewRoot) {
    $previewDir = Get-ChildItem -LiteralPath $previewRoot -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { (ConvertFrom-HexName $_.Name) -eq 'preview' } | Select-Object -First 1
    if ($previewDir) {
        $newest = Get-ChildItem -LiteralPath $previewDir.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest) { $result.LastProfileLoad = $newest.LastWriteTime.ToString('o') }
    }
}

# --- 3) mods_info.sii --------------------------------------------------------
$modsPath = Join-Path $GameRoot 'mods_info.sii'
if (Test-Path -LiteralPath $modsPath) {
    $mods = Select-String -LiteralPath $modsPath -Pattern 'info\[\d+\]:\s*"([^"]+)"' -AllMatches
    $result.Mods = @($mods | ForEach-Object { $_.Matches[0].Groups[1].Value })
}

# --- 4) enumerate profiles ----------------------------------------------------
function Get-ProfileArea {
    param([string]$Area)
    $areaPath = Join-Path $GameRoot $Area
    if (-not (Test-Path -LiteralPath $areaPath)) { return @() }
    $out = @()
    foreach ($dir in (Get-ChildItem -LiteralPath $areaPath -Directory -Force -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $lastSession = $null
        $ls = Join-Path $dir.FullName 'last_session_config.sii'
        if (Test-Path -LiteralPath $ls) { $lastSession = (Get-Item -LiteralPath $ls).LastWriteTime.ToString('o') }
        $out += [pscustomobject]@{
            Area         = $Area
            HexFolder    = $dir.Name
            Name         = ConvertFrom-HexName $dir.Name
            Files        = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Force -ErrorAction SilentlyContinue).Count
            LastWrite    = $dir.LastWriteTime.ToString('o')
            LastSession  = $lastSession
        }
    }
    return $out
}
$result.Profiles = @(Get-ProfileArea 'steam_profiles') + @(Get-ProfileArea 'profiles')

if ($result.HexFolder -and $result.ProfileArea) {
    $match = $result.Profiles | Where-Object { $_.HexFolder -eq $result.HexFolder -and $_.Area -eq $result.ProfileArea } | Select-Object -First 1
    if ($match -and -not $result.ActiveProfile) { $result.ActiveProfile = $match.Name }
}

# Fallback: derive hex folder + area from the detected name (game.log.txt in the
# main menu has no "Loading save" line because the log rotated on startup).
if (-not $result.HexFolder -and $result.ActiveProfile) {
    $hex = ConvertTo-HexName $result.ActiveProfile
    $match = $result.Profiles | Where-Object { $_.HexFolder -eq $hex } | Select-Object -First 1
    if ($match) {
        $result.HexFolder = $match.HexFolder
        $result.ProfileArea = $match.Area
        $result.SavePath = "(steam_profiles|profiles)/$($match.HexFolder)/save"
    }
}

if ($Json) {
    [pscustomobject]$result | ConvertTo-Json -Depth 5
    return
}

function Show-OrUnknown {
    param([object]$Value)
    if ($null -eq $Value -or ([string]$Value) -eq '') { return '(unknown)' }
    return [string]$Value
}

Write-Host '=== ETS2 active profile ===' -ForegroundColor Cyan
Write-Host ("  Name         : {0}" -f (Show-OrUnknown $result.ActiveProfile))
Write-Host ("  Hex folder   : {0}" -f (Show-OrUnknown $result.HexFolder))
Write-Host ("  Area         : {0}" -f (Show-OrUnknown $result.ProfileArea))
Write-Host ("  Profile type : {0}" -f (Show-OrUnknown $result.ProfileType))
Write-Host ("  Source       : {0}" -f (Show-OrUnknown $result.Source))
Write-Host ("  Save path    : {0}" -f (Show-OrUnknown $result.SavePath))
Write-Host ("  Last load    : {0}" -f (Show-OrUnknown $result.LastProfileLoad))
Write-Host ''
Write-Host '=== Identity ===' -ForegroundColor Cyan
Write-Host ("  Machine      : {0}" -f (Show-OrUnknown $result.MachineName))
Write-Host ("  Steam user id: {0}" -f (Show-OrUnknown $result.SteamUserDataId))
Write-Host ("  SteamID64    : {0}" -f (Show-OrUnknown $result.SteamId64))
Write-Host ("  Account name : {0}" -f (Show-OrUnknown $result.SteamAccountName))
Write-Host ("  Steam nick   : {0}" -f (Show-OrUnknown $result.SteamPersonaName))
Write-Host ''
Write-Host ("--- Mods ({0}) ---" -f $result.Mods.Count) -ForegroundColor Yellow
$result.Mods | ForEach-Object { Write-Host "  $_" }
Write-Host ''
Write-Host '--- Profiles on disk ---' -ForegroundColor Yellow
$result.Profiles | Format-Table Area, HexFolder, Name, Files, LastSession -AutoSize
