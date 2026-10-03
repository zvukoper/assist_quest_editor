<#
.SYNOPSIS
    Decodes an ETS2/ATS .sii file to readable text.

.DESCRIPTION
    Handles the full chain:
      * "ScsC"  -> decrypt (AES-256-CBC + zlib) -> payload
      * "BSII"  -> decode binary SII (versions 1/2/3) to textual SiiNunit
      * "SiiN"  -> already text, copied through

    The BSII decoder is a small C# class (ci/scs_bsii_decoder.cs), compiled on
    the fly with Add-Type for speed on large game.sii files (multi-MB).

.PARAMETER Path
    Input .sii file (encrypted ScsC or binary BSII or plain text).

.PARAMETER Out
    Output text file. Default: <Path>.txt next to the input.

.PARAMETER AsText
    Write/return textual SII (default). Use -Raw to instead dump the decrypted
    binary payload as-is.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Out,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scs_sii_codec_lib.ps1')

if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }
if (-not $Out) { $Out = "$Path.txt" }

$bytes = [IO.File]::ReadAllBytes($Path)
$sig = ''
if ($bytes.Length -ge 4) { $sig = [Text.Encoding]::ASCII.GetString($bytes[0..3]) }

$payload = $null
if ($sig -eq 'ScsC') {
    Write-Host "Decrypting ScsC -> payload..." -ForegroundColor Gray
    $payload = (Invoke-ScsDecrypt -Path $Path).Bytes
} else {
    $payload = $bytes
}

if ($payload.Length -ge 4) { $sig = [Text.Encoding]::ASCII.GetString($payload[0..3]) }

if ($Raw) {
    [IO.File]::WriteAllBytes($Out, $payload)
    Write-Host "Raw payload ($sig) -> $Out ($($payload.Length) bytes)" -ForegroundColor Green
    return
}

if ($sig -eq 'BSII') {
    Write-Host 'Decoding BSII binary SII -> text...' -ForegroundColor Gray
    Add-Type -Path (Join-Path $PSScriptRoot 'scs_bsii_decoder.cs')
    $text = [Aqe.Scs.BsiiDecoder]::Decode([byte[]]$payload)
    [IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Decoded -> $Out ($($text.Length) chars)" -ForegroundColor Green
}
elseif ($sig -eq 'SiiN') {
    $text = [Text.Encoding]::UTF8.GetString($payload)
    [IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Text SII -> $Out ($($text.Length) chars)" -ForegroundColor Green
}
else {
    throw "Unknown payload signature '$sig'."
}
