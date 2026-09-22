# Память проекта — Assist Quest Editor

## 1. Назначение

Assist Quest Editor — отдельное приложение-песочница и рабочий прототип универсальной системы создания и исполнения сюжетных квестов.

Проект сознательно отделён от ETS2 Assist. На первом этапе запрещено встраивать редактор в ETS2 Assist: сначала на искусственных данных проверяются архитектура, граф логики, состояния, события, условия, диалоги, инвентарь, награды, визуализация и симулятор.

Главная цель — получить зрелое ядро quest system, которое позже можно подключить к ETS2 Assist, а потенциально и к другим играм, например DayZ, через отдельные адаптеры источников данных и экспортёры.

## 2. Базовая идея

Квест не должен знать, откуда пришли игровые данные.

Вместо этого существует абстрактный слой Data Channels. Сейчас каналами управляет Simulator. Позже те же контракты получают реализацию для ETS2 Assist, телеметрии, карты, памяти игры, API игры или других игровых интеграций.

Схема:

Simulator / ETS2 / DayZ / другой источник
→ Data Channel Adapter
→ Data Channel Hub
→ Quest Runtime
→ UI / состояние квеста / эффекты

Quest Runtime не содержит кода конкретной игры.

## 3. Инструменты и диагностика

Операционный набор инструментов и ограничения среды закреплены в `MemoryAI/TOOLING.md`. Перед локальной диагностикой использовать этот документ как источник истины по доступному tooling.

В нём также описан workaround для корпоративной блокировки `file:`: тестовый `data:text/html` документ позволяет отдельно проверить поведение реального browser engine и frontend-кода, не доказывая при этом production-загрузку `file:` ресурсов.

## 3. Главные архитектурные принципы

Проект заимствует лучшие идеи WolvenKit Quest/Scene Editor, но не копирует его типы один-в-один.

Обязательные принципы:

1. Quest Graph и Scene Graph разделены.
2. Состояние квеста отделено от определения квеста.
3. Поток выполнения отделён от данных.
4. Condition является полноценным деревом выражений, а не набором специальных if.
5. Wait / ожидание события является отдельной семантической сущностью.
6. Choice является блокирующим взаимодействием с несколькими выходами.
7. Normal Flow и Interrupt/Cut Flow являются разными видами связей.
8. Каждая нода имеет постоянный идентификатор.
9. Socket является отдельной сущностью и имеет тип, имя и направление.
10. Количество некоторых sockets может быть динамическим.
11. Phase/Scene может содержать вложенный граф.
12. Definition данных не зависит от runtime-состояния.
13. Игровой мир является отдельным слоем от логики квеста.
14. UI является представлением состояния, а не источником истины.
15. Внутренняя модель должна быть расширяемой без переделки существующих нод.

### Городская карта Simulator

- исходные СДО остаются отдельными world points;
- города загружаются отдельным fixture `data/world/cities.json`, собранным из `zvukoper/ets2_assist:data/localized_cities/cities_sibirmap.json`;
- город имеет `WorldPoint.IsCity=true`, не становится редактируемой СДО-точкой и не меняет текущий selection contract;
- Simulator рисует городскую точку отдельным золотистым слоем с названием, а обычные СДО — цветом категории;
- hover точки даёт белую обводку, обычные точки получили постоянную чёрную тень;
- маркер игрока — оранжевая точка с красной обводкой, без кодирования heading формой.
16. Новые игровые интеграции добавляются адаптерами, а не условными ветками внутри Quest Runtime.

## 3.1. Пакетная схема изменений и CI

Основной барьер — локальный прогон `ci/run_local.ps1`. Он повторяет набор проверок `ci.yml` и вызывается из `pull.ps1` до `compile.ps1`; при ошибке сборка не выполняется, а отчёт пишется в `MemoryAI/LOGS/CI_errors.md`.

Автозапуск GitHub Actions по push отключён: `ci.yml` запускается только вручную. Причина — экономия времени Windows runner, так как набор проверок локально тот же.

GitHub CI обязателен, когда нужна проверка того, что локально получить нельзя: чистая среда `windows-latest` без локальных кешей и untracked-файлов, а также зафиксированный в workflow Node.js 22 вместо версии с текущей машины.

