// Свойства мира и кампании: «Ред.» и «ℹ️».
//
// Почему настоящий запуск: окно свойств пишет в тот же файл ресурса, где лежат
// квесты, активность и стартовые условия мира. Ошибка здесь стоит дороже всего —
// правка описания могла бы молча обнулить игровые настройки кампании. Поэтому
// проверяется не «есть ли поле», а ФАКТИЧЕСКОЕ содержимое файла до и после.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const service = read("src/AssistQuestEditor.App/ResourcePropertiesService.cs");
const form = read("src/AssistQuestEditor.App/Host/ResourcePropertiesForm.cs");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
const mainJs = read("src/AssistQuestEditor.App/Web/main.js");
const worldStore = read("src/AssistQuestEditor.App/WorldStore.cs");
const campaignStore = read("src/AssistQuestEditor.App/CampaignStore.cs");

// --- 1. Оба пункта меню обрабатываются ---
check(/world_edit/.test(mainJs) && /world_info/.test(mainJs) &&
      /campaign_edit/.test(mainJs) && /campaign_info/.test(mainJs),
  "В меню нет пунктов «Ред.»/«Сведения»: главный запрос пользователя не выполнен.");
// «Ред.» и «ℹ️» отличаются РОВНО наличием класса редактирования, поэтому
// campaign: true обязателен — иначе Host не узнает, какую кампанию открывать.
check(/id: "campaign_edit"[^}]*campaign: true/.test(mainJs),
  "Пункт «Редактировать кампанию» не помечен как действие над кампанией.");
check(/id: "campaign_info"[^}]*campaign: true/.test(mainJs),
  "Пункт «Сведения о кампании» не помечен как действие над кампанией.");

for (const action of ["world_edit", "world_info", "campaign_edit", "campaign_info"]) {
  check(mainForm.includes(`case "${action}":`),
    `Host не обрабатывает «${action}».`);
}
check(/ShowResourceProperties\(kind: "world", edit: true\)/.test(mainForm),
  "Пункт «Редактировать мир» не открывает окно свойств в режиме правки.");
check(/ShowResourceProperties\(kind: "world", edit: false\)/.test(mainForm),
  "Пункт «Сведения о мире» не открывает окно свойств в режиме просмотра.");
