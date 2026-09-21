# Учебный квест «Спецмаринад для Руслана»

Это готовый sandbox-пример для изучения Assist Quest Editor. Это не точная копия игрового квеста ETS2 Assist.

## Файлы

- `data/quests/tutorial_ruslan_shashlik.json` — Quest Graph: общий ход квеста.
- `data/scenes/ruslan_start.json` — Scene Graph первого разговора.
- `data/scenes/gosha_meat.json` — Scene Graph Гоши.
- `data/scenes/ruslan_finish.json` — Scene Graph финала.

## Главное правило

Quest Graph управляет **когда и что делать**.
Scene Graph управляет **конкретной сценой и её presentation semantics**.
Data Channels хранят **состояние**.
Interface Channel переносит **запрос UI и ответ пользователя**.
UI только показывает запрос и отправляет событие обратно.

## Пример связи

```json
"parameters": {
  "sceneId": "ruslan_start"
}
```

Это означает: «запусти canonical Scene с ID `ruslan_start`».

В Scene:

```json
"parameters": {
  "choiceId": "ruslan.offer"
}
```

Это означает: «данная Choice node использует resource `ruslan.offer`».

В resource:

```json
{
  "id": "ruslan.offer",
  "speaker": "Руслан",
  "text": "Нужно найти особое мясо...",
  "options": [...]
}
```

Каждый option отдельно указывает:

```json
"outputSocketId": "choice.accept"
```

Поэтому вариант и ветка связаны ID, а не положением кнопки.

## Как Choice возвращается в Quest Graph

```
Interface
  ↓ ChoiceSelected
SceneRuntime
  ↓ SceneCompleted
QuestRuntime
  ↓ states.Variables["scene.ruslan_start.lastChoiceId"]
  ↓ Condition
```

## Полный маршрут

```
Start
  ↓
SetStatus(Active)
  ↓
SetStep(meet_ruslan)
  ↓
DialogueScene(ruslan_start)
  ↓
Condition: принято?
  ├─ Нет → End
  └─ Да
      ↓ Interaction(city:chelyabinsk)
      ↓ DialogueScene(gosha_meat)
      ↓ Condition: мясо получено?
          ├─ Нет → End
          └─ Да
              ↓ GiveItem(ruslan.raw_meat, 1)
              ↓ Interaction(city:ekat)
              ↓ Condition: ItemCount >= 1
              ↓ DialogueScene(ruslan_finish)
              ↓ Condition: финал подтверждён?
                  ├─ Нет → End
                  └─ Да
                      ↓ RemoveItem(ruslan.raw_meat, 1)
                      ↓ AddReputation(ruslan, 10)
                      ↓ SetStep(completed)
                      ↓ End
```

## Как пройти

Версия `1.0.40.128-QUEST-EXAMPLE-R1` автоматически открывает этот учебный Quest Definition.

1. Перемести игрока к `city:ekat` — Екатеринбург.
2. Запусти Runtime.
3. Выбери «Да, берусь».
4. Перемести игрока к `city:chelyabinsk` — Челябинск.
5. Выбери «Да, беру».
6. Верни игрока к Екатеринбургу.
7. Выбери «Да, заканчиваем».
8. Проверь Inventory и Reputation.

Для отрицательных веток выбирай отрицательный вариант. Текущий Step покажет, по какой ветке завершился сценарий.

## Что изучать

Сначала открой Quest Graph и найди `scene-ruslan-start`.
Затем открой `data/scenes/ruslan_start.json`.

Сопоставь:

```
Quest: sceneId
   ↓
Scene: choiceId
   ↓
Choice resource: id
   ↓
Choice option: outputSocketId
   ↓
Scene Graph connection
   ↓
SceneCompleted
   ↓
Quest Condition
```

Эта цепочка — основа дальнейшей архитектуры.
