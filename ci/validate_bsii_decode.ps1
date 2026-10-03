param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Continue'
$decoder = Join-Path $PSScriptRoot 'decode_ets2_save.ps1'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$files = Get-ChildItem -LiteralPath $SourceDir -Recurse -File -Filter *.sii
"{0} files to decode" -f $files.Count

foreach ($f in $files) {
    $rel = $f.FullName.Substring($SourceDir.Length + 1)
    $flat = ($rel -replace '[\\/]', '_')
    $out = Join-Path $OutDir ($flat + '.txt')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $decoder -Path $f.FullName -Out $out 2>&1 | Out-Null
    if (Test-Path -LiteralPath $out) {
        $txt = [IO.File]::ReadAllText($out)
        $open = ([regex]::Matches($txt, '\{')).Count
        $close = ([regex]::Matches($txt, '\}')).Count
        $status = if ($open -eq $close) { 'OK' } else { 'MISMATCH' }
        "{0,-40} chars={1,-9} braces={2}/{3} {4}" -f $rel, $txt.Length, $open, $close, $status
    } else {
        "{0,-40} FAIL (no output)" -f $rel
    }
}