Для ускорения диагностики используется кумулятивная схема:

1. связанные изменения одной задачи накапливаются вместе;
2. локальный прогон проверок выполняется до коммита;
3. готовый пакет отправляется одним commit в main;
4. при необходимости проверки на чистой среде запускается один ручной проход `ci.yml`;
5. после результата создаётся только один следующий исправляющий commit, если ошибка найдена.

Не создавать последовательность микрокоммитов одной задачи только ради промежуточных проверок. MemoryAI, документацию, тесты, код и связанные CI-изменения, относящиеся к одному исправлению, по возможности включать в тот же пакет.

Исключения: отдельный checkpoint по прямой просьбе пользователя, независимая крупная часть работы или техническая причина, из-за которой изменения необходимо разделить.

Workflow использует отмену устаревших незавершённых ручных запусков через concurrency, чтобы повторный запуск не оставлял несколько конкурирующих проходов.

## 3.2. Текущий аудит Node Runtime

В Graph Editor зарегистрирован полный текущий каталог NodeType и динамические sockets. Однако наличие типа в каталоге не означает готовность runtime. Матрица истины хранится в `MemoryAI/RUNTIME_NODE_MATRIX.md`.

На 1.0.40.124 собственную runtime-семантику ещё требуется довести для Phase, Reward, And, Or, Not, Switch и Random; у WaitForCondition параметр `conditionId` пока не разрешается в отдельное условие. Condition также поддерживает пока ограниченный набор операторов.

Для ручной проверки всех типов добавлен `data/quests/demo_all_node_types.json`. Он содержит один достижимый поток через все типы и отдельные ветки Choice.

## 4. Первый тестовый сценарий

Первый эталонный сценарий — «Спецмаринад для Руслана».

Сценарий взят из актуального ETS2 Assist, но в этом проекте будет реализован самостоятельно как тестовая история.

Логика:

- Начальное состояние: квест доступен.
- Игрок подходит к Руслану.
- Руслан предлагает две сюжетные ветки: попробовать лучший шашлык или взять поручение.
- При согласии квест становится Active, этап — meat_pickup.
- Появляется интерактивная точка мясника Гоши.
- Гоша выдаёт предмет «Мясо в спецмаринаде».
- Этап становится return_to_ruslan.
- Игрок возвращается к Руслану.
- Руслан проверяет наличие мяса.
- При успешной передаче мясо удаляется.
- Квест получает статус Completed.
- Выдаются награды: легендарный шашлык, репутация Руслана, репутация Гоши.
- Отдельно сохраняется возможность отложить задание и позже получить предложение снова.

Сюжет используется как архитектурный тест, а не как финальный контентный формат.

## 5. Что именно должен проверять первый прототип

Первый прототип должен доказать, что система умеет:

- запускать квест;
- активировать interaction по близости к точке;
- отображать marker;
- строить условие из нескольких частей;
- переключать статус и этап;
- открывать Scene;
- показывать диалог;
- показывать Choice;
- отправлять выбранный вариант обратно в runtime;
- применять Effects;
- хранить Flags;
- работать с Inventory;
- выдавать и проверять предметы;
- выдавать Reputation;
- ожидать событие или условие;
- менять состояние точки мира по этапу;
- визуально отображать изменения прямо на карте Simulator;
- сохранять и восстанавливать состояние квеста;
- показывать диагностические журналы.

## 6. Симулятор

Simulator — не отдельная декоративная форма, а искусственный игровой мир.

Он должен иметь:

- карту тестового полигона;
- ту же систему координат, что и выбранная база точек;
- города и другие точки из подготовленного набора данных;
- маркер грузовика/игрока;
- ручное перемещение маркера;
- боковую панель игровых переменных;
- редактор значений;
- управление Flags;
- управление Inventory;
- генерацию событий;
- отображение активных квестовых точек;
- квестовые уведомления;
- квестовое окно;
- диалоговый интерфейс;
- инвентарь;
- горячие клавиши TAB и I;
- отладочное отображение источника и значения каждого Data Channel.

Перемещение грузовика в Simulator напрямую публикует новую позицию в World/Player Data Channel.

## 7. Data Channels

Минимальный первый набор:

