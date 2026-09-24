// Демо-мир: архив, который предлагает кнопка «Пропустить» при первом запуске.
//
// Проверка РАСПАКОВЫВАЕТ архив во временную папку и читает получившееся
// ресурсное дерево, а не сверяет список строк. Причина: архив — это сжатый
// бинарник, и ошибка в нём (неверный путь, забытый файл манифеста, кириллица,
// потерянная при упаковке) не видна ни в исходниках, ни по размеру файла.
// Убедиться можно только распаковав.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const archive = "data/DemoWorld.aqezip";
const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

if (!fs.existsSync(archive)) {
  console.log("Демо-мир: FAIL");
  console.log(" - Нет файла " + archive + ": сборка ресурсов должна его создавать.");
  process.exit(1);
}

const archiveBytes = fs.statSync(archive).size;
check(archiveBytes > 0, "Архив демо-мира пуст.");

// Распаковка через PowerShell Expand-Archive — стандартный инструмент, а не
// самодельный распаковщик: если содержимое архива не читается штатным
// архиватором, приложение тоже его не прочитает.
//
// Оговорка, из-за которой файл копируется под именем .zip: Expand-Archive
// отказывается работать с НЕИЗВЕСТНЫМ ему расширением
// (NotSupportedArchiveFileExtension), а .aqezip он не знает. Это ограничение
// инструмента, а не признак плохого архива: содержимое .aqezip — обычный ZIP,
// и приложение читает его через ZipArchive напрямую. Поэтому проверяется
// СОДЕРЖИМОЕ (под нейтральным расширением), а не реакция Expand-Archive на
// наше расширение.
const staging = path.join(os.tmpdir(), "aq-demo-smoke-" + Date.now());
fs.mkdirSync(staging, { recursive: true });

const neutralCopy = path.join(staging, "DemoWorld.zip");
fs.copyFileSync(archive, neutralCopy);

let extracted = false;
try {
  execFileSync("powershell", [
    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-Command", `Expand-Archive -LiteralPath '${neutralCopy}' -DestinationPath '${path.join(staging, "out")}' -Force`
  ], { stdio: ["ignore", "pipe", "pipe"] });
  extracted = true;
} catch (error) {
  failures.push("Архив не распаковался штатным архиватором: " + String(error.stderr || error.message));
}

const outRoot = path.join(staging, "out");

