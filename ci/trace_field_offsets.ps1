param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Block = 'bank'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scs_sii_codec_lib.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'scs_bsii_decoder.cs')
[Aqe.Scs.BsiiDecoder]::TraceBlock = $Block
$p = (Invoke-ScsDecrypt -Path $Path).Bytes
try { [Aqe.Scs.BsiiDecoder]::Decode([byte[]]$p) | Out-Null } catch { }
[Aqe.Scs.BsiiDecoder]::TraceLog | ForEach-Object { $_ }
