# Архитектура проекта

## 1. Слои

### Host
WinForms C#.

Отвечает за жизненный цикл приложения, окна, WebView2, файловую систему, логи и интеграционные сервисы.

### Web Presentation
HTML/CSS/JavaScript в WebView2.

Отвечает только за визуальное представление и ввод пользователя для web-интерфейсов.

### Quest Domain
Чистая игровая логика:

- QuestDefinition;
- QuestGraph;
- SceneGraph;
- Node;
- Socket;
- Connection;
- Condition;
- Effect;
- QuestState;
- Interaction;
- Reward.

Домен не знает о WinForms, WebView2, ETS2, DayZ и конкретной телеметрии.

### Quest Runtime
Исполняет граф:

- получает сигналы;
- читает Data Channels;
- меняет QuestState;
- исполняет Effects;
- открывает Scene;
- ждёт Conditions/Events;
- публикует runtime events для UI.

### Data Channel Layer
Унифицированный вход/выход игрового состояния.

Минимальные контракты:

IDataChannelHub
IDataChannel<T>
IEventChannel<T>
IDataSourceAdapter

Runtime должен получать зависимости через интерфейсы.

### Simulator Adapter
Реализация IDataSourceAdapter для виртуальной игры.

### Future Game Adapters
ETS2 Assist, telemetry, DayZ и другие адаптеры реализуются вне Quest Domain.

## 2. Quest Graph

Основной граф отвечает на вопрос: «что происходит с квестом?»

Базовые типы первого этапа:

- Start;
- End;
- Phase;
- Interaction;
- Condition;
- And;
- Or;
- Not;
- Switch;
- Random;
- Wait;
- WaitForCondition;
- WaitForEvent;
- SetStatus;
- SetStep;
- SetFlag;
- SetVariable;
- DialogueScene;
- Reward;
- GiveItem;
- RemoveItem;
- AddReputation;
- RemoveReputation.

Типы можно расширять через Node Registry.

## 3. Scene Graph

Scene отвечает на вопрос: «как выглядит и ощущается конкретная сцена?»

Первый минимальный набор:

- SceneStart;
- Dialogue;
- Choice;
- SceneWait;
- SceneEvent;
- SceneEnd.

Позже сюда добавляются:

- timeline;
- actor;
- animation;
- audio;
- camera;
- VFX;
- branching;
- interrupt;
- subtitles;
- presentation effects.

Scene может быть вложенной в Quest Graph через ссылку на ресурс Scene.

## 4. Состояние

Definition и Runtime State не объединять.

QuestDefinition неизменяемо описывает квест.

QuestState хранит:

- Status;
- CurrentStep;
- Flags;
- Variables;
- Dialogue state;
- completed/visited data;
- runtime timestamps.

Inventory, Reputation и глобальные Facts являются отдельными domain state stores, но доступны через Data Channels.

## 5. Ноды

Каждая нода:

- имеет NodeId;
- имеет NodeType;
- имеет position для редактора;
- имеет параметры;
- имеет sockets;
- может иметь metadata.

NodeId постоянен внутри ресурса. Переименование ноды не должно менять её ID.

NodeType является зарегистрированным техническим типом.

Пользовательский заголовок является отдельным полем.

## 6. Sockets

Socket:

- SocketId;
- Name;
- Direction;
- FlowKind;
- optional metadata.

Directions:

- Input;
- Output.

FlowKind первого этапа:

- Normal;
- Cut.

Cut резервируется под interrupt/принудительное прекращение текущего потока по принципу WolvenKit.

Связь хранит исходный SocketId и целевой SocketId. Нельзя считать координаты нод достаточным источником связей.

## 7. Conditions

Condition — дерево.

Примеры:

ItemCount(special_marinade_meat) >= 1

QuestStep == return_to_ruslan

Distance(player, ruslan) <= 35

AND(A, OR(B, C))

Первый набор операндов:

- FactEquals;
- FactCompare;
- FlagEquals;
- QuestStatusIs;
- QuestStepIs;
- ItemCountCompare;
- ReputationCompare;
- DistanceCompare;
- VariableCompare;
- EventOccurred;
- True/False.

