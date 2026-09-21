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

# Подключаем помощники: уведомление и проверку активности расширения CI Monitor.
# Отсутствие файла не должно ломать сборку, поэтому это только отметка в выводе.
$script:NotifyEnabled = $false
$script:EditorMonitorActive = $false
$toastHelper = Join-Path $PSScriptRoot 'ci\WindowsToast.ps1'

if (Test-Path -LiteralPath $toastHelper) {
    . $toastHelper

    if (Get-Command -Name 'Show-AssistQuestToast' -CommandType Function -ErrorAction SilentlyContinue) {
        $script:NotifyEnabled = -not $NoNotify
    }

    if (Get-Command -Name 'Test-AssistQuestEditorMonitorActive' -CommandType Function -ErrorAction SilentlyContinue) {
        $script:EditorMonitorActive = Test-AssistQuestEditorMonitorActive -RepositoryRoot $PSScriptRoot
    }
} elseif (-not $NoNotify) {
    Write-Host "Уведомления Windows недоступны: не найден $toastHelper" -ForegroundColor DarkGray
}

function Show-CheckNotification {
    <#
        Одно уведомление на прогон с учётом активности расширения CI Monitor.

        Требование: при локальном прогоне уведомляет расширение, поэтому штатное
        уведомление скрипта подавляется. Признак активности проверяется в момент
        показа, а не только на старте: редактор мог быть открыт во время прогона.
    #>
    param(
        [string]$Title,
        [string]$Body
    )

    if ($NoNotify) { return }

    if (Test-AssistQuestEditorMonitorActive -RepositoryRoot $PSScriptRoot) {
        Write-Host 'Уведомление покажет расширение CI Monitor.' -ForegroundColor DarkGray
        return
    }

    if (-not $script:NotifyEnabled) { return }

    if (Show-AssistQuestToast -Title $Title -Body $Body) {
        Write-Host 'Показано уведомление Windows.' -ForegroundColor DarkGray
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
    # -NoNotify: уведомление показывает pull.ps1, чтобы оно было только одно и
    # учитывало активность расширения CI Monitor.
    & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner -NoNotify

    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Проверки не пройдены. Сборка отменена.' -ForegroundColor Red

        Show-CheckNotification `
            -Title 'Локальные проверки не пройдены' `
            -Body ("Проверки перед сборкой не прошли.`nСборка Assist Quest Editor отменена.")

        exit 1
    }

    Write-Host ''
    Write-Host 'Проверки пройдены. Запускаю сборку.' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== Сборка ===' -ForegroundColor Cyan

# Параметры передаются хэш-таблицей, а не массивом строк.
# Сплаттинг массива со строкой «-NoLaunch» для параметра типа [switch] не
# работает: строка воспринимается как обычный аргумент, switch остаётся $false,
# и приложение запускается вопреки флагу. Хэш-таблица привязывает параметр по имени.
$compileArgs = @{}
if ($NoLaunch) { $compileArgs['NoLaunch'] = $true }

& (Join-Path $PSScriptRoot 'compile.ps1') @compileArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "compile.ps1 завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}