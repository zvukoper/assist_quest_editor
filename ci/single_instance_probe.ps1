<#
.SYNOPSIS
    Проверка единственного экземпляра приложения (single instance).

.DESCRIPTION
    Запускает опубликованный AssistQuestEditor.exe, затем второй раз с путём к
    файлу. Ожидается:
        1) работает ровно один процесс;
        2) второй запуск завершается сам, не создавая окна;
        3) путь доходит до первого экземпляра (записи в журнале).

    Проверка требует опубликованного exe: single instance — поведение процесса,
    а не домена, поэтому Playwright/юнит-тестом его не проверить.

    Запускается как отдельная проверка в ci\run_local.ps1 при наличии publish.
#>
param(
    [string]$ExePath,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $RepositoryRoot 'bin\Release\net10.0-windows\win-x64\publish\AssistQuestEditor.exe'
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "Single instance probe: пропущено, exe не найден: $ExePath" -ForegroundColor Yellow
    exit 0
}

$logPath = Join-Path $RepositoryRoot 'MemoryAI\LOGS\assist_quest_editor.log'
$target = Join-Path $RepositoryRoot 'data\quests\tutorial_ruslan_shashlik.aqquest'

function Get-AppLogTail {
    param([int]$Count = 400)
    if (-not (Test-Path -LiteralPath $logPath)) { return @() }
    return @(Get-Content -LiteralPath $logPath -Tail $Count -Encoding utf8)
}

function Wait-ForLogMarker {
    param([string]$Pattern, [int]$TimeoutMs = 30000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $tail = Get-AppLogTail
        if ($tail | Select-String -Pattern $Pattern -Quiet) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Stop-ProbeApp {
    Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

Stop-ProbeApp

$failures = New-Object System.Collections.Generic.List[string]

try {
    # Первый экземпляр: обычный запуск.
    $first = Start-Process -FilePath $ExePath -PassThru
    if (-not (Wait-ForLogMarker -Pattern 'Single instance: экземпляр первый')) {
        $failures.Add('Первый экземпляр не зафиксировал захват мьютекса.')
    }

    # Второй экземпляр с путём к файлу: должен передать путь и завершиться.
    $second = Start-Process -FilePath $ExePath -ArgumentList "`"$target`"" -PassThru
    if (-not $second.WaitForExit(30000)) {
        $failures.Add('Второй экземпляр не завершился: ожидалась передача запроса.')
        try { $second.Kill() } catch { }
    }
    elseif ($second.ExitCode -ne 0) {
        $failures.Add("Второй экземпляр завершился с кодом $($second.ExitCode).")
    }

    Start-Sleep -Milliseconds 700

    $running = @(Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue)
    if ($running.Count -ne 1) {
        $failures.Add("Ожидался ровно один процесс, найдено: $($running.Count).")
    }

    if (-not (Wait-ForLogMarker -Pattern 'Single instance: получен запрос открытия')) {
        $failures.Add('Первый экземпляр не получил запрос открытия по каналу.')
    }

    $tail = Get-AppLogTail -Count 600
    if (-not ($tail | Select-String -Pattern 'Внешний запуск: обработка запроса' -Quiet)) {
        $failures.Add('Первый экземпляр не обработал внешний запуск.')
    }
}
finally {
    Stop-ProbeApp
}

if ($failures.Count) {
    Write-Host 'ОШИБКА: single instance probe' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host ('- ' + $_) -ForegroundColor Red }
    exit 1
}

Write-Host 'Single instance probe: OK' -ForegroundColor Green
