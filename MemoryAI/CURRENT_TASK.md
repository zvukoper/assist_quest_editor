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

Текущий рабочий слой — Quest Runtime + системная автоматическая проверка. Quest Graph authoring baseline уже подключён к canonical Runtime и Simulator. Ручным тестом подтверждены цепи из нескольких Interaction и маршрут через несколько Trigger/ветвлений.

1. довести canonical Runtime Registry и обработчики нод по матрице `MemoryAI/RUNTIME_NODE_MATRIX.md`;
2. пройти матрицу из семи автоматических сценариев и использовать её как обязательный регрессионный барьер;
3. реализовать полный эталонный квест Руслана на canonical graph;
4. довести Runtime UI: маркеры, уведомления, диалоги, выборы и награды;
5. добавить отдельное сохранение/восстановление Quest Runtime State;
6. затем продолжить специализированные редакторы, Scene Graph и World Resolver.

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

Текущая рабочая версия: 1.0.40.129-QUEST-SIM-INVENTORY-R1.

Последние рабочие вехи:
- 1.0.40.106 — исправлена camelCase сериализация Simulator snapshot;
- 1.0.40.107 — исправлен camelCase контракт simulator_context/coordinate между Simulator и Editor;
- 1.0.40.108 — начат функциональный Quest Graph Editor через общий QuestGraphStore;
- 1.0.40.109 — добавлены drag нод, wheel zoom и regression smoke для canvas interaction;
- 1.0.40.110 — добавлены pan canvas, canonical параметры нод, динамические sockets и JSON Save/Open/New.
- 1.0.40.111 — добавлен Quest Runtime, интеграция Simulator → Runtime, пробуждение Runtime по ChannelChanged, корректное продолжение после Choice и матрица из семи автоматических сценариев.
- 1.0.40.122 — найдена причина серии красных CI: smoke-тест не загружал `theme.css`, поэтому проверки геометрии контекстного меню были бессмысленны. Harness приведён к production-условиям, исправлен устаревший `svgBox` после смены viewport, сброс dirty-меток нод после сохранения, version drift и читаемость `final_gate` в CI. Добавлен локальный прогон `ci/run_local.ps1`, `pull.ps1` собирает только при успешных проверках, ошибки пишутся в `MemoryAI/LOGS/CI_errors.md`.
- 1.0.40.124 — Simulator получил города, усиленную тень точек, hover-подсветку и оранжевый/красный маркер игрока; добавлен демонстрационный Quest Definition со всеми типами нод. Следующий фокус — настоящая runtime-семантика нод, которые пока являются pass-through.
- 1.0.40.125 — исправлен переход после Wait: по истечении таймера Runtime идёт по `wait.out`, не переисполняет Wait; добавлен отдельный regression test на `Wait → SetStep → End`.
- 1.0.40.126 — добавлен `interfaces` Data Channel и реальный интерфейс Choice в Simulator; выбор возвращается через `ChoiceSelected`, а requestId защищает от устаревших ответов; пользователь физически подтвердил обе ветки Choice.
- 1.0.40.127 — добавлен canonical Scene Graph и отдельный SceneRuntime; DialogueScene теперь запускает Scene resource, Scene Choice получает текст и стабильные option IDs из canonical Scene, а не из QuestNode.Parameters; после успешного log push AppLogger прекращает запись до выхода.

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

Текущий Graph Editor поддерживает: выбор ноды, добавление нод из списка типов, редактирование Title/X/Y и Parameters, динамические sockets, удаление нод, создание/удаление связей Output → Input, Undo/Redo, validation и JSON New/Open/Save/Save As.

Host передаёт Graph UI enum значения строками через `JsonStringEnumConverter`, поэтому SocketDirection, FlowKind и GraphDiagnosticSeverity имеют единый transport contract.

Следующие незавершённые части Graph Editor: comments, copy/paste, multi-select, minimap и полноценные schema-редакторы Quest/Scene/Condition/Effect. Drag нод и wheel zoom уже реализованы как presentation interaction с отправкой финальных X/Y через canonical Store. Viewport хранится отдельно в Web UI и не входит в canonical graph resource.

