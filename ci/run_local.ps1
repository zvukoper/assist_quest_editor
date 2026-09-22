<#
.SYNOPSIS
    Локальный прогон тех же проверок, что выполняет .github/workflows/ci.yml.

.DESCRIPTION
    Повторяет шаги CI на этой машине без GitHub Actions:
        Playwright install / Chromium install / Playwright smoke /
        Simulator <-> Editor contract / Quest Graph UI smoke /
        Scene Graph UI smoke / Scene interface UI smoke /
        Quest Graph layout / Pan follows cursor /
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
# Значение фиксировано: publish присутствует всегда (как «пропущено»), поэтому
# число не зависит от -IncludePublish.
# Держать в актуальном состоянии при добавлении/удалении Invoke-Check.
$script:TotalChecks = 26

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
Write-Host "Node.js: $(& node --version 2>$null)"
Write-Host "PowerShell: $($PSVersionTable.PSVersion)"

$global:LASTEXITCODE = 0
$script:GitBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
$script:GitCommit = (& git rev-parse --short HEAD 2>$null | Out-String).Trim()
if (-not [string]::IsNullOrWhiteSpace($script:GitBranch)) { Write-Host "Ветка: $script:GitBranch" }
if (-not [string]::IsNullOrWhiteSpace($script:GitCommit)) { Write-Host "Commit: $script:GitCommit" }

# Заголовок прогона: имя ветки и коммит. Так плашка локального режима
# отличается от предыдущего прогона, и в уведомлении видно, что проверялось.
# Join-String доступен только в PowerShell 7, поэтому используем -join.
$titleParts = @($script:GitBranch, $script:GitCommit) |
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

# Шаг 4.1: горячие клавиши Simulator не должны зависеть от раскладки.
Invoke-Check -Name 'Горячие клавиши Simulator' -Body {
    node ci/hotkey_probe.mjs
}

# Шаг 4.1.1: сохранение квеста должно обновлять каталог на карте.
# Каталог квестов кэшируется, поэтому правка точки активации без перечитывания
# файлов не видна: квест остаётся на прежнем месте, и это выглядит как ошибка
# отрисовки, хотя данные просто не перечитаны.
Invoke-Check -Name 'Обновление каталога квестов' -Body {
    node ci/catalog_reload_smoke.mjs
}

# Шаг 4.1.2: текст на плашке квеста должен читаться.
# Проверка меряет реальные пиксели canvas: фон плашки однородный, поэтому
# полупрозрачный фон + чёрный текст давали нечитаемую плашку («текст стал
# чёрным»), и никакая другая проверка этого не видела.
Invoke-Check -Name 'Читаемость плашки квеста' -Body {
    node ci/quest_plate_smoke.mjs
}

# Шаг 4.2: маркер игрока (обводки, тень, перекрест) на canvas карты.
Invoke-Check -Name 'Маркер игрока Simulator' -Body {
    node ci/player_marker_smoke.mjs
}

# Шаг 5: smoke реального editor.js, включая контекстные меню Quest Graph.
Invoke-Check -Name 'Quest Graph Playwright smoke' -Body {
    node ci/quest_graph_smoke.mjs
}

# Шаг 5.1: кнопка «Перестроить» в нодовом редакторе.
Invoke-Check -Name 'Перестроение нод Quest Graph' -Body {
    node ci/graph_layout_smoke.mjs
}

# Шаг 5.2: smoke реального sceneEditor.js. В workflow GitHub эта проверка есть,
# а в локальном прогоне её не было — из-за этого проверка устарела незаметно
# (ждала переименованный #sceneEditTitle) и падала ещё до сценария.
Invoke-Check -Name 'Scene Graph UI smoke' -Body {
    node ci/scene_graph_smoke.mjs
}

# Шаг 5.2: отдельное workspace для Dialogue/Choice content.
Invoke-Check -Name 'Dialogue Workspace UI smoke' -Body {
    node ci/dialogue_workspace_smoke.mjs
}


