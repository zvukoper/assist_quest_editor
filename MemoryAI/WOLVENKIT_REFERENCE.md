# WolvenKit — архитектурный reference для Assist Quest Editor

## Статус документа

Последняя сверка с upstream: **2026-09-20**.

Источник: https://github.com/WolvenKit/WolvenKit  
Проверенная ветка: `main`  
Проверенный HEAD на момент сверки: `11720772f1e20581301b3dec88a59f7b5ee05675`

Это не руководство по копированию WolvenKit. Это обязательный внешний архитектурный reference, с которым сравниваются наши решения до того, как они станут частью canonical model.

## 1. Философия WolvenKit

Из README, developer documentation и текущего кода WolvenKit следует несколько важных принципов.

### 1.1. Инструмент должен ускорять реальную работу

WolvenKit позиционируется как самостоятельный modding tool, который должен упрощать и ускорять рабочий процесс, а не быть просто визуализатором данных.

Для нас это означает: каждый редактор должен иметь связь с реальным authoring workflow. Нельзя строить «красивые формы» без определения того, какое canonical data действие они выполняют.

### 1.2. IDE-like рабочее пространство

WolvenKit строится как IDE-подобное приложение:

- проект;
- Project Explorer;
- Asset Browser;
- Properties;
- документы/редакторы;
- tool panes;
- лог;
- меню/toolbar;
- docking/floating.

Для нас это важнее конкретного внешнего вида: специализированные Quest/Scene/World редакторы должны быть частью общей resource/document/workspace модели, а не независимыми мини-приложениями.

### 1.3. Function over form

В developer guidelines прямо зафиксирован приоритет функции над декоративностью.

Для нас: сначала корректная модель, редактирование, диагностика, навигация и восстановление состояния; визуальные эффекты не должны определять архитектуру.

### 1.4. Один инструмент — много специализированных представлений

WolvenKit поддерживает разные представления одного содержимого: Asset Browser, Properties, File Editor, Preview, специальные graph/document editors.

Для нас это подтверждает принцип «одна canonical сущность → несколько специализированных представлений», а не «каждый редактор владеет своей копией данных».

### 1.5. Расширяемость через сервисы, фабрики и специализированные editor layers

Текущий `GenericHost.cs` явно регистрирует services, factories, ViewModels и Views через DI.

Для нас это важный сигнал: расширение должно идти через registry/factory/service boundaries, а не через большой `switch` в центральном runtime или host.

---

## 2. Архитектурные решения WolvenKit, критичные для нас

### 2.1. Разделение platform-independent logic и UI

`WolvenKit.App` содержит platform-independent MVVM слой, а `WolvenKit` содержит WPF Views.

Для нас аналогичный принцип уже выражен через:

- Quest Domain / Runtime;
- Host;
- Web Presentation.

Следовательно, Quest Domain нельзя связывать с WebView2/WinForms так же, как WolvenKit не привязывает свою platform-independent application model к конкретному View.

### 2.2. Dependency Injection и фабрики

WolvenKit регистрирует сервисы и factory interfaces через DI, включая document, pane, node и page factories.

Для нас это означает:

- новые node types — через registry;
- новые handlers — через service/handler registration;
- создание редакторов — через фабрики/механизмы, а не ручное конструирование в центральном окне;
- зависимости — через интерфейсы и constructor injection.

### 2.3. Document/resource-centric модель

WolvenKit организует работу вокруг проектов, ресурсов и документов, открываемых специализированными редакторами.

Для нас QuestDefinition, SceneDefinition, World definitions и связанные ресурсы должны быть самостоятельными canonical resources. Редактор — это View/Editor над ресурсом, а не источник существования ресурса.

### 2.4. Graph — это настоящая модель, а не картинка

В текущем WolvenKit graph layer существуют:

- Graph;
- Node;
- Input/Output connectors;
- Connection;
- dynamic inputs;
- специализированные Quest/Scene/Behavior graph types.

Для нас это подтверждает выбранную модель:

`NodeId + NodeType + Sockets + Connections + Parameters`

