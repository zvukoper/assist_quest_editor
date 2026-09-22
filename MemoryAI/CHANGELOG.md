## 2026-09-22 — 1.0.40.144 Потеря строк журнала при двух процессах

- исправлена случайная потеря строк в журнале: `File.AppendAllText` открывает файл
  с общим доступом только на чтение, поэтому одновременная запись двух процессов
  падала с `IOException`, а журнал такие сбои глушит — строка молча пропадала;
- случай воспроизводился штатно: второй экземпляр пишет, что передал путь, ровно
  в тот момент, когда первый пишет, что запрос получил;
- добавлен `InterprocessLogWriter`: дозапись защищена именованным мьютексом, общим
  для процессов (имя выводится из пути журнала, поэтому разные журналы не ждут
  друг друга), с повторами на случай кратковременной блокировки файла;
- из `AppLogger` и `QuestLogger` убрана блокировка внутри процесса: она не помогала
  против второго процесса;
- из-за этого дефекта проверка «Single instance probe» падала случайным образом:
  маркер терялся в журнале, хотя приложение работало верно;
- сама проверка усилена: читаются только строки, появившиеся после её старта,
  иначе маркер прошлого прогона засчитывался как успех текущего, и проверка
  перестала бы что-либо проверять;
- версия поднята до `1.0.40.144-LOG-RACE-FIX`.

## 2026-09-22 — 1.0.40.143 Синхронизация ресурсов data с публикацией

- ресурсы публикуются папкой `data` рядом с EXE: содержимое single-file
  распаковывается в `%TEMP%\.net\...\<случайный>`, который не виден человеку и
  копит копии прошлых сборок (в кэше найдено пять разных извлечений);
- добавлен `ResourceRootResolver` (Domain): ресурсы читаются из проверенной папки
  рядом с EXE, а при её отсутствии — из каталога приложения;
- добавлен `ci/sync_data_resources.ps1`: копирование по SHA-256 (а не по mtime),
  удаление файлов, которых нет в манифесте, обязательная `schemaVersion` для
  `.aqquest`/`.aqscene`, манифест `data-manifest.json`;
- добавлена проверка `ResourceIntegrityChecker` (Domain): `Missing`, `Modified`,
  `SchemaMismatch`, `UnexpectedFile`, `ManifestMissing`, `ManifestIncompatible`;
- проверка выполняется на сборке (цель MSBuild), при запуске (диалог вместо
  молчаливой работы со старыми данными) и в CI;
- добавлен режим `AssistQuestEditor.exe --verify-resources` с кодами возврата
  0/2/3 и отчётом рядом с EXE;
- добавлены `ResourceIntegrityTests` (158 тестов домена);
- версия поднята до `1.0.40.143-RESOURCE-SYNC-R1`.

## 2026-09-22 — 1.0.40.142 Портрет НПЦ в списке репутации

- исправлен битый портрет: страницы лежат в подкаталоге `Web`, а ресурсы игры —
  в `data/` рядом с executable, поэтому относительный `data/images/...` запрашивал
  несуществующий `Web/data/...`;
- путь ресурса приводится к корню приложения (`../data/...`) в `resolveAssetUrl`;
- если портрет всё равно не загрузился, остаётся рамка-заглушка вместо значка
  битого изображения;
- добавлена проверка `ci/reputation_avatar_smoke.mjs`: она воспроизводит раскладку
  публикации, навигирует по `file://` и требует `naturalWidth > 0`, а также
  самопроверкой доказывает, что различает рабочую и сломанную формы пути;
- версия поднята до `1.0.40.142-REPUTATION-AVATAR-FIX`.

## 2026-09-22 — 1.0.40.141 Репутация НПЦ и игровой контент

- добавлен `ReputationScale`: единая шкала названий, порогов и цветов; 10000 и -10000
  дают одинаковые 100% прогресса, а сторона показывается цветом и знаком числа;
