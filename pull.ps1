<#
.SYNOPSIS
    Обновление исходников из git, локальные проверки CI и сборка приложения.

.DESCRIPTION
    Порядок действий:
        1. git pull --ff-only;
        2. локальный прогон проверок ci/run_local.ps1 (те же шаги, что в ci.yml);
        3. только при успешных проверках запускается compile.ps1;
        4. при неудачных проверках сборка не выполняется, показывается
           Windows-уведомление, скрипт завершается с кодом 1.

    Проверки идут до сборки сознательно: compile.ps1 останавливает запущенный
    AssistQuestEditor и удаляет bin, obj, publish и профиль WebView2, поэтому
    запускать его на заведомо сломанном коде не имеет смысла.

.PARAMETER SkipChecks
    Пропустить локальные проверки и собрать сразу после git pull.

.PARAMETER NoLaunch
    Передать в compile.ps1: собрать, но не запускать приложение.

.PARAMETER NoNotify
    Не показывать Windows-уведомление о не пройденных проверках.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pull.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pull.ps1 -NoLaunch

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pull.ps1 -SkipChecks
#>
param(
    [switch]$SkipChecks,
    [switch]$NoLaunch,
    [switch]$NoNotify
)

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

# Подключаем помощник уведомлений. Его отсутствие не должно ломать сборку.
$script:NotifyEnabled = $false
if (-not $NoNotify) {
    $toastHelper = Join-Path $PSScriptRoot 'ci\WindowsToast.ps1'
    if (Test-Path -LiteralPath $toastHelper) {
        . $toastHelper
        if (Get-Command -Name 'Show-AssistQuestToast' -CommandType Function -ErrorAction SilentlyContinue) {
            $script:NotifyEnabled = $true
        }
    } else {
        Write-Host "Уведомления Windows недоступны: не найден $toastHelper" -ForegroundColor DarkGray
    }
}

if ($SkipChecks) {
    Write-Host ''
    Write-Host 'Локальные проверки пропущены (-SkipChecks).' -ForegroundColor Yellow
} else {
    $runner = Join-Path $PSScriptRoot 'ci\run_local.ps1'

    if (-not (Test-Path -LiteralPath $runner)) {
        Write-Host "Не найден локальный прогон проверок: $runner" -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host '=== Проверки перед сборкой ===' -ForegroundColor Cyan

    # run_local.ps1 сам печатает итоговую таблицу и называет упавшие проверки.
    # -NoNotify: уведомление показывает pull.ps1, чтобы не было двух уведомлений.
    & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner -NoNotify

    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Проверки не пройдены. Сборка отменена.' -ForegroundColor Red

        if ($script:NotifyEnabled) {
            $null = Show-AssistQuestToast `
                -Title 'Проверки не пройдены' `
                -Body "Проверки перед сборкой не прошли.`nСборка Assist Quest Editor отменена."

            Write-Host 'Показано уведомление Windows.' -ForegroundColor DarkGray
        }

        exit 1
    }

    Write-Host ''
    Write-Host 'Проверки пройдены. Запускаю сборку.' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== Сборка ===' -ForegroundColor Cyan

$compileArgs = @()
if ($NoLaunch) { $compileArgs += '-NoLaunch' }

& (Join-Path $PSScriptRoot 'compile.ps1') @compileArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "compile.ps1 завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}