- Player — позиция, базовые игровые состояния.
- World — точки мира и текущая пространственная привязка.
- Facts — произвольные факты/переменные квеста.
- Events — одноразовые и потоковые игровые события.
- Inventory — предметы и количества.
- Reputation — репутации.
- Environment — подготовленное место для погоды/времени/окружения.
- System — технические сигналы runtime.

Каждый канал должен иметь контракт интерфейса. Quest Runtime работает только с контрактом.

## 8. Будущие адаптеры

Позже возможны:

- ETS2 Assist Adapter.
- ETS2 Telemetry Adapter.
- ETS2 World Adapter.
- DayZ Adapter.
- Другие игровые источники.

Для каждого адаптера важны два направления:

1. Импорт состояния/событий из игры в Data Channels.
2. Экспорт действий Quest Runtime обратно в игру.

Это позволит одной quest logic использовать разные источники мировых данных и разные game APIs.

## 9. Визуальная платформа

Основная оболочка — C# WinForms, как в ETS2 Assist.

Для визуально сложных интерфейсов используется WebView2:

- графы;
- карта;
- диалоги;
- квестовое окно;
- симулятор;
- панели и визуальные отладчики.

Web-часть на первом этапе предпочтительно делать на чистых HTML/CSS/JavaScript без тяжёлого фреймворка. Причина — простая интеграция с WebView2, лёгкая диагностика и высокая совместимость с уже используемым подходом ETS2 Assist.

Playwright + Chromium являются обязательным тестовым слоем web UI.

## 10. Локализация

Вся пользовательская часть на русском языке.

Архитектура сразу должна поддерживать локализацию:

- пользовательские строки нельзя разбрасывать по JS/C#;
- тексты UI хранятся через ключи;
- язык выбирается через Localization service;
- первая локаль — ru-RU;
- добавление другой локали не должно требовать изменения бизнес-логики;
- идентификаторы нод и data keys не переводятся;
- экспортёр сам отвечает за соответствие игровым именам.

## 11. Версии

За основу формата берётся схема ETS2 Assist: числовая версия плюс описательный суффикс.

Актуальная рабочая версия ETS2 Assist на момент создания памяти имеет базу 1.0.40.100. Для этого проекта используется собственный описатель.

Базовая версия песочницы: 1.0.40.100-QUEST-EDITOR-SANDBOX-R0.

Перед каждым физическим тестированием пользователем версия должна быть увеличена и описатель должен однозначно отражать изменение.


### Внешний архитектурный reference

Для стратегических и архитектурных решений обязательна сверка с актуальным upstream WolvenKit:

`https://github.com/WolvenKit/WolvenKit`

Основные заимствуемые принципы: IDE/resource-centric workflow, canonical model отдельно от views, graph Node/Socket/Connection, stable IDs, отдельное хранение editor layout state, DI/factories/registries, contextual validation и function-over-form.

Полная процедура и конкретные проверенные источники: `MemoryAI/WOLVENKIT_REFERENCE.md`.

Reference последовательно использовать перед изменениями, которые могут повлиять на canonical model и дальнейшую расширяемость. Сознательные архитектурные расхождения необходимо документировать.

## 12. Текущий этап

Каркас приложения уже создан и прошёл физическую проверку. Simulator реально загружает 5115 точек СДО из `data/world/sdo_points.json`, показывает их на карте по координатам ETS2 X/Z, использует цвет категорий, выделяет выбранную точку толстой оранжевой обводкой и показывает красный треугольник игрока.

Между Simulator и Editor стабилизирован контракт WebView2: сообщения сериализуются в camelCase. Выбранная СДО и координаты игрока успешно передаются в редактор мира/локаций.

Текущая версия проекта: `1.0.40.111-QUEST-RUNTIME-R1`.

Текущий функциональный фокус — Quest Runtime и его автоматическая регрессия. Quest Graph authoring baseline уже включает drag, pan/zoom, dynamic sockets, undo/redo, validation и JSON persistence. Реальная интеграция с ETS2 Assist, телеметрией и игровыми событиями пока запрещена.

## 14. Фактическое состояние на 2026-09-20

### Рабочая инфраструктура

