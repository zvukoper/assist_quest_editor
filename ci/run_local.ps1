<#
.SYNOPSIS
    Локальный прогон тех же проверок, что выполняет .github/workflows/ci.yml.

.DESCRIPTION
    Повторяет шаги CI на этой машине без GitHub Actions:
        Playwright install / Chromium install / Playwright smoke /
        Simulator <-> Editor contract / Quest Graph UI smoke /
        Web JavaScript syntax / .NET SDK / .NET build / domain tests /
        single-file publish.

    Итог повторяет логику final_gate: если упала любая обязательная проверка,
    скрипт завершается кодом 1 и печатает имя упавшей проверки.

    Каждый прогон перезаписывает отчёт MemoryAI/LOGS/CI_errors.md: сводка по всем
    проверкам, причина каждой ошибки и полный вывод упавших проверок.
    При успешном прогоне отчёт фиксирует это явно, без раздела с ошибками.

    Проверка публикации разрушительна: compile.ps1 останавливает запущенный
    AssistQuestEditor и удаляет bin, obj, publish и профиль WebView2.
    Поэтому она выполняется только при явном -IncludePublish.

.PARAMETER IncludePublish
    Дополнительно выполнить шаг single-file publish (compile.ps1 -NoLaunch).

.PARAMETER SkipInstall
    Не ставить Playwright и Chromium, а только проверить их наличие.

.PARAMETER NoNotify
    Не показывать Windows-уведомление о не пройденных проверках.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1 -IncludePublish
#>
param(
    [switch]$IncludePublish,
    [switch]$SkipInstall,
    [switch]$NoNotify
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

# Внешние инструменты (node, dotnet) пишут в UTF-8. Без этого консоль читает их
# вывод в кодировке системы, и русский текст в отчёте превращается в мусор.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
} catch {
    Write-Host "Не удалось переключить кодировку вывода на UTF-8: $($_.Exception.Message)" -ForegroundColor DarkGray
}

# Отчёт о проверках пишется и при успехе, и при ошибке: успешный прогон тоже
# должен быть зафиксирован, иначе нельзя отличить «всё хорошо» от «прогон не делали».
$script:CiReportPath = Join-Path $root 'MemoryAI\LOGS\CI_errors.md'

# Подключаем помощник уведомлений. Он необязателен: его отсутствие не должно
# ломать проверки, поэтому недоступность только отмечается в выводе.
$script:NotifyEnabled = $false
if (-not $NoNotify) {
    $toastHelper = Join-Path $PSScriptRoot 'WindowsToast.ps1'
    if (Test-Path -LiteralPath $toastHelper) {
        . $toastHelper
        if (Get-Command -Name 'Show-AssistQuestToast' -CommandType Function -ErrorAction SilentlyContinue) {
            $script:NotifyEnabled = $true
        } else {
            Write-Host 'Уведомления Windows недоступны: помощник не загрузился.' -ForegroundColor DarkGray
        }
    } else {
        Write-Host "Уведомления Windows недоступны: не найден $toastHelper" -ForegroundColor DarkGray
    }
}

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Name,
        [object]$Ok,
        [double]$Seconds,
        [string]$Detail = '',
        [object]$Output = $null
    )

    $null = $results.Add([pscustomobject]@{
        Name    = $Name
        Ok      = $Ok
        Seconds = $Seconds
        Detail  = $Detail
        Output  = $Output
    })
}

