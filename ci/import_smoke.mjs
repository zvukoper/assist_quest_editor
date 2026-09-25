// Импорт кампании и квеста в родителя.
//
// Почему настоящий запуск: импорт — это ФАЙЛОВАЯ операция, и «правильный код»
// ничего не говорит о том, куда лёг ресурс и уцелели ли соседи. Раскладка при
// этом — контракт обмена (файл кампании в папке мира, файл квеста в папке
// кампании), поэтому проверяется фактическое дерево на диске.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const importService = read("src/AssistQuestEditor.App/ResourceImportService.cs");
const parentForm = read("src/AssistQuestEditor.App/Host/ImportParentForm.cs");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
const archiveService = read("src/AssistQuestEditor.App/WorldArchiveService.cs");

// --- 1. Родитель спрашивается, а не выдумывается ---
check(/class ImportParentForm/.test(parentForm),
  "Нет выбора родителя: кампания и квест некуда положить.");
check(/Импорт " \+ kindLabel \+ " — выберите родителя/.test(parentForm),
  "Заголовок окна выбора родителя не объясняет, что выбирается.");
// Мир из архива рекомендуется, но НЕ применяется автоматически: решение за
// пользователем, иначе ресурс уедет в мир, которого он не выбирал.
check(/указан в архиве/.test(parentForm) && /preferred < 0/.test(parentForm),
  "Мир из архива не рекомендуется или навязывается: выбор не должен быть молчаливым.");
// Список кампаний строится при смене мира: у разных миров разные кампании, и
// список от прежнего мира отправил бы квест в чужую.
check(/private void ReloadCampaigns/.test(parentForm) &&
      /SelectedIndexChanged \+= \(_, _\) => ReloadCampaigns/.test(parentForm),
  "Список кампаний не пересчитывается при смене мира.");
// Проверка неполного выбора в FormClosing: кнопка с DialogResult закрывает окно
// сама, и сообщить о неполном выборе из Click было бы негде.
check(/FormClosing \+= \(_, e\) =>/.test(parentForm) && /e\.Cancel = true/.test(parentForm),
  "Окно выбора родителя закрывается без проверки: импорт пойдёт некуда.");
check(/без родителя у ресурса нет адреса/.test(parentForm),
  "Отсутствие мира не объясняется словами.");
check(/квест лежит внутри кампании/.test(parentForm),
  "Отсутствие кампании не объясняется словами.");

// --- 2. Раскладка берётся из контракта, а не собирается на месте ---
check(/WorldPaths\.CampaignsRoot\(world\.FolderPath\)/.test(importService),
  "Импорт кампании не использует контракт раскладки: путь может разойтись с каталогом.");
check(/WorldPaths\.QuestsFolderPath\(campaign\.FolderPath\)/.test(importService),
  "Импорт квеста не использует контракт раскладки.");
check(/WorldPaths\.CampaignFileName/.test(importService),
  "Имя файла кампании задано литералом вместо контракта.");
// Квест переносится как ОДИН ФАЙЛ, а не папка: квесты кампании лежат в общей
// папке quests, и удаление её целиком стёрло бы соседей. Ставим именно копию
// готового файла, поэтому принимаем и Copy, и Move, но только в targetFile.
check(
  /File\.Copy\(tempQuest, targetFile, overwrite: true\)/.test(importService) ||
  /File\.Move\([^,]+, targetFile, overwrite: true\)/.test(importService),
  "Квест должен устанавливаться как один отдельный файл; соседние квесты нельзя затрагивать.");
