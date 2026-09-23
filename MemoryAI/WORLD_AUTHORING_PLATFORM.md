# Концепция проекта — World Authoring & Simulation Platform

## Статус

Зафиксировано 23.09.2026 как архитектурное продолжение проекта **Assist Quest Editor**.

Название репозитория и пользовательское название **Assist Quest Editor** сохраняются. Исторически проект вырос из редактора квестов, и для игрока это по-прежнему самое понятное краткое описание. Внутренняя архитектура и документация больше не должны искусственно ограничивать проект словом «квест».

> **Assist Quest Editor — это редактор и runtime-платформа для создания, симуляции и последующего прохождения интерактивного мира поверх различных источников мира. Квесты являются первым и главным authoring-сценарием, но не пределом модели.**

## 1. К чему эволюционировал проект

Первоначальная задача была простой: создать редактор квестов, проверить граф, запустить его в симуляторе и позднее подключить ETS2.

В процессе разработки стало очевидно, что квесты, сцены, NPC, предметы, репутация, инвентарь, точки мира, события, радио, магазины, сервисы и мини-игры требуют одних и тех же базовых механизмов состояния, условий, событий, ссылок и действий.

Поэтому правильная единица проектирования теперь — **мир**.

Квест — один из видов контента, который работает внутри этого мира.

## 2. Параллельный мир

Проект создаёт не замену ETS2 и не набор скриптов, привязанных к ETS2.

Он создаёт **параллельный интерактивный мир**, который может использовать внешний физический мир как один из способов перемещения и получения данных.

```
                    ПАРАЛЛЕЛЬНЫЙ МИР

      Квесты / Сцены / Диалоги / NPC
      Предметы / Инвентарь / Экономика
      Репутация / Факты / Переменные
      Радио / События / Мини-игры
      Маркеры / Геокэши / Маршруты
      Пользовательский контент
                       |
                WORLD RUNTIME
                       |
          +------------+------------+
          |            |            |
       Sandbox       ETS2          DayZ
       Provider     Adapter       Adapter
```

Сам мир не обязан исчезнуть вместе с конкретной игрой.

## 3. Жёсткая архитектурная граница

Главный инвариант:

> **Content и Runtime не знают, какой World Provider находится под ними.**

Квест не спрашивает, в ETS2 ли он, как ETS2 хранит координаты или как DayZ обозначает игрока. Он работает через общие контракты: позиция, точки мира, время, погода, события, состояние игрока и доступные взаимодействия.

Provider переводит свои данные в эти контракты.

## 4. Слои системы

### World Provider
Источник физического или виртуального мира: Standalone/Sandbox, ETS2, DayZ и будущие providers.

### World State
Текущее состояние мира и игрока: позиция, время, погода, WorldPoints, факты, переменные, статусы, инвентарь, деньги, опыт, репутация, характеристики, телеметрия, runtime-состояния и события.

### Content
Канонические определения: Quest, Scene, Dialogue, Interaction, WorldPoint, NPC, Item, а затем Shop, Service, Reward, Condition, Event, Communication, MiniGame, Route, Marker и UserContent.

### Runtime
Единый исполнитель мира: Condition Engine, Event Bus, Action/Effect Engine, Interaction Resolver, Quest Runtime, Scene Runtime, Communication Runtime, MiniGame Runtime и Persistence.

### Presentation
Карта, маркеры, диалоги, уведомления, инвентарь, магазин, радио, мини-игры, HUD и будущий overlay.

### Authoring
Инструменты создания Content: Quest Graph, Scene/Dialogue, World/Point, NPC, Item, Condition/Effect, Interaction, Validation и Resource Browser.

### Simulation
Искусственное управление тем же World State и Runtime: teleport, факты, репутация, предметы, время, события, snapshots и сценарии.

## 5. Три режима одного Runtime

Не должно быть трёх разных реализаций мира.

**Authoring** создаёт Content.

**Sandbox / Simulation** мгновенно меняет состояние и инъектирует события.

**Game** позволяет игроку естественно ходить, ехать, ждать, взаимодействовать, покупать, разговаривать и получать последствия.

Разница только в способе подачи входных данных и UI.

## 6. Quest — не центр всей модели

Квест остаётся важнейшим объектом, но рядом существуют самостоятельные сущности.

```
NPC
  └─ Interaction Hub
       ├─ Introduction
       ├─ Common dialogue
       ├─ Quest entry
       ├─ Shop
       ├─ Service
       └─ Special interaction
```

Поэтому:

**Quest != Dialogue != Scene != Interaction Point.**

## 7. Interaction как точка входа

```
Interaction
    ↓
Context
    ↓
Available Entries
    ↓
Conditions
    ↓
Scene / Dialogue / Quest / Shop / Service / ...
```

Например, один NPC может иметь первое знакомство, постоянный общий диалог, отдельные квесты и специальные варианты, доступные по репутации, времени, фактам и состоянию мира.

## 8. Единый Action/Effect System

Последствия разных подсистем должны использовать общий слой:

- GiveItem / RemoveItem;
- BuyItem / SellItem / ConsumeItem;
- AddMoney / RemoveMoney;
- AddFact / SetVariable;
- StartQuest / CompleteQuest / StartScene;
- OpenShop;
- StartMiniGame;
- SendRadioMessage;
- CreateWorldPoint / RevealLocation / AddMapMarker / AddRoute;
- Sleep / Wait;
- RepairTruck;
- ChangeReputation;
- Notify.

Новый контентный тип не должен изобретать собственную систему последствий.

## 9. Мини-игры

Сложное действие может иметь собственный MiniGame Runtime. Например, ремонт:

```
Диагностика
    ↓
Разборка
    ↓
Доступ к неисправной системе
    ↓
Repair MiniGame
    ↓
Сборка
    ↓
Запуск двигателя
```

MiniGame возвращает результат: success/failure, quality, timeSpent, damage и rewards.

## 10. Коммуникация и радио

Радио не должно быть специальным видом Quest Dialogue.

```
Communication
  ├─ CB
  ├─ Long Range Radio
  ├─ Phone
  ├─ Messages
  └─ Emergency Channel
```

Сообщение имеет sender, channel, priority, conditions, delay, cooldown/once policy, payload и effects.

## 11. User Content и асинхронный обмен

В перспективе между игроками могут переноситься маршруты, координаты, заметки, геокэши, фотографии, предметы, квесты и пользовательские события.

Начальная модель:

```
World Package
    ↓
Export
    ↓
Import в другой World
```

Это не обязательно прямой multiplayer.

## 12. Sandbox — полноценный World Provider

Симулятор нельзя считать временной заглушкой. Он должен стать самым удобным provider для разработки:

```
Standalone World
    + Runtime
    + Authoring bridge
    + Simulation controls
    + Scenario runner
```

Именно здесь новые механики сначала проверяются без реальной игры.

## 13. Scenario / Test Runtime

Следующий уровень после ручной симуляции — воспроизводимые сценарии:

```
RESET
PLAYER position = world.ruslan_grill
WORLD time = 18:30
WEATHER = rain
FACT quest.ruslan.introductionSeen = false
REPUTATION npc.ruslan = 0
RUN
EXPECT InteractionAvailable(ruslan)
SELECT ruslan.introduction
EXPECT Fact(quest.ruslan.introductionSeen) = true
```

Сценарии используют тот же Runtime, что Sandbox и Game. Они дадут regression tests, сохранённые игровые ситуации, воспроизведение багов и демонстрации.

## 14. Snapshots и Replay

```
World Snapshot
      ↓
Simulation
      ↓
Event sequence
      ↓
Result
```

Это позволит вернуть систему точно в состояние перед ошибкой и повторить цепочку действий.

## 15. Canonical Content и Editor State

Канонические данные и состояние редактора должны разделяться.

**Canonical:** ID, тип, параметры, sockets, connections, ссылки и semantic data.

**Editor State:** X/Y, zoom, pan, selection, collapsed state, временные точки и другие визуальные данные.

## 16. Reference semantics

Ссылки должны быть типизированными:

```
Reference
  → WorldPoint
  → NPC
  → Item
  → Scene
  → Dialogue
  → Interaction
  → ...
```

UI показывает человеческое имя, canonical model хранит стабильный ID. Неизвестный ID не уничтожается.

## 17. Почему ETS2 остаётся важным

ETS2 — первый реальный provider, на котором архитектура должна доказать свою жизнеспособность.

План:

```
Sandbox Provider
      ↓
проверка Runtime
      ↓
первая демо-версия мира
      ↓
ETS2 Adapter
      ↓
реальное прохождение
      ↓
обратная связь игроков
```

ETS2 предоставляет физический мир, дороги, города и инфраструктуру. Наш слой добавляет поверх него персонажей, взаимодействия, историю, состояние и последствия.

## 18. Демо-версия — первая большая цель

Не требуется сразу реализовать все будущие подсистемы.

Нужен вертикальный срез:

```
WorldPoint
   ↓
Interaction
   ↓
Condition
   ↓
Quest
   ↓
Scene / Dialogue
   ↓
Choice
   ↓
State change
   ↓
Item / Money / Reputation
   ↓
следующее Interaction
   ↓
Quest completion
   ↓
последствие в мире
```

Этот срез должен полностью работать в Sandbox, а затем подключаться к ETS2 Provider.

## 19. Критерий готовности к первой демо

Для первой публичной демо-версии достаточно стабильных:

1. World Provider contract;
2. World State;
3. WorldPoint/Interaction;
4. Conditions;
5. Quest Runtime;
6. Scene/Dialogue Runtime;
7. Choice;
8. Inventory;
9. Money/Experience;
10. Reputation;
11. базовых NPC;
12. сохранения Runtime State;
13. Sandbox simulation;
14. базовой authoring infrastructure;
15. validation;
16. воспроизводимых regression scenarios.

## 20. Главный принцип развития

Не строить каждую новую функцию как специальный «квестовый костыль».

Спрашивать:

> **Какой общий механизм мира нужен этой функции?**

Магазину нужны Conditions — развивается Condition Engine. Радио вызывает последствия — используется Event + Action. Ремонт требует интерактивности — используется MiniGame Runtime. NPC имеет много вариантов — развивается Interaction/Entry Resolver. Новый provider приносит данные — используется Data Source Adapter.

## 21. Итоговая формулировка

**Assist Quest Editor** — это пользовательски простой «редактор квестов», внутри которого формируется универсальная платформа создания и симуляции интерактивных миров.

Она должна позволять создавать контент, связывать его с миром, моделировать состояние игрока и мира, исполнять правила и последствия, тестировать сценарии без реальной игры, подключать реальный World Provider и в перспективе превращать ту же модель в полноценный игровой режим.

Квесты — историческая точка старта и основной первый сценарий.

> **Конечный объект проекта — не квест, а интерактивный мир.**

Главная проверка архитектуры:

> **Можно ли создать небольшой самостоятельный мир, полностью пройти его в Sandbox, подключить к нему внешний World Provider и дать его другим людям без переписывания игровой логики?**

Эта цель является направлением проекта до первой рабочей демо-версии для игроков.
