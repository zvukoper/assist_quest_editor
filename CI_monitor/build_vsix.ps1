# Build the CI Monitor VS Code extension into a .vsix package.
#
# Usage:
#   .\build_vsix.ps1
#   .\build_vsix.ps1 -SkipInstall
#
# By default dependencies are installed from package-lock.json before packaging.

[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ExtensionDirectory = $PSScriptRoot

function Invoke-Npm {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & npm @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "npm завершился с кодом $LASTEXITCODE: npm $($Arguments -join ' ')"
    }
}

Push-Location $ExtensionDirectory

try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw 'Node.js не найден в PATH. Установите Node.js 22 или более новую совместимую версию.'
    }

    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw 'npm не найден в PATH.'
    }

    $PackageJsonPath = Join-Path $ExtensionDirectory 'package.json'
    $PackageLockPath = Join-Path $ExtensionDirectory 'package-lock.json'

    if (-not (Test-Path -LiteralPath $PackageJsonPath)) {
        throw "Не найден package.json: $PackageJsonPath"
    }

    if (-not (Test-Path -LiteralPath $PackageLockPath)) {
        throw "Не найден package-lock.json: $PackageLockPath"
    }

    $Package = Get-Content -LiteralPath $PackageJsonPath -Raw | ConvertFrom-Json

    Write-Host "CI Monitor v$($Package.version)" -ForegroundColor Cyan
    Write-Host "Каталог: $ExtensionDirectory"

    if (-not $SkipInstall) {
        Write-Host 'Установка зависимостей через npm ci...' -ForegroundColor Yellow
        Invoke-Npm @('ci')
    }
    else {
        Write-Host 'npm ci пропущен (-SkipInstall).' -ForegroundColor DarkYellow
    }

    Write-Host 'Сборка VSIX...' -ForegroundColor Yellow
    Invoke-Npm @('run', 'package')

    $Vsix = Get-ChildItem -LiteralPath $ExtensionDirectory -Filter '*.vsix' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $Vsix) {
        throw 'После npm run package файл .vsix не найден.'
    }

    Write-Host ''
    Write-Host "Готово: $($Vsix.FullName)" -ForegroundColor Green
}
finally {
    Pop-Location
}
