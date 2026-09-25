<#
.SYNOPSIS
    Локальный прогон проверок CI: быстрый гейт, приёмочный пакет или ночной набор.

.DESCRIPTION
    Проверки разбиты на три НАБОРА, потому что держать все 49 проверок в одном
    обязательном прогоне бессмысленно: R32 (контроль мутаций) занимает ~7 минут,
    публикация разрушительна, а проверка заставки требует запуска exe. Раньше это
    всё шло в одном гейте, из-за чего его никто не ждал, и он перестал быть гейтом.

    -Suite fast       (по умолчанию) — то, что обязано проходить перед каждым
                      pull. Около минуты: установка, smoke групп (Simulator,
                      Editor, Scene, Location, Dynamic Event, Road/Map),
                      целостность контента, репутация, синтаксис web, сборка .NET
                      и тесты домена.
    -Suite acceptance — ручная приёмка перед выпуском: публикация, единственный
                      экземпляр, архив/импорт/экспорт, хранение мира, свойства
                      ресурсов, создание, автосохранение, инвентарь, иконки,
                      читаемость диалогов, заставка. Шаг публикации включён всегда:
                      без него приёмка проверяла бы прошлую сборку.
    -Suite nightly    — тяжёлое и внерелизное: контроль мутаций R32, ручные
                      перекрёстки, визуализация Location, проверка памяти.

    Группы smoke прогоняются через ci/lib/run_suite.mjs: одна сессия Chromium на
    группу вместо браузера на каждую проверку. Состав групп — в ci/suites.json.

    Итог повторяет логику final_gate: если упала любая обязательная проверка,
    скрипт завершается кодом 1 и печатает имя упавшей проверки.

    Каждый прогон перезаписывает отчёт MemoryAI/LOGS/CI_errors.md: сводка по всем
    проверкам, причина каждой ошибки и полный вывод упавших проверок.
    При успешном прогоне отчёт фиксирует это явно, без раздела с ошибками.

    Шаг публикации разрушителен: compile.ps1 останавливает запущенный
    AssistQuestEditor и удаляет bin, obj, publish и профиль WebView2. Поэтому он
    входит только в -Suite acceptance (или явно через -IncludePublish).

.PARAMETER Suite
    Набор проверок: fast (по умолчанию), acceptance или nightly.

.PARAMETER IncludePublish
    Выполнить шаг single-file publish и в наборе fast/nightly.

.PARAMETER SkipInstall
    Не ставить Playwright и Chromium, а только проверить их наличие.

.PARAMETER NoNotify
    Не показывать Windows-уведомление о не пройденных проверках.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1 -Suite acceptance

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1 -Suite nightly
#>
param(
    [ValidateSet('fast', 'acceptance', 'nightly')]
    [string]$Suite = 'fast',
    [switch]$IncludePublish,
    [switch]$SkipInstall,
    [switch]$NoNotify
)

# Приёмка без публикации проверяла бы ПРОШЛУЮ сборку, поэтому там шаг включён
# всегда и не отключается: это не удобство, а условие достоверности.
if ($Suite -eq 'acceptance') { $IncludePublish = $true }

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

# Состояние локального прогона читает расширение CI Monitor: оно показывает
# номер проверки, статус и число пройденных проверок. Файл — единственный канал
# данных, поэтому расширению не важно, откуда запущен скрипт.
#
# Файл лежит ВНЕ MemoryAI/LOGS намеренно: папка логов очищается после успешной
# публикации, а здесь хранится номер последней проверки. Иначе после каждой
# успешной сборки нумерация начиналась бы заново с #1.
$script:StateDir = Join-Path $root '.ci-state'
$script:LocalStatePath = Join-Path $script:StateDir 'local-run.json'

# Признак активности редактора. Расширение обновляет его каждую секунду.
# Нужен, чтобы не было двух уведомлений об одном прогоне: если редактор открыт,
# уведомляет расширение, а штатное уведомление скрипта подавляется.
$script:HeartbeatPath = Join-Path $script:StateDir 'monitor.heartbeat'

# Общее число проверок в прогоне — нужно для плашки расширения CI Monitor.
# Считается по фактическому набору и НЕ задаётся числом: прежде здесь стояла
# константа, и после каждой правки списка она расходилась с действительностью
# (доходило до 11 при 16 реальных проверках), а плашка показывала неверный
# процент. Значение выводится из списка ниже.
$script:TotalChecks = 0