Condition system должна позволять добавлять новые operators без переписывания runtime.

## 8. Events

Событие является отдельным сообщением, а не Boolean-переменной.

Минимальная модель:

EventType
Timestamp
Source
Payload

Примеры:

- PlayerMoved;
- InteractionEntered;
- InteractionExited;
- HornPressed;
- InventoryChanged;
- ItemReceived;
- CustomEvent.

WaitForEvent подписывается на event channel и завершается по совпадению фильтра.

## 9. Effects

Effect описывает последствия:

- SetStatus;
- SetStep;
- SetFlag;
- SetVariable;
- AddItem;
- RemoveItem;
- AddReputation;
- AddValue;
- Notify;
- Complete;
- Fail.

Effect не должен напрямую вызывать WinForms или Javascript.

Runtime публикует результат через события и Data Channels.

## 10. Interaction / World

Interaction связывает quest logic с точкой мира.

Разделяются:

- WorldPoint definition;
- WorldPointResolver;
- Interaction availability;
- Marker state;
- Trigger radius;
- visual state.

В будущем resolver может получать точки:

- из статической базы;
- из ETS2 Assist;
- из карты;
- из игры;
- из генератора.

Quest Runtime не обязан знать, как была найдена координата.

## 11. Persistence

Минимально:

QuestSaveState
InventoryState
GlobalFactsState
ReputationState

Сохранение не должно содержать UI-координаты редактора без специального слоя editor metadata.

Definition files и player save state хранятся раздельно.

## 12. Расширяемость

Node Registry регистрирует NodeType → factory/editor/runtime handler.

Для нового типа ноды отдельно определяются:

- schema;
- runtime handler;
- visual editor;
- serialization;
- optional validation.

Это оставляет путь для постепенного переноса дополнительных идей WolvenKit без разрушения базовой модели.

## 13. Editor model

Редактор имеет три связанные, но независимые представления:

1. Quest Graph Editor.
2. Scene Editor.
3. World/Simulator Editor.

World editor не меняет логику графа автоматически.

Graph editor может ссылаться на world objects и scenes.

Scene editor может ссылаться на quest facts/events, но не владеет их состоянием.

## 14. Import/Export

Внутренний граф — canonical model.

JSON — первый формат хранения и тестирования.

Позже:

- ETS2 exporter;
- ETS2 Assist bridge;
- DayZ exporter;
- другие exporters.

Экспортёр переводит canonical quest model в API конкретной игры.

## 15. UI truth

Source of truth:

1. Quest Runtime / Domain State.
2. Data Channels.
3. Web UI.

Нельзя делать DOM источником состояния.

После перерисовки WebView состояние должно быть восстановлено из backend/domain snapshot.

## 16. Двухоконная рабочая станция

Приложение имеет два независимых рабочих окна:

1. Главное окно редактора — проектирование определений.
2. Окно Simulator — искусственный игровой мир и управление живыми данными.

Главное окно не должно содержать игровую симуляцию внутри себя. Оно запускает дочерние редакторы и предоставляет навигацию между ресурсами.

Simulator автоматически переносится на второй физический монитор, если он доступен. При одном мониторе используется отдельное окно на основном экране.

## 17. Редакторы главного окна

### 17.1. Два способа открыть редактор

Есть ровно два разных пользовательских сценария, и их нельзя смешивать:

1. **Левый сайдбар — это селектор содержимого рабочей области.** Клик по пункту
   сайдбара заменяет панель в ЭТОМ ЖЕ окне. Второе окно не создаётся.
2. **Кнопка «Открыть» в главной форме — это запрос нового окна.** Каждый вызов
   открывает отдельное окно редактора со своей панелью.

Это соответствует концепции File Editor в WolvenKit: дерево слева выбирает
содержимое правой панели, а документы живут вкладками в том же окне, а не
множеством самостоятельных окон.

Реализация в Host:

- `EditorForm` держит ОДНУ изменяемую строку `_activePane` (`graph`, `scene`,
  `dialogue` или `locations`) вместо набора readonly-флагов; свойства `IsGraph`,
  `IsScene`, `IsDialogue`, `IsLocation` — вычисляемые;