- репутация переведена с фракций на НПЦ: канал `reputation` хранит `NpcReputationEntry`
  (значение + контакт), контакт фиксируется репликой или выбором НПЦ;
- ноды `AddReputation`/`RemoveReputation` используют `npcId`, добавлена ветвящая
  нода `ReputationCompare`;
- `SceneChoiceOption.Requirement` скрывает недоступный вариант вместо показа
  неактивным, поэтому «возможности просто не видно» выполняется буквально;
- учебный квест: +500 Руслану и +350 Гоше за основной квест; Гоша продаёт домашнюю
  колбасу за 450 ₽ при репутации от 350 (+25 за покупку, покупка повторяется);
  при репутации Гоши от 400 открывается доставка шашлыка (+100 Руслану, +150 Гоше);
- добавлены сцены `gosha_shop`, `ruslan_delivery`, `gosha_delivery` и предмет
  `gosha.homemade_sausage`;
- правая панель персонажа получила табы «Персонаж» и «Репутация» со списком
  контактировавших НПЦ, портретом, диапазоном и прогрессбаром;
- добавлены проверки `ci/check_quest_graph.mjs` и `ci/reputation_flow.mjs`;
- версия поднята до `1.0.40.141-QUEST-REPUTATION-R1`.

## 2026-09-21 — 1.0.40.130 Splash startup surface

- добавлен стартовый экран `SplashScreen.png` 800×450;
- PNG подключён как publish content и входит в single-file;
- MainForm скрыт до готовности WebView2, затем splash закрывается;
- Simulator также раскрывается только после готовности своего WebView2;
- версия поднята до `1.0.40.130-QUEST-SPLASH-R1`.

## 2026-09-21 — 1.0.40.129 Simulator Inventory / Player HUD

- added stable ItemId + quantity inventory state and a tiny starter item catalog;
- quest GiveItem/RemoveItem now mark acquired items as new and emit dedicated `InventoryChanged` events;
- added backpack/grid inventory UI, new-item beacon, quest inventory notifications and tooltips;
- added player vitals, money/XP/reserve, SPECIAL-like character stats and skills;
- added quest nodes for changing health, energy, hydration, fatigue, money, experience and character stats;
- reserved buff/debuff semantics in `MemoryAI/CHARACTER_EFFECTS.md` without displaying/applying them;
- coalesced Simulator snapshots, Journal refresh and Choice rendering to reduce visible flicker;
- synced Simulator resource cache-busting/version to `1.0.40.129`.

# Журнал изменений памяти и архитектуры

## 2026-09-21 — 1.0.40.128 Готовый учебный квест

- Scene catalog загружает canonical Scene Definition из `data/scenes/*.json`;
- учебный Quest Definition загружается из `data/quests/tutorial_ruslan_shashlik.json` и используется как стартовый graph;
- Condition получил оператор `VariableEquals` для ветвления по результату Scene Choice;
- добавлены три Scene Definition: `ruslan_start`, `gosha_meat`, `ruslan_finish`;
- добавлен полный учебный маршрут с World interaction, Inventory и Reputation;
- добавлен `README_TUTORIAL_RUSLAN.md`, объясняющий связь Quest Graph, Scene Graph, ресурса Choice и Data Channels;
- версия поднята до `1.0.40.128-QUEST-EXAMPLE-R1`.


## 2026-09-21 — 1.0.40.127 Canonical Scene Graph + SceneRuntime