if (extracted) {
  const read = rel => {
    const target = path.join(outRoot, ...rel.split("/"));
    return fs.existsSync(target) ? fs.readFileSync(target, "utf8") : null;
  };
  const exists = rel => fs.existsSync(path.join(outRoot, ...rel.split("/")));

  // --- Манифест ---
  const manifestText = read("archive.json");
  check(manifestText !== null, "В архиве нет archive.json: диалог импорта не сможет показать содержимое.");

  let manifest = null;
  if (manifestText !== null) {
    try {
      const doc = JSON.parse(manifestText);
      check(doc.schemaVersion === 1, "Манифест архива должен быть версии 1: " + doc.schemaVersion);
      check(doc.format === "aqezip", "Формат манифеста должен быть aqezip: " + doc.format);
      manifest = doc.definition;
    } catch (error) {
      failures.push("Манифест архива не разбирается: " + error.message);
    }
  }

  if (manifest) {
    check(manifest.kind === "world", "Демо-архив должен содержать мир, а не " + manifest.kind);
    check(manifest.id === "demo", "Id демо-мира должен быть demo: " + manifest.id);
    check(manifest.fullName === "Демо Мир", "Полное имя демо-мира: " + manifest.fullName);
    // Описание обязано говорить, что ресурс пустой: иначе автор ищет в нём
    // содержимое, которого нет.
    check(/разработке|пустой/i.test(manifest.description || ""),
      "Описание демо-мира должно сообщать, что ресурс в разработке: " + manifest.description);
    check(manifest.includesDependencies === true,
      "Демо-архив обязан быть самодостаточным (includesDependencies=true).");
    check(Array.isArray(manifest.entries) && manifest.entries.length >= 2,
      "Состав архива должен быть перечислен: " + JSON.stringify(manifest.entries));
    // Кириллица в манифесте — литералами: архив распаковывают и читают глазами.
    check(manifestText.includes("Демо Мир"),
      "Кириллица в манифесте должна остаться литералами, а не \\uXXXX.");
  }

  // --- Дерево ресурсов ---
  //
  // Содержимое мира лежит в КОРНЕ архива, а имя папки применяется при импорте
  // (из манифеста). Это не мелочь: папка внутри архива была бы лишним уровнем,
  // который импортёру пришлось бы срезать, а имя папки в архиве могло бы не
  // совпасть с желаемым («Демо Мир (2)» при импорте рядом).
  check(exists("world.aqworld"), "В архиве нет файла мира world.aqworld.");
  check(exists("campaigns/common/campaign.aqcampaign"),
    "В архиве нет общей кампании common.");
  check(exists("campaigns/common/quests/common_intro.aqquest"),
    "В архиве нет демонстрационного квеста common_intro.");
  // Пустые каталоги обязаны сохраниться: ZIP знает только файлы, поэтому без
  // явных записей каталогов автор не увидел бы, куда класть сцены и сохранения,
  // — а именно ради этого структура и запаковывается.
  check(exists("campaigns/common/scenes"),
    "В архиве нет каталога scenes: автор не увидит, куда класть сцены.");
  check(exists("Saves"),
    "В архиве нет каталога Saves внутри мира.");

  const worldText = read("world.aqworld");
  if (worldText) {
    try {
      const doc = JSON.parse(worldText);
      check(doc.format === "aqworld", "Файл мира должен иметь формат aqworld: " + doc.format);
      check(doc.definition?.id === "demo", "Id мира в файле: " + doc.definition?.id);
      check(doc.definition?.lastCampaignId === "common",
        "Мир должен помнить общую кампанию как последнюю: " + doc.definition?.lastCampaignId);
      // Файлы архива должны быть каноническими: LF и завершающий перевод строки.
      // Архив собирается тем же писателем, что и обычные миры, поэтому
      // расхождение здесь означало бы, что путь записи разошёлся.
      check(!worldText.includes("\r"), "Файл мира в архиве не должен содержать CR.");
      check(worldText.endsWith("}\n"), "Файл мира в архиве должен завершаться переводом строки.");
      check(worldText.includes("Демо Мир"), "Кириллица в файле мира должна остаться литералами.");
    } catch (error) {
      failures.push("Файл мира в архиве не разбирается: " + error.message);
    }
  }

  const campaignText = read("campaigns/common/campaign.aqcampaign");
  if (campaignText) {
    try {
      const doc = JSON.parse(campaignText);
      check(doc.definition?.worldId === "demo",
        "Кампания в архиве должна ссылаться на свой мир: " + doc.definition?.worldId);
    } catch (error) {
      failures.push("Кампания в архиве не разбирается: " + error.message);
    }
  }
}

try { fs.rmSync(staging, { recursive: true, force: true }); } catch { /* временная папка */ }

// Синхронизация ресурсов обязана уметь копировать бинарник: до появления архива
// все ресурсы были текстовыми, и «копировать как текст» испортило бы zip.
const sync = fs.readFileSync("ci/sync_data_resources.ps1", "utf8");
check(/Copy-Item/.test(sync),
  "Синхронизация ресурсов должна копировать файлы как есть, а не перекодировать текст.");
check(!/Set-Content[^;]*DemoWorld/i.test(sync),
  "Синхронизация не должна перезаписывать архив текстом.");

if (failures.length) {
  console.log("Демо-мир: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log(`Демо-мир: OK архив ${archiveBytes} Б, распаковывается, структура и манифест на месте.`);
