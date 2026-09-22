# Учебный квест «Спецмаринад для Руслана»

Это готовый sandbox-пример для изучения Assist Quest Editor. Это не точная копия игрового квеста ETS2 Assist.

## Файлы

- `data/quests/tutorial_ruslan_shashlik.aqquest` — Quest Graph: общий ход квеста.
- `data/scenes/ruslan_start.aqscene` — Scene Graph первого разговора.
- `data/scenes/gosha_meat.aqscene` — Scene Graph Гоши.
- `data/scenes/ruslan_finish.aqscene` — Scene Graph финала.
- `data/scenes/gosha_shop.aqscene` — Scene Graph покупки домашней колбасы.
- `data/scenes/ruslan_delivery.aqscene` — Scene Graph заказа на доставку шашлыка.
- `data/scenes/gosha_delivery.aqscene` — Scene Graph передачи шашлыка Гоше.

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
              ↓ AddReputation(ruslan, +500)
              ↓ AddReputation(gosha, +350)
              ↓ SetStep(gosha_shop)
              ↓ Interaction(city:chelyabinsk)
              ↓ DialogueScene(gosha_shop)
              ↓ Купил колбасу?
                  ├─ Нет → SetStep(ruslan_delivery)
                  └─ Да
                      ↓ RemoveMoney(450)
                      ↓ GiveItem(gosha.homemade_sausage, 1)
                      ↓ AddReputation(gosha, +25)
                      ↓ назад к Interaction(city:chelyabinsk) — цикл покупок
              ↓ SetStep(ruslan_delivery)
              ↓ ReputationCompare(gosha >= 400)
                  ├─ Нет → SetStep(completed)
                  └─ Да
                      ↓ Interaction(city:ekat)
                      ↓ DialogueScene(ruslan_delivery)
                      ↓ Заказ принят?
                          ├─ Нет → SetStep(completed)
                          └─ Да
                              ↓ Interaction(city:chelyabinsk)
                              ↓ DialogueScene(gosha_delivery)
                              ↓ Шашлык передан?
                                  ├─ Нет → SetStep(completed)
                                  └─ Да
                                      ↓ AddReputation(ruslan, +100)
                                      ↓ AddReputation(gosha, +150)
                                      ↓ SetStep(completed)
                                      ↓ End
```

## Репутация

Репутация ведётся по каждому НПЦ отдельно (`npcId` — `ruslan` или `gosha`),
с максимальной шкалой 10000 очков в каждом направлении. Прогрессбар растёт
слева направо всегда, а цвет и знак числа показывают сторону:

- серая заливка в нейтральной зоне от -300 до 300;
- lime при репутации выше 300;
- красная при репутации ниже -300, при этом число получает минус, но прогресс
  считается по модулю: -10000 — это тоже 100%.

Названия диапазонов (из ТЗ):

| Положительные | Отрицательные |
|---|---|
| Неопасный 300..500 | Подозрительный -500..-300 |
| Знакомый 500..1000 | Чужак -1000..-500 |
| Приятель 1000..3000 | Нежелательный -3000..-1000 |
| Друг 3000..6000 | Враг -6000..-3000 и ниже |
| Свой 6000 и выше | |

Контакты фиксируются автоматически: как только игрок видит реплику или вариант
выбора НПЦ, этот НПЦ появляется в списке репутации.

В правой панели Simulator есть табы «Персонаж» и «Репутация»: во вкладке
репутации видны только те НПЦ, с которыми контакт уже состоялся.

Версия `1.0.40.141-QUEST-REPUTATION-R1` автоматически открывает этот учебный Quest Definition.

1. Перемести игрока к `city:ekat` — Екатеринбург.
2. Запусти Runtime.
3. Выбери «Да, берусь».
4. Перемести игрока к `city:chelyabinsk` — Челябинск.
5. Выбери «Да, беру» — мясо получено, Руслан даёт +500, Гоша +350.
6. Верни игрока к Екатеринбургу.
7. Выбери «Да, заканчиваем» — мясо списывается и начисляется репутация.
8. Гоша теперь продаёт домашнюю колбасу: 450 ₽ за штуку, +25 репутации за
   каждую покупку. Купить можно только при репутации Гоши от 350 — иначе
   варианта покупки в диалоге не видно.
9. Дойдя до репутации Гоши 400, получи у Руслана заказ на доставку шашлыка:
   +100 репутации Руслану и +150 Гоше.
10. Проверь Inventory и вкладку «Репутация».

Для отрицательных веток выбирай отрицательный вариант. Текущий Step покажет, по какой ветке завершился сценарий.

## Что изучать

Сначала открой Quest Graph и найди `scene-ruslan-start`.
Затем открой `data/scenes/ruslan_start.aqscene`.

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
