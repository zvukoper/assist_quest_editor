<#
.SYNOPSIS
    SCS "SiiNunit" codec: decrypt/encrypt encrypted SII files (signature "ScsC").

.DESCRIPTION
    SCS save/profile files start with the signature "ScsC" (0x43736353).
    Such a file is:
        header (56 bytes)
            UInt32 signature      "ScsC"
            Byte[32] HMAC         (integrity; not verified here)
            Byte[16] InitVector   AES-256-CBC IV
            UInt32  DataSize      size of the decompressed payload
        body
            AES-256-CBC( 32-byte key, IV ) of a zlib stream
            -> zlib inflate -> payload (usually a "BSII" binary SII, version 2)

    This script exposes -Decrypt (default) and -Encrypt and is a thin CLI over
    the functions in scs_sii_codec_lib.ps1.
#>
[CmdletBinding(DefaultParameterSetName = 'Decrypt')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Decrypt')][string]$Decrypt,
    [Parameter(Mandatory = $true, ParameterSetName = 'Encrypt')][string]$Encrypt,
    [Parameter(Mandatory = $true, ParameterSetName = 'Inspect')][string]$Inspect,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scs_sii_codec_lib.ps1')

if ($PSCmdlet.ParameterSetName -eq 'Decrypt') {
    if (-not $Out) { throw 'Decrypt requires -Out <path>.' }
    $result = Invoke-ScsDecrypt -Path $Decrypt -OutputPath $Out
    Write-Host "Decrypted: $Decrypt -> $Out ($($result.Length) bytes, type=$($result.Type))" -ForegroundColor Green
}
elseif ($PSCmdlet.ParameterSetName -eq 'Encrypt') {
    if (-not $Out) { throw 'Encrypt requires -Out <path>.' }
    $len = Invoke-ScsEncrypt -Path $Encrypt -OutputPath $Out
    Write-Host "Encrypted: $Encrypt -> $Out ($len bytes)" -ForegroundColor Green
}
else {
    $info = Get-ScsHeaderInfo -Path $Inspect
    $info | Format-List
}