# Набор проверок: имя нужно отчёту и плашке («Локальный прогон (acceptance)»).
$script:Suite = $Suite

# Подавление уведомления скрипта. Вызывающий скрипт может взять уведомление на
# себя: pull.ps1 показывает одно уведомление с учётом признака активности
# редактора, поэтому run_local.ps1 в этом случае не уведомляет вообще.
$script:SuppressNotify = $NoNotify

# Номер локального прогона. Продолжает нумерацию прошлого прогона, поэтому
# каждый запуск получает собственный номер, как проверка в GitHub.
$script:LocalRunNumber = 0
$script:LocalRunStartedAt = $null
$script:LocalChecksCompleted = 0
$script:LocalFailedNames = New-Object System.Collections.Generic.List[string]

# Подключаем помощник уведомлений и проверки активности редактора. Он
# необязателен: его отсутствие не должно ломать проверки, поэтому недоступность
# только отмечается в выводе и отключает уведомления.
$script:NotifyEnabled = $false
$script:HelperEnabled = $false
$toastHelper = Join-Path $PSScriptRoot 'WindowsToast.ps1'
if (Test-Path -LiteralPath $toastHelper) {
    . $toastHelper
    if (Get-Command -Name 'Test-AssistQuestEditorMonitorActive' -CommandType Function -ErrorAction SilentlyContinue) {
        $script:HelperEnabled = $true
    }
    if (-not $NoNotify -and (Get-Command -Name 'Show-AssistQuestToast' -CommandType Function -ErrorAction SilentlyContinue)) {
        $script:NotifyEnabled = $true
    } elseif (-not $NoNotify) {
        Write-Host 'Уведомления Windows недоступны: помощник не загрузился.' -ForegroundColor DarkGray
    }
} elseif (-not $NoNotify) {
    Write-Host "Уведомления Windows недоступны: не найден $toastHelper" -ForegroundColor DarkGray
}

$results = New-Object System.Collections.Generic.List[object]

