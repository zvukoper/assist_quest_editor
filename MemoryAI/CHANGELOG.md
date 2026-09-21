# Журнал изменений памяти и архитектуры

## 2026-09-21 — 1.0.40.122 Локальный прогон как основной барьер, CI только вручную

- автозапуск GitHub Actions по push в main отключён: `ci.yml` теперь запускается только через `workflow_dispatch`;
- причина: набор проверок совпадает с `ci/run_local.ps1`, а каждый commit больше не занимает Windows runner;
- GitHub CI сохранён для того, чего локально получить нельзя: чистой среды `windows-latest` без локальных кешей и untracked-файлов и зафиксированного в workflow Node.js 22;
- `import_sdo.yml` продолжает работать: запуск `ci.yml` после импорта СДО выполняется явно через `workflow_dispatch`; добавлен комментарий, почему запуск явный;
- concurrency переведён на группу ручных запусков: повторный ручной запуск отменяет предыдущий незавершённый;
- `MemoryAI/INSTRUCTIONS.md` §8 и §18 и `MemoryAI/PROJECT_MEMORY.md` §3.1 приведены в соответствие: основной барьер — локальный прогон, физический тест после успешного локального прогона и bump версии;
- проверено, что YAML обоих workflow валиден: `ci.yml` → `on = workflow_dispatch`, `import_sdo.yml` без изменений в триггерах.

## 2026-09-21 — 1.0.40.122 Отчёт о проверках в MemoryAI/LOGS

- `ci/run_local.ps1` теперь пишет `MemoryAI/LOGS/CI_errors.md`: сводка по всем проверкам, причина каждой ошибки и полный вывод упавших проверок;
- отчёт формируется и при успехе (с явной отметкой «Ошибок нет»), и при ошибке: иначе нельзя отличить «всё хорошо» от «прогон не делали»;
- для каждой ошибки сохраняется точная позиция (`file:line` из node) и полный стек;
- вывод внешних процессов читается в UTF-8 (`[Console]::OutputEncoding`), иначе русский текст в отчёте превращался в мусор;
- из отчёта убраны служебные обёртки PowerShell: строки stderr приходят как `RemoteException`, и `ToString()` добавлял многострочный блок со `строка/знак/CategoryInfo`;
- при перехвате вывода сохранена подсветка консоли (`InformationRecord` → `HostInformationMessage`);
- `ErrorActionPreference = 'Continue'` внутри проверки: при `*>&1` строки stderr становятся `ErrorRecord`, и со `Stop` первая же строка стека обрывала сбор вывода;
- `compile.ps1` больше не удаляет `CI_errors.md` при очистке `MemoryAI/LOGS`: отчёт сохраняется вместе с `README.md`;
- `ci/validate_memory.mjs` расширен на `CI_errors.md`.

## 2026-09-21 — 1.0.40.122 Локальный прогон CI перед сборкой

- добавлен `ci/run_local.ps1`: повторяет все шаги `ci.yml` без GitHub, печатает итоговую таблицу и завершается кодом 1 с именами упавших проверок (как `final_gate`);
- `pull.ps1` теперь выполняет `git pull --ff-only`, затем локальные проверки и запускает `compile.ps1` только при их успехе;
- при неудачных проверках сборка не выполняется, выводится список упавших проверок и показывается уведомление Windows;
- добавлен переиспользуемый `ci/WindowsToast.ps1` на WinRT `Windows.UI.Notifications` — без сторонних модулей и прав администратора;
- уведомление отправляется отдельным процессом `powershell.exe` с `-EncodedCommand`, поэтому доступно и из PowerShell 7;
- добавлены флаги `-SkipChecks`, `-NoLaunch`, `-NoNotify` в `pull.ps1` и `-IncludePublish`, `-SkipInstall`, `-NoNotify` в `run_local.ps1`;
- шаг single-file publish в локальный прогон не входит по умолчанию: он останавливает запущенный редактор и удаляет bin, obj, publish и профиль WebView2;
- `ci-results/` добавлен в `.gitignore`;
- зафиксировано, что `.ps1` с кириллицей требует UTF-8 с BOM: без BOM PowerShell 5.1 читает файл как ANSI и парсер падает;
- проверены оба пути: при сломанном `editor.js` сборка не запускается и приходит уведомление, при исправном — выполняется полный цикл публикации.

