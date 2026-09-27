<#
.SYNOPSIS
    Монитор производительности процесса Assist Quest Editor.

.DESCRIPTION
    Консольный аналог «диспетчера задач» для ОДНОГО процесса: во времени снимает
    загрузку процессора, память, потоки и дескрипторы, а в конце печатает сводку
    (среднее, максимум, p95, рост памяти).

    Зачем это нужно отдельно от браузерного замера (ci/perf_probe.mjs):
     * браузерный замер отвечает на вопрос «КАКОЙ СЛОЙ карты тормозит»
       (сетка/дороги/точки/квесты/игрок/HUD);
     * этот монитор отвечает на вопрос «что делает ПРОЦЕСС»: сколько ест памяти,
       сколько грузит процессор, растут ли потоки и дескрипторы от перерисовок.
    Вместе они закрывают задачу: сначала измеряем процесс, затем находим слой
    внутри кадра.

    ПОЧЕМУ Get-Process, А НЕ Get-Counter:
    имена счётчиков Windows локализованы («% Processor Time» в русской Windows
    называется иначе), поэтому скрипт, ищущий счётчики по английскому имени,
    ломается на другой локали. Get-Process локали не зависит, а процент загрузки
    процессора считается из приращения TotalProcessorTime — тем же способом,
    которым пользуется сам диспетчер задач.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\perf_monitor.ps1

.EXAMPLE
    # Другое имя процесса, минута наблюдения, опрос раз в 500 мс
    .\ci\perf_monitor.ps1 -ProcessName myapp -DurationSeconds 60 -IntervalMs 500

.EXAMPLE
    # Машиночитаемый отчёт, чтобы сравнить два прогона
    .\ci\perf_monitor.ps1 -Json

.EXAMPLE
    # Ждать запуска приложения, затем замер
    .\ci\perf_monitor.ps1 -Wait
#>
[CmdletBinding()]
param(
    # Имя процесса без .exe. Для single-file сборки приложения — AssistQuestEditor.
    [string]$ProcessName = 'AssistQuestEditor',

    # Сколько секунд наблюдать.
    [int]$DurationSeconds = 60,

    # Пауза между опросами, мс.
    [int]$IntervalMs = 1000,

    # Печатать итог в виде JSON.
    [switch]$Json,

    # Ждать появления процесса, а не выходить сразу.
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

# Точка отсчёта для процессорного времени: без неё нельзя получить процент.
$script:prevCpuMs = $null

function Get-CpuPercent {
    <#
        Процент загрузки процессора за интервал.

        Приращение процессорного времени процесса делится на прошедшее время и
        на число логических процессоров. Именно так считает диспетчер задач:
        100% означает «заняты все ядра».
    #>
    param($Process, [double]$ElapsedMs)

    if ($ElapsedMs -le 0) { return 0 }
    if ($null -eq $script:prevCpuMs) {
        $script:prevCpuMs = $Process.TotalProcessorTime.TotalMilliseconds
        return $null
    }

    $cpuMs = $Process.TotalProcessorTime.TotalMilliseconds - $script:prevCpuMs
    $script:prevCpuMs = $Process.TotalProcessorTime.TotalMilliseconds

    $cores = [Environment]::ProcessorCount
    if ($cores -le 0) { $cores = 1 }
    return [Math]::Round($cpuMs / $ElapsedMs / $cores * 100, 1)
}

function Get-Percentile {
    param([double[]]$Values, [double]$Ratio)

    if (-not $Values -or $Values.Count -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Round(($sorted.Count - 1) * $Ratio)))
    return [Math]::Round($sorted[$index], 1)
}

function ConvertTo-Megabytes {
    param([double]$Bytes)
    return [Math]::Round($Bytes / 1MB, 1)
}