function Write-LocalRunState {
    <#
        Публикация состояния локального прогона для расширения CI Monitor.

        Формат совпадает с сериализацией в расширении, поэтому плашка показывает
        локальный прогон теми же средствами, что и проверку GitHub.

        Файл пишется атомарно (временный файл + Move-Item): расширение читает его
        параллельно, и частично записанный JSON оно разобрать не сможет.
    #>
    param(
        [string]$Status,
        [string]$CurrentCheck = '',
        [string]$ReportPath = '',
        [string]$Branch = '',
        [string]$Commit = '',
        [string]$Title = ''
    )

    $payload = [ordered]@{
        runNumber       = $script:LocalRunNumber
        status          = $Status
        totalChecks     = $script:TotalChecks
        completedChecks = $script:LocalChecksCompleted
        updatedAt       = (Get-Date).ToUniversalTime().ToString('o')
    }

    if (-not [string]::IsNullOrWhiteSpace($CurrentCheck)) { $payload.currentCheck = $CurrentCheck }
    if ($script:LocalFailedNames.Count -gt 0) { $payload.failedChecks = @($script:LocalFailedNames) }
    if ($script:LocalRunStartedAt) { $payload.startedAt = $script:LocalRunStartedAt }
    if ($Status -in @('success', 'failure', 'cancelled')) {
        $payload.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    if (-not [string]::IsNullOrWhiteSpace($Branch)) { $payload.branch = $Branch }
    if (-not [string]::IsNullOrWhiteSpace($Commit)) { $payload.commit = $Commit }
    if (-not [string]::IsNullOrWhiteSpace($Title)) { $payload.title = $Title }
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) { $payload.reportPath = $ReportPath }

    $directory = Split-Path -Parent $script:LocalStatePath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $payload | ConvertTo-Json -Depth 4
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $temp = "$($script:LocalStatePath).tmp"

    try {
        [System.IO.File]::WriteAllText($temp, $json, $utf8NoBom)
        Move-Item -LiteralPath $temp -Destination $script:LocalStatePath -Force
    } catch {
        # Диагностический канал не должен ломать проверки.
        Write-Host "Не удалось записать состояние локального прогона: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

function Test-EditorMonitorActive {
    <#
        Проверка, что расширение CI Monitor активно и сейчас обновляет признак
        активности. Реализация вынесена в ci/WindowsToast.ps1, чтобы pull.ps1 и
        этот скрипт не дублировали одну и ту же логику.
    #>
    return (Test-AssistQuestEditorMonitorActive -RepositoryRoot $root)
}

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

    # Сообщаем расширению, что началась следующая проверка: плашка показывает
    # имя текущей проверки и уже достигнутый прогресс.
    Write-LocalRunState -Status 'in_progress' -CurrentCheck $Name `
        -Branch $script:GitBranch -Commit $script:GitCommit -Title $script:RunTitle

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

    # Прогресс считается по завершённым проверкам: неуспешная проверка тоже
    # завершена, поэтому она увеличивает счётчик, но попадает в список ошибок.
    $script:LocalChecksCompleted++

    if ($ok) {
        Write-Host "$Name : успех ($seconds s)" -ForegroundColor Green
    } else {
        Write-Host "$Name : ОШИБКА ($seconds s) — $detail" -ForegroundColor Red
        $null = $script:LocalFailedNames.Add($Name)
    }

    Write-LocalRunState -Status 'in_progress' -Branch $script:GitBranch `
        -Commit $script:GitCommit -Title $script:RunTitle
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
Write-Host "Набор: $($script:Suite)"
Write-Host "Node.js: $(& node --version 2>$null)"
Write-Host "PowerShell: $($PSVersionTable.PSVersion)"

$global:LASTEXITCODE = 0
$script:GitBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
$script:GitCommit = (& git rev-parse --short HEAD 2>$null | Out-String).Trim()
if (-not [string]::IsNullOrWhiteSpace($script:GitBranch)) { Write-Host "Ветка: $script:GitBranch" }
if (-not [string]::IsNullOrWhiteSpace($script:GitCommit)) { Write-Host "Commit: $script:GitCommit" }

# Заголовок прогона: набор, имя ветки и коммит. Так плашка локального режима
# отличается от предыдущего прогона, и в уведомлении видно, что проверялось.
# Join-String доступен только в PowerShell 7, поэтому используем -join.
$titleParts = @("набор $($script:Suite)", $script:GitBranch, $script:GitCommit) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$script:RunTitle = $titleParts -join ' · '
if ([string]::IsNullOrWhiteSpace($script:RunTitle)) { $script:RunTitle = 'Локальный прогон' }

# Номер прогона продолжает нумерацию прошлого запуска. Это позволяет расширению
# уведомлять один раз на прогон, как оно делает для проверок GitHub.
try {
    if (Test-Path -LiteralPath $script:LocalStatePath) {
        $previous = Get-Content -LiteralPath $script:LocalStatePath -Raw | ConvertFrom-Json
        $previousNumber = [int]$previous.runNumber
        if ($previousNumber -gt 0) { $script:LocalRunNumber = $previousNumber }
    }
} catch {
    Write-Host "Не удалось прочитать прошлое состояние прогона: $($_.Exception.Message)" -ForegroundColor DarkGray
}

$script:LocalRunNumber++
$script:LocalRunStartedAt = (Get-Date).ToUniversalTime().ToString('o')
Write-Host "Локальная проверка #$($script:LocalRunNumber)" -ForegroundColor DarkGray

Write-LocalRunState -Status 'in_progress' -Branch $script:GitBranch -Commit $script:GitCommit -Title $script:RunTitle

# Признак активности редактора нужен и на старте: если расширение работает,
# уведомление о падении покажет оно, а не скрипт.
$script:EditorMonitorActive = Test-EditorMonitorActive
if ($script:EditorMonitorActive) {
    Write-Host 'Расширение CI Monitor активно: уведомление покажет оно.' -ForegroundColor DarkGray
}

# ============================================================================
# Список проверок набора
# ============================================================================
#
# Проверки описываются данными, а не последовательными вызовами: так общее число
# проверок набора (TotalChecks для плашки расширения) получается ИЗ СПИСКА и не
# может разойтись с ним. Раньше число стояло константой и расходилось после
# каждой правки — плашка показывала неверный процент.
#
# Порядок регистрации = порядок выполнения. Проверки, общие для всех наборов
# (окружение), идут первыми; сборка .NET обязана идти до тестов домена.

$checkList = New-Object System.Collections.Generic.List[object]

function New-Check {
    param(
        [string]$Id,
        [string]$Name,
        [string[]]$Suites,
        [scriptblock]$Body
    )

    $null = $checkList.Add([pscustomobject]@{
        Id     = $Id
        Name   = $Name
        Suites = $Suites
        Body   = $Body
    })
}

# Публикация и проба единственного экземпляра входят в приёмку ВСЕГДА (без
# публикации приёмка проверяла бы прошлую сборку), а в остальные наборы —
# только по явному -IncludePublish.
$publishSuites = if ($IncludePublish) { @('fast', 'acceptance', 'nightly') } else { @('acceptance') }

# --- Окружение: нужно любому набору -----------------------------------------

New-Check -Id 'playwright' -Name 'Установить Playwright' -Suites @('fast', 'acceptance', 'nightly') -Body {
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

New-Check -Id 'chromium' -Name 'Установить Chromium' -Suites @('fast', 'acceptance', 'nightly') -Body {
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

# --- Быстрый гейт ------------------------------------------------------------

# Базовый smoke реального browser engine.
New-Check -Id 'playwrightSmoke' -Name 'Playwright + Chromium smoke' -Suites @('fast') -Body {
    node ci/playwright_smoke.mjs
}

# Группы smoke. Одна сессия Chromium на группу: прогон через
# ci/lib/run_suite.mjs, состав групп — ci/suites.json. Групповой прогон не
# останавливается на первой упавшей проверке, поэтому за один запуск видно ВСЕ
# расхождения группы, как это делал набор отдельных шагов.
New-Check -Id 'simulator' -Name 'Simulator smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs simulator
}

New-Check -Id 'editor' -Name 'Editor smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs editor
}

New-Check -Id 'scene' -Name 'Scene smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs scene
}

New-Check -Id 'location' -Name 'Location smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs location
}

