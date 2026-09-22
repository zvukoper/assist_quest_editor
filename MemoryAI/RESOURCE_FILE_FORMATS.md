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

## Architectural rule

Do not tie an extension to a UI window. A future combined editor can still open `.aqscene`.

Do not create abbreviations for schema versions. Add a new semantic extension only when the resource itself is a genuinely different resource type.

The authoritative registry is `src/AssistQuestEditor.App/Core/ResourceFileTypes.cs`.