- WinForms Host + WebView2;
- отдельное окно Simulator с автоматическим размещением на втором мониторе;
- загрузка СДО ETS2 Assist: 71 категория / 5115 точек;
- выбор СДО на карте и red player marker;
- передача выбранной СДО и координат игрока в World Editor;
- диагностический лог `MemoryAI/LOGS/assist_quest_editor.log`;
- version cache-busting WebView2;
- CI с Node/Chromium/Playwright/.NET/single-file publish.
- При compile.ps1 контекст исходного Git-репозитория (root, remote, branch, commit) вшивается в assembly metadata; опубликованный EXE использует этот root для записи MemoryAI/LOGS и команды push.

### Quest Graph

Canonical model:
- `QuestGraph`;
- `QuestNode`;
- `SocketDefinition`;
- `QuestConnection`;
- `QuestGraphStore`;
- `QuestGraphFactory`;
- `QuestNodeCatalog`.

Starter graph:
`Start → Condition → DialogueScene → End`, 4 ноды и 3 connections.

Graph Store:
- AddNode;
- UpdateNode;
- RemoveNode;
- Connect;
- Disconnect;
- NodeId сохраняется при редактировании;
- удаление ноды очищает связанные connections;
- connections разрешены только Output → Input;
- duplicate connection запрещена.

Host:
- один `QuestGraphStore` создаётся в `MainForm`;
- тот же store передаётся всем `EditorForm`;
- Graph editor получает изменения через WebView2 actions;
- Web JSON сериализуется camelCase.

Graph Web UI:
- выбор ноды;
- добавление ноды из списка зарегистрированных типов;
- редактирование Title/X/Y;
- удаление ноды;
- создание связи кликом Output → Input;
- удаление связи;
- отображение sockets и connections в inspector.

Пока не реализованы:
- comments;
- copy/paste;
- multi-select;
- minimap;
- полноценные schema-редакторы Quest/Scene/Condition/Effect;
- отдельная persistence для Quest Runtime State.

### Регрессии, которые уже исправлены

- ранняя отправка snapshot до NavigationCompleted;
- PascalCase snapshot → camelCase contract;
- PascalCase simulator_context / coordinate → camelCase;
- отсутствие `formatPosition()` в Simulator Web UI.

### Контроль продолжения для другого агента

Начинать с ветки `main`. Сначала проверить HEAD, CI и версии файлов. Затем прочитать `MemoryAI/INSTRUCTIONS.md`, `MemoryAI/ARCHITECTURE.md`, `MemoryAI/CURRENT_TASK.md` и этот файл.

Не переносить canonical graph state в JavaScript. Не подключать ETS2 Assist/telemetry/runtime integration, пока не выполнен следующий утверждённый этап sandbox.

Следующий логичный шаг — довести canonical Quest Runtime handlers и пройти автоматическую матрицу из семи сценариев. После этого реализовать полный сценарий Руслана и Runtime UI.

## 13. Что нельзя потерять при смене агента

Нельзя возвращаться к архитектуре «JSON с next и набором эффектов» как к основному внутреннему представлению.

JSON может быть форматом хранения/экспорта, но внутреннее представление должно быть графовым.

Нельзя смешивать:

- состояние;
- world points;
- события;
- UI;
- scene presentation;
- quest logic;
- игровые интеграции.

Нельзя превращать Simulator в специальный режим Quest Runtime. Simulator — отдельный источник данных через те же контракты, через которые позже придут реальные игры.


## 15. Глобальное состояние окон

Версия 1.0.40.121-QUEST-WINDOW-LAYOUT-R1 вводит централизованное сохранение геометрии окон в %LOCALAPPDATA%/AssistQuestEditor/ui-settings.json. Для окон сохраняются координаты, размер и состояние Normal/Maximized. Восстановление проверяет доступные рабочие области экранов и возвращает потерянное окно на основной экран, если сохранённая позиция больше не видима.

Окно журнала при стандартных настройках отделено от сайдбара и занимает всю высоту рабочей области основного монитора. Глобальная кнопка настроек открывается шестерёнкой рядом с версией; команда «Сброс настроек окон» удаляет сохранённую геометрию всех окон.


## 2026-09-21 — Интерфейсные запросы Runtime