## 2026-09-21 — 1.0.40.122 Quest Graph menu + CI smoke harness fix

- найдена настоящая причина серии красных CI (запуски 184–207): `ci/quest_graph_smoke.mjs` загружал `editor.js`, но не `theme.css`;
- без реального stylesheet `.graphContextMenu` не получал `position:fixed`, и все проверки геометрии меню по viewport были бессмысленны;
- harness теперь загружает реальный `theme.css` через `addStyleTag`, поэтому контекстное меню проверяется в том виде, в котором оно существует в production;
- исправлена вторая ошибка harness: после `setViewportSize` использовался устаревший `svgBox`; канвас выше компактного viewport, и его центр оказывался за пределами окна, из-за чего колесо и pan не попадали в SVG;
- pan и zoom теперь нацелены на точку, гарантированно видимую в окне, с проверкой через `elementFromPoint`;
- исправлена реальная ошибка UI: `graphDirtyNodeIds` никогда не очищался, поэтому оранжевые звёздочки изменённых нод оставались навсегда после сохранения; набор очищается, когда Host сообщает `documentDirty = false`;
- удалён мёртвый флаг `pendingDirtySelection`, который записывался, но нигде не читался;
- добавлена regression-проверка сброса dirty-меток после сохранения (подтверждено, что без исправления она падает);
- устранён version drift: `simulator.html` использовал `?v=1.0.40.118`, из-за чего WebView2 мог отдавать устаревшие `theme.css` и `simulator.js`; footer `main.html` не совпадал с `VersionInfo`;
- все версии синхронизированы на 1.0.40.122-QUEST-GRAPH-MENU-R1 (VersionInfo, `.csproj`, три HTML);
- `final_gate` в CI теперь пишет таблицу результатов по каждой проверке в `GITHUB_STEP_SUMMARY`, чтобы падение называло шаг, а не только «exit code 1»;
- локально подтверждены: Playwright smoke, contract smoke, Quest Graph smoke (3 прогона), `node --check` ×3, `validate_memory`, `dotnet build`, 26/26 domain tests.

## 2026-09-21 — 1.0.40.111 Quest Runtime + CI diagnostics

- завершено подключение canonical Quest Runtime к Simulator через Data Channel Hub;
- Runtime пробуждается по `ChannelChanged` для ожидающих Condition/Interaction;
- исправлено продолжение Runtime после выбора Choice;
- добавлена матрица из семи автоматических сценариев: Graph, Parameters, Simple Runtime, Interaction, Event, Choice, Save/Load;
- исправлены оставшиеся нарушения xUnit2013 в Graph Store tests;
- CI продолжает остальные проверки после сбоя отдельного шага и формирует сводный `MemoryAI/LOGS/assist_quest_editor.log`;
- CI сохраняет TRX и диагностический лог отдельным artifact;
- Host логирует runtime transitions и Simulator events;
- синхронизированы VersionInfo и `.csproj` на 1.0.40.111.


## 2026-09-20 — 1.0.40.110 Quest Graph authoring

- добавлен pan canvas через среднюю кнопку мыши и Space + ЛКМ;
- параметры нод стали частью canonical QuestNode;
- Switch, Random и Choice получили динамические Output sockets через outputCount;
- Inspector умеет редактировать и добавлять параметры;
- добавлено versioned QuestDefinitionDocument schema=1;
- Graph Editor получил Новый, Открыть, Сохранить и Сохранить как…;
- добавлены smoke-проверки параметров и pan;
- версия поднята до 1.0.40.110 перед физическим тестированием.