- переключение панели внутри окна идёт сообщением `activate_pane` (Web → Host) с
  ответом `active_pane` (Host → Web); это НЕ `open_editor`;
- `MainForm` хранит список окон (`List<EditorForm>`): `OpenEditor` поднимает уже
  существующее окно нужной панели, `CreateEditor` всегда создаёт новое;
- межредакторная навигация (Scene → Quest Graph и обратно) использует `EnsureEditorFor`,
  то есть ищет или создаёт окно, но не плодит дубликаты;
- информационные панели (Data Channels, Conditions, Validation, Node Registry и
  т.п.) переключаются локально в Web UI и в Host не сообщают.

Базовый набор:

- Quest Graph Editor — канонический граф логики;
- Scene Graph / Dialogue Editor — сцены, диалоги и выборы;
- World / Point Editor — WorldPoint, координаты, радиусы, маркеры, anchors и Resolver;
- Data Channel Inspector — описание каналов и контрактов;
- Condition / Effect Editor — деревья условий и каталог последствий;
- Localization Editor — строки через ключи и ресурсы локалей;
- Validation — диагностика графов, ссылок и схем;
- Node Registry — расширение NodeType, schema, runtime handler, visual editor и serialization.

При дальнейшем развитии добавляются:

- редактор Rewards;
- редактор Item definitions;
- редактор Interaction definitions;
- редактор Event definitions;
- редактор Variables/Facts schemas;
- Project/Asset Browser;
- Runtime Trace / Debugger;
- Import/Export и миграции схем.

Эти инструменты не должны превращать runtime-state в часть definition model.

## 18. Редакторы Simulator

Simulator предоставляет отдельные accordion-панели:

- Игрок и мир;
- Факты;
- Статусы;
- Состояния;
- Инвентарь;
- Репутация;
- Телеметрия;
- Окружение;
- События;
- Система / диагностика.

Игрок изменяется числами X/Y/Z, быстрыми переходами к тестовым точкам, кликом по карте или перетаскиванием маркера.

Телеметрия расширяема и уже содержит скорость, RPM, газ, тормоз, руль, топливо, температуры, повреждения и Horn.

События вводятся вручную как EventType + Source + Payload. Это позволяет воспроизводить будущие игровые сигналы без реальной игры.

## 19. Визуальное правило

Весь web UI наследует текущую палитру и базовые движения ETS2 Assist:

- accent #fab003;
- accent-2 #b4810c;
- panel #262626;
- bg #0a0c10;
- hover #181f23;
- selected #4b5a66;
- text #e7edf4;
- muted #a6a6a6;
- blue #12abe5;
- red #cf0c0c;
- lime #11fb06;
- шрифт Open Sans с системным fallback;
- тёмные полупрозрачные панели;
- тонкие разделители;
- вертикальные скроллбары;
- плавные fade/slide/zoom-анимации без резких cut-переходов.

Единая тема хранится в src/AssistQuestEditor.App/Web/theme.css.


## 19. Внешний архитектурный reference: WolvenKit

Наш заявленный «прародительский» reference — upstream **WolvenKit**: https://github.com/WolvenKit/WolvenKit

Обязательные для нас заимствуемые принципы:

- resource/document-centric IDE-like workflow;
- platform-independent application/domain logic отдельно от UI;
- DI, factories, services и registries для расширения;
- canonical graph как Node/Socket/Connection model;
- стабильные идентификаторы;
- dynamic sockets как часть модели, а не UI hack;
- editor layout/viewport state отдельно от canonical graph content;
- contextual validation;
- function over form;
- специализированные редакторы поверх общей инфраструктуры.

Важно: WolvenKit решает задачу authoring файлов REDengine, а Assist Quest Editor — задачу универсального quest authoring/runtime. Поэтому мы **сверяем принципы, а не копируем типы и форматы**.

Подробный конспект, список проверенных исходников и обязательная процедура сверки находятся в `MemoryAI/WOLVENKIT_REFERENCE.md`.

Перед архитектурными изменениями агент обязан проверить актуальный upstream `main` WolvenKit и зафиксировать существенные расхождения.