# Шаг 5.2.1: интерфейсный слой Scene Runtime (Dialogue Continue + Choice).
# Та же причина, что и у проверки выше: она была только в GitHub Actions.
Invoke-Check -Name 'Scene interface UI smoke' -Body {
    node ci/scene_interface_smoke.mjs
}

# Навигация по resource reference: Quest node → Scene → обратно к исходной ноде.
Invoke-Check -Name 'Resource navigation smoke' -Body {
    node ci/resource_navigation_smoke.mjs
}

# Шаг 5.3: pan средней кнопкой следует за курсором в обоих нодовых редакторах.
# Отдельная проверка нужна потому, что pan-ассерты в quest/scene smoke требуют
# лишь изменения viewBox: при несовпадении аспекта канваса и viewBox pan
# «разбегается», но viewBox всё равно меняется, и слабые ассерты это пропускают.
Invoke-Check -Name 'Pan следует за курсором' -Body {
    node ci/pan_smoke.mjs
}

# Шаг 5.5: целостность игрового Quest Graph и сцен.
# Граф собирается и кодом, и правкой JSON, поэтому его структура проверяется
# отдельно: несвязанный Input или ссылка на несуществующую сцену не ломают
# сборку, но Runtime молча зависает на такой ноде.
Invoke-Check -Name 'Целостность Quest Graph и сцен' -Body {
    node ci/check_quest_graph.mjs
node ci/check_campaigns.mjs
}

# Шаг 5.6: правила репутации на реальном контенте.
# Юнит-тесты собирают сцену кодом, а здесь проверяется сам .aqquest: повторная
# покупка колбасы, цена 450 ₽, +25 репутации и доставка при репутации Гоши 400.
Invoke-Check -Name 'Правила репутации в контенте' -Body {
    node ci/reputation_flow.mjs
}

# Шаг 5.7: портрет НПЦ в списке репутации реально загружается.
# Нужен отдельный шаг потому, что битая картинка не видна ни домену, ни обычному
# smoke: элемент <img> в разметке есть, ошибка только в сетевом запросе.
Invoke-Check -Name 'Портрет репутации загружается' -Body {
    node ci/reputation_avatar_smoke.mjs
}