function Invoke-Check {
    param(
        [string]$Name,
        [scriptblock]$Body
    )

    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    $started = Get-Date

    # Сбрасываем код выхода, иначе проверка без внешнего процесса унаследует
    # значение от предыдущей команды и может ложно упасть или ложно пройти.
    $global:LASTEXITCODE = 0
    $ok = $true
    $detail = ''

    # Собираем весь вывод проверки в один список: *>&1 сливает Write-Host,
    # предупреждения и поток ошибок. Поэтому в отчёт попадают и текст
    # node-стека, и сообщения dotnet, а не только итоговая строка.
    $captured = New-Object System.Collections.Generic.List[string]
    $previousErrorActionPreference = $ErrorActionPreference

    try {
        # Continue обязателен: при *>&1 строки stderr внешнего процесса
        # превращаются в ErrorRecord, и с ErrorActionPreference = 'Stop' первая
        # же строка стека прерывает проверку, обрезая вывод до одной строки.
        # Явный throw при этом всё равно прерывает выполнение.
        $ErrorActionPreference = 'Continue'

        & $Body *>&1 | ForEach-Object {
            $record = $_

            if ($null -eq $record) {
                return
            }

            # Write-Host приходит как InformationRecord с HostInformationMessage:
            # сохраняем исходный цвет, иначе консоль потеряет подсветку.
            if ($record -is [System.Management.Automation.InformationRecord]) {
                $message = $record.MessageData

                if ($message -is [System.Management.Automation.HostInformationMessage]) {
                    $null = $captured.Add($message.Message)

                    if ($message.ForegroundColor -ne $null) {
                        Write-Host $message.Message -ForegroundColor $message.ForegroundColor
                    } else {
                        Write-Host $message.Message
                    }

                    return
                }
            }

            if ($record -is [System.Management.Automation.ErrorRecord]) {
                # Строки stderr внешнего процесса приходят как RemoteException,
                # и ToString() добавляет к ним многострочную обёртку PowerShell
                # (строка, знак, CategoryInfo). Для отчёта нужен только текст.
                $text = if ($record.Exception -is [System.Management.Automation.RemoteException]) {
                    $record.Exception.Message
                } else {
                    $record.ToString()
                }

                $null = $captured.Add($text)
                Write-Host $text -ForegroundColor Red
                return
            }

            $plain = $record.ToString()
            $null = $captured.Add($plain)
            Write-Host $plain
        }

        if ($LASTEXITCODE -ne 0) {
            throw "код выхода $LASTEXITCODE"
        }
    } catch {
        $ok = $false
        $detail = $_.Exception.Message
        $null = $captured.Add("ОШИБКА: $detail")
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $seconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    Add-Result -Name $Name -Ok $ok -Seconds $seconds -Detail $detail -Output $captured

    if ($ok) {
        Write-Host "$Name : успех ($seconds s)" -ForegroundColor Green
    } else {
        Write-Host "$Name : ОШИБКА ($seconds s) — $detail" -ForegroundColor Red
    }
}

function Write-CiReport {
    <#
        Пишет отчёт о прогоне в MemoryAI/LOGS/CI_errors.md.

        Отчёт формируется и при успехе, и при ошибке: так видно и факт успешного
        прогона, и причину падения с полным выводом упавшей проверки.
    #>
    param(
        [string]$Status,
        [AllowEmptyString()][string]$FailedNames = '',
        [string]$Commit = '',
        [string]$Branch = ''
    )

    $lines = New-Object System.Collections.Generic.List[string]

    $lines.Add('# Отчёт локальных проверок CI')
    $lines.Add('')
    $lines.Add('Файл перезаписывается каждым прогоном `ci/run_local.ps1`.')
    $lines.Add('CI его не проверяет и не загружает: это диагностический материал для агента.')
    $lines.Add('')
    $lines.Add("- Время: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    if (-not [string]::IsNullOrWhiteSpace($Branch)) { $lines.Add("- Ветка: $Branch") }
    if (-not [string]::IsNullOrWhiteSpace($Commit)) { $lines.Add("- Commit: $Commit") }
    $lines.Add("- Итог: $Status")
    if (-not [string]::IsNullOrWhiteSpace($FailedNames)) {
        $lines.Add("- Упавшие проверки: $FailedNames")
    }
    $lines.Add('')
    $lines.Add('## Сводка')
    $lines.Add('')
    $lines.Add('| Проверка | Результат | Время, с | Причина |')
    $lines.Add('|---|---|---|---|')

    foreach ($result in $results) {
        if ($result.Ok -eq $true) {
            $state = 'успех'
        } elseif ($result.Ok -eq $false) {
            $state = 'ОШИБКА'
        } else {
            $state = 'пропущено'
        }

        # Вертикальная черта сломала бы таблицу Markdown.
        $reason = ($result.Detail -replace '\|', '\|')
        $lines.Add("| $($result.Name) | $state | $($result.Seconds) | $reason |")
    }

    $failedResults = @($results | Where-Object { $_.Ok -eq $false })

    if ($failedResults.Count -eq 0) {
        $lines.Add('')
        $lines.Add('## Ошибки')
        $lines.Add('')
        $lines.Add('Ошибок нет: все обязательные проверки пройдены.')
    } else {
        foreach ($result in $failedResults) {
            $lines.Add('')
            $lines.Add("## ОШИБКА: $($result.Name)")
            $lines.Add('')
            $lines.Add("Причина: $($result.Detail)")
            $lines.Add('')
            $lines.Add('```text')

            $output = @($result.Output)
            if ($output.Count -eq 0) {
                $lines.Add('(вывод проверки пуст)')
            } else {
                foreach ($line in $output) {
                    $lines.Add([string]$line)
                }
            }

            $lines.Add('```')
        }
    }

    $directory = Split-Path -Parent $script:CiReportPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # BOM не нужен: файл читают GitHub и агент, оба ожидают UTF-8.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($script:CiReportPath, $lines, $utf8NoBom)

    return $script:CiReportPath
}

Write-Host '=== Локальный прогон проверок CI ===' -ForegroundColor Cyan
Write-Host "Репозиторий: $root"
Write-Host "Node.js: $(& node --version 2>$null)"
Write-Host "PowerShell: $($PSVersionTable.PSVersion)"

$global:LASTEXITCODE = 0
$gitBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
$gitCommit = (& git rev-parse --short HEAD 2>$null | Out-String).Trim()
if (-not [string]::IsNullOrWhiteSpace($gitBranch)) { Write-Host "Ветка: $gitBranch" }
if (-not [string]::IsNullOrWhiteSpace($gitCommit)) { Write-Host "Commit: $gitCommit" }

if (-not $IncludePublish) {
    Write-Host 'Шаг single-file publish пропускается: он останавливает запущенный AssistQuestEditor и удаляет bin/obj/publish.' -ForegroundColor Yellow
    Write-Host 'Для полного прогона добавьте -IncludePublish.' -ForegroundColor Yellow
}

# Шаг 1: установка Playwright (в CI это отдельный шаг с continue-on-error).
Invoke-Check -Name 'Установить Playwright' -Body {
    if (Test-Path -LiteralPath '.\node_modules\playwright') {
        Write-Host 'Playwright уже установлен локально.'
        return
    }

    if ($SkipInstall) {
        throw 'Playwright не установлен, а указан -SkipInstall.'
    }

    npm install --no-save playwright@1.57.0
    if ($LASTEXITCODE -ne 0) {
        throw "npm install завершился с кодом $LASTEXITCODE"
    }
}

# Шаг 2: установка Chromium (в CI это отдельный шаг с continue-on-error).
Invoke-Check -Name 'Установить Chromium' -Body {
    $cache = Join-Path $env:LOCALAPPDATA 'ms-playwright'
    $installed = @(Get-ChildItem -LiteralPath $cache -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue)

    if ($installed.Count -gt 0) {
        Write-Host "Chromium уже установлен: $($installed[0].Name)"
        return
    }

    if ($SkipInstall) {
        throw 'Chromium не установлен, а указан -SkipInstall.'
    }

    npx playwright install chromium
    if ($LASTEXITCODE -ne 0) {
        throw "npx playwright install завершился с кодом $LASTEXITCODE"
    }
}

# Шаг 3: базовый smoke реального browser engine.
Invoke-Check -Name 'Playwright + Chromium smoke' -Body {
    node ci/playwright_smoke.mjs
}

# Шаг 4: контракт camelCase между Simulator и Editor.
Invoke-Check -Name 'Контракт simulator <-> editor' -Body {
    node ci/selection_context_smoke.mjs
}

# Шаг 5: smoke реального editor.js, включая контекстные меню Quest Graph.
Invoke-Check -Name 'Quest Graph Playwright smoke' -Body {
    node ci/quest_graph_smoke.mjs
}

# Шаг 6: синтаксис web JavaScript.
Invoke-Check -Name 'Синтаксис web JavaScript' -Body {
    $files = @(
        '.\src\AssistQuestEditor.App\Web\main.js',
        '.\src\AssistQuestEditor.App\Web\editor.js',
        '.\src\AssistQuestEditor.App\Web\simulator.js'
    )

    foreach ($file in $files) {
        node --check $file
        if ($LASTEXITCODE -ne 0) {
            throw "node --check не прошёл: $file"
        }
    }
}

# Шаг 7: наличие .NET SDK.
Invoke-Check -Name '.NET SDK' -Body {
    dotnet --version
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet не найден в PATH.'
    }
}

# Шаг 8: сборка всех исходных проектов.
Invoke-Check -Name 'Сборка проектов .NET' -Body {
    $projects = @(Get-ChildItem -Path src -Recurse -File -Filter *.csproj)
    if ($projects.Count -eq 0) {
        throw 'В src нет C# проектов.'
    }

    foreach ($project in $projects) {
        Write-Host "-> $($project.Name)" -ForegroundColor DarkGray

        dotnet restore $project.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "restore не удался: $($project.Name)"
        }

        dotnet build $project.FullName --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "build не удался: $($project.Name)"
        }
    }
}