Для интерактивных Runtime-нод введён отдельный Data Channel `interfaces`. Сейчас он используется для Choice: Runtime публикует `InterfaceChoiceDialog`, Simulator отображает модальное окно, а ответ UI возвращает через `IEventChannel` событием `ChoiceSelected` с `requestId` и `index`. RequestId уникален для каждого показа интерфейса, чтобы старые ответы не пересекались с новым запуском квеста.


## 2026-09-21 — Inventory technical debt

Зафиксировано: подключение Inventory к Quest Runtime уже рабочее для базовых `GiveItem`, `RemoveItem` и `ItemCountCompare`, однако semantics пока минимальны. Отложены полноценная обработка недостаточного количества при RemoveItem, stack/unique rules, validation item definitions и связанные inventory constraints. Это сознательно не блокирует текущий Scene Editor этап.


## 2026-09-21 — Scene Editor R1 checkpoint

Canonical Scene authoring следует тому же правилу, что и Quest Graph: Web UI отправляет действия, источником истины остаётся domain resource.

Контракт:
- SceneDefinition — полный ресурс сцены;
- SceneGraphStore — mutable authoring state внутри Host;
- SceneGraphLayout — детерминированная раскладка нод;
- SceneGraphValidator — domain validation;
- EditorForm — transport/actions и JSON persistence;
- sceneEditor.js — presentation: drag/pan/zoom, sockets, context menu, inspector и toolbar.

Обязательная кнопка «Перестроить» должна сохраняться во всех последующих версиях, пока Scene Editor содержит ноды.


## 2026-09-21 — Scene Editor interaction parity checkpoint

Scene Editor canvas должен оставаться производной от отработанного Quest Graph canvas. При последующих изменениях сначала проверяется решение Quest Graph и только затем оно переносится в Scene Editor с заменой canonical actions/model.

Не создавать отдельную механику drag/pan/zoom/socket hover/context menu без необходимости. sceneEditor.js и editor.js должны сохранять functional parity по базовым canvas interactions.


## 2026-09-22 — Resource format registry

Stable resource namespace is now fixed as long descriptive extensions rather than abbreviated editor-specific extensions. Canonical registry: `src/AssistQuestEditor.App/Core/ResourceFileTypes.cs`.

Current types:
`.aqquest` Quest, `.aqscene` Scene, `.aqdialogue` Dialogue, `.aqcampaign` Campaign, `.aqpoint` World Point, `.aqcity` City, `.aqitem` Item, `.aqloc` Localization, `.aqregistry` Node Registry, `.aqsnapshot` Runtime Snapshot, `.aqresource` reserved generic resource.

Schema revisions keep the same extension and change `schemaVersion`; JSON also contains `format` as a defensive type discriminator.

Windows file associations are per-user under HKCU\\Software\\Classes with versioned ProgIDs, OpenWithProgIds, DefaultIcon and open command. Registration does not overwrite an existing user-selected association. Resource-specific ICO files are generated into %LOCALAPPDATA%\\AssistQuestEditor\\FileIcons and Explorer is refreshed with SHChangeNotify.

Current Quest and Scene editors are migrated from generic JSON to `.aqquest` and `.aqscene`, including data files, loaders, publish globs, Open/Save filters and double-click activation. Detailed contract: `MemoryAI/RESOURCE_FILE_FORMATS.md`.


## 2026-09-22 — Scene Dialogue is now a real Runtime phase

The canonical Scene pipeline is now fully interactive for dialogue:

`Quest DialogueScene` references a canonical `.aqscene`; Scene Runtime executes `SceneStart → Dialogue → Choice → SceneEnd` with explicit waits.

Dialogue uses `InterfaceDialogue` and a unique requestId. The Simulator interface renders the dialogue and sends `DialogueContinue`; Scene Runtime validates the requestId before advancing. Choice keeps the existing `InterfaceChoiceDialog / ChoiceSelected` contract.

Do not reintroduce direct Quest-level dialogue presentation as the primary path. New conversation content belongs to Scene resources. The current sandbox keeps Dialogue/Choice resource collections inside SceneDefinition; `.aqdialogue` and `.aqchoice` are reserved resource file types for the later resource extraction layer, not required to break Scene ownership now.

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