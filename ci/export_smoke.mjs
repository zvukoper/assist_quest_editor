// Экспорт миров и кампаний: папкой и архивом .aqezip.
//
// Почему настоящий запуск, а не чтение исходников: обе ветки выгрузки —
// это запись файлов, и «правильный код» ничего не говорит о том, легло ли на
// диск то же содержимое, что в источнике. Поэтому проба запускает приложение с
// `--export-probe` (тот же `ResourceExportService`, что и в меню) и сравнивает
// деревья файлов.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const exportService = read("src/AssistQuestEditor.App/ResourceExportService.cs");
const exportForm = read("src/AssistQuestEditor.App/Host/ResourceExportForm.cs");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
const mainJs = read("src/AssistQuestEditor.App/Web/main.js");

// --- 1. Галочка «Архивация в aqezip» ---
check(/Text = "Архивация в aqezip"/.test(exportForm),
  "В диалоге экспорта нет галочки «Архивация в aqezip».");
// Умолчание — папка: архив необратимо превращает ресурс в один файл, и
// отмеченный по умолчанию флаг означал бы потерю читаемой копии без выбора.
check(/Text = "Архивация в aqezip",\s*\n\s*Checked = false/.test(exportForm),
  "Галочка архивации отмечена по умолчанию: папка с файлами должна быть умолчанием.");
check(/ArchiveRequested => _archive\.Checked/.test(exportForm),
  "Решение диалога о режиме выгрузки не читается наружу.");
// Диалог не должен знать о диске: иначе его нельзя проверить без файловой системы.
check(!/File\.(Write|Copy|Delete)|Directory\.(Create|Delete|Move)/.test(exportForm),
  "Диалог экспорта сам трогает файловую систему — выгрузку обязан выполнять вызывающий код.");
// Обе ветки называются в интерфейсе по-разному: одно действие даёт разные
// последствия, и узнать об этом надо до нажатия.
check(/\.aqezip для пересылки/.test(exportForm),
  "Диалог не объясняет разницу между папкой и архивом.");
check(/_destination\.Text = ArchiveRequested/.test(exportForm),
  "Диалог не показывает разный путь для папки и архива.");

// --- 2. Обе ветки действительно реализованы, и не только для мира ---
check(/public static ResourceExportResult ExportFolder/.test(exportService),
  "Нет выгрузки папкой.");
check(/public static ResourceExportResult ExportArchive/.test(exportService),
  "Нет выгрузки архивом.");
// Зависимости для архива включаются всегда: «квест без сцен» — не усечённый
// ресурс, а неработающий.
check(/includeDependencies: true\)/.test(exportService),
  "Архив экспорта обязан включать зависимости: иначе пересланный ресурс не работает.");
// Пустые каталоги копируются, как и при упаковке: в мире они несут смысл.
check(/EnumerateDirectories\(source, "\*", SearchOption\.AllDirectories\)/.test(exportService),
  "Выгрузка папкой обязана сохранять пустые каталоги: без них теряется структура.");
// Имя ресурса приходит из данных, поэтому режется до одного сегмента.
// Проверка идёт по факту: разделители и «..» отвергаются, а не «исправляются»,
// и путь дополнительно сверяется с корнем выгрузки.
check(/IndexOfAny\(new\[\] \{ '\/', '\\\\', ':' \}\)/.test(exportService),
  "Разделители пути и «..» в имени ресурса не отвергаются: выгрузка уйдёт за пределы Exported.");
check(/EnsureInside/.test(exportService),
  "Собранный путь не сверяется с корнем выгрузки — нужна вторая линия защиты.");

check(/case "export_world":/.test(mainForm) && /case "export_campaign":/.test(mainForm),
  "Пункты меню экспорта не обрабатываются Host.");
