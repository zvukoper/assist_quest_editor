# Authoring UX — Reference Picker, Data Bridge и Auto Layout Guard

## Статус

Документ обновлён 23.09.2026 после фиксации концепции WORLD_AUTHORING_PLATFORM.md.

Authoring UX теперь является общей инфраструктурой создания Content для параллельного мира, а не набором удобств только для Quest Graph.

Главные инварианты:
- Content и Runtime не зависят от World Provider;
- Simulator является полноценным Sandbox Provider;
- ссылки используют стабильные canonical IDs;
- Coordinate и WorldPoint reference — разные semantics;
- неизвестные ссылки не уничтожаются;
- Auto Layout защищает от плохого imported/agent-created layout и не ломает хороший пользовательский layout.

## 1. Reference Picker

Общий Reference Picker / Parameter Editor должен:
- знать тип ожидаемой ссылки;
- использовать соответствующий каталог;
- искать по имени или ID;
- показывать человеку понятное имя;
- сохранять стабильный ID;
- показывать отсутствующую ссылку;
- не очищать неизвестное значение;
- поддерживать навигацию к связанному ресурсу.

Главный принцип: отображаемое имя и стабильный ID — разные вещи.

## 2. Три класса параметров

### Reference
Первые типы:
- worldPointId → WorldPoint;
- sceneId → Scene;
- itemId → Item;
- npcId → NPC.

В дальнейшем: Dialogue, Choice, Condition, Reward, Skill и другие canonical resources.

### Enum
Закрытые значения: status, comparison, operator, kind, QuestStartMode и другие зарегистрированные enum.

### Free / Autocomplete
Расширяемые значения: step, Fact keys, Variable paths, eventType.

## 3. Реализованный Reference UX baseline

Quest Graph получает:
- Scene catalog;
- Item catalog;
- NPC catalog;
- WorldPoint catalog из того же World Data Channel, который использует Simulator.

Reference-поля используют единый registry для worldPointId, sceneId, itemId и npcId.

Known resource отображается по имени и сохраняется по ID. Unknown resource не теряется и показывает диагностику «не найден».

sceneId сохраняет навигацию «Открыть сцену».

Это первый вертикальный срез общей Reference infrastructure. Новые ресурсы не должны получать отдельные controls.

## 4. WorldPoint и Coordinate

Coordinate — просто X/Y/Z. Для него используется Simulator bridge.

WorldPoint reference — стабильная ссылка на именованный canonical объект мира.

Поэтому:
- coordinate field → Simulator bridge;
- worldPointId → Reference Picker;
- WorldPoint catalog → World Data Channel;
- temporary point → отдельная будущая authoring сущность.

## 5. Simulator coordinate bridge

Базовый bridge уже существует:

Editor → coordinate_request → Host → WorldSelection/Player Data Channel → coordinate → Editor.

Источники:
1. текущая позиция игрока;
2. выбранная точка Simulator.

Следующий UX-шаг — сделать этот bridge доступным непосредственно рядом с coordinate-полями, а не только в World editor.

## 6. Temporary Point

Следующий этап:
- ЛКМ по пустому месту Simulator → временная точка;
- она не попадает автоматически в canonical WorldPoint catalog;
- её можно выбрать;
- координаты доступны через bridge;
- явная команда «Создать WorldPoint» превращает её в canonical resource.

Обычный клик по карте не должен незаметно создавать permanent content.

## 7. Auto Layout Guard

В Domain добавлен QuestGraphLayout.IsObviouslyPoor().

Guard выявляет:
- одинаковые координаты;
- физическое перекрытие нод;
- чрезмерно сжатую область для нескольких нод.

QuestGraphStore.Replace(QuestDefinition) автоматически применяет deterministic QuestGraphLayout для явно плохого imported graph.

AddNode также использует guard:
- хороший layout сохраняется;
- плохой agent-created/imported layout получает автоматическую раскладку.

Ручная «Перестроить» остаётся принудительной операцией.

## 8. Дальнейшее разделение Editor State

Сейчас X/Y находятся в QuestNode как промежуточное решение.

Целевая модель:

Canonical Quest Graph:
- NodeId;
- NodeType;
- Parameters;
- Sockets;
- Connections.

Editor State:
- X/Y;
- Zoom;
- Pan;
- Selection;
- Collapsed state;
- другие визуальные данные.

После этого Auto Layout должен работать с editor state, а не менять semantic resource.

## 9. Общая Resource Reference Registry

Текущий GRAPH_REFERENCE_EDITORS должен эволюционировать в декларативную registry-модель:

ReferenceSpec:
- expected resource kind;
- catalog;
- display formatter;
- ID selector;
- search fields;
- missing-value behavior;
- navigation action.

Специализированные UI должны быть тонкими представлениями этой схемы.

## 10. План каталогов

WorldPointCatalog
NpcCatalog
SceneCatalog
DialogueCatalog
ItemCatalog
ShopCatalog
ServiceCatalog
QuestCatalog
MiniGameCatalog
CommunicationCatalog
InteractionCatalog

UI каталога не должен появляться раньше соответствующей canonical сущности.

## 11. Simulator как authoring context

Simulator предоставляет authoring context:
- текущий Player;
- выбранный WorldPoint;
- временная карта-точка;
- текущий World State;
- доступные catalog entities.

Это не делает Simulator источником истины для Content: он только предоставляет данные через Data Channels/Host bridge.

## 12. Три режима

Authoring → Canonical Content.

Sandbox → управление World State → тот же Runtime.

Game → естественный ввод игрока → тот же Runtime.

Ни один режим не должен создавать отдельную модель квестов.

## 13. Следующий порядок реализации

A. Reference infrastructure — текущий baseline создан для WorldPoint/Scene/Item/NPC. Следом вынести registry из ad-hoc UI и унифицировать formatter/search/missing/navigation.

B. Simulator coordinate bridge — подключить существующий bridge к coordinate-полям.

C. Temporary Point — map click, selection, bridge, явное создание WorldPoint.

D. Canonical catalogs — Dialogue, Choice, Condition, Reward, Skill и т. д. только вместе с canonical resources.

E. Editor State separation — вынести X/Y/zoom/pan/selection из semantic resource.

F. Scenario Runtime — RESET, SET WORLD, SET PLAYER, SET FACT, SET INVENTORY, RUN, EXPECT, SELECT.

## 14. Критерий готовности

Автор должен уметь:
1. выбрать WorldPoint без копирования ID;
2. найти Scene/Item/NPC поиском;
3. видеть имя и стабильный ID;
4. получить координаты игрока;
5. получить координаты выбранной точки;
6. видеть неизвестный ID и не терять его;
7. создавать граф агентом без заранее рассчитанных X/Y;
8. получать Auto Layout только для явно плохой раскладки;
9. открывать хороший граф без неожиданного перемещения;
10. использовать одну Reference infrastructure в новых ресурсах.

## 15. WolvenKit reference

При дальнейшем развитии снова сверяться с актуальным upstream WolvenKit.

В актуальном upstream сохраняются релевантные для нас принципы: resource-oriented editing, contextual property editing, dropdown/search support, специализированные редакторы поверх общей инфраструктуры и работа со стабильными ресурсными ссылками. Мы заимствуем эти принципы, но не REDengine-specific model. citeturn1search5turn1search1

## 16. Что не входит в этот pass

Не смешивать сюда:
- ETS2 integration;
- DayZ integration;
- real telemetry;
- full Shop runtime;
- Radio runtime;
- MiniGame runtime;
- asynchronous player exchange.

Они строятся позже на уже закреплённых World/State/Content/Runtime границах.

## 17. Итог

Authoring UX теперь рассматривается как первая инфраструктура World Authoring.

Первый полезный результат:

выбрать сущность по имени → сохранить стабильную ссылку → получить координату из Sandbox → автоматически получить нормальную раскладку графа.

Дальнейшие редакторы подключаются к этим же механизмам, а не создают новые локальные варианты.

## 8. Location Editor — текущий приоритет

Location Editor принят как основной следующий authoring-инструмент перед расширением вторичных редакторов.

Первый UX-срез:
- Fixed / Dynamic;
- стабильный ID и имя Location;
- выбор Fixed WorldPoint по имени/ID или из Simulator selection;
- динамические критерии как список AND-предикатов с НЕ;
- provider-specific criterion как расширяемый параметр, сохраняемый без потери данных;
- ограничения истории Selection/Visit;
- Показать точку;
- Тест с количеством раундов, по умолчанию 8, и визуализацией выбранных кандидатов на карте.

Общий Condition Tree на этом этапе не нужен. Spatial criteria должны развиваться отдельно от игрового Condition Engine, чтобы географический запрос мог использовать capabilities конкретного World Provider.

Location Editor — canonical resource, поэтому результат Dynamic Query не должен сохраняться как постоянный WorldPoint. Авторский файл хранит правило, а Runtime хранит выбранного кандидата.

Будущие пользовательские группы точек (например, красивые загородные дома у воды) должны стать ещё одним видом spatial source, а не обходным механизмом через ручные координаты.
