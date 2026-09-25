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
    check(!/common_intro/i.test(manifestText),
      "Демо-мир не должен содержать старый Common demo quest.");
    check(/Dispatcher|Диспетчер/i.test(manifest.description || ""),
      "Описание демо-мира должно упоминать Dynamic Event Dispatcher.");
    check(manifest.includesDependencies === true,
      "Демо-архив обязан быть самодостаточным (includesDependencies=true).");
    check(Array.isArray(manifest.entries) && manifest.entries.some(entry => entry.path === "world.aqworld"),
      "Состав архива должен содержать world.aqworld: " + JSON.stringify(manifest.entries));
    // Кириллица в манифесте — литералами: архив распаковывают и читают глазами.
    check(manifestText.includes("Демо Мир"),
      "Кириллица в манифесте должна остаться литералами, а не \\uXXXX.");
  }

  // --- Дерево ресурсов ---
  check(exists("world.aqworld"), "В архиве нет файла мира world.aqworld.");
  check(exists("campaigns/training/campaign.aqcampaign"),
    "В архиве нет кампании «Обучение».");
  check(exists("campaigns/training/quests/test_dynamic_cache.aqquest"),
    "В архиве нет квеста «Тестовый динамический тайник».");
  check(exists("locations/training_dynamic_cache_location.aqlocation"),
    "В архиве нет Dynamic Location для тайника.");
  check(exists("events/test_dynamic_cache.aqevent"),
    "В архиве нет Dynamic Event Dispatcher ресурса.");
  check(exists("Saves"),
    "В архиве нет каталога Saves внутри мира.");


  const worldText = read("world.aqworld");
  if (worldText) {
    try {
      const doc = JSON.parse(worldText);
      check(doc.format === "aqworld", "Файл мира должен иметь формат aqworld: " + doc.format);
      check(doc.definition?.id === "demo", "Id мира в файле: " + doc.definition?.id);
      check(doc.definition?.lastCampaignId === "training",
        "Демо-мир должен выбирать учебную кампанию «Обучение»: " + doc.definition?.lastCampaignId);
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

  const campaignText = read("campaigns/training/campaign.aqcampaign");
  if (campaignText) {
    try {
      const doc = JSON.parse(campaignText);
      check(doc.definition?.id === "training",
        "Id учебной кампании должен быть training: " + doc.definition?.id);
      check(doc.definition?.fullName === "Обучение",
        "Полное имя кампании должно быть «Обучение»: " + doc.definition?.fullName);
      check(doc.definition?.active === true,
        "Учебная кампания должна быть активной.");
      check(doc.definition?.quests?.some(q => q.questId === "test_dynamic_cache"),
        "Кампания не содержит test_dynamic_cache.");
    } catch (error) {
      failures.push("Файл кампании «Обучение» не разбирается: " + error.message);
    }
  }

  const locationText = read("locations/training_dynamic_cache_location.aqlocation");
  if (locationText) {
    try {
      const doc = JSON.parse(locationText);
      check(doc.definition?.mode === "Dynamic",
        "Location тайника должна быть Dynamic.");
      const types = (doc.definition?.query?.criteria || []).map(c => String(c.type || "").toLowerCase());
      check(types.includes("categoryisany"),
        "Location тайника не содержит categoryisany.");
      check(types.includes("nearbyroad"),
        "Location тайника не содержит nearbyroad.");
      check(types.includes("distancefromnearestcity"),
        "Location тайника не содержит distancefromnearestcity.");
      check(types.includes("distancefromplayer"),
        "Location тайника не содержит distancefromplayer.");
      check(doc.definition?.query?.history?.maxSelectionCount === 0,
        "Location тайника должна исключать повторный выбор уже использованной точки.");
    } catch (error) {
      failures.push("Location тайника не разбирается: " + error.message);
    }
  }

  const eventText = read("events/test_dynamic_cache.aqevent");
  if (eventText) {
    try {
      const doc = JSON.parse(eventText);
      const def = doc.definition;
      check(def?.name === "Тестовый динамический тайник",
        "Dynamic Event имеет неверное имя: " + def?.name);
      check(def?.questId === "test_dynamic_cache",
        "Dynamic Event не связан с test_dynamic_cache.");
      check(def?.trigger?.type === "DynamicEventDiscovery",
        "Тайник должен ждать DynamicEventDiscovery.");
      check(def?.trigger?.sourceDefinitionId === "test_dynamic_cache",
        "Тайник должен ждать обнаружения самого себя.");
      check(def?.trigger?.minGameHours === 5 / 60 &&
            def?.trigger?.maxGameHours === 5 / 60,
        "Следующий тайник должен появляться через 5 игровых минут.");
      check(def?.spawnPolicy?.lifetimeGameHours === 10 / 60,
        "Срок жизни тайника должен быть 10 игровых минут.");
      check(def?.spawnPolicy?.spawnOnSimulationStart === true,
        "Первый тайник должен создаваться при запуске симуляции.");
      check(def?.spawnPolicy?.respawnOnExpired === true,
        "Истёкший тайник должен пересоздаваться.");
    } catch (error) {
      failures.push("Dynamic Event тайника не разбирается: " + error.message);
    }
  }

  const questText = read("campaigns/training/quests/test_dynamic_cache.aqquest");
  if (questText) {
    try {
      const doc = JSON.parse(questText);
      const def = doc.definition;
      const nodeTypes = (def?.graph?.nodes || []).map(n => String(n.nodeType || "").toLowerCase());
      check(def?.title === "Тестовый динамический тайник",
        "Неверное название квеста: " + def?.title);
      check(nodeTypes.includes("giveitem") &&
            nodeTypes.includes("addmoney") &&
            nodeTypes.includes("addskill"),
        "Квест тайника не содержит выдачу предмета, денег и очков навыка.");
      check((def?.graph?.nodes || []).some(n =>
        n.nodeType === "GiveItem" && n.parameters?.itemId === "note" && n.parameters?.count === "1"),
        "Квест должен выдавать предмет «Записка».");
      check((def?.graph?.nodes || []).some(n =>
        n.nodeType === "AddMoney" && n.parameters?.amount === "5000"),
        "Квест должен выдавать 5000 рублей.");
      check((def?.graph?.nodes || []).some(n =>
        n.nodeType === "AddSkill" && n.parameters?.skillId === "scout" && n.parameters?.amount === "50"),
        "Квест должен добавлять +50 к навыку «Разведчик».");
    } catch (error) {
      failures.push("Квест тайника не разбирается: " + error.message);
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