- добавлены самостоятельные canonical ресурсы SceneDefinition, SceneGraph, SceneNode, SceneDialogue, SceneChoice и SceneChoiceOption;
- SceneChoiceOption хранит стабильный option ID и явный OutputSocketId, поэтому связь content → branch не зависит от порядка массива;
- добавлены ISceneCatalog, SceneCatalog и fixture ruslan_start;
- SceneRuntime исполняет SceneStart → Dialogue → Choice → SceneEnd, открывая уже существующий интерфейсный канал;
- DialogueScene Quest Runtime теперь оркестрирует SceneRuntime и после SceneCompleted сохраняет выбранный option ID в states;
- существующий Quest Graph Choice не удалён: он остаётся совместимым regression seam, пока новый Scene path проходит физический тест;
- после успешной публикации LOGS AppLogger необратимо подавляет новые записи до завершения процесса; визуальный статус push продолжает работать;
- версия поднята до 1.0.40.127-QUEST-SCENE-R1 для физического тестирования canonical Scene path.


## 2026-09-21 — 1.0.40.126 Интерфейс Choice через Data Channel

- добавлен канал `interfaces` как canonical канал запросов пользовательского интерфейса;
- `QuestRuntime` при входе в `Choice` создаёт `InterfaceChoiceDialog` с requestId, текстом, говорящим и вариантами из Output sockets;
- Simulator показывает настоящее модальное окно выбора поверх карты и отправляет выбор обратно как `ChoiceSelected` через существующий event channel;
- `requestId` проверяется Runtime, поэтому устаревший интерфейс не может выбрать вариант для уже изменившегося состояния;
- после выбора, остановки, завершения или ошибки активный интерфейс очищается;
- domain regression дополнен проверкой содержимого interface channel и его очистки после выбора;
- добавлен frontend `interface.js` и стили интерфейсного диалога;
- успешная отправка LOGS в GitHub больше не записывается в диагностический лог: успешный результат остаётся только визуальным статусом; ошибки commit/push по-прежнему логируются;
- версия поднята до `1.0.40.126-QUEST-INTERFACE-CHOICE-R1` для физического прогона обеих веток Choice.


## 2026-09-21 — 1.0.40.125 Исправлен переход после Wait

- устранён дефект `QuestRuntime.Tick()`: после истечения таймера Runtime больше не запускает текущую `Wait` повторно;
- перед продолжением фиксируется ожидающая нода, таймер сбрасывается, затем Runtime проходит через её первый Output и продолжает `Advance()`;
- если ожидающая нода исчезла из графа, Runtime корректно переходит в Failed с диагностикой;
- добавлен регрессионный тест `WaitTimerResumesThroughOutputAndDoesNotReenterWait`: реальный таймер истекает, поток проходит `Wait → SetStep → End`, состояние ожидания очищается;
- версия поднята до `1.0.40.125-QUEST-RUNTIME-WAIT-FIX-R1` для физического тестирования.


## 2026-09-21 — 1.0.40.124 Карта Simulator: города, hover и новый маркер игрока

- Simulator теперь получает отдельный слой городов из fixture `data/world/cities.json`, собранного из актуальной базы `zvukoper/ets2_assist:data/localized_cities/cities_sibirmap.json`;
- города приходят в тот же `WorldPoint`, что и СДО, но имеют `IsCity=true`, поэтому Web UI может визуально отделять их от игровых точек;
- точки СДО получили более заметную чёрную тень, что улучшает разделение плотных групп;
- при наведении точка получает отдельную белую подсветку и увеличивается; клик по городу не меняет выбор СДО;
- маркер игрока заменён с красного направленного треугольника на оранжевую точку с красной обводкой: направление больше не кодируется, поскольку в Simulator его источник пока не используется для визуального указателя;
- легенда Simulator обновлена под новый UX;
- добавлен `data/quests/demo_all_node_types.json`: один валидный Quest Definition, содержащий все типы нод из текущего каталога, включая динамические sockets Switch/Random/Choice;
- выполнен аудит runtime: авторинг и sockets покрывают весь каталог, но собственная семантика пока отсутствует у Phase/Reward/And/Or/Not/Switch/Random, а WaitForCondition не использует `conditionId`; отдельным следующим этапом остаётся доведение этих обработчиков и расширение Condition/WaitForCondition.

## 2026-09-21 — 1.0.40.123 Логи возвращены под контроль git