Quest Graph smoke проверяет реальный `editor.js`: загрузку `quest_graph`, создание ноды через `graph_add_node`, изменение свойств через `graph_update_node`, запуск `graph_validate` и команды Undo/Redo.
## Проверки

- Domain regression: `tests/AssistQuestEditor.Domain.Tests/QuestGraphStoreTests.cs`.
- Web regression: `ci/selection_context_smoke.mjs` и `ci/quest_graph_smoke.mjs`.
- CI: Node, Chromium/Playwright, web syntax, .NET build/test, single-file publish.
- CI запускает отдельную матрицу: Graph / Parameters / Simple Runtime / Interaction / Event / Choice / Save/Load.
- `MemoryAI/LOGS` остаётся контейнером физических диагностических логов; CI его не проверяет и не загружает.



## Runtime и автоматическая матрица

`QuestRuntime` находится в `src/AssistQuestEditor.Domain/QuestRuntime.cs` и получает данные только через Data Channel contracts. Simulator публикует события и изменения каналов; Host записывает runtime transitions/events в диагностический лог.

Автоматическая матрица:
- 1 Graph
- 2 Parameters
- 3 Simple Runtime
- 4 Interaction
- 5 Event
- 6 Choice
- 7 Save/Load

Последний физически тестируемый выпуск: `1.0.40.125-QUEST-RUNTIME-WAIT-FIX-R1`.
Следующий физический тест выполняется на версии `1.0.40.128-QUEST-EXAMPLE-R1`.

## Отложенная задача — полноценная валидация Inventory

Текущие `GiveItem`/`RemoveItem` подключены к canonical `inventory` Data Channel и работают с `ItemId + quantity`, но пока отсутствуют полноценные правила inventory semantics: ошибка/ветка при недостаточном количестве для `RemoveItem`, явная политика stack/unique item, проверка допустимости item definition и другие ограничения. Не считать текущую реализацию финальной inventory-системой. Вернуться к этому после завершения Scene Editor baseline.

## 2026-09-21 — 1.0.40.130 Splash startup surface

- добавлен отдельный стартовый экран `SplashScreen.png` размером 800×450 из `src/AssistQuestEditor.App/Assets`;
- splash показывается до создания основного WebView UI и закрывается после первого успешного `BrowserReady` главного окна;
- MainForm и SimulatorForm не показывают промежуточный пустой WebView: они становятся видимыми только после готовности собственного browser;
- asset включён в self-contained single-file publish;
- версия поднята до `1.0.40.130-QUEST-SPLASH-R1`.

## 2026-09-21 — 1.0.40.129 Simulator Inventory / Player HUD

Слой Simulator теперь содержит минимальную gameplay-подачу инвентаря и player state:

- backpack button внизу карты + клавиша `I`;
- 24 inventory slots;
- `ruslan.raw_meat` визуализируется как «Мясо» с цветным квадратом;
- новые предметы имеют оранжевый пульсирующий beacon; закрытый backpack пульсирует при наличии непросмотренного предмета;
- hover по предмету помечает его просмотренным через Data Channel;
- квестовые GiveItem/RemoveItem показывают уведомления на 6 секунд с fade 100ms и входным смещением 22px;
- ручные изменения Simulator inventory уведомления не показывают;
- верхняя player state panel: здоровье, энергия/жидкость, усталость;
- нижняя панель: деньги, опыт, резерв;
- соседняя панель `Персонаж` с семью SPECIAL-like статами и двумя навыками;
- buffs/debuffs зарезервированы и документированы, но не отображаются/не применяются;
- добавлены player-state quest nodes;
- snapshot rendering, Choice rendering и detached Journal refresh coalesced, чтобы убрать повторные перерисовки и мерцание.

## 2026-09-21 — 1.0.40.131 Scene Editor R1

Scene Editor R1 переведён с UI-заглушки на canonical pipeline:

- SceneDefinition → SceneGraphStore → EditorForm Host → WebView Scene Editor;
- добавлены CRUD для Scene nodes и connections с проверкой Output → Input;
- Node Registry содержит SceneStart, Dialogue, Choice, SceneWait, SceneEvent и SceneEnd;
- добавлены Undo/Redo, validation и JSON New/Open/Last/Save/Save As;
- добавлена кнопка «Перестроить»: раскладка считается в Domain через SceneGraphLayout, а не в браузере;
- Drag нод, pan, wheel zoom, context menu, подтверждение DEL и соединение Output → Input являются presentation interaction;
- SceneCatalog получил список ресурсов и Upsert, поэтому сохранённая сцена сразу становится видна SceneRuntime;
- добавлены domain regression SceneGraphStoreTests и Playwright smoke ci/scene_graph_smoke.mjs;
- версия выпуска: 1.0.40.131-QUEST-SCENE-EDITOR-R1.

Следующий слой Scene Editor: отдельное редактирование Dialogue/Choice resources, timeline, preview, actor/camera/animation, copy/paste и minimap.


## 2026-09-21 — 1.0.40.132 Scene Editor R2: Quest Graph interaction parity

После физического сравнения Scene Editor и Quest Graph зафиксировано правило: canvas Scene Editor не изобретается заново. Он использует адаптированную копию отработанного Quest Graph interaction layer.

Унаследованы:
- геометрический socket hit-test;
- pointer capture;
- drag с локальным preview и одной финальной записью координат;
- pan средней кнопкой и Space + ЛКМ;
- zoom колесом вокруг курсора;
- snap кабеля;
- hover/compatible/incompatible подсветка и cursor states;
- pending connection preview;
- context menu у canvas/node/socket с clamp и wheel scrolling;
- DEL с подтверждением;
- per-node dirty marker и очистка после Host save;
- тот же node visual markup и connector zones.

Также SceneGraphLayout синхронизирован с каноническим Quest Graph layout, а theme CSS получил те же #questGraphSvg эффекты для #sceneGraphSvg.

Версия физического тестирования: 1.0.40.132-QUEST-SCENE-EDITOR-R2.


## 2026-09-22 — 1.0.40.133 Resource Formats R1

Зафиксирован и внедрён стабильный реестр типизированных ресурсов:

- `.aqquest` — Quest;
- `.aqscene` — Scene;
- `.aqdialogue` — Dialogue;
- `.aqchoice` — Choice;
- `.aqcampaign` — Campaign;
- `.aqworld` — World;
- `.aqpoint` — World Point;
- `.aqcity` — City;
- `.aqitem` — Item definition;
- `.aqcondition` — reusable Condition;
- `.aqeffect` — reusable Effect;
- `.aqloc` — Localization;
- `.aqregistry` — Node Registry;
- `.aqsnapshot` — Runtime Snapshot;
- `.aqresource` — зарезервированный generic resource.

Правило: расширение описывает семантический тип ресурса, а не окно редактора; версия схемы меняется через `schemaVersion`, расширение при этом не меняется. Канонический реестр находится в `src/AssistQuestEditor.App/Core/ResourceFileTypes.cs`, подробная спецификация — `MemoryAI/RESOURCE_FILE_FORMATS.md`.

Quest/Scene editor Open/Save dialogs больше не предлагают общий `.json`: Quest принимает `.aqquest`, Scene принимает `.aqscene`. Loaders также фильтруют каталог по этим расширениям и проверяют `format` внутри JSON.

Существующие sandbox-ресурсы мигрированы без изменения графов:
- `tutorial_ruslan_shashlik.aqquest`;
- `ruslan_start.aqscene`;
- `gosha_meat.aqscene`;
- `ruslan_finish.aqscene`.

Добавлены Windows file associations для текущего пользователя через `HKCU\\Software\\Classes`, versioned ProgID, OpenWithProgIds, DefaultIcon и shell/open/command. Регистрация не перезаписывает уже выбранную пользователем ассоциацию. Explorer уведомляется через SHChangeNotify.

Для каждого зарегистрированного типа приложение генерирует отдельный маленький ICO в `%LOCALAPPDATA%\\AssistQuestEditor\\FileIcons`. `.aqquest` и `.aqscene` открываются двойным щелчком в соответствующем редакторе. `--unregister-file-associations` удаляет зарегистрированные нами ProgID/association entries.

Версия физического тестирования: `1.0.40.133-RESOURCE-FORMATS-R1`.


