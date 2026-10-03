<#
    SCS SII codec library (encrypted "ScsC" format).

    Format (little-endian):
        offset 0   : UInt32  signature "ScsC" (0x43736353)
        offset 4   : 32 bytes HMAC (not verified)
        offset 36  : 16 bytes AES-256-CBC IV
        offset 52  : UInt32  DataSize = length of the inflated payload
        offset 56  : encrypted body = AES-256-CBC( zlib(payload) )

    The AES-256 key is the well-known SCS "Savegame Decrypter" key
    (JohnnyGuitar). See TheLazyTomcat/SII_Decrypt.
#>

$script:ScsSiiKey = [byte[]]@(
    0x2A, 0x5F, 0xCB, 0x17, 0x91, 0xD2, 0x2F, 0xB6,
    0x02, 0x45, 0xB3, 0xD8, 0x36, 0x9E, 0xD0, 0xB2,
    0xC2, 0x73, 0x71, 0x56, 0x3F, 0xBF, 0x1F, 0x3C,
    0x9E, 0xDF, 0x6B, 0x11, 0x82, 0x5A, 0x5D, 0x0A
)

function Test-ScsFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 56) { return $false }
    return ($bytes[0] -eq 0x53 -and $bytes[1] -eq 0x63 -and $bytes[2] -eq 0x73 -and $bytes[3] -eq 0x43)
}

function Get-ScsHeaderInfo {
    param([string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 56) { throw "File too small to be an ScsC file: $Path" }
    $sig = [Text.Encoding]::ASCII.GetString($bytes[0..3])
    $iv = $bytes[36..51]
    $dataSize = [BitConverter]::ToUInt32($bytes, 52)
    return [pscustomobject]@{
        Path          = $Path
        Signature     = $sig
        InitVector    = (($iv | ForEach-Object { $_.ToString('x2') }) -join '')
        DataSize      = $dataSize
        EncryptedSize = $bytes.Length - 56
    }
}

<#
    Decrypts an ScsC file and returns the inflated payload bytes.
    Returns an object with .Bytes, .Length and .Type ('BSII' binary SII or
    'SiiN' plain text or 'unknown').
#>
function Invoke-ScsDecrypt {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$OutputPath
    )
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 56) { throw "File too small to be an ScsC file: $Path" }
    if (-not ($bytes[0] -eq 0x53 -and $bytes[1] -eq 0x63 -and $bytes[2] -eq 0x73 -and $bytes[3] -eq 0x43)) {
        throw "Not an ScsC file (bad signature): $Path"
    }

    $iv = [byte[]]$bytes[36..51]
    $dataSize = [BitConverter]::ToUInt32($bytes, 52)
    $cipher = [byte[]]$bytes[56..($bytes.Length - 1)]

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.KeySize = 256
        $aes.BlockSize = 128
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::None
        $aes.Key = $script:ScsSiiKey
        $aes.IV = $iv
        $decryptor = $aes.CreateDecryptor()
        $plain = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
    } finally {
        $aes.Dispose()
    }

    # The decrypted body is a zlib stream.
    $payload = Expand-Zlib -Data $plain -ExpectedSize $dataSize

    $type = 'unknown'
    if ($payload.Length -ge 4) {
        $sig = [Text.Encoding]::ASCII.GetString($payload[0..3])
        if ($sig -eq 'BSII') { $type = 'BSII' }
        elseif ($sig -eq 'SiiN') { $type = 'SiiN' }
    }

    if ($OutputPath) {
        [IO.File]::WriteAllBytes($OutputPath, $payload)
    }
    return [pscustomobject]@{ Bytes = $payload; Length = $payload.Length; Type = $type }
}

function Expand-Zlib {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Data,
        [int]$ExpectedSize = 0
    )
    $ms = New-Object IO.MemoryStream(, $Data)
    try {
        # skip the 2-byte zlib header
        $ms.Position = 2
        $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)
        try {
            if ($ExpectedSize -gt 0) {
                $out = New-Object IO.MemoryStream
                $buffer = New-Object byte[] 65536
                while (($read = $ds.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $out.Write($buffer, 0, $read)
                }
                return $out.ToArray()
            } else {
                $out = New-Object IO.MemoryStream
                $ds.CopyTo($out)
                return $out.ToArray()
            }
        } finally { $ds.Dispose() }
    } finally { $ms.Dispose() }
}

<#
    Encrypts a payload (BSII binary / plain text SII) into an ScsC file.
    Produces a zero HMAC (the game does not appear to validate it for loading;
    if it does, this step must be revisited) and a random IV.
#>
function Invoke-ScsEncrypt {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [byte[]]$InitVector,
        [byte[]]$Hmac
    )
    $payload = [IO.File]::ReadAllBytes($Path)

    # zlib-compress (deflate + zlib header 0x78 0x01 + adler32).
    $compressed = Compress-Zlib -Data $payload

    if (-not $InitVector) {
        $InitVector = New-Object byte[] 16
        $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
        try { $rng.GetBytes($InitVector) } finally { $rng.Dispose() }
    }
    if (-not $Hmac) { $Hmac = New-Object byte[] 32 }

    # AES-256-CBC, no padding: pad with zeros to a 16-byte boundary.
    $padLen = (16 - ($compressed.Length % 16)) % 16
    if ($padLen -gt 0) {
        $compressed = $compressed + (New-Object byte[] $padLen)
    }

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.KeySize = 256
        $aes.BlockSize = 128
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::None
        $aes.Key = $script:ScsSiiKey
        $aes.IV = $InitVector
        $encryptor = $aes.CreateEncryptor()
        $cipher = $encryptor.TransformFinalBlock($compressed, 0, $compressed.Length)
    } finally {
        $aes.Dispose()
    }

    $out = New-Object IO.MemoryStream
    $out.Write([byte[]]@(0x53, 0x63, 0x73, 0x43), 0, 4)   # "ScsC"
    $out.Write($Hmac, 0, 32)
    $out.Write($InitVector, 0, 16)
    $out.Write([BitConverter]::GetBytes([uint32]$payload.Length), 0, 4)
    $out.Write($cipher, 0, $cipher.Length)
    [IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
    return $out.Length
}

function Compress-Zlib {
    param([Parameter(Mandatory = $true)][byte[]]$Data)
    $out = New-Object IO.MemoryStream
    # zlib header: CMF=0x78, FLG=0x01 (check bits valid for no preset dict)
    $out.Write([byte[]]@(0x78, 0x01), 0, 2)
    $ds = New-Object IO.Compression.DeflateStream($out, [IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $ds.Write($Data, 0, $Data.Length)
    } finally { $ds.Dispose() }

    # Adler-32 checksum
    $adler = Get-Adler32 -Data $Data
    $out.Write([byte[]]@(
        [byte](($adler -shr 24) -band 0xFF),
        [byte](($adler -shr 16) -band 0xFF),
        [byte](($adler -shr 8) -band 0xFF),
        [byte]($adler -band 0xFF)), 0, 4)
    return $out.ToArray()
}

function Get-Adler32 {
    param([Parameter(Mandatory = $true)][byte[]]$Data)
    $a = [uint32]1
    $b = [uint32]0
    foreach ($byte in $Data) {
        $a = ($a + $byte) % 65521
        $b = ($b + $a) % 65521
    }
    return [uint32](([uint32]$b -shl 16) -bor $a)
}