check(/ExportWorld\(\)/.test(mainForm) && /ExportCampaign\(/.test(mainForm),
  "Экспорт мира/кампании не доведён до выгрузки.");
// Не выбрана кампания при нескольких доступных — выгрузилась бы не та.
check(/не определена активная кампания/.test(mainForm),
  "Экспорт кампании не отказывается работать при неопределённой кампании.");
check(/campaign: true/.test(mainJs),
  "main.js не помечает действия над кампанией: Host не узнает, какую выгружать.");

// --- 3. Настоящий запуск обеих веток ---
const exe = findProbeExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для пробы выгрузки.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-export-"));
  const source = path.join(workspace, "источник");
  const output = path.join(workspace, "out");

  // Источник с вложенностью, пустым каталогом и файлами разных типов: именно
  // структура — то, что теряется при наивной упаковке.
  fs.mkdirSync(path.join(source, "campaigns", "common", "quests"), { recursive: true });
  fs.mkdirSync(path.join(source, "campaigns", "common", "scenes"), { recursive: true });
  fs.mkdirSync(path.join(source, "Saves"), { recursive: true });
  fs.writeFileSync(path.join(source, "world.aqworld"), '{"format":"aqworld"}\n', "utf8");
  fs.writeFileSync(path.join(source, "campaigns", "common", "campaign.aqcampaign"),
    '{"format":"aqcampaign"}\n', "utf8");
  fs.writeFileSync(path.join(source, "campaigns", "common", "quests", "intro.aqquest"),
    '{"format":"aqquest"}\n', "utf8");

  let reportCounter = 0;
  const runProbe = (extra = [], moment = null) => {
    // Отчёт — свой на каждый запуск. Без этого вторая проба читала бы файл
    // первой и подтверждала бы не свой результат.
    reportCounter += 1;
    const report = path.join(workspace, `report-${reportCounter}.txt`);
    const momentArgs = moment === null ? [] : ["--moment", moment];

    execFileSync(exe, ["--export-probe", source, "Тестовый Мир", "--out", output,
                      "--report", report, ...momentArgs, ...extra],
      { stdio: "pipe", timeout: 120000 });

    return fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  };

  // Момент фиксирован: он задаёт имя папки выгрузки, и только с ним проверяемо
  // поведение при уже занятом имени.
  const MOMENT = "2026-09-24T12:00:00Z";

  // --- Папкой ---
  const folderReport = runProbe([], MOMENT);
  check(/Архив: нет/.test(folderReport), "Проба папкой сообщила архивный режим: " + folderReport);
  const folderPath = /Путь: (.+)/.exec(folderReport)?.[1]?.trim() ?? "";
  check(folderPath !== "", "Проба папкой не назвала путь: " + folderReport);
  check(folderPath.startsWith(output), "Выгрузка ушла мимо указанного корня: " + folderPath);
  // Метка времени в пути: без неё две выгрузки перемешались бы.
  check(/\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}/.test(folderPath),
    "В пути выгрузки нет метки времени: две выгрузки подряд смешаются: " + folderPath);

  const folderTree = listTree(folderPath);
  check(folderTree.includes("world.aqworld"), "В выгрузке нет файла мира: " + folderTree.join(", "));
  check(folderTree.includes("campaigns/common/campaign.aqcampaign"),
    "В выгрузке нет кампании: " + folderTree.join(", "));
  check(folderTree.includes("campaigns/common/quests/intro.aqquest"),
    "В выгрузке нет квеста: " + folderTree.join(", "));
  // Пустой каталог обязан сохраниться: автор должен видеть, куда класть сохранения.
  check(folderTree.includes("Saves/"),
    "Пустой каталог Saves потерян при выгрузке папкой: " + folderTree.join(", "));
  check(fileSize(path.join(folderPath, "world.aqworld")) ===
        fileSize(path.join(source, "world.aqworld")),
    "Размер файла мира изменился при выгрузке.");

  // --- Архивом ---
  const archiveReport = runProbe(["--archive"], MOMENT);
  check(/Архив: да/.test(archiveReport), "Проба архивом сообщила папочный режим: " + archiveReport);
  const archivePath = /Путь: (.+)/.exec(archiveReport)?.[1]?.trim() ?? "";
  check(archivePath.endsWith(".aqezip"),
    "Архив выгружен без расширения .aqezip: " + archivePath);
  check(fs.existsSync(archivePath), "Файла архива нет на диске: " + archivePath);
  check(fs.statSync(archivePath).size > 0, "Архив пустой.");
  // Имя совпадает с уже выгруженной папкой из предыдущего шага. Архив обязан
  // получить номер, а не попытаться открыть каталог как файл: иначе выгрузка
  // падала бы ровно в том случае, ради которого нумерация и сделана.
  const archiveRoot = path.dirname(archivePath);
  check(path.basename(archiveRoot) === momentFolder(folderPath),
    "Архив ушёл в другую папку с меткой времени, чем папка: " +
    folderPath + " против " + archivePath);
  check(fs.statSync(folderPath).isDirectory(),
    "Выгрузка папкой перестала быть каталогом после выгрузки архива с тем же именем.");

  // Содержимое архива — обычный ZIP, поэтому состав проверяется распаковкой:
  // «файл есть» не значит «внутри то, что нужно».
  const zipCopy = archivePath + ".zip";
  fs.copyFileSync(archivePath, zipCopy);

  const unpackRoot = path.join(workspace, "unpacked");
  extractZip(zipCopy, unpackRoot);

  const unpackedTree = listTree(unpackRoot);
  check(unpackedTree.includes("archive.json"),
    "В архиве нет манифеста: " + unpackedTree.join(", "));
  check(unpackedTree.includes("world.aqworld"),
    "В архиве нет файла мира: " + unpackedTree.join(", "));
  check(unpackedTree.includes("campaigns/common/quests/intro.aqquest"),
    "В архиве нет квеста: " + unpackedTree.join(", "));
  check(unpackedTree.includes("Saves/"),
    "В архиве нет пустого каталога Saves: " + unpackedTree.join(", "));

  const manifest = JSON.parse(fs.readFileSync(path.join(unpackRoot, "archive.json"), "utf8"));
  check(manifest.format === "aqezip", "Манифест архива не aqezip: " + manifest.format);
  check(manifest.schemaVersion === 1, "Версия схемы архива не 1: " + manifest.schemaVersion);
  check(manifest.definition.kind === "world",
    "Манифест описывает не мир: " + manifest.definition.kind);
  check(manifest.definition.includesDependencies === true,
    "Манифест не подтверждает включённые зависимости.");
  // Состав манифеста обязан быть фактическим: иначе он расходится с содержимым.
  const manifestPaths = manifest.definition.entries.map(entry => entry.path);
  for (const required of ["world.aqworld", "campaigns/common/quests/intro.aqquest"]) {
    check(manifestPaths.includes(required),
      `В манифесте нет записи «${required}»: ` + manifestPaths.join(", "));
  }
  check(manifestPaths.length === unpackedTree.filter(item => !item.endsWith("/") &&
        item !== "archive.json").length,
    "Число записей манифеста не совпадает с числом файлов: " +
    manifestPaths.length + " против " + unpackedTree.length);

  // --- Одинаковое имя не затирает прошлую выгрузку ---
  // Тот же момент, что и у первой выгрузки: имя папки совпадает намеренно —
  // иначе проверять нечего.
  const secondReport = runProbe([], MOMENT);
  const secondPath = /Путь: (.+)/.exec(secondReport)?.[1]?.trim() ?? "";
  check(secondPath !== folderPath,
    "Повторная выгрузка перезаписала предыдущую: " + secondPath);
  check(/\(2\)/.test(secondPath),
    "Занятое имя не получило номер «(2)»: " + secondPath);
  check(fs.existsSync(folderPath), "Первая выгрузка исчезла после второй.");
  check(fs.readdirSync(path.dirname(folderPath)).length >= 2,
    "Вторая выгрузка не легла рядом с первой в тот же каталог.");

  // --- Архив поверх одноимённой папки ---
  // Имя архива — «Тестовый Мир» плюс расширение, и оно не конфликтует с папкой:
  // расширение гарантируется сервисом, а не вызывающим кодом.
  check(archivePath !== folderPath,
    "Архив и папка получили одно и то же имя: расширение .aqezip не добавляется.");

  const secondArchiveReport = runProbe(["--archive"], MOMENT);
  const secondArchivePath = /Путь: (.+)/.exec(secondArchiveReport)?.[1]?.trim() ?? "";
  check(secondArchivePath !== archivePath,
    "Повторный архив перезаписал предыдущий: " + secondArchivePath);
  check(/\(2\)/.test(secondArchivePath),
    "Занятое имя архива не получило номер «(2)»: " + secondArchivePath);
  check(fs.existsSync(archivePath), "Первый архив исчез после второго.");

  // --- Проба не выходит за корень при «..» в имени ---
  // Отчёт уносится за пределы каталога выгрузки: подмена «побег» обязана быть
  // отвергнута, а не превратиться в запись рядом с корнем.
  const escapeReport = path.join(workspace, "escape-report.txt");
  let escapeRejected = false;
  try {
    execFileSync(exe, ["--export-probe", source, "../побег", "--out", output,
                       "--report", escapeReport, "--moment", MOMENT],
      { stdio: "pipe", timeout: 120000 });
  } catch {
    escapeRejected = true;
  }
  check(escapeRejected,
    "Имя ресурса с «..» не отвергнуто: выгрузка может записать файлы вне каталога Exported.");
  check(!fs.existsSync(path.join(output, "побег")) &&
        !fs.existsSync(path.join(path.dirname(output), "побег")),
    "Имя с «..» всё-таки записало файлы вне каталога выгрузки.");
}