New-Check -Id 'dynamicEvent' -Name 'Dynamic Event smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs dynamicEvent
}

New-Check -Id 'map' -Name 'Road/Map smoke' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs map
}

# Целостность контента: граф квестов собирается и кодом, и правкой JSON, поэтому
# проверяется отдельно от сборки — несвязанный Input или ссылка на
# несуществующую сцену компилируются, но Runtime молча зависает на такой ноде.
New-Check -Id 'campaignIntegrity' -Name 'Campaign integrity' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs campaignIntegrity
}

New-Check -Id 'questIntegrity' -Name 'Quest graph integrity' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs questIntegrity
}

# Правила репутации на реальном контенте: юнит-тесты собирают сцену кодом, а
# здесь проверяется сам .aqquest — повторная покупка, цена и порог репутации.
New-Check -Id 'reputation' -Name 'Reputation flow' -Suites @('fast') -Body {
    node ci/lib/run_suite.mjs reputation
}

# Синтаксис web JavaScript. Список обязан покрывать ВСЕ скрипты, которые
# подключает WebView: пропущенный файл не ловит даже ошибка синтаксиса, и панель
# просто остаётся пустой (именно так locationEditor.js был сломан с самого
# создания редактора — лишний `+` перед `:` в тернарном операторе).
New-Check -Id 'webSyntax' -Name 'Синтаксис web JavaScript' -Suites @('fast') -Body {
    $files = @(
        '.\src\AssistQuestEditor.App\Web\main.js',
        '.\src\AssistQuestEditor.App\Web\editor.js',
        '.\src\AssistQuestEditor.App\Web\sceneEditor.js',
        '.\src\AssistQuestEditor.App\Web\dialogueWorkspace.js',
        '.\src\AssistQuestEditor.App\Web\locationEditor.js',
        '.\src\AssistQuestEditor.App\Web\dynamicEventEditor.js',
        '.\src\AssistQuestEditor.App\Web\interface.js',
        '.\src\AssistQuestEditor.App\Web\simulator.js',
        '.\src\AssistQuestEditor.App\Web\inventory.js',
        '.\src\AssistQuestEditor.App\Web\playerPanels.js',
        '.\src\AssistQuestEditor.App\Web\junctions.js',
        '.\src\AssistQuestEditor.App\Web\cityBoundaries.js',
        '.\src\AssistQuestEditor.App\Web\web_log.js'
    )

    foreach ($file in $files) {
        node --check $file
        if ($LASTEXITCODE -ne 0) {
            throw "node --check не прошёл: $file"
        }
    }
}