## 2026-09-20 — Quest Graph: drag и wheel zoom

- Graph nodes теперь перетаскиваются ЛКМ непосредственно по canvas.
- Финальные координаты drag отправляются через `graph_update_node`, поэтому canonical `QuestGraphStore` остаётся источником данных.
- Колесо мыши масштабирует Quest Graph вокруг позиции курсора.
- Viewport (`x/y/width/height`) хранится отдельно от `QuestGraph` и переживает rerender после изменения ноды.
- Playwright smoke проверяет drag с изменением X/Y и реальный wheel zoom.
- Версия поднята до `1.0.40.109-QUEST-EDITOR-INTERACTION-R1` перед физическим тестом.

## 2026-09-20 — Quest Graph: Undo/Redo, Validation и transport contract

- `QuestGraphStore` хранит snapshot history для Undo/Redo; новая правка очищает Redo history.
- Добавлен `QuestGraphValidator` со стабильными diagnostic codes для структуры графа, sockets, connections и достижимости нод.
- Host поддерживает `graph_undo`, `graph_redo`, `graph_validate`; `quest_graph` передаёт состояние history и validation diagnostics.
- Web Graph Editor получил Undo/Redo/Проверить и горячие клавиши Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z.
- `JsonStringEnumConverter` синхронизировал enum transport с Web UI.
- Domain tests и Playwright smoke расширены новыми контрактами.

## 2026-09-20 — Архитектурный reference WolvenKit

- добавлен обязательный внешний архитектурный reference: upstream `WolvenKit/WolvenKit`;
- добавлен `MemoryAI/WOLVENKIT_REFERENCE.md` с кратким конспектом философии, graph/document architecture, DI/factories, stable IDs, editor metadata и contextual validation;
- в `INSTRUCTIONS.md` зафиксирована обязательная сверка актуального WolvenKit `main` перед архитектурными и логическими изменениями;
- в `ARCHITECTURE.md` и `PROJECT_MEMORY.md` добавлена явная связь с reference;
- отдельно зафиксировано, что заимствуются принципы, а не REDengine-specific типы и форматы.

## 2026-09-20 — 1.0.40.108-QUEST-EDITOR-QUEST-GRAPH-R1

- Quest Graph переведён из статического demo SVG в рабочий редактор.
- Добавлен canonical `QuestGraphStore` в Quest Domain.
- Добавлен `QuestGraphFactory.CreateStarter()` с эталонным графом Руслана.
- Добавлен `QuestNodeCatalog` с базовыми Input/Output sockets и ветвлением.
- Host хранит один общий QuestGraphStore и передаёт его всем EditorForm.
- Graph Web UI умеет выбирать ноды, менять Title/X/Y, добавлять и удалять ноды, создавать и удалять connections.
- Добавлены domain tests для поведения QuestGraphStore.
- Версия повышена до 1.0.40.108 перед физическим тестированием.

## 2026-09-20 — 1.0.40.107-QUEST-EDITOR-SDO-MAP-R6-SELECTION-CONTEXT-FIX

- исправлен контракт `simulator_context`: EditorForm сериализует вложенные `selection.point.position.x/y/z` в camelCase;
- исправлен `formatPosition()` в Simulator Web UI;
- устранены `ReferenceError: formatPosition is not defined` и `undefined` при передаче координат в редактор локаций;
- добавлен selection/context smoke test;
- версия повышена до 1.0.40.107 перед физическим тестированием.

## 2026-09-20 — 1.0.40.106-QUEST-EDITOR-SDO-MAP-R5-SNAPSHOT-FIX

- исправлена сериализация Simulator snapshot: для Web UI принудительно используется camelCase;
- устранена причина ошибки `Cannot read properties of undefined (reading 'position')`;
- версия поднята перед повторным физическим тестом;
- CI разрешает диагностический `MemoryAI/LOGS/assist_quest_editor.log` для анализа.