SVG/Canvas/Web UI может только отображать и изменять эту модель через backend/domain API.

### 2.5. Стабильная identity важнее внешнего имени

Graph nodes в WolvenKit используют отдельные идентификаторы, а пользовательское отображение/заголовок не является заменой identity.

Для нас уже принято:

- NodeId постоянен;
- переименование не меняет NodeId;
- connections хранят SocketId, а не координаты;
- editor position — это метаданные представления.

### 2.6. Layout/editor state отделён от содержимого графа

Это особенно важный принцип.

В `RedGraph` WolvenKit состояние вида графа сохраняется отдельно от самого graph resource через `GraphEditorStates`: координаты нод, viewport и прочие editor-specific данные не смешиваются с canonical graph data.

Для нас это означает:

- canonical Quest Graph не должен загрязняться UI layout;
- X/Y, zoom, pan, selection, collapsed state, comments и аналогичные данные относятся к editor metadata;
- сохранение definition и сохранение layout — разные уровни.

Именно этот принцип должен быть соблюдён до введения полноценного drag/pan/zoom persistence.

### 2.7. Dynamic sockets должны быть частью модели, а не UI hack

WolvenKit умеет создавать dynamic inputs в graph editor, когда тип ноды это поддерживает.

Для нас dynamic sockets должны существовать в canonical model/registry semantics, а не появляться только в JavaScript после клика мышью.

### 2.8. Специализированный редактор не должен ломать общую graph infrastructure

WolvenKit использует общие graph primitives и отдельные Quest/Scene/Behavior semantics.

Для нас:

- общий graph infrastructure;
- отдельные node catalogs/handlers;
- разные Scene/Quest semantics;
- не создавать отдельную несовместимую graph систему для каждого редактора.

### 2.9. Контекстное редактирование и validation

WolvenKit File Editor поддерживает contextual hints и validating editor.

Для нас это означает стратегическое требование:

- редактор должен знать schema/type constraints;
- ошибочные связи/значения должны диагностироваться как можно раньше;
- validation должна быть частью domain/editor pipeline, а не финальным «парсером после разработки».

### 2.10. Авторинг и импорт/экспорт — разные ответственности

WolvenKit разделяет просмотр/редактирование данных и import/export/build workflows.

Для нас canonical Quest model должна оставаться game-agnostic. ETS2/DayZ exporters/adapters переводят модель наружу и не должны заставлять runtime принимать форму одной игры.

---

## 3. Что именно заимствуем

Обязательные для нашего проекта идеи:

1. **IDE/resource-centric workflow**, а не набор несвязанных окон.
2. **Canonical model отдельно от Views.**
3. **Graph как graph model**, а не SVG-state.
4. **Stable IDs для сущностей и sockets.**
5. **Editor metadata отдельно от canonical content.**
6. **DI/factories/registries для расширения.**
7. **Contextual validation.**
8. **Function over form.**
9. **Специализированные редакторы поверх общих infrastructure primitives.**
10. **Возможность нескольких представлений одного ресурса без копирования истины.**

---

## 4. Что НЕ копируем

WolvenKit решает другую задачу: authoring и исследование файлов REDengine/Cyberpunk 2077.

Поэтому мы **не должны** механически переносить:

- REDengine types;
- RED4 file schema;
- конкретную WPF/Nodify implementation;
- структуру `RedGraph`;
- игровые ограничения Cyberpunk;
- их resource formats;
- их runtime assumptions.

Мы заимствуем **философские и архитектурные принципы**, а canonical model Assist Quest Editor остаётся собственной и ориентированной на универсальную quest/runtime систему.

---

## 5. Обязательная процедура сверки

Перед любым изменением, которое влияет на:

- Quest Domain;
- Quest Graph;
- Scene Graph;
- Node/Socket/Connection model;
- Condition/Effect model;
- Runtime execution;
- Persistence;
- Editor/resource infrastructure;
- adapter/exporter boundaries;
- крупный UI/editor workflow,

агент обязан:

1. прочитать этот документ;
2. проверить **актуальный upstream `main` WolvenKit**;
3. посмотреть релевантные source/docs, а не только README;
4. ответить себе, не нарушает ли предлагаемое решение уже известный принцип;
5. если наше решение сознательно расходится с WolvenKit — зафиксировать причину в MemoryAI или в архитектурной задаче.

Нельзя использовать WolvenKit как абсолютную истину. Его контекст отличается от нашего. Но нельзя и игнорировать его при принятии решений, для которых он является нашим заявленным «прародительским» reference.

## 6. Повторная сверка 2026-09-21

Перед введением SceneRuntime снова проверен актуальный upstream `main`. Relevant HEAD: `11720772f1e20581301b3dec88a59f7b5ee05675`.

Подтверждено в исходниках `scnChoiceNode.cs`, `scnChoiceNodeOption.cs`, `RedGraph.Scene.cs`, `SceneGraphViewModel.cs` и `SceneEditingHelper.cs`: Choice является Scene Graph node; option имеет отдельный stable screenplay item ID; создание Choice в редакторе одновременно создаёт screenplay/localization records и ссылку node option на них; Dialogue выделен отдельной поверхностью Scene Editor.

Следствие для Assist Quest Editor: canonical Scene resource должен быть самостоятельным от Quest Graph; interface adapter получает presentation request/result, а stable option IDs важнее порядкового индекса. Первый SceneRuntime сознательно упрощает screenplay/localization до game-agnostic SceneDialogue/SceneChoiceOption; полноценный localization store не смешивается с этим runtime срезом.

## 7. Первичный набор источников для повторной сверки

- Repository: https://github.com/WolvenKit/WolvenKit
- Overview: https://wiki.redmodding.org/wolvenkit/features/overview
- Interface: https://wiki.redmodding.org/wolvenkit/wolvenkit-app/editor
- File Editor: https://wiki.redmodding.org/wolvenkit/wolvenkit-app/editor/file-editor
- Developer Contributing: https://github.com/WolvenKit/WolvenKit/blob/main/docs/CONTRIBUTING.md
- Current DI composition: https://github.com/WolvenKit/WolvenKit/blob/main/WolvenKit/GenericHost.cs
- Current graph model: https://github.com/WolvenKit/WolvenKit/blob/main/WolvenKit.App/ViewModels/GraphEditor/RedGraph.cs
- Quest graph view: https://github.com/WolvenKit/WolvenKit/blob/main/WolvenKit/Views/Documents/QuestPhaseGraphView.xaml

Дата этого конспекта: 2026-09-20.

## 8. Inventory reference check — 2026-09-21

Для минимального Simulator inventory повторно просмотрены актуальные inventory-related RED4 sources в зафиксированном upstream WolvenKit HEAD `11720772f1e20581301b3dec88a59f7b5ee05675`:

- `WolvenKit.RED4/Types/Classes/gameSItemStack.cs` — stack identity через `ItemID` и количество через `Quantity`;
- `WolvenKit.RED4/Types/Classes/gameInventoryItemData.cs` — presentation/runtime metadata, включая `Name`, `Description`, `IsNew`, `IsBroken`, `SlotIndex` и `PositionInBackpack`;
- `WolvenKit.RED4/Types/Classes/InventoryDataManagerV2.cs` — inventory management/transactions boundary;
- `WolvenKit.RED4/Types/Classes/UIInventoryItemsManager.cs` — отдельный UI-side inventory manager;
- `WolvenKit.RED4/Types/Classes/ItemsNotificationQueue.cs` — отдельная очередь inventory/currency/XP notifications; текущий источник содержит `ShowDuration = 6.0F`.

Следствие для Assist Quest Editor:

1. Inventory canonical state использует стабильный `ItemId` и quantity.
2. Item definition/metadata отделены от stack quantity.
3. Состояние нового предмета — presentation/runtime state, а не часть QuestDefinition.
4. Уведомления — отдельная Simulator presentation semantics, а не Effect внутри конкретного квеста.
5. Мы не копируем REDengine types; это game-agnostic адаптация тех же архитектурных границ.