- из `.gitignore` убраны правила `*.log` и `MemoryAI/LOGS/*`: журналы приложения снова отслеживаются и попадают в коммит;
- исключение из репозитория было нужно только чтобы не мешать удалённому CI; автозапуск CI по push отключён, поэтому причина отпала;
- `*.log` удалён как глобальное правило: иначе журнал `MemoryAI/LOGS/assist_quest_editor.log` пришлось бы разигнорировать отдельным правилом, что только усложнило бы конфигурацию;
- проверено, что других `.log` вне `bin`, `obj` и `node_modules` в репозитории нет, поэтому правило ничего больше не скрывало;
- `MemoryAI/LOGS/README.md` обновлён: журналы и `CI_errors.md` теперь часть репозитория.

## 2026-09-21 — 1.0.40.123 Подключение кабеля, подсветка сокетов

- найдена и исправлена причина, по которой кабель не соединял ноды: обработчик соединения висел только на `click`, а если нажать на Output, протянуть мышь и отпустить над Input, браузер присылает click общему предку точек нажатия и отпускания, а не сокетам;
- соединение теперь начинается по `pointerdown` на Output и завершается по `pointerup` над Input, поэтому протяжка кабеля работает, как и клик-клик;
- сокет ищется по геометрии, а не через `event.target`: круг сокета лежит на границе ноды, и если край перекрыт другой нодой, прямоугольник верхней ноды перехватывал нажатие — из-за этого клик по сокету попадал в ноду, а курсор мигал между «палец» и «рука»;
- добавлена подсветка сокета под курсором: толстая белая обводка (класс `hovered`);
- совместимый Input при выбранном Output подсвечивается зелёным, несовместимый — красным, поэтому результат соединения виден до клика;
- добавлен снап: конец кабеля притягивается к центру сокета (радиус 22 px), а отпускание в пределах этого радиуса завершает соединение;
- курсор подтверждает результат: `crosshair` на сокете, `copy` на совместимом Input, `not-allowed` на несовместимом;
- кабели получили `pointer-events: none`: они не перехватывают указатель, поэтому курсор больше не мигает у сокета;
- в smoke добавлены проверки: протяжка кабеля, подсветка `hovered`, совместимость `compatible`, курсор, точность снапа и снятие подсветки при уходе указателя.

## 2026-09-21 — 1.0.40.122 Локальный режим расширения CI Monitor

- расширение CI Monitor получило два режима (`ciMonitor.mode`): GitHub (разбор страницы Actions) и локальный (прогон `ci/run_local.ps1`);
- добавлен режим `auto`: локальный прогон показывается, пока идёт, и ещё `ciMonitor.localHoldSeconds` секунд после завершения, затем плашка сама возвращается к GitHub;
- в локальном режиме показывается процент выполнения по числу завершённых проверок из 11; если общее число неизвестно, процент не показывается;
- добавлены команды: переключение режима, задание номера и статуса локальной проверки, открытие отчёта, копирование пути к файлу состояния;
- **при успешных проверках уведомление не показывается**: проверки пройдены, значит приложение соберётся и запустится — этого достаточно. Уведомление приходит только о проблемах (ошибка или отмена);
- при локальном прогоне уведомляет расширение; штатное уведомление скрипта подавляется признаком активности редактора (`.ci-state/monitor.heartbeat`, свежесть ≤ 15 с);
- номер локального прогона и признак активности вынесены в `.ci-state/`: папка `MemoryAI/LOGS` очищается после успешной публикации, поэтому нумерация проверок сохраняется между сборками;
- очистка `MemoryAI/LOGS` после успешной публикации оставлена без изменений: при успехе логи не нужны, при падении они сохраняются для разбора.

## 2026-09-21 — 1.0.40.122 Локальный прогон как основной барьер, CI только вручную