# Шаг 9: тесты домена с TRX, как в CI.
Invoke-Check -Name 'Тесты домена' -Body {
    $tests = @(Get-ChildItem -Path tests -Recurse -File -Filter *.csproj)
    if ($tests.Count -eq 0) {
        throw 'В tests нет C# тестовых проектов.'
    }

    $resultDir = Join-Path $root 'ci-results\domain-tests'
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null

    foreach ($test in $tests) {
        Write-Host "-> $($test.Name)" -ForegroundColor DarkGray

        dotnet restore $test.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "restore не удался: $($test.Name)"
        }

        $trxName = $test.BaseName + '.trx'
        dotnet test $test.FullName --configuration Release --no-restore --logger "trx;LogFileName=$trxName" --results-directory $resultDir
        if ($LASTEXITCODE -ne 0) {
            throw "тесты не прошли: $($test.Name)"
        }
    }

    Write-Host "TRX: $resultDir" -ForegroundColor DarkGray
}

# Шаг 10: single-file публикация. Разрушительна, поэтому только по запросу.
if ($IncludePublish) {
    Invoke-Check -Name 'Single-file publish' -Body {
        powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\compile.ps1 -NoLaunch
        if ($LASTEXITCODE -ne 0) {
            throw "compile.ps1 завершился с кодом $LASTEXITCODE"
        }

        $publish = Join-Path $root 'bin\Release\net10.0-windows\win-x64\publish'
        $files = @(Get-ChildItem -LiteralPath $publish -File -Force)

        if ($files.Count -ne 1 -or $files[0].Extension -ne '.exe') {
            Write-Host 'В каталоге публикации находятся:' -ForegroundColor Red
            $files | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
            throw 'compile.ps1 не создал ровно один EXE.'
        }

        Write-Host "Single-file публикация: $($files[0].FullName)" -ForegroundColor Green
    }
} else {
    Add-Result -Name 'Single-file publish' -Ok $null -Seconds 0 -Detail 'пропущено' -Output @('Шаг пропущен: нужен -IncludePublish.')
    Write-Host ''
    Write-Host 'Single-file publish : пропущено (нужен -IncludePublish)' -ForegroundColor Yellow
}

