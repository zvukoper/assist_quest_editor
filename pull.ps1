$ErrorActionPreference = 'Stop'

Write-Host "=== Обновление Assist Quest Editor ===" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.git'))) {
    Write-Host "Каталог не является Git-репозиторием: $PSScriptRoot" -ForegroundColor Red
    exit 1
}

& git -C $PSScriptRoot pull --ff-only
if ($LASTEXITCODE -ne 0) {
    Write-Host "git pull завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "=== Сборка ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'compile.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "compile.ps1 завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}