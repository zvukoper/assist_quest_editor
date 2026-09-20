# План реализации

## Этап 0 — Архитектурный фундамент

Сделать:

- память проекта;
- строгие правила;
- CI;
- базовую версию;
- skeleton C# WinForms;
- WebView2 host;
- Node/Playwright test harness.

Критерий: приложение стартует, CI зелёный, WebView2 показывает тестовую страницу, Playwright проверяет базовое взаимодействие.

## Этап 1 — Domain model

Сделать canonical model:

- QuestDefinition;
- QuestState;
- QuestGraph;
- SceneGraph;
- Node;
- Socket;
- Connection;
- Condition tree;
- Effect;
- Interaction;
- Reward;
- Event.

Без UI-логики внутри model.

Критерий: квест сериализуется и десериализуется без потери данных.

## Этап 2 — Data Channel Hub

Сделать:

- IDataChannelHub;
- typed channels;
- event channels;
- subscription lifecycle;
- snapshot;
- diagnostics;
- SimulatorAdapter.

Критерий: вручную изменённая позиция/переменная/инвентарь/событие видны Quest Runtime.

## Этап 3 — Quest Runtime

Сделать:

- запуск;
- stop;
- reset;
- node execution;
- state transitions;
- condition evaluation;
- wait;
- event handling;
- effects;
- persistence.

Критерий: квест Руслана проходит сценарий без UI редактора.

## Этап 4 — Simulator

Сделать отдельное окно:

- тестовая карта;
- координатная сетка;
- база городов/точек;
- draggable player marker;
- panel Facts;
- panel Events;
- panel Inventory;
- panel Reputation;
- panel Environment;
- event buttons;
- current Data Channel values.

Критерий: пользователь может пройти Руслана только через Simulator, вручную создавая необходимые игровые данные.

## Этап 5 — Runtime UI

Сделать:

- quest markers;
- nearby interaction notification;
- quest list;
- dialogue;
- choice;
- inventory;
- rewards;
- quest state.

Поведение должно быть максимально близким к будущей интеграции в Assist, но без подключения к ETS2 pause API.

## Этап 6 — Quest Graph Editor

Сделать:

- canvas;
- pan/zoom;
- create/delete node;
- connect sockets;
- select node;
- properties panel;
- dynamic sockets;
- graph save/load;
- comments;
- copy/paste;
- undo/redo;
- validation;
- minimap.

## Этап 7 — Scene Editor

Сделать:

- scene graph;
- dialogue editor;
- choice editor;
- asset references;
- basic timeline;
- preview.

## Этап 8 — Руслан как полный эталон

Перенести весь сценарий «Спецмаринад для Руслана» на canonical graph без специальных hard-coded веток.

Обязательно проверить:

- обе ветки начала;
- отказ;
- повторное предложение;
- Гошу;
- предмет;
- возврат;
- проверку предмета;
- завершение;
- награды;
- persistence;
- перезапуск приложения;
- повторное прохождение.

## Этап 9 — Полировка архитектуры

Проверить:

- отсутствие game-specific зависимостей;
- расширяемость Node Registry;
- расширяемость Condition operators;
- локализацию;
- schema versioning;
- migration;
- стабильность serialization;
- diagnostics.

## Этап 10 — Интеграционный контракт

Только после завершения sandbox:

- formalize ETS2 Assist adapter contract;
- подключить реальные world coordinates;
- подключить telemetry;
- подключить real events;
- map/export integration.

После этого можно отдельно проектировать DayZ adapter.

## Правило этапов

Не перескакивать к реальной интеграции только потому, что один тестовый путь уже работает.

Сначала довести canonical architecture и simulator до состояния, в котором перенос источника данных не требует изменения Quest Domain.