- автозапуск GitHub Actions по push в main отключён: `ci.yml` теперь запускается только через `workflow_dispatch`;
- причина: набор проверок совпадает с `ci/run_local.ps1`, а каждый commit больше не занимает Windows runner;
- GitHub CI сохранён для того, чего локально получить нельзя: чистой среды `windows-latest` без локальных кешей и untracked-файлов и зафиксированного в workflow Node.js 22;
- `import_sdo.yml` продолжает работать: запуск `ci.yml` после импорта СДО выполняется явно через `workflow_dispatch`; добавлен комментарий, почему запуск явный;
- concurrency переведён на группу ручных запусков: повторный ручной запуск отменяет предыдущий незавершённый;
- `MemoryAI/INSTRUCTIONS.md` §8 и §18 и `MemoryAI/PROJECT_MEMORY.md` §3.1 приведены в соответствие: основной барьер — локальный прогон, физический тест после успешного локального прогона и bump версии;
- проверено, что YAML обоих workflow валиден: `ci.yml` → `on = workflow_dispatch`, `import_sdo.yml` без изменений в триггерах.

## 2026-09-21 — 1.0.40.122 Отчёт о проверках в MemoryAI/LOGS

- `ci/run_local.ps1` теперь пишет `MemoryAI/LOGS/CI_errors.md`: сводка по всем проверкам, причина каждой ошибки и полный вывод упавших проверок;
- отчёт формируется и при успехе (с явной отметкой «Ошибок нет»), и при ошибке: иначе нельзя отличить «всё хорошо» от «прогон не делали»;
- для каждой ошибки сохраняется точная позиция (`file:line` из node) и полный стек;
- вывод внешних процессов читается в UTF-8 (`[Console]::OutputEncoding`), иначе русский текст в отчёте превращался в мусор;
- из отчёта убраны служебные обёртки PowerShell: строки stderr приходят как `RemoteException`, и `ToString()` добавлял многострочный блок со `строка/знак/CategoryInfo`;
- при перехвате вывода сохранена подсветка консоли (`InformationRecord` → `HostInformationMessage`);
- `ErrorActionPreference = 'Continue'` внутри проверки: при `*>&1` строки stderr становятся `ErrorRecord`, и со `Stop` первая же строка стека обрывала сбор вывода;
- `ci/validate_memory.mjs` следит, чтобы в `MemoryAI/LOGS` оставались только диагностические файлы (`README.md`, `CI_errors.md`, `*.log`);
- номер локального прогона и признак активности редактора вынесены в `.ci-state/`, а не в `MemoryAI/LOGS`: папка логов очищается после успешной публикации, поэтому нумерация проверок сохраняется между сборками;
- очистка `MemoryAI/LOGS` после успешной публикации оставлена без изменений: при успехе логи не нужны, при падении они сохраняются для разбора.

## 2026-09-21 — 1.0.40.122 Локальный прогон CI перед сборкой

- добавлен `ci/run_local.ps1`: повторяет все шаги `ci.yml` без GitHub, печатает итоговую таблицу и завершается кодом 1 с именами упавших проверок (как `final_gate`);
- `pull.ps1` теперь выполняет `git pull --ff-only`, затем локальные проверки и запускает `compile.ps1` только при их успехе;
- при неудачных проверках сборка не выполняется, выводится список упавших проверок и показывается уведомление Windows;
- добавлен переиспользуемый `ci/WindowsToast.ps1` на WinRT `Windows.UI.Notifications` — без сторонних модулей и прав администратора;
- уведомление отправляется отдельным процессом `powershell.exe` с `-EncodedCommand`, поэтому доступно и из PowerShell 7;
- добавлены флаги `-SkipChecks`, `-NoLaunch`, `-NoNotify` в `pull.ps1` и `-IncludePublish`, `-SkipInstall`, `-NoNotify` в `run_local.ps1`;
- шаг single-file publish в локальный прогон не входит по умолчанию: он останавливает запущенный редактор и удаляет bin, obj, publish и профиль WebView2;
- `ci-results/` добавлен в `.gitignore`;
- зафиксировано, что `.ps1` с кириллицей требует UTF-8 с BOM: без BOM PowerShell 5.1 читает файл как ANSI и парсер падает;
- проверены оба пути: при сломанном `editor.js` сборка не запускается и приходит уведомление, при исправном — выполняется полный цикл публикации.