try {
    $existing = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if (-not $existing.Count) {
        if ($Wait) {
            Write-Host "Процесс '$ProcessName' не запущен: ожидаю появления..."
            while (-not @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue).Count) {
                Start-Sleep -Milliseconds 250
            }
        } else {
            Write-Warning "Процесс '$ProcessName' не найден. Запустите приложение или укажите -ProcessName/-Wait."
            exit 2
        }
    }

    $samples = [Math]::Max(1, [int]($DurationSeconds * 1000 / [Math]::Max(1, $IntervalMs)))

    Write-Host "Монитор производительности: процесс '$ProcessName'"
    Write-Host ("Наблюдение: {0} с, опрос каждые {1} мс" -f $DurationSeconds, $IntervalMs)
    if (-not $Json) {
        Write-Host ""
        Write-Host "  время     CPU%   рабочая МБ   приватная МБ   потоки   дескрипторы"
    }

    $rows = New-Object System.Collections.Generic.List[object]
    $previousAt = Get-Date

    for ($index = 0; $index -lt $samples; $index++) {
        $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $process) {
            Write-Host "Процесс завершился на измерении $($index + 1)."
            break
        }

        $now = Get-Date
        $elapsedMs = ($now - $previousAt).TotalMilliseconds
        $previousAt = $now

        $cpu = Get-CpuPercent -Process $process -ElapsedMs $elapsedMs

        # Первое измерение только задаёт точку отсчёта процессорного времени.
        if ($null -ne $cpu) {
            $row = [pscustomobject]@{
                Time         = $now.ToString('HH:mm:ss')
                CpuPercent   = $cpu
                WorkingSetMb = ConvertTo-Megabytes -Bytes $process.WorkingSet64
                PrivateMb    = ConvertTo-Megabytes -Bytes $process.PrivateMemorySize64
                Threads      = $process.Threads.Count
                Handles      = $process.HandleCount
            }
            $rows.Add($row)

            if (-not $Json) {
                Write-Host ("  {0}  {1,6}  {2,11}  {3,13}  {4,7}  {5,12}" -f
                    $row.Time, $row.CpuPercent, $row.WorkingSetMb, $row.PrivateMb, $row.Threads, $row.Handles)
            }
        }

        Start-Sleep -Milliseconds $IntervalMs
    }

    if ($rows.Count -eq 0) {
        Write-Warning "Не удалось собрать ни одного измерения."
        exit 3
    }

    $cpuValues = @($rows | ForEach-Object { [double]$_.CpuPercent })
    $workingSet = @($rows | ForEach-Object { [double]$_.WorkingSetMb })
    $privateMem = @($rows | ForEach-Object { [double]$_.PrivateMb })
    $threads = @($rows | ForEach-Object { [double]$_.Threads })
    $handles = @($rows | ForEach-Object { [double]$_.Handles })

    $summary = [pscustomobject]@{
        Process            = $ProcessName
        Samples            = $rows.Count
        CpuAvgPercent      = [Math]::Round(($cpuValues | Measure-Object -Average).Average, 1)
        CpuMaxPercent      = [Math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 1)
        CpuP95Percent      = Get-Percentile -Values $cpuValues -Ratio 0.95
        WorkingSetAvgMb    = [Math]::Round(($workingSet | Measure-Object -Average).Average, 1)
        WorkingSetMaxMb    = [Math]::Round(($workingSet | Measure-Object -Maximum).Maximum, 1)
        WorkingSetMinMb    = [Math]::Round(($workingSet | Measure-Object -Minimum).Minimum, 1)
        PrivateAvgMb       = [Math]::Round(($privateMem | Measure-Object -Average).Average, 1)
        PrivateMaxMb       = [Math]::Round(($privateMem | Measure-Object -Maximum).Maximum, 1)
        ThreadsAvg         = [Math]::Round(($threads | Measure-Object -Average).Average, 1)
        ThreadsMax         = [Math]::Round(($threads | Measure-Object -Maximum).Maximum, 1)
        HandlesMax         = [Math]::Round(($handles | Measure-Object -Maximum).Maximum, 1)
        # Разница «максимум минус минимум» рабочей памяти за наблюдение: рост
        # при неизменной сцене означает утечку или накопление слоёв.
        WorkingSetGrowthMb = [Math]::Round(
            ($workingSet | Measure-Object -Maximum).Maximum -
            ($workingSet | Measure-Object -Minimum).Minimum, 1)
    }

    if ($Json) {
        Write-Output ($summary | ConvertTo-Json)
    } else {
        Write-Host ""
        Write-Host "=== Сводка ==="
        Write-Host ("Измерений:          {0}" -f $summary.Samples)
        Write-Host ("CPU: среднее {0}%, максимум {1}%, p95 {2}%" -f
            $summary.CpuAvgPercent, $summary.CpuMaxPercent, $summary.CpuP95Percent)
        Write-Host ("Рабочая память:     среднее {0} МБ, максимум {1} МБ, рост {2} МБ" -f
            $summary.WorkingSetAvgMb, $summary.WorkingSetMaxMb, $summary.WorkingSetGrowthMb)
        Write-Host ("Приватная память:   среднее {0} МБ, максимум {1} МБ" -f
            $summary.PrivateAvgMb, $summary.PrivateMaxMb)
        Write-Host ("Потоки:             среднее {0}, максимум {1}" -f
            $summary.ThreadsAvg, $summary.ThreadsMax)
        Write-Host ("Дескрипторы (макс): {0}" -f $summary.HandlesMax)
        Write-Host ""
        Write-Host "Как читать: рост рабочей памяти при неизменной сцене — утечка;"
        Write-Host "CPU p95 близко к 100% — кадр упирается в процессор."
    }
} catch {
    Write-Warning "Монитор не смог собрать показатели: $($_.Exception.Message)"
    exit 3
}