# Дополнительно: проверка памяти. В CI не входит, но полезна локально.
Invoke-Check -Name 'Проверка памяти (вне CI)' -Body {
    node ci/validate_memory.mjs
}

Write-Host ''
Write-Host '=== Итог локального CI ===' -ForegroundColor Cyan

foreach ($result in $results) {
    if ($result.Ok -eq $true) {
        $state = 'успех'
        $color = 'Green'
    } elseif ($result.Ok -eq $false) {
        $state = 'ОШИБКА'
        $color = 'Red'
    } else {
        $state = 'пропущено'
        $color = 'Yellow'
    }

    Write-Host ("  {0,-38} {1,10} {2,7} s" -f $result.Name, $state, $result.Seconds) -ForegroundColor $color
}

$failed = @($results | Where-Object { $_.Ok -eq $false })
if ($failed.Count -gt 0) {
    $failedNames = ($failed | ForEach-Object { $_.Name }) -join ', '

    Write-Host ''
    Write-Host ('Локальный CI завершён с ошибками: ' + $failedNames) -ForegroundColor Red

    if ($script:NotifyEnabled) {
        # Уведомление может не дойти (отключены уведомления в системе и т.п.);
        # это не влияет на код возврата.
        $sent = Show-AssistQuestToast `
            -Title 'Проверки не пройдены' `
            -Body ("Локальный CI: " + $failedNames + "`nСборка отменена.")

        if ($sent) {
            Write-Host 'Показано уведомление Windows.' -ForegroundColor DarkGray
        }
    }

    $reportPath = Write-CiReport -Status 'ОШИБКА' -FailedNames $failedNames `
        -Commit $gitCommit -Branch $gitBranch
    Write-Host "Отчёт об ошибках: $reportPath" -ForegroundColor Yellow

    exit 1
}

Write-Host ''
Write-Host 'Локальный CI: все обязательные проверки успешны.' -ForegroundColor Green

$reportPath = Write-CiReport -Status 'успех' -Commit $gitCommit -Branch $gitBranch
Write-Host "Отчёт о проверках: $reportPath" -ForegroundColor DarkGray

exit 0
