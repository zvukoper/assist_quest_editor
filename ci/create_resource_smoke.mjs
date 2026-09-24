// Создание мира и кампании.
//
// Почему настоящий запуск: мир без общей кампании — это мир, в котором нечего
// создавать (квест не существует вне кампании), а кампания без папок quests и
// scenes — ресурс, в который нельзя положить квест. Обе вещи видны только на
// диске, поэтому проверяется фактическое дерево.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const worldStore = read("src/AssistQuestEditor.App/WorldStore.cs");
const campaignStore = read("src/AssistQuestEditor.App/CampaignStore.cs");
const createForm = read("src/AssistQuestEditor.App/Host/ResourceCreateForm.cs");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");

// --- 1. Пункты меню реализованы, а не заглушки ---
check(/case "create_world":/.test(mainForm) && /case "create_campaign":/.test(mainForm),
  "Host не обрабатывает пункты «Создать».");
for (const action of ["create_world", "create_campaign"]) {
  check(!new RegExp(`case "${action}":[\\s\\S]{0,120}?NotifyNotImplemented`).test(mainForm),
    `Пункт «${action}» всё ещё заглушка.`);
}
check(/private void CreateWorld\(\)/.test(mainForm) &&
      /private void CreateCampaign\(\)/.test(mainForm),
  "Создание мира/кампании не доведено до стора.");

// --- 2. Окно создания: проверка ДО записи ---
check(/class ResourceCreateForm/.test(createForm), "Нет окна создания ресурса.");
// Проверка в FormClosing: кнопка с DialogResult закрывает окно сама, и
// сообщить о недопустимом имени из Click было бы негде. Проверяется не только
// наличие обработчика, но и само условие-охранник: без него обработчик
// возвращается сразу и не проверяет НИЧЕГО (такая подмена должна валить проверку).
check(/FormClosing \+= \(_, e\) =>/.test(createForm) && /e\.Cancel = true/.test(createForm),
  "Окно создания не проверяет имя до записи: недопустимое имя уйдёт в стор.");
check(/if \(DialogResult != DialogResult\.OK\)[\s\S]{0,40}?return;/.test(createForm),
  "У проверки имени нет охранника на DialogResult: обработчик возвращается сразу.");
check((createForm.match(/e\.Cancel = true/g) || []).length >= 4,
  "Проверка имени покрывает не все случаи (пустое, недопустимое, полное, занятое).");
check(/ResourceNaming\.IsValidName\(NameValue\)/.test(createForm),
  "Окно создания не проверяет короткое имя правилом именования.");