## 2026-09-22 — 1.0.40.134 Scene Dialogue R1

Scene Runtime integration доведена до полноценной игровой семантики.

Фактическая цепочка теперь:
`QuestNode(DialogueScene)` → canonical `SceneDefinition` → `SceneStart` → `Dialogue` → пользовательское `DialogueContinue` → `Choice` → пользовательский `ChoiceSelected` → `SceneEnd` → возврат в Quest Runtime.

Dialogue больше не является pass-through. При входе в Scene Dialogue Runtime:
- находит `SceneDialogue` resource внутри Scene Definition;
- создаёт отдельный `InterfaceDialogue` с уникальным requestId;
- публикует его через canonical `interfaces` Data Channel;
- устанавливает `WaitingFor = Dialogue`;
- ждёт только matching `DialogueContinue`;
- устаревший requestId игнорируется;
- после Continue очищает Dialogue UI и `RuntimeStatesState.DialogueId/DialogueAnchor`, затем продолжает Scene Graph.

Choice сохранил существующий контракт `ActiveDialog + ChoiceSelected`, теперь Dialogue и Choice являются двумя последовательными состояниями одного Scene interface layer.

Web UI `interface.js` теперь умеет оба режима: Dialogue с кнопкой «Продолжить» и Choice с вариантами. Добавлены Enter/Space для продолжения Dialogue и цифровые клавиши для Choice.

SimulatorForm принимает `interface_dialogue_continue` и переводит его в `DialogueContinue` через Data Channel Events.

Добавлен Playwright smoke `ci/scene_interface_smoke.mjs`, проверяющий Dialogue rendering/Continue и не регрессирующий Choice.

Regression в `QuestRuntimeTests` обновлён для реальной цепочки Dialogue → Choice → End и добавлен тест на stale Dialogue request.

Итог: в эталонном `.aqquest` все три разговорных участка остаются `DialogueScene` и используют canonical `.aqscene`; прямого Quest-level Dialogue UI больше не требуется для основного пути.


## 2026-09-22 — Quest ↔ Scene integration checkpoint

После Scene Dialogue R1 завершён ещё один обязательный стык authoring:

- Quest Graph получает canonical Scene catalog от Host;
- параметр `DialogueScene.sceneId` в Quest Editor отображается как selector существующих Scene resources, а не свободный текст;
- неизвестная текущая ссылка всё равно отображается отдельно, чтобы её нельзя было тихо потерять;
- SceneCatalog получил `Changed` event;
- MainForm рассылает обновлённый catalog всем открытым EditorForm, поэтому сохранение/загрузка Scene в одном окне сразу обновляет selector в Quest Graph;
- `OpenResourcePath` поддерживает открытие `.aqquest`/ `.aqscene` через их canonical editor.

Итоговый canonical runtime/authoring контракт:
`Quest → DialogueScene(sceneId) → SceneDefinition → SceneStart → Dialogue → Choice → SceneEnd → Quest`.

Текущий физический тестовый выпуск: `1.0.40.135-QUEST-SCENE-INTEGRATION-R1`.

## 2026-09-22 — 1.0.40.136 Dialogue/Choice Authoring R1

Scene Editor получил специализированное редактирование conversation content без выноса Dialogue/Choice в отдельные файлы.

Архитектурный контракт:
- SceneDefinition.Graph отвечает за порядок, ветвление, sockets и connections;
- SceneDefinition.Dialogues и SceneDefinition.Choices являются canonical content resources текущей Scene;
- Dialogue node содержит только стабильную ссылку dialogueId;
- Choice node содержит стабильную ссылку choiceId;
- ID Dialogue/Choice/Option не редактируются как обычный текстовый параметр;
- SceneGraphStore является единственным владельцем мутаций контента, поэтому authoring получает Undo/Redo и ту же canonical persistence, что и graph;
- Choice options сохраняют свои stable IDs и OutputSocketId; добавление создаёт новый stable socket ID, удаление подключённой ветки запрещается до разрыва связи;
- при изменении текста Choice обновляются только подписи существующих Output sockets, без пересоздания sockets и потери connections;
- максимум Choice options: 16.

