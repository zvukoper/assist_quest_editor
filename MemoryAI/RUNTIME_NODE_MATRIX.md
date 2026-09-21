# Матрица Quest Node Runtime

Статус на 1.0.40.125. Разделяет наличие типа в canonical catalog/authoring и наличие реальной семантики в `QuestRuntime`.

| NodeType | Авторинг / sockets | Runtime | Текущая проверка |
|---|---|---|---|
| Start | ✅ | ✅ запуск потока | demo_all_node_types |
| End | ✅ | ✅ завершение | demo_all_node_types |
| Phase | ✅ | ⚠️ pass-through | demo_all_node_types |
| Interaction | ✅ | ✅ ожидание близости к WorldPoint | ручной маршрут + demo |
| Condition | ✅ | ✅ ограниченный набор операторов | demo + runtime tests |
| And | ✅ | ⚠️ pass-through первого output | demo_all_node_types |
| Or | ✅ | ⚠️ pass-through первого output | demo_all_node_types |
| Not | ✅ | ⚠️ pass-through первого output | demo_all_node_types |
| Switch | ✅ dynamic | ⚠️ pass-through первого output | demo_all_node_types |
| Random | ✅ dynamic | ⚠️ pass-through первого output | demo_all_node_types |
| Choice | ✅ dynamic | ✅ ожидание ChoiceSelected + branch | runtime tests + demo |
| Wait | ✅ | ✅ таймер с переходом по Output | runtime regression test + demo |
| WaitForCondition | ✅ | ⚠️ условие вычисляется, но `conditionId` не разрешается | demo |
| WaitForEvent | ✅ | ✅ ожидание события по eventType | demo + event test |
| SetStatus | ✅ | ✅ | demo |
| SetStep | ✅ | ✅ | demo |
| SetFlag | ✅ | ✅ | demo |
| SetVariable | ✅ | ✅ | demo |
| DialogueScene | ✅ | ✅ переход/событие scene | demo |
| Reward | ✅ | ⚠️ pass-through, реального reward handler нет | demo |
| GiveItem | ✅ | ✅ | demo |
| RemoveItem | ✅ | ✅ | demo |
| AddReputation | ✅ | ✅ | demo |
| RemoveReputation | ✅ | ✅ | demo |

## Что считать «готово»

Нода считается runtime-ready только после отдельного теста её поведения, а не потому, что она добавляется в Graph Editor и имеет sockets.

Для ветвящих нод следующий этап должен проверять каждый output отдельно. Для WaitForCondition нужно ввести разрешение `conditionId` на canonical expression/definition. Для Reward нужен реальный контракт награды. Phase должен определять границы/подграф фазы, а Switch/Random — выбирать выход по своим правилам.