if (failures.length) {
  console.log("Экспорт ресурсов: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Экспорт ресурсов: OK папка и архив дают одинаковое содержимое, зависимости включены, имя не затирается.");

function findProbeExecutable() {
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

function listTree(folder) {
  const result = [];
  const stack = [folder];
  while (stack.length) {
    const current = stack.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      const relative = path.relative(folder, full).split(path.sep).join("/");
      if (entry.isDirectory()) {
        result.push(relative + "/");
        stack.push(full);
      } else {
        result.push(relative);
      }
    }
  }
  return result;
}

// Объявление функции, а не const-стрелка: проба использует её ВЫШЕ по файлу, и
// стрелка в const упала бы с «Cannot access before initialization».
function fileSize(file) { return fs.statSync(file).size; }

// Имя подпапки с меткой времени внутри Exported: по нему проверяется, что оба
// режима выгрузки идут в ОДНУ выгрузку, а не в две разные.
function momentFolder(fullPath) {
  const parts = fullPath.split(path.sep);
  return parts[parts.length - 2];
}

// Разбор ZIP — через модуль .NET, а не через готовую зависимость: лишний пакет
// ради одной пробы в поставку тянуть не нужно, а PowerShell есть всегда.
function extractZip(zipPath, target) {
  fs.mkdirSync(target, { recursive: true });
  execFileSync("powershell", ["-NoProfile", "-Command",
    `Expand-Archive -LiteralPath '${zipPath}' -DestinationPath '${target}' -Force`],
    { stdio: "pipe", timeout: 120000 });
}
