# Журнал изменений памяти и архитектуры

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