## 2026-09-21 — 1.0.40.122 Quest Graph menu + CI smoke harness fix

- найдена настоящая причина серии красных CI (запуски 184–207): `ci/quest_graph_smoke.mjs` загружал `editor.js`, но не `theme.css`;
- без реального stylesheet `.graphContextMenu` не получал `position:fixed`, и все проверки геометрии меню по viewport были бессмысленны;
- harness теперь загружает реальный `theme.css` через `addStyleTag`, поэтому контекстное меню проверяется в том виде, в котором оно существует в production;
- исправлена вторая ошибка harness: после `setViewportSize` использовался устаревший `svgBox`; канвас выше компактного viewport, и его центр оказывался за пределами окна, из-за чего колесо и pan не попадали в SVG;
- pan и zoom теперь нацелены на точку, гарантированно видимую в окне, с проверкой через `elementFromPoint`;
- исправлена реальная ошибка UI: `graphDirtyNodeIds` никогда не очищался, поэтому оранжевые звёздочки изменённых нод оставались навсегда после сохранения; набор очищается, когда Host сообщает `documentDirty = false`;
- удалён мёртвый флаг `pendingDirtySelection`, который записывался, но нигде не читался;
- добавлена regression-проверка сброса dirty-меток после сохранения (подтверждено, что без исправления она падает);
- устранён version drift: `simulator.html` использовал `?v=1.0.40.118`, из-за чего WebView2 мог отдавать устаревшие `theme.css` и `simulator.js`; footer `main.html` не совпадал с `VersionInfo`;
- все версии синхронизированы на 1.0.40.122-QUEST-GRAPH-MENU-R1 (VersionInfo, `.csproj`, три HTML);
- `final_gate` в CI теперь пишет таблицу результатов по каждой проверке в `GITHUB_STEP_SUMMARY`, чтобы падение называло шаг, а не только «exit code 1»;
- локально подтверждены: Playwright smoke, contract smoke, Quest Graph smoke (3 прогона), `node --check` ×3, `validate_memory`, `dotnet build`, 26/26 domain tests.

## 2026-09-21 — 1.0.40.111 Quest Runtime + CI diagnostics

- завершено подключение canonical Quest Runtime к Simulator через Data Channel Hub;
- Runtime пробуждается по `ChannelChanged` для ожидающих Condition/Interaction;
- исправлено продолжение Runtime после выбора Choice;
- добавлена матрица из семи автоматических сценариев: Graph, Parameters, Simple Runtime, Interaction, Event, Choice, Save/Load;
- исправлены оставшиеся нарушения xUnit2013 в Graph Store tests;
- CI продолжает остальные проверки после сбоя отдельного шага и формирует сводный `MemoryAI/LOGS/assist_quest_editor.log`;
- CI сохраняет TRX и диагностический лог отдельным artifact;
- Host логирует runtime transitions и Simulator events;
- синхронизированы VersionInfo и `.csproj` на 1.0.40.111.


## 2026-09-20 — 1.0.40.110 Quest Graph authoring

- добавлен pan canvas через среднюю кнопку мыши и Space + ЛКМ;
- параметры нод стали частью canonical QuestNode;
- Switch, Random и Choice получили динамические Output sockets через outputCount;
- Inspector умеет редактировать и добавлять параметры;
- добавлено versioned QuestDefinitionDocument schema=1;
- Graph Editor получил Новый, Открыть, Сохранить и Сохранить как…;
- добавлены smoke-проверки параметров и pan;
- версия поднята до 1.0.40.110 перед физическим тестированием.