check(!/Directory\.Delete\(questsFolder/.test(importService),
  "Импорт квеста удаляет папку кампании целиком.");
// Имя файла квеста — папка назначения плюс каноническое расширение: произвольное
// имя сделало бы ресурс невидимым для каталога, хотя файл лежал бы на месте.
check(/QuestExtension = "\.aqquest"/.test(importService) &&
      /Path\.Combine\(questsFolder, targetName \+ QuestExtension\)/.test(importService),
  "Имя файла квеста не приводится к каноническому: каталог его не увидит.");

// --- 3. Временная папка и проверка перед переносом ---
// Разбор идёт в каталог временных файлов: путь задан двумя аргументами
// Path.Combine, поэтому переводы строк между ними допустимы.
check(/Path\.GetTempPath\(\),\s*\n\s*"aq-import-"/.test(importService),
  "Импорт распаковывает сразу на место: прерванный импорт оставит битый ресурс.");
// Перенос на место — НЕ безусловный Directory.Move: временная папка лежит на
// системном диске, а папка мира часто на другом, и переименование между томами
// невозможно в принципе. Прежняя проверка требовала именно Directory.Move, то
// есть закрепляла дефект: импорт падал у автора, а CI был зелёным.
check(/StagedFolderMover\.IntoPlace\(/.test(importService),
  "Импорт кампании должен ставить папку на место через StagedFolderMover.");
check(!/Directory\.Move\(staging/.test(importService),
  "Безусловный Directory.Move временной папки запрещён: он не работает между томами.");
// Без проверки на месте появился бы ресурс, который стор не увидит.
check(/throw new InvalidDataException\(\s*\n\s*"В архиве нет "/.test(importService),
  "Отсутствие файла ресурса в архиве не проверяется до переноса.");
check(/private static void Cleanup/.test(importService),
  "Временная папка не убирается при ошибке.");
// Вид архива проверяется: иначе папка мира легла бы внутрь кампании.
check(/private static void EnsureKind/.test(importService) &&
      /EnsureKind\(inspection, WorldArchiveKinds\.Campaign\)/.test(importService) &&
      /EnsureKind\(inspection, WorldArchiveKinds\.Quest\)/.test(importService),
  "Вид архива не проверяется: импорт «наугад» положит не тот ресурс.");
// Безопасность путей остаётся на распаковщике.
check(/WorldArchiveRules\.IsSafeEntryPath/.test(archiveService),
  "Распаковка архива потеряла проверку путей.");

// --- 4. Host доводит импорт до конца ---
check(/new ImportParentForm\(/.test(mainForm),
  "Главная форма не спрашивает родителя при импорте кампании/квеста.");
check(/ResourceImportService\.ImportQuest\(/.test(mainForm) &&
      /ResourceImportService\.ImportCampaign\(/.test(mainForm),
  "Импорт кампании/квеста не доведён до сервиса.");
// Чужой каталог: кампании с одним id (например common) есть в каждом мире.
check(/new CampaignStore\(folder, readOnly: true\)/.test(mainForm),
  "Кампании родителя читаются из общего каталога: подставится чужая.");
// Импорт в ТЕКУЩИЙ мир меняет каталог симулятора, и без перечитывания нового
// квеста на карте не будет.
check(/ReloadCatalog\("resource imported"\)/.test(mainForm),
  "Симулятор не перечитывает каталог после импорта: нового квеста не видно.");
check(/PostWorldSelection\(\);\s*\n\s*\}\s*\n\s*catch/.test(mainForm) ||
      /PostWorldSelection\(\);/.test(mainForm),
  "Селектор не обновляется после импорта: состав кампаний останется прежним.");

// --- 5. Настоящий запуск ---
const exe = findExecutable();

// Рабочий каталог объявлен ЗДЕСЬ, а не внутри ветки: убирать его нужно и при
// ошибке, и он должен быть виден коду уборки ниже.
let workspace = null;

if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки импорта.");
} else {
  workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-import-"));
  const worldFolder = path.join(workspace, "worlds", "TestWorld");
  const commonFolder = path.join(worldFolder, "campaigns", "common");
  const otherFolder = path.join(worldFolder, "campaigns", "story");

  fs.mkdirSync(path.join(commonFolder, "quests"), { recursive: true });
  fs.mkdirSync(path.join(otherFolder, "quests"), { recursive: true });

  writeWorld(worldFolder, "testworld", "TestWorld");
  writeCampaign(commonFolder, "common", "Common");
  writeCampaign(otherFolder, "story", "Сюжет");

  // Существующий квест-сосед: он обязан уцелеть при импорте квеста.
  const neighbour = path.join(commonFolder, "quests", "neighbour.aqquest");
  fs.writeFileSync(neighbour, '{"format":"aqquest","schemaVersion":1}\n', "utf8");

  // Архивы собираются тем же упаковщиком, что и «Экспорт».
  const campaignArchive = path.join(workspace, "campaign.aqezip");
  const questArchive = path.join(workspace, "quest.aqezip");

  buildArchive(exe, workspace, "campaign", campaignArchive);
  buildArchive(exe, workspace, "quest", questArchive);

  let probeCounter = 0;
  const runImport = (worldId, campaignId, archive, options = {}) => {
    probeCounter += 1;
    const report = path.join(workspace, `import-${probeCounter}.txt`);
    // Корень по умолчанию — рабочий каталог проверки. Импорт МИРА идёт в
    // отдельный корень: у мира нет родителя, и его адрес задаёт сам каталог
    // миров, поэтому проверка не должна зависеть от ранее созданных миров.
    const root = options.root ?? workspace;

    execFileSync(exe, ["--import-probe", root, worldId, campaignId, archive,
                       "--report", report, ...(options.extra ?? [])],
      { stdio: "pipe", timeout: 120000 });

    return fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  };

  // --- Кампания ---
  const campaignReport = runImport("testworld", "-", campaignArchive);
  check(/Импорт выполнен/.test(campaignReport), "Импорт кампании не подтверждён: " + campaignReport);

  const importedCampaignPath = /Путь: (.+)/.exec(campaignReport)?.[1]?.trim() ?? "";
  check(importedCampaignPath.startsWith(path.join(worldFolder, "campaigns")),
    "Кампания легла вне папки мира: " + importedCampaignPath);
  check(fs.existsSync(path.join(importedCampaignPath, "campaign.aqcampaign")),
    "В импортированной кампании нет файла кампании: " + listTree(importedCampaignPath).join(", "));

  // Соседние кампании обязаны уцелеть: импорт не переписывает каталог мира.
  check(fs.existsSync(path.join(commonFolder, "campaign.aqcampaign")) &&
        fs.existsSync(path.join(otherFolder, "campaign.aqcampaign")),
    "Импорт кампании уничтожил существующие кампании мира.");
  check(fs.existsSync(neighbour), "Импорт кампании стёр квест соседней кампании.");

  // --- Квест В конкретную кампанию ---
  const questReport = runImport("testworld", "common", questArchive);
  check(/Импорт выполнен/.test(questReport), "Импорт квеста не подтверждён: " + questReport);

  const importedQuestPath = /Путь: (.+)/.exec(questReport)?.[1]?.trim() ?? "";
  check(importedQuestPath.endsWith(".aqquest"),
    "Файл квеста без расширения .aqquest: " + importedQuestPath);
  check(path.dirname(importedQuestPath) === path.join(commonFolder, "quests"),
    "Квест лёг не в папку quests выбранной кампании: " + importedQuestPath);
  check(fs.existsSync(neighbour),
    "Импорт квеста стёр соседний квест кампании: " + listTree(path.dirname(importedQuestPath)).join(", "));

  // --- Квест в ДРУГУЮ кампанию: тот же архив, но другой родитель ---
  const secondQuestReport = runImport("testworld", "story", questArchive);
  const secondQuestPath = /Путь: (.+)/.exec(secondQuestReport)?.[1]?.trim() ?? "";
  check(path.dirname(secondQuestPath) === path.join(otherFolder, "quests"),
    "Квест ушёл не в ту кампанию: " + secondQuestPath);
  check(!path.dirname(secondQuestPath).includes("common"),
    "Квест лёг в прежнюю кампанию: выбор родителя не влияет на адрес.");

  // --- Повторный импорт без перезаписи не затирает первый ---
  const repeatReport = runImport("testworld", "common", questArchive);
  const repeatPath = /Путь: (.+)/.exec(repeatReport)?.[1]?.trim() ?? "";
  check(repeatPath !== importedQuestPath,
    "Повторный импорт перезаписал существующий квест: " + repeatPath);
  check(fs.existsSync(importedQuestPath), "Первый импортированный квест исчез после повтора.");
  check(fs.existsSync(neighbour), "Повторный импорт стёр соседний квест.");

  // --- Импорт МИРА ---
  //
  // Раньше этот путь не вызывался из командной строки вообще: «Пропустить» в
  // окне первого мира шло в WorldStore.ImportBundledDemoWorld, и та же ветка
  // переноса временной папки оставалась непроверяемой. Именно поэтому дефект
  // «Move will not work across volumes» — временная папка на системном диске,
  // каталог миров на другом — дожил до ручного запуска у автора. Здесь
  // проверяется ФАКТИЧЕСКАЯ раскладка на диске: мир появился целиком, вместе с
  // содержимым, а не только файлом мира.
  const worldSource = path.join(workspace, "src-world");
  fs.mkdirSync(path.join(worldSource, "campaigns", "story", "quests"), { recursive: true });
  fs.mkdirSync(path.join(worldSource, "Saves"), { recursive: true });
  fs.writeFileSync(path.join(worldSource, "world.aqworld"),
    JSON.stringify({
      schemaVersion: 1, format: "aqworld",
      definition: {
        id: "importedworld", name: "ImportedWorld", fullName: "Импортированный Мир",
        version: 1, metadata: { createdBy: "Тест", createdOn: "2026-09-01T10:00:00+00:00" }
      }
    }, null, 2) + "\n", "utf8");
  fs.writeFileSync(path.join(worldSource, "campaigns", "story", "campaign.aqcampaign"),
    JSON.stringify({
      schemaVersion: 1, format: "aqcampaign",
      definition: {
        id: "story", name: "Сюжет", version: 3, active: true,
        quests: [], files: [], worldId: "importedworld",
        metadata: { createdBy: "Тест", createdOn: "2026-09-01T10:00:00+00:00" }
      }
    }, null, 2) + "\n", "utf8");

  const worldArchive = path.join(workspace, "imported-world.aqezip");
  execFileSync(exe, ["--build-archive", worldSource, worldArchive, "world", "importedworld"],
    { stdio: "pipe", timeout: 120000 });

  // Импорт идёт в ОТДЕЛЬНЫЙ корень: у мира нет родителя, и адрес ему задаёт
  // каталог миров. Так проверка не зависит от уже созданного TestWorld.
  const worldRoot = path.join(workspace, "world-target");
  const worldReport = runImport("-", "-", worldArchive, { root: worldRoot });
  check(/Импорт выполнен/.test(worldReport), "Импорт мира не подтверждён: " + worldReport);

  const importedWorldPath = /Путь: (.+)/.exec(worldReport)?.[1]?.trim() ?? "";
  check(importedWorldPath.startsWith(path.join(worldRoot, "worlds")),
    "Мир лёг вне каталога миров: " + importedWorldPath);
  // Проверяется СОДЕРЖИМОЕ, а не только определитель: при переносе между томами
  // копирование могло бы донести один файл мира и потерять остальное — и мир
  // выглядел бы пустым, хотя «импорт прошёл».
  check(fs.existsSync(path.join(importedWorldPath, "world.aqworld")),
    "В импортированном мире нет world.aqworld: " + listTree(importedWorldPath).join(", "));
  check(fs.existsSync(path.join(importedWorldPath, "campaigns", "story", "campaign.aqcampaign")),
    "Импорт мира потерял кампанию: " + listTree(importedWorldPath).join(", "));
  check(fs.existsSync(path.join(importedWorldPath, "Saves")),
    "Импорт мира потерял ПУСТОЙ каталог Saves: " + listTree(importedWorldPath).join(", "));

  // Промежуточная папка не должна оставаться рядом: незавершённая установка
  // выглядела бы как второй мир.
  check(!fs.existsSync(importedWorldPath + ".installing"),
    "Рядом с импортированным миром осталась промежуточная папка установки.");

  // Повторный импорт того же архива кладётся рядом и НЕ трогает первый.
  const repeatWorldReport = runImport("-", "-", worldArchive, { root: worldRoot });
  const repeatWorldPath = /Путь: (.+)/.exec(repeatWorldReport)?.[1]?.trim() ?? "";
  check(repeatWorldPath !== importedWorldPath,
    "Повторный импорт мира перезаписал существующий: " + repeatWorldPath);
  check(fs.existsSync(path.join(importedWorldPath, "world.aqworld")),
    "Первый импортированный мир исчез после повторного импорта.");

  // --- Вид архива проверяется ---
  let kindRejected = false;
  try {
    // Квестовый архив в роль кампании: папка мира легла бы внутрь папки кампаний.
    runImport("testworld", "-", questArchive);
  } catch {
    kindRejected = true;
  }
  check(kindRejected, "Квестовый архив принят как кампания: вид ресурса не проверяется.");

  // --- Несуществующий родитель ---
  let parentRejected = false;
  try {
    runImport("testworld", "nosuch", questArchive);
  } catch {
    parentRejected = true;
  }
  check(parentRejected, "Импорт квеста принял несуществующую кампанию.");
}

// Рабочий каталог убирается ВСЕГДА, до вердикта: он временный и на 61 запись в
// каждой прогонке, и при провале проверки его оставляли в TEMP. Каталог
// назывался тем же префиксом `aq-import-`, что и подготовительные папки самого
// приложения, поэтому брошенные каталоги выглядели как «импорт не убирает за
// собой» — на них и ловилась ложная тревога. Уборка в `finally` не даёт проверке
// ни упасть на ошибке удаления, ни скрыть её: файл мог остаться заблокированным.
if (workspace !== null) {
  try {
    fs.rmSync(workspace, { recursive: true, force: true });
  } catch (cleanupError) {
    console.log("Предупреждение: не удалось убрать рабочий каталог " +
      workspace + ": " + cleanupError.message);
  }
}

if (failures.length) {
  console.log("Импорт кампании и квеста: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Импорт кампании и квеста: OK раскладка по контракту, соседи целы, повтор не затирает, вид ресурса проверяется.");

function writeWorld(folder, id, name) {
  fs.mkdirSync(folder, { recursive: true });
  const doc = {
    schemaVersion: 1, format: "aqworld",
    definition: { id, name, version: 1, metadata: { createdBy: "Тест", createdOn: "2026-09-01T10:00:00+00:00" } }
  };
  fs.writeFileSync(path.join(folder, "world.aqworld"), JSON.stringify(doc, null, 2) + "\n", "utf8");
}

function writeCampaign(folder, id, name) {
  fs.mkdirSync(folder, { recursive: true });
  const doc = {
    schemaVersion: 1, format: "aqcampaign",
    definition: {
      id, name, version: 1, active: id === "common",
      quests: [], files: [], worldId: "testworld",
      metadata: { createdBy: "Тест", createdOn: "2026-09-01T10:00:00+00:00" }
    }
  };
  fs.writeFileSync(path.join(folder, "campaign.aqcampaign"), JSON.stringify(doc, null, 2) + "\n", "utf8");
}

// Архивы собираются НАСТОЯЩИМ упаковщиком: проверка раскладки имеет смысл
// только для того вида архива, который приложение и создаёт.
function buildArchive(exe, workspace, kind, target) {
  const source = path.join(workspace, `src-${kind}`);
  fs.rmSync(source, { recursive: true, force: true });
  fs.mkdirSync(source, { recursive: true });

  if (kind === "campaign") {
    fs.mkdirSync(path.join(source, "quests"), { recursive: true });
    fs.writeFileSync(path.join(source, "campaign.aqcampaign"),
      JSON.stringify({
        schemaVersion: 1, format: "aqcampaign",
        definition: {
          id: "imported_campaign", name: "Импортированная", fullName: "Импортированная кампания",
          version: 1, active: false,
          // Форма CampaignQuestEntry, а не строки: канонический формат читает
          // объект, и список строк сделал бы кампанию нечитаемой — а «импорт
          // прошёл, но ресурс не читается» здесь и есть проверяемый случай.
          quests: [{ questId: "inner", relativePath: "quests/inner.aqquest",
                     status: "Enabled", order: 1 }],
          files: ["quests/inner.aqquest"],
          worldId: "testworld",
          metadata: { createdBy: "Тест", createdOn: "2026-09-01T10:00:00+00:00" }
        }
      }, null, 2) + "\n", "utf8");
    fs.writeFileSync(path.join(source, "quests", "inner.aqquest"),
      JSON.stringify({
        schemaVersion: 1, format: "aqquest",
        definition: {
          id: "inner", title: "Внутренний квест", description: "",
          graph: { id: "inner", name: "Внутренний квест", nodes: [], connections: [] },
          sceneIds: []
        }
      }, null, 2) + "\n", "utf8");
  } else {
    fs.writeFileSync(path.join(source, "imported_quest.aqquest"),
      JSON.stringify({
        schemaVersion: 1, format: "aqquest",
        definition: {
          id: "imported_quest", title: "Импортированный квест", description: "",
          // Граф ОБЯЗАТЕЛЕН: импорт переносит квест в родителя и переписывает
          // graph.id/graph.name, поэтому ресурс без графа — повреждённый, и
          // падение на нём было бы дефектом самой фикстуры, а не импорта.
          graph: { id: "imported_quest", name: "Импортированный квест", nodes: [], connections: [] },
          sceneIds: []
        }
      }, null, 2) + "\n", "utf8");
  }

  // Проба выгрузки умеет только мир — поэтому архив здесь собирается напрямую
  // через `--build-archive`, а не через папочный экспорт: вид ресурса задаётся
  // манифестом, и подменять его значило бы проверять не то.
  execFileSync(exe, ["--build-archive", source, target, kind, "testworld"],
    { stdio: "pipe", timeout: 120000 });
}

function listTree(folder) {
  if (!fs.existsSync(folder)) return [];
  const result = [];
  const stack = [folder];
  while (stack.length) {
    const current = stack.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      const relative = path.relative(folder, full).split(path.sep).join("/");
      if (entry.isDirectory()) { result.push(relative + "/"); stack.push(full); }
      else result.push(relative);
    }
  }
  return result;
}

function findExecutable() {
  const base = path.join(root, "src", "AssistQuestEditor.App", "bin");
  if (!fs.existsSync(base)) return null;

  // Берётся САМЫЙ СВЕЖИЙ exe: в дереве могут лежать сборки Debug и Release, и
  // произвольная из них оказалась бы устаревшей — проба проверяла бы прежний код.
  const found = [];
  const stack = [base];
  while (stack.length) {
    const current = stack.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (entry.name === "AssistQuestEditor.exe") found.push(full);
    }
  }

  return found.sort((left, right) =>
    fs.statSync(right).mtimeMs - fs.statSync(left).mtimeMs)[0] ?? null;
}