// Занятое имя проверяется до записи: иначе создание падало бы исключением стора
// и выглядело бы как поломка окна.
check(/Directory\.Exists\(Path\.Combine\(_parentFolder/.test(createForm),
  "Окно создания не замечает занятое имя: автор узнает об этом из исключения.");
// Автор должен видеть будущий путь папки ДО нажатия.
check(/_name\.TextChanged \+= \(_, _\) => UpdateFolderHint\(\)/.test(createForm),
  "Окно создания не показывает будущее имя папки при вводе.");
check(/Недопустимое короткое имя/.test(createForm) &&
      /Короткое имя не может быть пустым/.test(createForm),
  "Ошибки имени не объясняются словами.");

// --- 3. Структура, которую создаёт стор ---
// Квест не существует вне кампании, поэтому мир без кампании — мир, в котором
// нечего создавать. Правило уже действовало при создании мира; проверяем, что
// создание кампании его не обошло.
check(/SeedCommonCampaign/.test(worldStore),
  "Мир создаётся без общей кампании: в нём нельзя создать квест.");
check(/CreateCampaign\(/.test(campaignStore) && /public CampaignRecord CreateCampaign/.test(campaignStore),
  "Стор кампаний не умеет создавать кампанию.");
const createSection = campaignStore.slice(
  campaignStore.indexOf("public CampaignRecord CreateCampaign("),
  campaignStore.indexOf("public void Reload()"));
check(createSection.length > 0, "Метод создания кампании не найден.");
// Структура папок создаётся сразу: автор должен видеть, куда класть квесты.
check(/WorldPaths\.QuestsFolderPath\(folder\)/.test(createSection) &&
      /WorldPaths\.ScenesFolderPath\(folder\)/.test(createSection),
  "Кампания создаётся без папок quests/scenes: квест некуда положить.");
check(/WorldContentSeeder\.WriteCampaign/.test(createSection),
  "Кампания создаётся без файла: каталог её не увидит.");
// Новая кампания НЕ активна: активная задаёт мир симуляции, и её создание
// сменило бы мир как побочный эффект.
check(/Active: false/.test(createSection),
  "Создание кампании делает её активной: это сменило бы мир симуляции.");
// Подпись проставляется: у каждого ресурса обязаны быть автор и дата.
check(/WithCreated\(author, moment\)/.test(createSection),
  "Созданная кампания не подписана автором и датой.");
check(/ResourceNaming\.ToFolderName\(name\)/.test(createSection),
  "Имя папки кампании не нормализуется правилом именования.");
// Демо-квест новой кампании НЕ создаётся: это осознанное создание, а не мир.
check(!/SeedCommonCampaign|\.aqquest/.test(createSection),
  "Создание кампании подкладывает демонстрационный квест: это не заявлено.");

// --- 4. Синхронизация селектора ---
// «Создал мир, а его нигде нет» — самый заметный дефект этой ветки.
// Срез ограничивается следующим методом: иначе он доходит до CreateCampaign,
// и подмена в CreateWorld остаётся незамеченной (проверено негативным контролем).
const createWorldBody = mainForm.slice(
  mainForm.indexOf("private void CreateWorld()"),
  mainForm.indexOf("private void CreateCampaign()"));
const createCampaignBody = mainForm.slice(
  mainForm.indexOf("private void CreateCampaign()"),
  mainForm.indexOf("/// <summary>", mainForm.indexOf("private void CreateCampaign()")));

check(createWorldBody.includes("PostWorldSelection()"),
  "После создания мира селектор не обновляется: созданного мира не видно в списке.");
check(createCampaignBody.includes("PostWorldSelection()"),
  "После создания кампании селектор не обновляется.");
check(createCampaignBody.includes("ReloadCatalog"),
  "После создания кампании симулятор не перечитывает каталог: кампании не видно.");
// Живая смена мира поддерживается общим селектором без перезапуска.
check(
  /выбран и запомнен/i.test(mainForm) &&
  /без перезапуска приложения/i.test(mainForm),
  "Сообщение о создании мира не соответствует реализованной живой смене мира."
);

// --- 5. Настоящий запуск ---
const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки создания.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-create-"));
  let probeCounter = 0;

  const runCreate = (kind, name, worldId = "", fullName = "", description = "") => {
    probeCounter += 1;
    const report = path.join(workspace, `create-${probeCounter}.txt`);
    execFileSync(exe, ["--create-probe", workspace, kind, name, worldId, fullName, description,
                       "--report", report],
      { stdio: "pipe", timeout: 120000 });
    return fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  };

  // --- Мир ---
  const worldReport = runCreate("world", "Новый Мир", "", "Новый Мир Полное", "Описание мира");
  check(/Ресурс создан/.test(worldReport), "Мир не создан: " + worldReport);

  const worldFolder = /Папка: (.+)/.exec(worldReport)?.[1]?.trim() ?? "";
  // Id берётся из отчёта, а не из имени: он строится из имени папки по правилу
  // проекта, и жёстко зашитая форма разошлась бы с реализацией.
  const worldId = /Id: (.+)/.exec(worldReport)?.[1]?.trim() ?? "";
  check(worldId.length > 0, "Отчёт создания мира не назвал id: " + worldReport);
  check(worldFolder.startsWith(path.join(workspace, "worlds")),
    "Мир создан вне папки worlds: " + worldFolder);
  check(fs.existsSync(path.join(worldFolder, "world.aqworld")),
    "В созданном мире нет файла мира: " + listTree(worldFolder).join(", "));

  // Ключевое: общая кампания с демо-квестом обязана появиться сама.
  const commonFolder = path.join(worldFolder, "campaigns", "common");
  check(fs.existsSync(path.join(commonFolder, "campaign.aqcampaign")),
    "Мир создан без общей кампании: в нём нельзя создать квест. " +
    listTree(worldFolder).join(", "));
  check(fs.existsSync(path.join(commonFolder, "quests")),
    "В общей кампании нет папки quests.");
  check(fs.readdirSync(path.join(commonFolder, "quests")).some(f => f.endsWith(".aqquest")),
    "В общей кампании нет демонстрационного квеста.");
  check(fs.existsSync(path.join(worldFolder, "Saves")),
    "У созданного мира нет папки Saves: сохранения некуда писать.");

  // Мир обязан читаться: «создан, но не читается» — худший вид.
  const reloadReport = runCreate("campaign", "Проверка", worldId, "", "");
  check(/Ресурс создан/.test(reloadReport),
    "Созданный мир не читается: кампанию в него положить не удалось: " + reloadReport);

  // --- Кампания ---
  const campaignFolder = /Папка: (.+)/.exec(reloadReport)?.[1]?.trim() ?? "";
  check(fs.existsSync(path.join(campaignFolder, "campaign.aqcampaign")),
    "Кампания создана без файла: каталог её не увидит.");
  check(fs.existsSync(path.join(campaignFolder, "quests")) &&
        fs.existsSync(path.join(campaignFolder, "scenes")),
    "Кампания создана без папок quests/scenes: квест некуда положить. " +
    listTree(campaignFolder).join(", "));
  check(!fs.readdirSync(path.join(campaignFolder, "quests")).some(f => f.endsWith(".aqquest")),
    "Создание кампании подложило демонстрационный квест.");

  const definition = JSON.parse(fs.readFileSync(
    path.join(campaignFolder, "campaign.aqcampaign"), "utf8")).definition;

  check(definition.active === false,
    "Новая кампания активна: симуляция переключилась бы на неё как побочный эффект.");
  check(definition.worldId === worldId,
    "Кампания создана без указания родительского мира: " + definition.worldId +
    " вместо " + worldId);
  check(definition.quests?.length === 0,
    "Новая кампания содержит квесты: " + JSON.stringify(definition.quests));
  check(typeof definition.metadata?.createdBy === "string" &&
        definition.metadata.createdBy.length > 0,
    "Созданная кампания не подписана автором: " + JSON.stringify(definition.metadata));
  check(/^\d{4}-\d{2}-\d{2}T/.test(definition.metadata?.createdOn ?? ""),
    "Созданная кампания не имеет даты создания: " + definition.metadata?.createdOn);

  // --- Занятое имя и недопустимое имя отвергаются ---
  let duplicateRejected = false;
  try {
    runCreate("campaign", "Проверка", worldId);
  } catch {
    duplicateRejected = true;
  }
  check(duplicateRejected, "Создание приняло занятое имя кампании.");
  check(fs.readdirSync(path.join(worldFolder, "campaigns")).filter(f => f === "Проверка").length === 1,
    "Дубликат кампании всё-таки появился на диске.");

  let badNameRejected = false;
  try {
    runCreate("world", "../побег");
  } catch {
    badNameRejected = true;
  }
  check(badNameRejected, "Создание приняло недопустимое имя мира.");
  check(!fs.existsSync(path.join(path.dirname(workspace), "побег")),
    "Имя с «..» создало папку вне корня миров.");
}

if (failures.length) {
  console.log("Создание мира и кампании: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Создание мира и кампании: OK мир с общей кампанией и демо-квестом, кампания с папками, подписи и отказ на плохое имя.");

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