## 2026-09-20 — Quest Graph: drag и wheel zoom

- Graph nodes теперь перетаскиваются ЛКМ непосредственно по canvas.
- Финальные координаты drag отправляются через `graph_update_node`, поэтому canonical `QuestGraphStore` остаётся источником данных.
- Колесо мыши масштабирует Quest Graph вокруг позиции курсора.
- Viewport (`x/y/width/height`) хранится отдельно от `QuestGraph` и переживает rerender после изменения ноды.
- Playwright smoke проверяет drag с изменением X/Y и реальный wheel zoom.
- Версия поднята до `1.0.40.109-QUEST-EDITOR-INTERACTION-R1` перед физическим тестом.

## 2026-09-20 — Quest Graph: Undo/Redo, Validation и transport contract

- `QuestGraphStore` хранит snapshot history для Undo/Redo; новая правка очищает Redo history.
- Добавлен `QuestGraphValidator` со стабильными diagnostic codes для структуры графа, sockets, connections и достижимости нод.
- Host поддерживает `graph_undo`, `graph_redo`, `graph_validate`; `quest_graph` передаёт состояние history и validation diagnostics.
- Web Graph Editor получил Undo/Redo/Проверить и горячие клавиши Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z.
- `JsonStringEnumConverter` синхронизировал enum transport с Web UI.
- Domain tests и Playwright smoke расширены новыми контрактами.

## 2026-09-20 — Архитектурный reference WolvenKit

- добавлен обязательный внешний архитектурный reference: upstream `WolvenKit/WolvenKit`;
- добавлен `MemoryAI/WOLVENKIT_REFERENCE.md` с кратким конспектом философии, graph/document architecture, DI/factories, stable IDs, editor metadata и contextual validation;
- в `INSTRUCTIONS.md` зафиксирована обязательная сверка актуального WolvenKit `main` перед архитектурными и логическими изменениями;
- в `ARCHITECTURE.md` и `PROJECT_MEMORY.md` добавлена явная связь с reference;
- отдельно зафиксировано, что заимствуются принципы, а не REDengine-specific типы и форматы.

## 2026-09-20 — 1.0.40.108-QUEST-EDITOR-QUEST-GRAPH-R1

- Quest Graph переведён из статического demo SVG в рабочий редактор.
- Добавлен canonical `QuestGraphStore` в Quest Domain.
- Добавлен `QuestGraphFactory.CreateStarter()` с эталонным графом Руслана.
- Добавлен `QuestNodeCatalog` с базовыми Input/Output sockets и ветвлением.
- Host хранит один общий QuestGraphStore и передаёт его всем EditorForm.
- Graph Web UI умеет выбирать ноды, менять Title/X/Y, добавлять и удалять ноды, создавать и удалять connections.
- Добавлены domain tests для поведения QuestGraphStore.
- Версия повышена до 1.0.40.108 перед физическим тестированием.

## 2026-09-20 — 1.0.40.107-QUEST-EDITOR-SDO-MAP-R6-SELECTION-CONTEXT-FIX

- исправлен контракт `simulator_context`: EditorForm сериализует вложенные `selection.point.position.x/y/z` в camelCase;
- исправлен `formatPosition()` в Simulator Web UI;
- устранены `ReferenceError: formatPosition is not defined` и `undefined` при передаче координат в редактор локаций;
- добавлен selection/context smoke test;
- версия повышена до 1.0.40.107 перед физическим тестированием.

## 2026-09-20 — 1.0.40.106-QUEST-EDITOR-SDO-MAP-R5-SNAPSHOT-FIX

- исправлена сериализация Simulator snapshot: для Web UI принудительно используется camelCase;
- устранена причина ошибки `Cannot read properties of undefined (reading 'position')`;
- версия поднята перед повторным физическим тестом;
- CI разрешает диагностический `MemoryAI/LOGS/assist_quest_editor.log` для анализа.

