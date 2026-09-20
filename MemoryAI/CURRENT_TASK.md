# Текущая задача

## Статус

Каркас двухоконной рабочей станции создан и проходит автоматическую проверку.

Репозиторий: assist_quest_editor

Ветка разработки: только main

### Фактический текущий результат

Quest Graph больше не является статической SVG-заготовкой: канонический граф хранится в `QuestGraphStore` Domain и редактируется через Host/WebView2..

## Уже сделано

- создан репозиторий-песочница;
- зафиксирована универсальная архитектура Quest Domain / Runtime / Data Channels / Simulator / UI / adapters;
- создан C# WinForms host с WebView2;
- создано главное окно редактора;
- создано отдельное окно Simulator;
- при наличии второго монитора Simulator автоматически размещается на нём;
- создан единый визуальный web-стиль по правилам актуального web UI ETS2 Assist;
- добавлены редакторы Quest Graph, Scene Graph, World, Data Channels, Conditions/Effects, Localization, Validation и Node Registry;
- Simulator получил карту тестового полигона и редакторы Игрока и мира, Фактов, Статусов, Состояний, Инвентаря, Репутации, Телеметрии, Окружения и Событий;
- Data Channel Hub содержит каналы Player, World, Facts, Quest Statuses, States, Inventory, Reputation, Telemetry, Environment и System;
- добавлен контракт IDataSourceAdapter;
- добавлены domain tests и Playwright smoke для реальных исходных web-страниц.

## Сейчас делать

Следующий функциональный слой:

1. довести Quest Graph Editor до следующего цикла: перетаскивание нод, pan/zoom, validation, undo/redo и сохранение definition;
2. затем canonical Quest Runtime и registry handlers;
3. интеграция Simulator → Runtime;
4. автоматические переходы эталонного квеста Руслана;
4. runtime UI: маркеры, уведомления, диалоги, выборы и награды;
5. сохранение и восстановление состояния;
6. сохранение графа, undo/redo и миграции схем;
7. полноценное редактирование World Resolver и связей с графом.

## Не делать сейчас

Не подключать:

- ETS2 Assist;
- реальную телеметрию;
- реальные игровые события;
- реальные дороги;
- гигантскую карту;
- экспорт в ETS2;
- экспорт в DayZ.

Использовать только искусственную тестовую карту и fixture-данные песочницы.

## Ближайший функциональный тест

После появления Quest Runtime Simulator должен позволить пройти эталонный квест только через изменение каналов и карту:

- подойти к Руслану;
- увидеть доступную интеракцию;
- открыть Scene;
- выбрать ветку;
- увидеть изменение Status и Step;
- переместиться к Гоше;
- получить предмет;
- вернуться к Руслану;
- пройти проверку ItemCount;
- завершить квест;
- увидеть награды.

Любое изменение Simulator должно проходить через Data Channel contract.

## Инструменты сборки

В корне проекта находятся compile.ps1 и pull.ps1.

compile.ps1 создаёт self-contained single-file EXE: bin/Release/net10.0-windows/win-x64/publish/AssistQuestEditor.exe.

Перед каждой публикацией compile.ps1 останавливает запущенный AssistQuestEditor, удаляет целиком bin, obj, publish и управляемый профиль WebView2 в %LOCALAPPDATA%/AssistQuestEditor/WebView2, затем выполняет новый dotnet publish.

Web-ресурсы src/AssistQuestEditor.App/Web и data/world/sdo_points.json объявлены как publish content и включаются в single-file через IncludeAllContentForSelfExtract.

После успешной публикации compile.ps1 очищает MemoryAI/LOGS и только затем запускает EXE, поэтому логи физического теста начинаются с чистого запуска.

## Версия

Базовая версия каркаса: 1.0.40.101-QUEST-EDITOR-DUAL-WINDOW-R1.

Причина пустого Simulator найдена: C# snapshot сериализовался с PascalCase, тогда как Web UI ожидает camelCase.

Текущая рабочая версия: 1.0.40.109-QUEST-EDITOR-INTERACTION-R1.

Последние рабочие вехи:
- 1.0.40.106 — исправлена camelCase сериализация Simulator snapshot;
- 1.0.40.107 — исправлен camelCase контракт simulator_context/coordinate между Simulator и Editor;
- 1.0.40.108 — начат функциональный Quest Graph Editor через общий QuestGraphStore;
- 1.0.40.109 — добавлены drag нод, wheel zoom и regression smoke для canvas interaction.

Перед физическим тестированием пользователя версия обязательно увеличивается.

## Quest Graph — текущий рабочий контракт

- `QuestGraphStore` находится в `src/AssistQuestEditor.Domain/QuestGraphStore.cs`.
- `QuestGraphFactory.CreateStarter()` создаёт эталонный граф «Спецмаринад для Руслана».
- `QuestNodeCatalog.CreateSockets()` выдаёт sockets по типу ноды.
- Изменения графа выполняются через AddNode / UpdateNode / RemoveNode / Connect / Disconnect.
- NodeId сохраняется при редактировании.
- Удаление ноды автоматически удаляет все связанные connections.
- Связь разрешена только Output → Input, дубликаты запрещены.
- `MainForm` создаёт один QuestGraphStore на всё приложение; все EditorForm получают тот же экземпляр.
- Graph Web UI работает через действия graph_add_node, graph_update_node, graph_remove_node, graph_connect, graph_disconnect.
- Состояние выбора ноды в браузере является только presentation state.

Текущий Graph Editor поддерживает: выбор ноды, добавление нод из списка типов, редактирование Title/X/Y, удаление нод, создание/удаление связей Output → Input, Undo/Redo через QuestGraphStore и canonical validation.

Host передаёт Graph UI enum значения строками через `JsonStringEnumConverter`, поэтому SocketDirection, FlowKind и GraphDiagnosticSeverity имеют единый transport contract.

Следующие незавершённые части Graph Editor: pan canvas, dynamic sockets, comments, copy/paste, disk save/load, minimap. Drag нод и wheel zoom уже реализованы как presentation interaction с отправкой финальных X/Y через canonical Store. Viewport хранится отдельно в Web UI и не входит в canonical graph resource.

Quest Graph smoke проверяет реальный `editor.js`: загрузку `quest_graph`, создание ноды через `graph_add_node`, изменение свойств через `graph_update_node`, запуск `graph_validate` и команды Undo/Redo.
## Проверки

- Domain regression: `tests/AssistQuestEditor.Domain.Tests/QuestGraphStoreTests.cs`.
- Web regression: `ci/selection_context_smoke.mjs` и `ci/quest_graph_smoke.mjs`.
- CI: Node, Chromium/Playwright, web syntax, .NET build/test, single-file publish.



## Последняя проверочная точка

Quest Graph smoke проверяет реальный `editor.js`: загрузку `quest_graph`, создание ноды через `graph_add_node` и изменение свойств через `graph_update_node`.

Последний физически тестируемый выпуск: `1.0.40.108-QUEST-EDITOR-QUEST-GRAPH-R1`. Следующий физический тест выполняется на `1.0.40.109-QUEST-EDITOR-INTERACTION-R1`.
