# Assist Quest Editor

Песочница и рабочий прототип переносимой системы создания и исполнения квестов.

Проект развивается отдельно от ETS2 Assist: сначала полностью отрабатываем архитектуру, редактор, симулятор, runtime и визуальные интерфейсы на искусственных данных. Подключение ETS2 Assist и других игровых источников данных происходит только после стабилизации контрактов.

Документация проекта находится в `MemoryAI/`.

## Сборка

Для обычной разработки запускайте:

    powershell -ExecutionPolicy Bypass -File .\compile.ps1

Скрипт очищает старую публикацию, восстанавливает зависимости и публикует self-contained приложение под Windows x64. Проверяется, что каталог публикации содержит ровно один файл:

    bin\Release\net10.0-windows\win-x64\publish\AssistQuestEditor.exe

HTML/CSS/JavaScript включаются в single-file публикацию. При запуске .NET извлекает содержимое, необходимое приложению и WebView2. Это соответствует режиму single-file с IncludeAllContentForSelfExtract. citeturn623619search1

Для обновления исходников и сборки:

    powershell -ExecutionPolicy Bypass -File .\pull.ps1

`pull.ps1` выполняет три шага: обновляет исходники (`git pull --ff-only`), запускает локальные проверки CI и **только при их успехе** запускает `compile.ps1`. Если проверки не прошли, сборка не выполняется, печатается список упавших проверок и показывается уведомление Windows.

Параметры `pull.ps1`:

| Параметр | Назначение |
|---|---|
| `-SkipChecks` | Собрать сразу после `git pull`, без локальных проверок |
| `-NoLaunch` | Собрать, но не запускать приложение |
| `-NoNotify` | Не показывать уведомление Windows |

Для проверки публикации без запуска приложения:

    powershell -ExecutionPolicy Bypass -File .\compile.ps1 -NoLaunch

## Проверки

Те же проверки, которые выполняет CI, можно прогнать локально без GitHub:

    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1

Скрипт повторяет шаги `ci.yml` (Playwright, Chromium, smoke, контракт, синтаксис JS, .NET build, domain tests) и печатает итоговую таблицу. При падении он завершается кодом 1 и называет упавшие проверки — как это делает `final_gate` в CI.

Шаг публикации single-file в локальный прогон не входит: он останавливает запущенный AssistQuestEditor и удаляет `bin`, `obj`, `publish` и профиль WebView2. Чтобы выполнить полный цикл:

    powershell -ExecutionPolicy Bypass -File .\ci\run_local.ps1 -IncludePublish

Локальная проверка требует Node.js 22+, .NET SDK 10 и PowerShell 5.1+; скрипт сам ставит Playwright и Chromium, если их нет.

При неудачных проверках показывается уведомление Windows через `ci/WindowsToast.ps1` (WinRT `Windows.UI.Notifications`, без сторонних модулей и прав администратора). Уведомление можно отключить флагом `-NoNotify`.

Каждый прогон перезаписывает отчёт `MemoryAI/LOGS/CI_errors.md`: сводка по всем проверкам, причина каждой ошибки и полный вывод упавших проверок. При успешном прогоне отчёт фиксирует это явно.

После публикации `compile.ps1` очищает `MemoryAI/LOGS` — как и в CI. Если диагностические логи были зафиксированы в git, локальный прогон с `-IncludePublish` пометит их как удалённые.