## 11. Интерфейсный канал

Runtime не вызывает Web UI напрямую. Интерактивные запросы пользовательского интерфейса передаются через отдельный Data Channel `interfaces`.

Для Choice используется `InterfaceState.ActiveDialog`, содержащий стабильный на время ожидания `RequestId`, говорящего, текст и список вариантов. UI является только представлением этого состояния.

Ответ пользователя возвращается через существующий `IEventChannel<SimulatorEvent>` как `ChoiceSelected` с `requestId` и `index`. Runtime проверяет requestId и только затем меняет состояние графа.

Такой contract позволяет Simulator использовать тот же интерфейсный поток, который позже сможет реализовать реальный игровой adapter, без ссылки Domain/Runtime на WinForms или WebView2.


## Scene Graph и SceneRuntime

Scene Graph является самостоятельной canonical моделью, а Quest Graph не хранит presentation text конкретного Choice. DialogueScene — оркестрационный узел Quest Graph: он запускает SceneRuntime по стабильному sceneId и ждёт SceneCompleted.

Минимальный canonical Scene resource содержит SceneGraph, SceneDialogue, SceneChoice и SceneChoiceOption. Каждый SceneChoiceOption имеет стабильный ID и явный OutputSocketId. Интерфейсный канал получает из него только presentation request; Web UI не является источником истины.

В актуальном WolvenKit scnChoiceNode является Scene Graph node, а scnChoiceNodeOption связывается с отдельным screenplay item через ScreenplayOptionId; текст разрешается через screenplay/localization storage. Наш SceneChoiceOption — упрощённый game-agnostic эквивалент этого принципа. Полноценный localization resource layer добавляется отдельно.

Текущий Quest Graph Choice сознательно оставлен как compatibility/regression seam. После физической проверки DialogueScene → SceneRuntime → Choice его presentation-параметры можно выводить из canonical Scene модели и затем убрать прямую зависимость Choice от QuestNode.Parameters.

## 20. Simulator inventory, player HUD and character state

Simulator gameplay state is divided by responsibility:

- `InventoryState` stores stable `ItemId` → quantity and a separate `NewItemIds` presentation-state set.
- `ItemDefinition` stores presentation metadata (`Name`, `Description`, `Category`, `Color`) independently from quantity.
- `PlayerVitalsState` stores health, energy, hydration and fatigue.
- `PlayerProgressState` stores money, experience and a reserved third resource.
- `CharacterState` stores SPECIAL-like stats, skills and reserved Buffs/Debuffs.

Quest-driven inventory changes are published as a dedicated `InventoryChanged` event. Simulator presentation shows acquisition/removal notifications only when the event source is `QuestRuntime`. Manual simulator inventory edits therefore remain silent.

The current starter item catalog is intentionally tiny and code-backed. The intended future direction is a resource-centric Item Editor and item definition files; the gameplay inventory must continue to refer to stable ItemIds.

Player-state quest effects are explicit node types: `SetHealth`, `SetEnergy`, `SetHydration`, `SetFatigue`, `AddExperience`, `AddMoney`, `RemoveMoney`, `SetCharacterStat`. They modify channels through Runtime and never call Web UI directly.

Backpack, item 'new' beacon, notifications, tooltips and Character panel are Simulator presentation. They are not part of QuestDefinition.

Buff/debuff semantics are documented separately in `MemoryAI/CHARACTER_EFFECTS.md`. In this stage they are reserved only: not displayed and not automatically applied.


## 21. World Authoring & Simulation Platform

Проект эволюционирует от узкого Quest Editor к универсальной платформе создания и
симуляции параллельного интерактивного мира. Пользовательское название Assist Quest
Editor сохраняется как исторически понятное, но архитектурно Quest не является верхним
уровнем модели.

Главный инвариант:

> Content и Runtime не знают конкретный World Provider.

Модель слоёв:

- World Provider — Sandbox, ETS2, DayZ и будущие adapters;
- World State — текущее состояние мира и игрока;
- Content — Quest, Scene, Dialogue, Interaction, WorldPoint, NPC, Item и будущие
  Shop/Service/Communication/MiniGame/UserContent;