UI ориентирован на актуальный WolvenKit Scene Editor:
- Node Properties остаётся contextual inspector;
- Dialogue/Choice content редактируется специализированным UI при выборе соответствующей graph node;
- есть список resource IDs, Speaker, Text/Title, редактирование option text и Add Option;
- отсутствующая ссылка явно показывается как ошибка, а для Dialogue/Choice предусмотрено создание resource прямо из node;
- при отсутствии выбранной node inspector показывает обзор Dialogue/Choice resources и позволяет открыть связанную graph node.

Сверка с WolvenKit показала правильный долгосрочный паттерн: Scene Editor отделяет Node Properties от Dialogue content, использует отдельную dialogue-oriented область авторинга, создаёт stable IDs и обновляет Choice sockets без необязательной регенерации. Не копируется внутренний RED4 screenplay/localization формат; в sandbox сохраняется собственная canonical модель.

Проверки:
- добавлены domain regression tests для создания/изменения Dialogue, создания Choice, add/remove option, сохранения stable IDs и запрета удаления подключённой ветки;
- Playwright Scene Graph smoke расширен проверками Dialogue/Choice authoring;
- физический build/publish и GitHub Actions run на этой версии здесь не запускались.
## 2026-09-22 — 1.0.40.137 Dialogue Workspace R1

Добавлено отдельное рабочее пространство `editor.html#dialogue` для авторинга conversation content.

Контракт:
- Dialogue Workspace и Scene Graph открываются как отдельные EditorForm окна, но используют общий `SceneGraphStore`;
- список показывает Dialogue и Choice resources текущей Scene, поддерживает поиск по ID/Speaker/Title/Text;
- Dialogue редактируется по полям Speaker/Text; Resource ID стабилен и не редактируется вручную;
- есть создание нового standalone Dialogue resource и безопасное удаление: referenced Dialogue нельзя удалить;
- Choice редактируется по Title/Speaker/Text и option text; stable OptionId/OutputSocketId не меняются;
- Add Option работает только для Choice, привязанного к Graph node, потому что новый option обязан иметь canonical Output socket;
- переход `Открыть в Graph` выбирает связанную Scene node;
- Scene selector и Save/Save As работают из Dialogue Workspace;
- изменения проходят через `SceneGraphStore`, поэтому сохраняют canonical persistence и Undo/Redo.

WolvenKit reference:
- отдельный dialogue-oriented authoring layer остаётся отделённым от graph/node properties;
- Scene Graph продолжает отвечать за flow и связи, а dialogue workspace — за content;
- внутренний RED4 screenplay/localization data model WolvenKit не копируется в sandbox; собственная `SceneDefinition.Dialogues/Choices` остаётся canonical до отдельного migration design.

Regression:
- `ci/dialogue_workspace_smoke.mjs` проверяет загрузку ресурсов, поиск, редактирование Dialogue, Choice authoring, Add Option и переход Graph ↔ Dialogue;
- локальный `ci/run_local.ps1` теперь имеет 18 контрольных шагов и проверяет syntax `dialogueWorkspace.js`;
- `.github/workflows/ci.yml` запускает Dialogue Workspace smoke;
- domain tests покрывают Dialogue lifecycle;
- физическая сборка/publish для этой версии ещё не выполнялись здесь.

## 2026-09-22 — 1.0.40.138 Scene Document Context + automatic node layout

