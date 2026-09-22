# Учебный квест «Спецмаринад для Руслана»

Это готовый sandbox-пример для изучения Assist Quest Editor. Это не точная копия игрового квеста ETS2 Assist.

## Файлы

- data/quests/tutorial_ruslan_shashlik.aqquest — основной самостоятельный квест: мясо для Руслана.
- data/quests/gosha_homemade_sausage.aqquest — независимый repeatable side quest Гоши.
- data/quests/ruslan_shashlik_delivery.aqquest — независимый одноразовый квест доставки.
- data/scenes/ruslan_start.aqscene — первая Scene Руслана.
- data/scenes/gosha_meat.aqscene — Scene Гоши с мясом.
- data/scenes/ruslan_finish.aqscene — финальная Scene первого квеста.
- data/scenes/gosha_shop.aqscene — Scene магазина Гоши.
- data/scenes/ruslan_delivery.aqscene — Scene заказа доставки.
- data/scenes/gosha_delivery.aqscene — Scene передачи шашлыка.

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

### Quest 1 — Спецмаринад для Руслана

Proximity(city:ekat)
  ↓
Start
  ↓
DialogueScene(ruslan_start)
  ↓
получить мясо у Гоши
  ↓
DialogueScene(ruslan_finish)
  ↓
RemoveItem(мясо)
  ↓
AddReputation(ruslan, +500)
  ↓
AddReputation(gosha, +350)
  ↓
End

После End Quest 1 получает статус Completed и не переходит в другие Quest Definition.

### Quest 2 — Домашняя колбаса у Гоши

Proximity(city:chelyabinsk, gosha >= 350)
  ↓
Start
  ↓
DialogueScene(gosha_shop)
  ├─ Купить → -450 ₽ → +1 колбаса → +25 репутации → следующий визит
  └─ Уйти → End

Это отдельный repeatable Quest. После End игрок свободен. Следующий запуск происходит только при следующем возвращении в область активации.

### Quest 3 — Доставка шашлыка

Proximity(city:ekat, gosha >= 400)
  ↓
Start
  ↓
DialogueScene(ruslan_delivery)
  ├─ Отказаться → End
  └─ Взять заказ
       ↓
       Proximity(city:chelyabinsk)
       ↓
       DialogueScene(gosha_delivery)
       ├─ Не передавать → End
       └─ Передать
            ↓ +100 Руслану
            ↓ +150 Гоше
            ↓ End

Quest 3 не зависит от Quest 2 graph connection. Он становится доступным только по собственному условию активации.
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

Версия 1.0.40.145-INDEPENDENT-QUEST-LIFECYCLE использует каталог независимых Quest Definition.

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