# Сборка всех исходных проектов. Отдельной проверки «.NET SDK» больше нет: если
# dotnet отсутствует, эта проверка падает с внятной ошибкой, а `dotnet --version`
# дублировал её и добавлял лишний шаг в замер времени набора.
New-Check -Id 'dotnetBuild' -Name 'Сборка проектов .NET' -Suites @('fast') -Body {
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

# Тесты домена с TRX, как в CI.
New-Check -Id 'domainTests' -Name 'Тесты домена' -Suites @('fast') -Body {
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

# --- Приёмка -----------------------------------------------------------------

# Архив .aqezip: упаковка сжимает, манифест читается БЕЗ распаковки, пути
# защищены от выхода за пределы папки, импорт идёт через временную папку.
# Демо-мир проверяется ОТДЕЛЬНО и внутри той же группы: там стережётся
# ПОСТАВЛЯЕМЫЙ файл, а здесь — код упаковки и распаковки, который легко
# расходится сам с собой.
New-Check -Id 'archive' -Name 'Archive smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs archive
}

# Импорт кампании и квеста в родителя — ФАЙЛОВАЯ операция: проверяется
# фактическое дерево на диске и то, что соседние ресурсы уцелели.
New-Check -Id 'import' -Name 'Import smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs import
}

# Экспорт мира и кампании: папкой и архивом. Проверяется настоящим запуском с
# `--export-probe` — тем же кодом, что в меню, — и сравнением деревьев файлов.
New-Check -Id 'export' -Name 'Export smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs export
}

# Хранение контента: единое дерево миров, пользовательская папка в Документах,
# автоматическая общая кампания с демо-квестом и первичная настройка ДО главного
# окна. Проверка статическая: перепутанный сегмент пути компилируется без ошибок.
New-Check -Id 'worldStorage' -Name 'World storage smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs worldStorage
}

# Свойства мира и кампании. Проверяется НАСТОЯЩИЙ файл ресурса: кампания хранит
# в одном файле и описание, и квесты, и стартовые условия мира, поэтому потеря
# игрового поля при правке описания — самый дорогой дефект здесь.
New-Check -Id 'resourceProperties' -Name 'Resource properties smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs resourceProperties
}

# Создание мира и кампании. Проверяется СТРУКТУРА на диске: мир без общей
# кампании — мир, в котором нечего создавать.
New-Check -Id 'createResources' -Name 'Create/import resources smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs createResources
}

# Автосохранение прохождения. Проверка обязана быть ПОВЕДЕНЧЕСКОЙ: прежний
# дефект (запись в несуществующий каталог ВНЕ дерева миров) был невидим для
# чтения исходников — код выглядел корректным, а запись падала.
New-Check -Id 'autosave' -Name 'Autosave smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs autosave
}

# Окно игрока: сетка инвентаря слева, персонаж и репутация справа. Размер ячейки
# задают ДВА места (CSS страницы и правило раскладки окна), и расхождение видно
# только сравнением — иначе содержимое либо не влезает, либо остаётся пустая полоса.
New-Check -Id 'inventory' -Name 'Inventory smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs inventory
}

# Иконки. У внутренних типов файлов нет собственных специконок, и все они
# получают иконку приложения. Проверка поведенческая: ICO собирается тем же
# кодом, что окна и ассоциации, и кадры извлекаются из полученных байтов.
New-Check -Id 'icon' -Name 'Icon smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs icon
}

# Читаемость текста в тёмных диалогах. Проверка ЗАМЕРЯЮЩАЯ: цвета заданы в коде
# верно, но у выключенной кнопки WinForms рисует текст системным серым, и на
# тёмном фоне он становится нечитаемым (замерено 2,1:1 при минимуме 4,5:1).
New-Check -Id 'visualRegression' -Name 'Visual regression smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs visualRegression
}