check(/ShowResourceProperties\(kind: "campaign", edit: true/.test(mainForm) &&
      /ShowResourceProperties\(kind: "campaign", edit: false/.test(mainForm),
  "Кампания не различает «Ред.» и «Сведения».");

// --- 2. Одно окно на два режима: иначе содержимое показали бы по-разному ---
check(/public ResourcePropertiesForm\(ResourceProperties properties, bool editMode\)/.test(form),
  "Окно свойств потеряло параметр режима: «Ред.» и «Сведения» разойдутся.");
// Инвариант: в режиме «Сведения» НИ ОДИН контрол не активен. Проверять счётчик
// вхождений бесполезно — их шесть, и подмена одного оставляет пять (проверено:
// порог «>= 4» пропустил такую подмену). Смотреть надо на признак безусловного
// включения: его в файле быть не должно вообще.
check(!/Enabled\s*=\s*true/.test(form),
  "В окне свойств есть безусловно активный контрол: в режиме «Сведения» просмотр " +
  "позволял бы правку.");
// Поля ввода привязаны к режиму через ReadOnly, а НЕ Enabled = false. У
// выключенного поля WinForms рисует текст системным серым и игнорирует заданный
// цвет: на тёмном фоне контраст падал до 3,3:1 (замерено пробой читаемости).
// Правило изменилось намеренно — важно, чтобы правка была НЕВОЗМОЖНА, а способ
// её запрета теперь другой.
const readOnlyFields = (form.match(/ReadOnly = !editMode/g) || []).length;
check(readOnlyFields >= 3,
  "Поля окна свойств не привязаны к режиму через ReadOnly: часть останется " +
  "редактируемой при просмотре (найдено " + readOnlyFields + " из 3).");
// Запрет правки у ПОЛЕЙ проверяется на срезах по каждому полю, а не по всему
// файлу: `Enabled = editMode` остаётся у КНОПОК, и это верно — кнопки рисуются
// DarkFlatButton, где выключенный текст читаем. Общая проверка по файлу путала бы
// два разных случая (и уже дала ложное падение).
for (const field of ["_shortName = new TextBox", "_fullName = new TextBox", "_description = new TextBox"]) {
  const start = form.indexOf(field);
  check(start >= 0, "Не найдено поле окна свойств: " + field + ".");
  const body = start < 0 ? "" : form.slice(start, form.indexOf("};", start));

  check(/ReadOnly = !editMode/.test(body),
    `Поле ${field} не защищено от правки в режиме просмотра.`);
  // Комментарии снимаются перед поиском запрещённого приёма: пояснение к правке
  // НАЗЫВАЕТ то, чего делать нельзя («ReadOnly, а НЕ Enabled = false»), и поиск по
  // тексту с комментариями ловил бы само объяснение. Этот промах уже случался в
  // проекте дважды (см. gotchas).
  const code = body
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/\/\/[^\n]*/g, " ");

  check(!/Enabled\s*=/.test(code),
    `Поле ${field} выключается через Enabled: его текст станет системно-серым и нечитаемым.`);
}
// В режиме просмотра активной остаётся РОВНО кнопка закрытия, и это проверяется
// на срезе по кнопке: счётчик по всему файлу ничего не говорит о том, ЧТО именно
// осталось активным.
check(/Text = "Сохранить",[\s\S]{0,400}?Enabled = editMode/.test(form),
  "Кнопка сохранения должна быть активна только в режиме правки.");
// В режиме просмотра кнопка обязана называться «Закрыть», а не «Сохранить»:
// «Сохранить» обещало бы запись, которой не будет.
check(/Text = editMode \? "Отмена" : "Закрыть"/.test(form),
  "Кнопка окна свойств называется «Сохранить»/«Сохранить» в обоих режимах.");
// Диалог не должен трогать диск: иначе его нельзя проверить и он сам решает,
// куда записывать.
check(!/File\.(Write|Copy|Delete|Move)\s*\(/.test(form),
  "Окно свойств пишет на диск само: запись обязана делать стор.");

// --- 3. Правка НЕ трогает игровые поля кампании ---
// Самый дорогой дефект здесь: кампания хранит в одном файле и описание, и
// квесты, и активность, и стартовые условия мира.
check(/record\.Replace\(record\.Definition with[\s\S]{0,600}?Name = name\.Trim\(\)/.test(campaignStore),
  "Правка кампании не собирает определение через `with`: часть полей могла бы обнулиться.");
const updateStart = campaignStore.indexOf("public void UpdateCampaign(");
const updateEnd = campaignStore.indexOf("public void RegisterQuest(", updateStart);
const campaignUpdate = campaignStore.slice(
  updateStart,
  updateEnd > updateStart ? updateEnd : campaignStore.indexOf("public void SetQuestEnabled(", updateStart));
check(campaignUpdate.length > 0, "Метод правки кампании не найден.");
for (const forbidden of ["Quests = ", "Active = ", "Geo = ", "StartDate = ", "StartConditions = ",
                         "WorldId = "]) {
  check(!campaignUpdate.includes(forbidden),
    `Правка кампании меняет игровое поле «${forbidden.trim()}»: это не свойство описания.`);
}
for (const required of ["Name =", "FullName =", "Description =", "ImageFile =", "WithModified"]) {
  check(campaignUpdate.includes(required),
    `Правка кампании не записывает «${required}».`);
}
// Даты создания не должны обновляться правкой: по ним у получателя решается
// вопрос о перезаписи при импорте.
check(!campaignUpdate.includes("WithCreated"),
  "Правка кампании перезаписывает дату создания: импорт у получателя сравнивал бы не то.");

// Id не пересчитывается от нового имени: на него ссылаются кампании и сохранения.
const worldUpdate = worldStore.slice(
  worldStore.indexOf("public WorldRecord UpdateWorld("),
  worldStore.indexOf("public void RememberCampaign("));
check(worldUpdate.length > 0, "Метод правки мира не найден.");
check(!/Id\s*=\s*MakeId/.test(worldUpdate),
  "Правка мира пересчитывает id от имени: ссылки из кампаний и сохранений порвутся.");
check(!worldUpdate.includes("WithCreated"),
  "Правка мира перезаписывает дату создания.");
check(/Id = worldId|FindWorld\(worldId\)/.test(worldUpdate),
  "Правка мира не адресуется по идентификатору.");
// Файл мира ищется по имени ПАПКИ, поэтому переносить папку при правке нельзя.
check(!/Directory\.Move|Move\(.*WorldFolder/.test(worldUpdate),
  "Правка мира переносит папку: файл мира не найдётся, ссылки на сохранения порвутся.");

// --- 4. Изображение: имя каноническое, путь относительный ---
check(/public static string CopyImageIn/.test(service),
  "Нет копирования изображения в папку ресурса.");
check(/Path\.GetFileName\(fileName\)/.test(service),
  "Имя файла изображения берётся как есть: путь из чужой папки попал бы в определение.");
// Абсолютный путь в определении сломал бы переносимость архива и папки.
check(/return leaf;/.test(service),
  "Изображение записывается не именем файла: ресурс перестанет переноситься.");
// Окно показывает, что изображения нет, а не отказывается открываться.
check(/Изображение не задано/.test(service),
  "Отсутствие изображения не сообщается словами.");
// Файл не должен оставаться заблокированным: иначе повторный выбор того же
// файла падал бы с «используется другим процессом».
check(/File\.OpenRead\(path\)/.test(form) && /_preview\.Image\?\.Dispose\(\)/.test(form),
  "Изображение читается напрямую и/или не освобождается: файл останется заблокированным.");

// --- 5. Сведения показываются и в режиме правки ---
check(/BuildInfoLines/.test(service) && /BuildInfoLines\(properties\)/.test(form),
  "Окно правки не показывает сведения: автор не увидит автора и дату.");
check(/Создан|Created by: не указан/.test(service),
  "Отсутствие автора не сообщается явно.");
check(/Родитель не указан/.test(service),
  "Пустой родитель кампании показан как ошибка, а он бывает у старых файлов.");
check(/в папке мира/.test(service),
  "Чужой родитель кампании не выделяется: перенос в неправильную папку остался бы незамеченным.");

// --- 6. Настоящий запуск: правка кампании не теряет игровые поля ---
const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки правки.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-props-"));
  const worldFolder = path.join(workspace, "worlds", "TestWorld");
  const campaignFolder = path.join(worldFolder, "campaigns", "common");
  fs.mkdirSync(campaignFolder, { recursive: true });

  // Мир и кампания с ЗАПОЛНЕННЫМИ игровыми полями: именно они должны уцелеть.
  const worldDoc = {
    schemaVersion: 1,
    format: "aqworld",
    definition: {
      id: "testworld", name: "TestWorld", fullName: "Исходное имя", version: 1,
      description: "Исходное описание",
      metadata: {
        createdBy: "Автор Один", createdOn: "2026-09-01T10:00:00+00:00",
        modifiedBy: "Автор Один", modifiedOn: "2026-09-01T10:00:00+00:00"
      }
    }
  };

  const campaignDoc = {
    schemaVersion: 1,
    format: "aqcampaign",
    definition: {
      id: "common", name: "Common", version: 1, active: true,
      quests: [{ questId: "intro", relativePath: "quests/intro.aqquest", status: "Enabled", order: 1 }],
      files: ["quests/intro.aqquest"],
      geo: { latitude: 55.16, longitude: 61.4 },
      startDate: "2026-06-01T00:00:00+00:00",
      startConditions: { weather: "clear", rainPercent: 10, visibilityMeters: 8000 },
      worldId: "testworld",
      fullName: "Общая кампания",
      description: "Описание кампании",
      metadata: {
        createdBy: "Автор Один", createdOn: "2026-09-01T10:00:00+00:00",
        modifiedBy: "Автор Один", modifiedOn: "2026-09-01T10:00:00+00:00"
      }
    }
  };

  fs.writeFileSync(path.join(worldFolder, "world.aqworld"),
    JSON.stringify(worldDoc, null, 2) + "\n", "utf8");
  fs.writeFileSync(path.join(campaignFolder, "campaign.aqcampaign"),
    JSON.stringify(campaignDoc, null, 2) + "\n", "utf8");

  // Проверка правки через пробу: она вызывает ТЕ ЖЕ методы сторов, что окно.
  let probeCounter = 0;
  const runEdit = (kind, id, name, fullName = "", description = "") => {
    // Отчёт — свой на каждый прогон: иначе вторая проба прочитает файл первой.
    probeCounter += 1;
    const report = path.join(workspace, `report-${probeCounter}.txt`);

    execFileSync(exe, ["--properties-probe", workspace, kind, "testworld", id, name,
                      fullName, description, "--report", report],
      { stdio: "pipe", timeout: 120000 });

    return fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  };

  // 1. Кампания: правка описания.
  const editReport = runEdit("campaign", "common", "Новое имя кампании",
                             "Новое полное имя", "Новое описание");
  check(/Свойства обновлены/.test(editReport),
    "Проба правки кампании не подтвердила запись: " + editReport);

  const afterCampaign = JSON.parse(fs.readFileSync(
    path.join(campaignFolder, "campaign.aqcampaign"), "utf8")).definition;

  check(afterCampaign.name === "Новое имя кампании",
    "Имя кампании не сохранилось: " + afterCampaign.name);
  check(afterCampaign.fullName === "Новое полное имя",
    "Полное имя кампании не сохранилось: " + afterCampaign.fullName);
  check(afterCampaign.description === "Новое описание",
    "Описание кампании не сохранилось: " + afterCampaign.description);

  // ГЛАВНОЕ: игровые поля обязаны уцелеть до последнего.
  check(afterCampaign.quests?.length === 1 && afterCampaign.quests[0].questId === "intro",
    "Правка описания кампании ПОТЕРЯЛА квесты: " + JSON.stringify(afterCampaign.quests));
  check(afterCampaign.quests?.[0]?.status === "Enabled",
    "Правка описания кампании изменила статус квеста: " +
    JSON.stringify(afterCampaign.quests?.[0]?.status));
  check(afterCampaign.active === true,
    "Правка описания кампании сняла активность: " + afterCampaign.active);
  check(afterCampaign.geo?.latitude === 55.16 && afterCampaign.geo?.longitude === 61.4,
    "Правка описания кампании потеряла геокоординату: " + JSON.stringify(afterCampaign.geo));
  check(afterCampaign.startDate?.startsWith("2026-06-01"),
    "Правка описания кампании сбросила дату старта мира: " + afterCampaign.startDate);
  check(afterCampaign.startConditions?.weather === "clear" &&
        afterCampaign.startConditions?.visibilityMeters === 8000,
    "Правка описания кампании потеряла стартовые условия: " +
    JSON.stringify(afterCampaign.startConditions));
  check(afterCampaign.worldId === "testworld",
    "Правка описания кампании сменила родительский мир: " + afterCampaign.worldId);
  check(afterCampaign.files?.includes("quests/intro.aqquest"),
    "Правка описания кампании потеряла состав файлов: " + JSON.stringify(afterCampaign.files));

  // Даты создания сохранены, modified обновлён.
  check(afterCampaign.metadata?.createdBy === "Автор Один" &&
        afterCampaign.metadata?.createdOn?.startsWith("2026-09-01"),
    "Правка кампании переписала дату создания: " + JSON.stringify(afterCampaign.metadata));
  check(afterCampaign.metadata?.modifiedOn !== "2026-09-01T10:00:00+00:00",
    "Правка кампании не обновила дату изменения: " + afterCampaign.metadata?.modifiedOn);

  // 2. Мир: правка не должна ломать ссылку кампании на него.
  const worldReport = runEdit("world", "testworld", "TestWorld", "Новое имя мира",
                              "Описание мира");
  check(/Свойства обновлены/.test(worldReport),
    "Проба правки мира не подтвердила запись: " + worldReport);

  const afterWorld = JSON.parse(fs.readFileSync(
    path.join(worldFolder, "world.aqworld"), "utf8")).definition;

  check(afterWorld.id === "testworld",
    "Правка мира пересчитала id: ссылки из кампаний порвутся: " + afterWorld.id);
  check(afterWorld.fullName === "Новое имя мира",
    "Полное имя мира не сохранилось: " + afterWorld.fullName);
  check(afterWorld.metadata?.createdOn?.startsWith("2026-09-01"),
    "Правка мира переписала дату создания: " + JSON.stringify(afterWorld.metadata));

  // Файл мира обязан остаться на месте: иначе мир «пропадёт» из каталога.
  check(fs.existsSync(path.join(worldFolder, "world.aqworld")),
    "После правки файл мира исчез: мир не читается.");
  check(fs.existsSync(path.join(campaignFolder, "campaign.aqcampaign")),
    "После правки мира кампания исчезла: правка затронула чужую папку.");

  // 3. Небезопасное имя отвергается, файл остаётся прежним.
  const beforeBad = fs.readFileSync(path.join(campaignFolder, "campaign.aqcampaign"), "utf8");
  const badReport = path.join(workspace, "bad-report.txt");
  let rejected = false;
  try {
    execFileSync(exe, ["--properties-probe", workspace, "campaign", "testworld", "common",
                       "../побег", "", "", "--report", badReport],
      { stdio: "pipe", timeout: 120000 });
  } catch {
    rejected = true;
  }
  check(rejected, "Недопустимое имя кампании принято: файл можно испортить из окна свойств.");
  check(fs.existsSync(badReport) && /Недопустимое имя/.test(fs.readFileSync(badReport, "utf8")),
    "Отказ по имени не сообщён: непонятно, почему правка не сработала.");
  check(fs.readFileSync(path.join(campaignFolder, "campaign.aqcampaign"), "utf8") === beforeBad,
    "Отвергнутая правка всё-таки изменила файл кампании.");
}

if (failures.length) {
  console.log("Свойства мира и кампании: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Свойства мира и кампании: OK правка описания сохраняет квесты, активность, гео и стартовые условия мира.");

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
