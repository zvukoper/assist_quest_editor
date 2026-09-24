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
// папке quests, и удаление её целиком стёрло бы соседей.
check(/File\.Move\(staged, targetFile, overwrite: true\)/.test(importService),
  "Квест копируется не как отдельный файл: соседние квесты кампании пострадают.");
check(!/Directory\.Delete\(questsFolder/.test(importService),
  "Импорт квеста удаляет папку кампании целиком.");
// Имя файла квеста — id плюс каноническое расширение: произвольное имя сделало
// бы ресурс невидимым для каталога, хотя файл лежал бы на месте.
check(/QuestExtension = "\.aqquest"/.test(importService) &&
      /fileName = desired \+ QuestExtension/.test(importService),
  "Имя файла квеста не приводится к каноническому: каталог его не увидит.");

// --- 3. Временная папка и проверка перед переносом ---
check(/Path\.GetTempPath\(\), "aq-import-"/.test(importService),
  "Импорт распаковывает сразу на место: прерванный импорт оставит битый ресурс.");
check(/Directory\.Move\(staging, targetFolder\)/.test(importService),
  "Импорт кампании не переносит содержимое целиком.");
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
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки импорта.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-import-"));
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
  const runImport = (worldId, campaignId, archive, extra = []) => {
    probeCounter += 1;
    const report = path.join(workspace, `import-${probeCounter}.txt`);

    execFileSync(exe, ["--import-probe", workspace, worldId, campaignId, archive,
                       "--report", report, ...extra],
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
      '{"schemaVersion":1,"format":"aqquest"}\n', "utf8");
  } else {
    fs.writeFileSync(path.join(source, "imported_quest.aqquest"),
      JSON.stringify({
        schemaVersion: 1, format: "aqquest",
        definition: { id: "imported_quest", name: "Импортированный квест" }
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
