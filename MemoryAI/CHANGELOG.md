# Журнал изменений памяти и архитектуры

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
