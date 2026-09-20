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