- Исправлен общий контекст Scene для Scene Graph и Dialogue Workspace: обе формы используют один `SceneDocumentSession` вместе с общим `SceneGraphStore`.
- `CurrentPath`, `LastPath` и dirty-state больше не принадлежат отдельному окну. Обычная команда `Сохранить сцену` в Dialogue Workspace сохраняет текущий открытый `.aqscene`; `Сохранить как…` остаётся явной операцией.
- Переключение Scene через selector/open/new и открытие `.aqscene` через file association теперь учитывают несохранённые изменения и предлагают сохранить, отбросить или отменить.
- Сверка с актуальным WolvenKit сохранена как архитектурный ориентир: Dialogue/Choice редактируются на Dialogue-вкладке текущего Scene resource; создание помечает именно Scene document dirty. Отдельное окно authoring в нашем sandbox не меняет владение ресурсом.
- `QuestGraphStore.AddNode` и `SceneGraphStore.AddNode` теперь сразу применяют canonical layout. Создание ноды и её автоперестроение являются одной Undo-операцией.
- Добавлены regression tests для общего Scene document context и автоматического layout после создания ноды.
- Три canonical sandbox Scene (`ruslan_start`, `gosha_meat`, `ruslan_finish`) не удалялись: они валидны и используются `tutorial_ruslan_shashlik.aqquest`. Ранее ошибочно размещённый `.aqscene` в `data/quests` уже удалён отдельным предыдущим commit.
- Текущая версия: `1.0.40.138-QUEST-SCENE-DOCUMENT-CONTEXT-R1`.
- Физическая сборка/publish после этих изменений здесь не выполнялись; текущие проверки базовой ветки до этой серии изменений были указаны в commit `cd1f616710b1d8fe8ee278403a0c91301f9c9293`.

## 2026-09-22 — Final cleanup for 1.0.40.138

- `data/quests` очищен от четырёх legacy `.json` экспортов: старые копии `Разговор с Русланом`, `Разговор с Русланом2`, старого учебного квеста и `demo_all_node_types`. Поиск репозитория не нашёл действующих ссылок на них; canonical resource set теперь использует `.aqquest` и `.aqscene`.
- В `data/scenes` оставлены только три canonical `.aqscene`: `ruslan_start`, `gosha_meat`, `ruslan_finish`; все три нужны tutorial quest.
- Scene selector при Cancel теперь принудительно получает обратно фактический `scene_definition`, чтобы визуальный выбор не расходился с текущим документом.

## 2026-09-22 — 1.0.40.139 Scene Persistence R2

- Исправлен cache-busting WebView2: `main.html`, `editor.html` и `simulator.html` больше не содержат устаревший query version `1.0.40.137`; все вложенные web assets получают текущую версию сборки.
- В Dialogue/Scene UI текст «Последняя JSON» заменён на «Последняя сцена», потому что canonical ресурс сцены — `.aqscene`.
- Версия приложения и отображаемая версия повышены до `1.0.40.139` перед следующим физическим тестом.
- В GitHub CI исправлен final gate: результат `Scene interface Playwright smoke` теперь действительно передаётся в итоговую проверку.
## 2026-09-22 — 1.0.40.141 Quest ↔ Scene Resource Navigation

- Добавлена двусторонняя навигация по ссылкам ресурсов: `DialogueScene.sceneId` → связанная `.aqscene` и обратно → исходная Quest Graph node по стабильному `NodeId`.
- В Inspector `DialogueScene` рядом с `sceneId` появился переход в соответствующую Scene, только если ссылка существует в `Scene Catalog`.
- При переходе из Quest Graph в Scene сохраняется контекст возврата; Scene Graph и Dialogue Workspace показывают `↩ Вернуться в Quest Graph`.
- Возврат открывает текущий Quest Graph и выбирает исходную node. Если Graph Editor ещё не готов, выбор откладывается до BrowserReady.
- Добавлен централизованный `QuestNodeReferenceCatalog` для текущих и прогнозируемых ссылок: Scene, WorldPoint, Condition, Reward и Item. Для будущих ресурсов UI/editor route может использовать тот же механизм.
- Добавлен отдельный `ci/resource_navigation_smoke.mjs` и подключён к локальному CI и GitHub workflow.
- Текущая версия: `1.0.40.141-QUEST-RESOURCE-NAVIGATION`.
- Физический запуск этой навигации после коммита ещё не выполнялся; предыдущие `575aff1` и `3b26a65` уже прошли физический build/publish и перестроение учебного Quest Graph.

## 2026-09-22 — Single instance и история последних файлов

Приложение запускалось вторым экземпляром при двойном клике по .aqquest/.aqscene,
пока редактор уже был открыт. Теперь работает единственный экземпляр:

- первый процесс захватывает именованный мьютекс (Local\AssistQuestEditor.SingleInstance.<SessionId>)
  и слушает именованный канал с тем же именем сессии;