# Заставка запуска и модальные диалоги. Баг: при удалённой пользовательской
# папке диалог первичной настройки открывался ПОД заставкой (она TopMost и висит
# поверх всего), и ни ввести псевдоним, ни отменить было нельзя.
New-Check -Id 'splash' -Name 'Splash smoke' -Suites @('acceptance') -Body {
    node ci/lib/run_suite.mjs splash
}

# Single-file публикация. Разрушительна: compile.ps1 останавливает запущенный
# AssistQuestEditor и удаляет bin, obj, publish и профиль WebView2.
New-Check -Id 'publish' -Name 'Single-file publish' -Suites $publishSuites -Body {
    powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\compile.ps1 -NoLaunch
    if ($LASTEXITCODE -ne 0) {
        throw "compile.ps1 завершился с кодом $LASTEXITCODE"
    }

    $publish = Join-Path $root 'bin\Release\net10.0-windows\win-x64\publish'
    $files = @(Get-ChildItem -LiteralPath $publish -File -Force)
    $dataDir = Join-Path $publish 'data'
    $manifest = Join-Path $dataDir 'data-manifest.json'

    # Рядом с EXE допускаются папка ресурсов и отчёты сборки. Папка нужна
    # потому, что содержимое single-file распаковывается в невидимый кэш,
    # а отчёты пишут проверка целостности и сидер демо-мира (приложение —
    # WinExe без консоли, поэтому его вердикт доходит только файлом). Любой
    # другой файл — остаток прошлой сборки, который легко принять за данные.
    $allowedPublishExtra = @('data-verify-report.txt', 'demo-world-report.txt')
    $exeFiles = @($files | Where-Object { $_.Extension -eq '.exe' })
    $unexpected = @($files | Where-Object {
        $_.Extension -ne '.exe' -and $allowedPublishExtra -notcontains $_.Name
    })

    if ($exeFiles.Count -ne 1 -or $unexpected.Count -gt 0) {
        Write-Host 'В каталоге публикации находятся:' -ForegroundColor Red
        $files | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
        throw 'compile.ps1 не создал ровно один EXE.'
    }

    # Ресурсы обязаны лежать рядом с EXE с манифестом: именно из этой папки
    # приложение читает данные, а не из кэша распаковки single-file.
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "Ресурсы публикации не опубликованы: не найден $manifest"
    }

    # Согласованность публикации проверяется ОТДЕЛЬНЫМ скриптом, и это важно:
    # дефект («манифест записал DemoWorld.aqezip нулевой длины») был ГОНКОЙ.
    # Приложение собрано как WinExe, поэтому `& $exe` не ждёт его завершения, и
    # манифест читал архив в момент, когда упаковщик только успел усечь файл.
    # Гонку нельзя проверить через саму сборку — упаковщик иногда успевает
    # дописать файл, и дефектный код даёт то верный манифест, то пустой
    # (проверено: мутация с несинхронным вызовом проходила насквозь). Поэтому
    # утверждение «манифест описывает диск» проверяется на ГОТОВОМ каталоге и
    # детерминированно, вместе с наличием отчёта проверки целостности.
    & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File ci/check_publish_resources.ps1 -PublishDirectory $publish
    if ($LASTEXITCODE -ne 0) {
        throw "Проверка согласованности публикации не пройдена: код $LASTEXITCODE"
    }

    $resourceCount = @(Get-ChildItem -LiteralPath $dataDir -Recurse -File).Count
    Write-Host "Single-file публикация: $($exeFiles[0].FullName)" -ForegroundColor Green
    Write-Host "Ресурсы рядом с EXE: файлов $resourceCount, манифест подтверждён." -ForegroundColor Green
}

# Единственный экземпляр приложения и режим проверок.
# Проверка идёт после публикации: она запускает exe, и на файле прошлой сборки
# результат был бы недостоверным. Старый exe не знает ключа -citest, принимает
# его за путь к файлу и запускается как обычный пользовательский старт — вместе
# с установочными сценариями. Поэтому проба сама сообщает «пропущено», если exe
# отсутствует, собран из другого коммита или имеет другую версию.
New-Check -Id 'singleInstance' -Name 'Single instance probe' -Suites $publishSuites -Body {
    & powershell -NoProfile -ExecutionPolicy Bypass -File ci/single_instance_probe.ps1
    if ($LASTEXITCODE -ne 0) {
        throw "single instance probe не прошёл: код $LASTEXITCODE"
    }
}

