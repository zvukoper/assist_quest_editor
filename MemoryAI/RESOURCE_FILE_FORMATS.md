# Assist Quest Resource File Format Registry

## Stable naming rule

All authoring resources use the `aq` namespace and a descriptive long extension:

- the extension identifies the resource type;
- the payload remains JSON;
- `schemaVersion` versions the JSON schema;
- a resource type is not tied to a particular editor window;
- editor Open/Save dialogs accept only the resource extensions they own.

The extension is deliberately not abbreviated (`.aqsn`, `.aqqs`) so a file remains understandable outside the application.

## Current registry

| Extension | Resource | Purpose | Status |
|---|---|---|---|
| `.aqquest` | Quest | Canonical Quest Definition / Quest Graph | **Implemented** |
| `.aqscene` | Scene | Canonical Scene Definition / Scene Graph | **Implemented** |
| `.aqdialogue` | Dialogue | Reusable dialogue content referenced by scenes | Reserved |
| `.aqchoice` | Choice | Reusable choice/options resource | Reserved |
| `.aqcampaign` | Campaign | High-level collection/description of a story campaign | Reserved |
| `.aqworld` | World | Authoring world container | Reserved |
| `.aqpoint` | World Point | Authoring point with world coordinates and metadata | Reserved |
| `.aqcity` | City | World-city reference resource | Reserved |
| `.aqitem` | Item | Item definition used by inventory/actions | Reserved |
| `.aqcondition` | Condition | Reusable condition definition | Reserved |
| `.aqeffect` | Effect | Reusable gameplay action/effect definition | Reserved |
| `.aqloc` | Localization | Localized strings/resource table | Reserved |
| `.aqregistry` | Node Registry | Node schemas, metadata and authoring registry | Reserved |
| `.aqsnapshot` | Runtime Snapshot | Simulator/runtime state snapshot, not an authoring resource | Reserved |
| `.aqresource` | Resource | Reserved generic resource type | Reserved |

## What is not a separate file type

Money, XP, reputation changes, facts, states, inventory changes, telemetry values, validation results and viewport state are runtime data/actions, not automatically independent resources.

An item **definition** is `.aqitem`; a `GiveItem(itemId, count)` node is still an action in a graph.

## JSON contract

A resource keeps its semantic type and schema version in the JSON as a second safety check:

```json
{
  "schemaVersion": 1,
  "format": "aqscene",
  "definition": {}
}
```

The extension is the primary discriminator. `format` is the defensive discriminator. `schemaVersion` is the migration boundary.

Schema revisions do **not** create new extensions. `.aqscene` remains `.aqscene` while `schemaVersion` changes.

## Windows file associations

The application registers the types for the current Windows user under `HKCU\\Software\\Classes`. Each resource receives:

1. an extension key;
2. a versioned application ProgID such as `AssistQuestEditor.Scene.1`;
3. `OpenWithProgids`;
4. `DefaultIcon`;
5. a `shell\\open\\command` verb.

Existing user-selected associations are not overwritten. Explorer is refreshed through `SHChangeNotify(SHCNE_ASSOCCHANGED)`.

The application generates a small per-resource ICO package in:

`%LOCALAPPDATA%\\AssistQuestEditor\\FileIcons`

This avoids shipping a collection of binary icon files with the single-file EXE. The generated icons are deterministic and tied to the registry entries.

Double-clicking an implemented `.aqquest` or `.aqscene` opens the corresponding editor. Registered future types currently report that their editor is reserved/not implemented.

To remove the registrations without uninstalling the application:

`AssistQuestEditor.exe --unregister-file-associations`

## Migration performed

Existing sandbox resources were moved from JSON-only extensions to their canonical resource extensions:

- `data/quests/tutorial_ruslan_shashlik.json` → `tutorial_ruslan_shashlik.aqquest`
- `data/scenes/ruslan_start.json` → `ruslan_start.aqscene`
- `data/scenes/gosha_meat.json` → `gosha_meat.aqscene`
- `data/scenes/ruslan_finish.json` → `ruslan_finish.aqscene`

The graph/scene payload itself was preserved; only the resource discriminator was added.

## Публикация ресурсов и синхронизация с каталогом data

Ресурсы публикуются **папкой `data` рядом с EXE**, а не только внутрь single-file.