# Шаг 5.8: синхронизация ресурсов и проверка по манифесту.
# Проверка нужна отдельно от сборки: она ловит именно потерянные, необновлённые и
# оставшиеся от прошлых сборок ресурсы, а не ошибки компиляции. Прогон идёт в
# временный каталог, поэтому рабочие файлы не трогаются.
Invoke-Check -Name 'Синхронизация ресурсов data' -Body {
    $tmp = Join-Path $env:TEMP ("aq-resources-" + [Guid]::NewGuid().ToString('N'))
    try {
        & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ci/sync_data_resources.ps1 `
            -SourceDirectory (Join-Path $root 'data') `
            -TargetDirectory $tmp `
            -AppVersion 'ci' `
            -Commit 'ci'
        if ($LASTEXITCODE -ne 0) {
            throw "sync_data_resources.ps1 завершился с кодом $LASTEXITCODE"
        }

        $manifest = Join-Path $tmp 'data-manifest.json'
        if (-not (Test-Path -LiteralPath $manifest)) {
            throw 'Манифест ресурсов не создан.'
        }

        $sourceCount = @(Get-ChildItem -LiteralPath (Join-Path $root 'data') -Recurse -File).Count
        $targetCount = @(Get-ChildItem -LiteralPath $tmp -Recurse -File).Count
        if ($targetCount -ne $sourceCount + 1) {
            # +1 — сам манифест.
            throw "Число ресурсов не совпало: источник $sourceCount, публикация $targetCount."
        }

        # Остаток прошлой сборки обязан удаляться, иначе в публикации накапливались
        # бы файлы, которых уже нет в источнике.
        $stale = Join-Path $tmp 'scenes\removed_by_test.aqscene'
        Set-Content -LiteralPath $stale -Value '{}' -Encoding UTF8
        & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ci/sync_data_resources.ps1 `
            -SourceDirectory (Join-Path $root 'data') `
            -TargetDirectory $tmp `
            -AppVersion 'ci' `
            -Commit 'ci'
        if (Test-Path -LiteralPath $stale) {
            throw 'Лишний ресурс не удалён из публикации.'
        }

        Write-Host "Ресурсы синхронизированы: файлов $sourceCount, лишние удаляются, манифест создан." -ForegroundColor Green
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Шаг 6: синтаксис web JavaScript.
Invoke-Check -Name 'Синтаксис web JavaScript' -Body {
    $files = @(
        '.\src\AssistQuestEditor.App\Web\main.js',
        '.\src\AssistQuestEditor.App\Web\editor.js',
        '.\src\AssistQuestEditor.App\Web\sceneEditor.js',
        '.\src\AssistQuestEditor.App\Web\dialogueWorkspace.js',
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
Invoke-Check -Name 'Тесты домена' -Body {    $tests = @(Get-ChildItem -Path tests -Recurse -File -Filter *.csproj)
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
        $dataDir = Join-Path $publish 'data'
        $manifest = Join-Path $dataDir 'data-manifest.json'

        # Рядом с EXE допускаются папка ресурсов и отчёт о её проверке. Папка нужна
        # потому, что содержимое single-file распаковывается в невидимый кэш,
        # а отчёт пишет сама проверка целостности. Любой другой файл — остаток
        # прошлой сборки, который легко принять за актуальные данные.
        $exeFiles = @($files | Where-Object { $_.Extension -eq '.exe' })
        $unexpected = @($files | Where-Object {
            $_.Extension -ne '.exe' -and $_.Name -ne 'data-verify-report.txt'
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

        $report = Join-Path $publish 'data-verify-report.txt'
        if (Test-Path -LiteralPath $report) {
            $reportLines = [IO.File]::ReadAllLines($report)
            foreach ($line in $reportLines) { Write-Host "  $line" -ForegroundColor DarkGray }
            if ($reportLines.Count -eq 0 -or $reportLines[0] -notmatch 'совпадают') {
                throw "Проверка целостности ресурсов публикации не подтверждена: $($reportLines[0])"
            }
        } else {
            throw "Приложение не оставило отчёт о ресурсах: $report"
        }

        $resourceCount = @(Get-ChildItem -LiteralPath $dataDir -Recurse -File).Count
        Write-Host "Single-file публикация: $($exeFiles[0].FullName)" -ForegroundColor Green
        Write-Host "Ресурсы рядом с EXE: файлов $resourceCount, манифест подтверждён." -ForegroundColor Green
    }
} else {
    Add-Result -Name 'Single-file publish' -Ok $null -Seconds 0 -Detail 'пропущено' -Output @('Шаг пропущен: нужен -IncludePublish.')

    # Пропущенный шаг тоже завершён: он есть в отчёте отдельной строкой.
    # Без этого успешный прогон показывал бы 10 из 11 (91%) вместо 100%.
    $script:LocalChecksCompleted++

    Write-Host ''
    Write-Host 'Single-file publish : пропущено (нужен -IncludePublish)' -ForegroundColor Yellow
}

# Шаг 11: единственный экземпляр приложения и режим проверок.
# Проверка идёт после публикации: она запускает exe, и на файле прошлой сборки
# результат был бы недостоверным. Старый exe не знает ключа -citest, принимает
# его за путь к файлу и запускается как обычный пользовательский старт — вместе
# с установочными сценариями. Поэтому проба сама сообщает «пропущено», если exe
# отсутствует, собран из другого коммита или имеет другую версию.
# Запускается с -citest: проверки не должны выполнять установочные сценарии.
Invoke-Check -Name 'Single instance probe' -Body {
    & powershell -NoProfile -ExecutionPolicy Bypass -File ci/single_instance_probe.ps1
    if ($LASTEXITCODE -ne 0) {
        throw "single instance probe не прошёл: код $LASTEXITCODE"
    }
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