- Runtime — Conditions, Events, Actions/Effects, Interaction Resolver, Quest/Scene
  Runtime и специализированные runtime-подсистемы;
- Presentation — карта, диалоги, inventory, shop, radio, mini-games и HUD;
- Authoring — создание canonical Content;
- Simulation — искусственное управление тем же World State и тем же Runtime.

Authoring, Sandbox и будущий Game mode являются разными способами использовать один
Runtime, а не тремя реализациями игровой логики.

Sandbox считается полноценным World Provider и должен стать основным инструментом
первичной проверки новых механик. ETS2 является первым реальным внешним Provider, а
не владельцем canonical model.

Первая публичная цель — вертикальный demo slice: WorldPoint → Interaction → Condition
→ Quest → Scene/Dialogue → Choice → State/Item/Money/Reputation → следующий
Interaction → завершение. Он должен полностью работать в Sandbox до подключения
реального Provider.

Подробная формулировка цели и границ находится в
MemoryAI/WORLD_AUTHORING_PLATFORM.md. Authoring UX и Reference/Auto Layout правила
зафиксированы в MemoryAI/AUTHORING_UX_PLAN.md.

## 8. Location Resource и динамический пространственный подбор

Location является отдельным canonical Content resource и не равен WorldPoint.

- WorldPoint — конкретная точка, которую предоставляет текущий World Provider.
- Location — логическое авторское место, которое может указывать на фиксированный WorldPoint или динамически искать подходящего кандидата.

Формат .aqlocation хранит:
- Id, Name, Description;
- Mode = Fixed | Dynamic;
- для Fixed — WorldPointId;
- TriggerRadius;
- LocationQuery;
- набор пространственных Criterion;
- ограничения History.

Spatial Query намеренно не объединён с общим Condition Tree. Общие Conditions отвечают за игровую логику, а Location Query — за поиск географического кандидата среди возможностей World Provider.

Принцип разрешения:
1. Dynamic Location получает множество кандидатов от World Provider;
2. применяет поддерживаемые критерии и ограничения истории;
3. выбирает кандидата случайно;
4. конкретный результат кэшируется на время текущей Runtime/Simulation session;
5. явный authoring-тест может выполнить новое независимое разрешение.

Неподдерживаемый Provider-критерий не должен превращаться в true. Resolver обязан вернуть диагностику и Supported = false.

История выбора/посещения является Runtime/Simulation state, а не canonical Location content. В будущем она должна жить через Data Channel/Runtime persistence и использоваться для повторного отбора.

Первый Sandbox набор критериев ограничен доступными WorldPoint данными: категории, имя, расстояние до WorldPoint, отрицательные категории и **соседство по категории**. ETS2/DayZ-specific свойства (тип дома, дорога, объекты сцены, полиция/транспорт и т.п.) подключаются через Provider capabilities, не меняя формат Location.

Критерии соседства (Sandbox):

- `NearbyCategory` (`value`, `meters`) — рядом с кандидатом в радиусе есть хотя бы одна точка указанной категории. Пример: «мужчина рядом с котом», «разбитая машина рядом с магазином».
- `NoNearbyCategory` (`value`, `meters`) — в радиусе нет ни одной точки указанной категории. Пример: отдельно стоящий человек.

Это два РАЗНЫХ критерия, а не один с «НЕ»: «НЕ (есть сосед)» и «(нет соседей)» не эквивалентны, когда подходящих соседей несколько. Соседом не считается сам кандидат, радиус обязан быть положительным.

`MinDistanceBetweenCandidates` (`meters`) — не фильтр кандидатов, а стратегия отбора раундов: выбранные точки обязаны отстоять друг от друга не менее чем на указанное расстояние, что распределяет набор по площади. Если кандидатов не хватает, раунды всё равно выдаются, но диагностика сообщает, что дистанция не выдержана.

Планируемые расширения:
- пользовательские группы точек и критерий InGroup;
- группы/комбинации групп;
- provider-specific spatial predicates;
- полноценная история Selection/Visit;
- критерии игрового и реального времени;
- weighted/anti-repeat selection.