# --- Ночной набор ------------------------------------------------------------

# Негативные контроли: КАЖДАЯ новая проверка обязана упасть на возвращённом
# дефекте. Мутация вносится во временную копию дерева — рабочие файлы не
# трогаются, а базовая линия проверяется тут же. Самый долгий шаг из всех
# (около семи минут), поэтому он вынесен из быстрого гейта: в гейте проверка,
# которую никто не ждёт, перестаёт быть гейтом.
New-Check -Id 'mutation' -Name 'R32 mutation controls' -Suites @('nightly') -Body {
    node ci/lib/run_suite.mjs mutation
}

# Тяжёлые проверки карты: ручной разбор перекрёстков и визуализация Location.
New-Check -Id 'heavyMap' -Name 'Heavy map smoke' -Suites @('nightly') -Body {
    node ci/lib/run_suite.mjs heavyMap
}

# Проверка памяти. В CI не входит, но полезна локально.
New-Check -Id 'memory' -Name 'Проверка памяти (вне CI)' -Suites @('nightly') -Body {
    node ci/lib/run_suite.mjs memory
}

# ============================================================================
# Прогон выбранного набора
# ============================================================================

$selectedChecks = @($checkList | Where-Object { $_.Suites -contains $script:Suite })

# Общее число проверок выводится из СПИСКА: расхождение с фактическим числом
# теперь невозможно по построению.
$script:TotalChecks = $selectedChecks.Count

Write-Host ''
Write-Host "=== Набор проверок: $($script:Suite) ===" -ForegroundColor Cyan

# Состояние публикуется заново уже с верным TotalChecks: расширение могло
# прочитать файл между нумерацией прогона и разбором списка проверок.
Write-LocalRunState -Status 'in_progress' -Branch $script:GitBranch -Commit $script:GitCommit -Title $script:RunTitle

foreach ($check in $selectedChecks) {
    Invoke-Check -Name $check.Name -Body $check.Body
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

    $reportPath = Write-CiReport -Status 'ОШИБКА' -FailedNames $failedNames `
        -Commit $script:GitCommit -Branch $script:GitBranch
    Write-Host "Отчёт об ошибках: $reportPath" -ForegroundColor Yellow

    # Финальное состояние пишется до уведомления: расширение должно увидеть
    # результат, даже если показ уведомления не удался.
    Write-LocalRunState -Status 'failure' -ReportPath $reportPath `
        -Branch $script:GitBranch -Commit $script:GitCommit -Title $script:RunTitle

    # Уведомление показывает расширение, если оно активно. Признак проверяется
    # повторно: редактор мог быть открыт уже во время прогона.
    if (Test-EditorMonitorActive) {
        Write-Host 'Уведомление покажет расширение CI Monitor.' -ForegroundColor DarkGray
    } elseif ($script:NotifyEnabled) {
        $sent = Show-AssistQuestToast `
            -Title "Проверка #$($script:LocalRunNumber) не пройдена" `
            -Body ("Локальный CI: " + $failedNames + "`nСборка отменена.")

        if ($sent) {
            Write-Host 'Показано уведомление Windows.' -ForegroundColor DarkGray
        }
    }

    exit 1
}

Write-Host ''
Write-Host 'Локальный CI: все обязательные проверки успешны.' -ForegroundColor Green

$reportPath = Write-CiReport -Status 'успех' -Commit $script:GitCommit -Branch $script:GitBranch
Write-Host "Отчёт о проверках: $reportPath" -ForegroundColor DarkGray

Write-LocalRunState -Status 'success' -ReportPath $reportPath `
    -Branch $script:GitBranch -Commit $script:GitCommit -Title $script:RunTitle

# Уведомление при успехе не показывается намеренно: проверки пройдены, значит
# приложение соберётся и запустится — этого достаточно. Уведомляем только о
# проблемах (ветка ошибки выше), поэтому отвлекающих сообщений не будет.
Write-Host 'Проверки пройдены: уведомление не требуется.' -ForegroundColor DarkGray

exit 0