## 2026-09-20 — 1.0.40.105-QUEST-EDITOR-SDO-MAP-R4-DIAGNOSTICS

- добавлен постоянный диагностический лог `MemoryAI/LOGS/assist_quest_editor.log`;
- логируется путь и размер SDO-файла, результат JSON-десериализации, число входных/валидных точек и диапазоны координат;
- логируется жизненный цикл WebView2, NavigationCompleted, URL страницы, версия браузера и отправка/получение snapshot;
- добавлен сбор `console.error`, `console.warn`, `window.error` и `unhandledrejection` из Web UI;
- добавлен version cache-busting для HTML/JS/CSS WebView;
- после успешной публикации compile.ps1 очищает старые логи, при ошибке публикации логи не удаляются.

## 2026-09-20 — Инициализация

- создан отдельный репозиторий-песочница assist_quest_editor;
- принято решение не подключать ETS2 Assist до стабилизации архитектуры;
- за основу взяты архитектурные идеи WolvenKit Quest/Scene Editor;
- определено разделение Quest Graph / Scene Graph / World / Data Channels / Runtime / UI;
- первым эталонным сценарием выбран «Спецмаринад для Руслана»;
- предусмотрена возможность будущей адаптации к ETS2 и DayZ;
- зафиксирована модель Simulator как искусственного источника данных;
- задан русский интерфейс с заранее подготовленной локализацией;
- задана строгая дисциплина CI и диагностики.

## 2026-09-20 — 1.0.40.101-QUEST-EDITOR-DUAL-WINDOW-R1

- создан двухоконный WinForms/WebView2 каркас;
- Simulator вынесен в отдельное окно для второго монитора;
- добавлены искусственные каналы Player, World, Facts, Quest Statuses, States, Inventory, Reputation, Telemetry, Environment и System;
- добавлены редакторы данных Simulator;
- добавлены Quest Graph, Scene Graph, World, Conditions/Effects, Localization, Validation и Node Registry;
- тема web UI выровнена по палитре и базовой анимации актуального web UI ETS2 Assist;
- добавлен IDataSourceAdapter для будущей замены Simulator на игровой источник;
- CI переведён на обязательные сборку src и запуск tests;
- Playwright smoke переведён с тестовой HTML-строки на реальные web-страницы проекта.

## 2026-09-20 — 1.0.40.103-QUEST-EDITOR-SDO-MAP-R2

- исправлена доставка Simulator snapshot: snapshot отправляется только после NavigationCompleted, а не сразу после Navigate();
- карта Simulator визуально выровнена под задачу СДО: точки используют цвет категории, выбранная точка получает толстую оранжевую обводку, игрок отображается красным треугольником с корректным углом;
- добавлена компактная легенда категорий СДО; подписи точек ограничены при общем обзоре и раскрываются при приближении/выборе;
- версия поднята перед физическим тестированием.

## 2026-09-20 — 1.0.40.104-QUEST-EDITOR-SDO-MAP-R3

- устранён рассинхрон версии;
- compile.ps1 перед публикацией удаляет целиком bin, obj, publish и управляемый профиль WebView2;
- WebView2 получает фиксированный профиль %LOCALAPPDATA%/AssistQuestEditor/WebView2;
- проверяются обязательные Web-файлы; single-file по-прежнему должен состоять только из одного EXE;
- версия поднята перед повторным физическим тестированием.

## 2026-09-20 — Инструменты сборки

- добавлен compile.ps1 по образцу ETS2 Assist, адаптированный под Assist Quest Editor;
- публикация выполняется как self-contained win-x64 single-file;
- web-ресурсы включаются в один EXE;
- добавлен ключ -NoLaunch для CI и проверки публикации без запуска;
- добавлен pull.ps1 для обновления main и запуска compile.ps1;
- CI теперь проверяет реальную single-file публикацию.