Причина: содержимое single-file распаковывается в `%TEMP%\.net\AssistQuestEditor\<случайный>`
при каждом запуске. Такой каталог не виден человеку, не заменяется по частям и
накапливает копии прошлых сборок — была зафиксирована ситуация, когда в кэше
одновременно лежали пять разных извлечений, и ресурс мог быть подставлен из
устаревшего. Папку рядом с EXE видит человек, и отдельный файл можно заменить без
пересборки.

Раскладка публикации:

```
publish/
  AssistQuestEditor.exe
  data/
    data-manifest.json      <- манифест: размер, SHA-256 и версия схемы каждого файла
    quests/*.aqquest
    scenes/*.aqscene
    images/*.png
    world/*.json
```

Приложение читает ресурсы из `ResourceRootResolver`:
1. `<каталог EXE>/data`, если в нём есть манифест — проверенная публикация;
2. `<BaseDirectory>/data` — запуск из сборки, отладка и распаковка single-file.

Правило выбора живёт в Domain (`ResourceRootResolver`), потому что оно покрыто
тестами, а не только ручным запуском.

### Синхронизация

`ci/sync_data_resources.ps1` копирует `data` в каталог публикации и пишет манифест:

- сравнение идёт **по SHA-256, а не по времени**: копия сохраняет mtime исходника,
  поэтому устаревший файл выглядел бы свежим;
- каталог получает **только набор из манифеста**: файл, которого нет в новой
  сборке (переименованный или удалённый ресурс), удаляется;
- версия схемы обязательна для `.aqquest`/`.aqscene`: файл без `schemaVersion`
  роняет сборку, а не проезжает дальше;
- пустые каталоги после удаления убираются.

Запускается автоматически как цель MSBuild `VerifyDataResources` (после `Build`)
и `SyncDataResourcesToPublish` (после `Publish`).

### Проверка целостности

`ResourceIntegrityChecker` (Domain) сверяет каталог с манифестом и сообщает:

| Вид проблемы | Что означает |
|---|---|
| `Missing` | файл из манифеста отсутствует |
| `Modified` | размер или хеш не совпали: файл изменён или от другой сборки |
| `SchemaMismatch` | версия схемы не та (или её нет у ресурса со схемой) |
| `UnexpectedFile` | файл, которого нет в манифесте — остаток прошлой сборки |
| `ManifestMissing` | каталог не публиковался либо манифест повреждён |
| `ManifestIncompatible` | манифест другой версии формата |

Проверка выполняется в трёх точках:

1. **сборка** — цель MSBuild запускает `AssistQuestEditor.exe --verify-resources` и
   падает при расхождении;
2. **запуск** — приложение проверяет ресурсы до создания окон и при расхождении
   показывает список проблем и причину вместо молчаливой работы со старыми данными;
   отсутствие манифеста запуск не блокирует (так работают отладка и тесты);
3. **CI** — проверка «Синхронизация ресурсов data» в `ci/run_local.ps1`.

Коды возврата `--verify-resources`: `0` — целостность подтверждена, `2` — ресурсы
разошлись, `3` — проверку выполнить не удалось. Отчёт пишется в
`data-verify-report.txt` рядом с EXE: приложение собрано как WinExe без консоли,
поэтому stdout из MSBuild не читается.

Покрытие: `ResourceIntegrityTests` (Domain) проверяет потерянный, изменённый и
лишний ресурс, несовместимую схему, ресурс без схемы, отсутствующий и повреждённый
манифест, а также выбор каталога ресурсов.

## Quest lifecycle and activation

.aqquest является самостоятельным документом Quest. Один Quest Graph не должен продолжаться прямым graph connection в другой .aqquest: граница документа должна оставаться явной.

Для sandbox-runtime условия добровольной активации хранятся в QuestDefinition.Activation:

- Manual — запуск только явной командой;
- Proximity — активация по WorldPoint и радиусу;
- RequiredReputationNpcId / RequiredReputation — дополнительный runtime gate;
- Repeatable — после завершения ресурс может быть активирован снова на следующем входе в область.

Это runtime-метаданные поверх canonical Quest resource. Они не превращают Quest в UI-окно: расширение .aqquest по-прежнему обозначает тип ресурса, а редактор лишь предоставляет authoring surface.
## Architectural rule

Do not tie an extension to a UI window. A future combined editor can still open `.aqscene`.

Do not create abbreviations for schema versions. Add a new semantic extension only when the resource itself is a genuinely different resource type.

The authoritative registry is `src/AssistQuestEditor.App/Core/ResourceFileTypes.cs`.