- последующие запуски передают путь по каналу и завершаются, не создавая окон;
- полученный путь обрабатывается как запрос на открытие, окно поднимается и активируется;
- если канал недоступен (первый экземпляр как раз завершается), второй процесс коротко
  ждёт мьютекс и становится первым; при недоступном канале второго окна всё равно не будет.

Открытие другого файла в занятом редакторе сначала проверяет сохранённость:
для Quest Graph проверка добавлена (строки `ConfirmGraphSwitch`), для Scene она уже была.
Повторное открытие того же файла не считается переключением: диалог не показывается и
документ не перезагружается, поэтому выделение и позиция не теряются.

История последних файлов:

- MRU-список на 10 записей ведётся отдельно для Quest и для Scene;
- список хранится в `ui-settings.json` и чистится от недоступных файлов при старте;
- кнопка «Последние ▾» есть в Нодовом редакторе и в Редакторе сцен;
- клик открывает выбранный файл; текущий документ помечен и повторно не перезагружается;
- открытие файла в одном окне сразу обновляет список в другом.

Проверки: `ci/single_instance_probe.ps1` (запуск двух экземпляров на опубликованном exe)
и `RecentFileListTests` (13 тестов MRU). Итог локального CI: 20 проверок, 0 ошибок.

## 2026-09-22 — 1.0.40.141 Репутация НПЦ и игровой контент

Репутация переведена с фракций на НПЦ и доведена до игровых правил ТЗ.

Шкала (`ReputationScale`, Domain):

- 10000 очков — это 100% прогрессбара; -10000 тоже 100%, прогрессбар не разворачивается;
- нейтральная зона -300..300, серая заливка; выше 300 — lime, ниже -300 — красная;
- подпись показывает очки репутации, а не проценты, со знаком «+» у положительных;
- диапазоны: Подозрительный -500..-300, Чужак -1000..-500, Нежелательный -3000..-1000,
  Враг -6000..-3000 и ниже; Неопасный 300..500, Знакомый 500..1000, Приятель 1000..3000,
  Друг 3000..6000, Свой 6000 и выше.

Репутация ведётся по НПЦ (`npcId`), а не по фракции: канал `reputation` хранит
`NpcReputationEntry` (значение + факт контакта). Контакт фиксируется автоматически,
когда игрок видит реплику или вариант выбора НПЦ, поэтому в списке репутации
появляются только знакомые НПЦ. Незнакомый Speaker (рассказчик) НПЦ не создаёт.

Ноды:

- `AddReputation`/`RemoveReputation` используют параметр `npcId` вместо `faction`;
- добавлена ветвящая нода `ReputationCompare` (`npcId`, `comparison`, `right`);
- `SceneChoiceOption.Requirement` (`npcId`, `minValue`) скрывает вариант выбора:
  недоступный вариант не попадает в список, а не показывается неактивным, и его
  нельзя выбрать даже прямым `optionId`; индекс считается по видимому списку.

Игровой контент учебного квеста:

- за квест Руслана: +500 Руслану и +350 Гоше;
- Гоша продаёт `gosha.homemade_sausage` за 450 ₽, но вариант покупки виден только
  при репутации от 350; каждая покупка даёт +25 репутации Гоши;
- покупка повторяется: после оплаты и начисления репутации поток возвращается
  к взаимодействию с Гошей, выход из магазина ведёт к следующему шагу;
- при репутации Гоши от 400 появляется заказ Руслана на доставку шашлыка:
  +100 Руслану и +150 Гоше.

Новые сцены: `gosha_shop.aqscene`, `ruslan_delivery.aqscene`, `gosha_delivery.aqscene`.

UI: правая панель персонажа получила табы «Персонаж» и «Репутация»; список
репутации прокручивается и показывает портрет, имя, название диапазона и
прогрессбар с цветом заливки.

Проверки: `ReputationScaleTests` и `ReputationRuntimeTests` (144 теста домена),
`ci/check_quest_graph.mjs` (целостность графа и ссылок на сцены),
`ci/reputation_flow.mjs` (повторная покупка, цена, порог 350 и доставка при 400).
Итог локального CI: 22 проверки, 0 ошибок.