## 2026-09-20 — 1.0.40.105-QUEST-EDITOR-SDO-MAP-R4-DIAGNOSTICS

- добавлен постоянный диагностический лог `MemoryAI/LOGS/assist_quest_editor.log`;
- логируется путь и размер SDO-файла, результат JSON-десериализации, число входных/валидных точек и диапазоны координат;
- логируется жизненный цикл WebView2, NavigationCompleted, URL страницы, версия браузера и отправка/получение snapshot;
- добавлен сбор `console.error`, `console.warn`, `window.error` и `unhandledrejection` из Web UI;
- добавлен version cache-busting для HTML/JS/CSS WebView;
- после успешной публикации compile.ps1 очищает старые логи, при ошибке публикации логи не удаляются.

## 2026-09-20 — Инициализация

- создан отдельный репозиторий-песочница assist_quest_editor;
- принято решение не подключать ETS2 Assist до стабилизации архитектуры;
- за основу взяты архитектурные идеи WolvenKit Quest/Scene Editor;
- определено разделение Quest Graph / Scene Graph / World / Data Channels / Runtime / UI;
- первым эталонным сценарием выбран «Спецмаринад для Руслана»;
- предусмотрена возможность будущей адаптации к ETS2 и DayZ;
- зафиксирована модель Simulator как искусственного источника данных;
- задан русский интерфейс с заранее подготовленной локализацией;
- задана строгая дисциплина CI и диагностики.

## 2026-09-20 — 1.0.40.101-QUEST-EDITOR-DUAL-WINDOW-R1

- создан двухоконный WinForms/WebView2 каркас;
- Simulator вынесен в отдельное окно для второго монитора;
- добавлены искусственные каналы Player, World, Facts, Quest Statuses, States, Inventory, Reputation, Telemetry, Environment и System;
- добавлены редакторы данных Simulator;
- добавлены Quest Graph, Scene Graph, World, Conditions/Effects, Localization, Validation и Node Registry;
- тема web UI выровнена по палитре и базовой анимации актуального web UI ETS2 Assist;
- добавлен IDataSourceAdapter для будущей замены Simulator на игровой источник;
- CI переведён на обязательные сборку src и запуск tests;
- Playwright smoke переведён с тестовой HTML-строки на реальные web-страницы проекта.

## 2026-09-20 — 1.0.40.103-QUEST-EDITOR-SDO-MAP-R2

- исправлена доставка Simulator snapshot: snapshot отправляется только после NavigationCompleted, а не сразу после Navigate();
- карта Simulator визуально выровнена под задачу СДО: точки используют цвет категории, выбранная точка получает толстую оранжевую обводку, игрок отображается красным треугольником с корректным углом;
- добавлена компактная легенда категорий СДО; подписи точек ограничены при общем обзоре и раскрываются при приближении/выборе;
- версия поднята перед физическим тестированием.

## 2026-09-20 — 1.0.40.104-QUEST-EDITOR-SDO-MAP-R3

- устранён рассинхрон версии;
- compile.ps1 перед публикацией удаляет целиком bin, obj, publish и управляемый профиль WebView2;
- WebView2 получает фиксированный профиль %LOCALAPPDATA%/AssistQuestEditor/WebView2;
- проверяются обязательные Web-файлы; single-file по-прежнему должен состоять только из одного EXE;
- версия поднята перед повторным физическим тестированием.

## 2026-09-20 — Инструменты сборки

- добавлен compile.ps1 по образцу ETS2 Assist, адаптированный под Assist Quest Editor;
- публикация выполняется как self-contained win-x64 single-file;
- web-ресурсы включаются в один EXE;
- добавлен ключ -NoLaunch для CI и проверки публикации без запуска;
- добавлен pull.ps1 для обновления main и запуска compile.ps1;
- CI теперь проверяет реальную single-file публикацию.
