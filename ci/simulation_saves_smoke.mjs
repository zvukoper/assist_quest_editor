// Сохранения симуляции, игровое время и индикатор светового дня.
//
// Проверяется связка Host → Web целиком, потому что каждая часть по отдельности
// молча ломается:
//   - Host не обработал действие → кнопка «мертвая», ошибок нет;
//   - состояние пишется на диск при ВЫКЛЮЧЕННОЙ симуляции → пробы пользователя
//     попадают в прохождение (нарушение режима, никак не заметное снаружи);
//   - загрузка не выключает симуляцию → немедленные срабатывания нод;
//   - индикатор светового дня не получает данных → пустой кружок.
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

function read(relative) {
  return fs.readFileSync(path.join(root, ...relative.split("/")), "utf8");
}

const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
const simulatorHtml = read("src/AssistQuestEditor.App/Web/simulator.html");
const themeCss = read("src/AssistQuestEditor.App/Web/theme.css");
const campaignModels = read("src/AssistQuestEditor.Domain/CampaignModels.cs");
const store = read("src/AssistQuestEditor.App/SimulationSaveStore.cs");

// 1. Действия панели сохранений обрабатываются Host'ом.
for (const action of ["list_saves", "create_save", "load_save", "overwrite_save", "delete_save", "rename_save"]) {
  check(
    simulatorForm.includes('"' + action + '"'),
    "SimulatorForm должен обрабатывать действие " + action + "."
  );
}

// 2. Загрузка сохранения обязана выключить симуляцию.
const loadSaveBody = simulatorForm.slice(
  simulatorForm.indexOf("private void LoadSave("),
  simulatorForm.indexOf("private void OverwriteSave(")
);
check(
  /SetSimulationRunning\(false\)/.test(loadSaveBody),
  "Загрузка сохранения обязана выключать симуляцию: иначе изменения немедленно " +
  "вызовут срабатывание нод (активация квестов, события, эффекты)."
);
check(
  /SimulationSaveMapper\.Apply/.test(loadSaveBody),
  "Загрузка сохранения обязана применять снимок к каналам симулятора."
);

// 3. Состояние прохождения пишется на диск только при включенной симуляции.
// Границы среза берутся по следующим объявлениям, а не в обратном порядке:
// PostSaveList объявлен ВЫШЕ PersistSession, и перепутанные границы давали
// пустую строку, из-за чего проверка падала на исправном коде.
const persistBody = simulatorForm.slice(
  simulatorForm.indexOf("private void PersistSession("),
  simulatorForm.indexOf("private string CurrentCampaignId(")
);
check(
  /if \(!_runtime\.SimulationRunning && !force\)/.test(persistBody),
  "PersistSession должен возвращаться без записи при выключенной симуляции."
);
check(
  /runtime\.SimulationRunning/.test(simulatorForm.slice(
    simulatorForm.indexOf("private void CreateSave("),
    simulatorForm.indexOf("private void LoadSave("))),
  "Создание сохранения должно требовать включённой симуляции."
);

// 4. Запуск симуляции продолжает прохождение, а сброс его очищает, НЕ выключая.
check(
  /LoadSession\(\)/.test(simulatorForm),
  "Запуск симуляции должен читать автосохранение прохождения."
);
check(
  /ClearSession\(\)/.test(simulatorForm),
  "«Сбросить» должен очищать сохранённое прохождение."
);
const resetBody = simulatorForm.slice(
  simulatorForm.indexOf('case "reset"'),
  simulatorForm.indexOf('case "reload_catalog"')
);
check(
  /ClearSession\(\)/.test(resetBody),
  "«Сбросить» должен вызывать ClearSession."
);
check(
  !/SetSimulationRunning\(false\)/.test(resetBody),
  "«Сбросить» НЕ должен выключать симуляцию: сброс и остановка — разные действия."
);

// 5. Снимок содержит данные светового дня, а календарь/астрономия считает домен.
check(
  /daylight = BuildDaylight\(snapshot\)/.test(simulatorForm),
  "Снимок должен содержать блок daylight."
);
check(
  /SolarAstronomy\.Describe/.test(simulatorForm),
  "Световой день должен считаться доменом (SolarAstronomy), а не в web-слое."
);
check(
  /GeoCoordinate\? Geo = null/.test(campaignModels),
  "CampaignDefinition должен иметь геокоординату для астрономии."
);
check(
  /"session"/.test(store) || /session.*Extension/.test(store),
  "Автосохранение прохождения должно иметь фиксированный путь."
);

// 6. Разметка и стили панели.
for (const id of ["savesPanel", "savesList", "createSave", "closeSaves", "daylightIndicator", "openSaves"]) {
  check(simulatorHtml.includes('id="' + id + '"'), "simulator.html должен содержать #" + id + ".");
}
check(/\.savesPanel\{/.test(themeCss), "theme.css должен стилизовать панель сохранений.");
check(/\.savesPanel\[hidden\]\{display:none\}/.test(themeCss),
  "Панель сохранений должна скрываться атрибутом hidden.");
check(/\.daylightSlot\{/.test(themeCss), "theme.css должен стилизовать слот индикатора.");

// 7. Поведенческая часть в реальном simulator.js.
const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 820 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>html,body{height:100%;margin:0;overflow:hidden}</style>
      <link rel="stylesheet" href="file:///${root.replace(/\\/g, "/")}/src/AssistQuestEditor.App/Web/theme.css"></head>
      <body>
        <header>
          <div id="hud"></div>
          <div id="daylightIndicator"></div>
          <button id="openCampaigns"></button>
          <button id="reloadCatalog"></button>
          <button id="openSaves"></button>
          <button id="simulationToggle"></button>
          <button id="reset"></button>
        </header>
        <main style="display:flex">
          <aside id="runtimeSide"></aside>
          <section id="mapWrap" style="width:900px;height:640px">
            <canvas id="mapCanvas" style="width:900px;height:640px"></canvas>
            <div id="mapStatusBar"><input type="checkbox" id="onlyQuestsToggle"><input type="checkbox" id="citiesToggle"><span id="mapStatusHint"></span></div>
            <button id="backpackButton"></button>
            <div id="inventoryNotifications"></div>
            <div id="playerOverlay" aria-hidden="true">
              <section id="inventoryPanel"></section>
              <section id="characterPanel"><div id="characterTabs"></div><div id="characterTabBody"></div></section>
            </div>
          </section>
          <aside id="side"></aside>
        </main>
        <div id="savesPanel" hidden>
          <div>
            <span id="savesRoot"></span>
            <button id="closeSaves"></button>
            <button id="createSave"></button>
            <span id="savesNotice"></span>
            <div id="savesList"></div>
          </div>
        </div>
        <script>
          window.__sent = [];
          window.confirm = () => true;
          window.chrome = {
            webview: {
              listeners: new Map(),
              addEventListener(type, handler) { this.listeners.set(type, handler); },
              postMessage(raw) { try { window.__sent.push(JSON.parse(raw)); } catch (e) { window.__sent.push(raw); } }
            }
          };
        </script>
        <script>
          ${simulatorJs.replaceAll("</script", "<\\/script")}
        </script>
      </body>
    </html>
  `);

  const pushSnapshot = daylight => page.evaluate((daylight) => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
          world: { coordinateSystem: "ETS2 X/Y/Z", categories: [], points: [] },
          selection: { point: null },
          facts: { values: {} }, flags: { values: {} }, variables: { values: {} },
          questStatuses: { quests: [] }, states: { states: [] }, inventory: { items: [] },
          reputation: { entries: {} },
          telemetry: {}, environment: {}, vitals: {}, progress: {}, character: {},
          clock: { startDate: "2026-01-01T00:00:00+00:00", elapsed: "05:30:00", running: true }
        },
        questCatalog: [], runtime: { questId: "", currentNodeId: null, status: "Stopped", waitingFor: "", message: "" },
        simulationRunning: true, enabledQuestIds: [], selectedQuest: { campaignId: "", questId: "" },
        itemCatalog: { items: [] }, npcCatalog: { npcs: [] }, reputationViews: {},
        journalDetached: false, daylight
      })
    });
  }, daylight);

  const dayLight = {
    gameDateLabel: "01.01.2026", gameTimeLabel: "12:00", seasonLabel: "Зима", running: true,
    dayFraction: 0.5, isDay: true, isPolarDay: false, isPolarNight: false,
    sunriseLabel: "07:50", sunsetLabel: "15:53", dayLengthLabel: "8 ч 03 мин", sunAltitude: 11.5
  };

  await pushSnapshot(dayLight);
  await page.waitForTimeout(250);

  // Индикатор должен отрисоваться и объяснить себя: без заголовка непонятно,
  // что это за кружок.
  const indicator = await page.evaluate(() => {
    const slot = document.getElementById("daylightIndicator");
    const svg = slot?.querySelector("svg");
    const title = slot?.querySelector("title");
    const circles = slot ? slot.querySelectorAll("circle").length : 0;
    return {
      hasSvg: !!svg,
      title: title ? title.textContent : "",
      circles,
      label: svg ? svg.getAttribute("aria-label") : ""
    };
  });

  check(indicator.hasSvg, "Индикатор светового дня не отрисовался.");
  check(indicator.circles >= 3, "Индикатор должен рисовать контур, светило и тень (" +
    indicator.circles + " окружностей).");
  check(/восход/.test(indicator.title) && /закат/.test(indicator.title),
    "Подсказка индикатора должна содержать восход и закат: " + indicator.title);
  check(/07:50/.test(indicator.label), "Индикатор должен показывать время восхода из данных Host.");

  // Ночью заливка меняется: иначе индикатор не отличал бы день от ночи.
  const dayFill = await page.evaluate(() =>
    document.querySelector("#daylightIndicator circle[fill='#ffd21f']") !== null);
  check(dayFill, "Днём светило должно быть жёлтым (#ffd21f).");

  await pushSnapshot({ ...dayLight, dayFraction: 1.4, isDay: false, gameTimeLabel: "23:30" });
  await page.waitForTimeout(250);

  const nightFill = await page.evaluate(() =>
    document.querySelector("#daylightIndicator circle[fill='#ffd21f']") !== null);
  check(!nightFill, "Ночью жёлтой заливки быть не должно.");

  // HUD обязан показывать игровую дату и время.
  const hudText = await page.evaluate(() => document.getElementById("hud").textContent);
  check(/01\.01\.2026/.test(hudText), "HUD должен показывать игровую дату: " + hudText);
  check(/23:30/.test(hudText), "HUD должен показывать игровое время: " + hudText);

  // Панель сохранений: открытие запрашивает список, создание отправляет действие.
  await page.click("#openSaves");
  await page.waitForTimeout(150);

  const panelState = await page.evaluate(() => ({
    hidden: document.getElementById("savesPanel").hidden,
    sent: window.__sent.map(message => message && message.action).filter(Boolean)
  }));
  check(!panelState.hidden, "Клик по «Сохранения» должен показывать панель.");
  check(panelState.sent.includes("list_saves"),
    "Открытие панели должно запрашивать список сохранений: " + JSON.stringify(panelState.sent));

  // Список: строка с именем, размером, датами и тремя действиями.
  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "save_list",
        root: "C:\\\\saves",
        simulationRunning: true,
        items: [{
          path: "C:\\\\saves\\\\a.aqsave", name: "2026-05-15 18-30-42", sizeLabel: "2.4 КБ", sizeBytes: 2458,
          createdLabel: "15.05.2026 18:30", gameDateLabel: "03.01.2026", gameTimeLabel: "05:30",
          playedLabel: "5 ч 30 мин", campaignId: "sibir_map"
        }]
      })
    });
  });
  await page.waitForTimeout(200);

  const row = await page.evaluate(() => {
    const input = document.querySelector("#savesList [data-save-name]");
    const load = document.querySelector("#savesList [data-save-load]");
    const overwrite = document.querySelector("#savesList [data-save-overwrite]");
    const remove = document.querySelector("#savesList [data-save-delete]");
    return {
      name: input ? input.value : "",
      meta: document.querySelector("#savesList .savesRowMeta")?.textContent || "",
      hasLoad: !!load, hasOverwrite: !!overwrite, hasDelete: !!remove
    };
  });

  check(row.name === "2026-05-15 18-30-42", "Строка должна показывать имя сохранения.");
  check(/2\.4 КБ/.test(row.meta), "Строка должна показывать размер файла: " + row.meta);
  check(/15\.05\.2026 18:30/.test(row.meta), "Строка должна показывать дату создания: " + row.meta);
  check(/03\.01\.2026/.test(row.meta), "Строка должна показывать игровую дату: " + row.meta);
  check(/5 ч 30 мин/.test(row.meta), "Строка должна показывать длительность прохождения: " + row.meta);
  check(row.hasLoad && row.hasOverwrite && row.hasDelete,
    "Строка должна иметь действия «Загрузить», «Перезаписать» и «Удалить».");

  // Загрузка отправляет путь и подтверждается.
  await page.click("#savesList [data-save-load]");
  await page.waitForTimeout(150);
  const afterLoad = await page.evaluate(() => window.__sent.filter(message => message && message.action === "load_save"));
  check(afterLoad.length === 1 && /a\.aqsave/.test(afterLoad[0].path || ""),
    "Клик по «Загрузить» должен отправить load_save с путём сохранения.");

  // Переименование шлёт ровно одно действие и новое имя.
  await page.evaluate(() => {
    const input = document.querySelector("#savesList [data-save-name]");
    input.value = "Прохождение Руслана";
    input.dispatchEvent(new Event("blur"));
  });
  await page.waitForTimeout(150);
  const afterRename = await page.evaluate(() =>
    window.__sent.filter(message => message && message.action === "rename_save"));
  check(afterRename.length === 1 && afterRename[0].name === "Прохождение Руслана",
    "Переименование должно отправить rename_save с новым именем: " + JSON.stringify(afterRename));

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));

  if (!failures.length) {
    console.log("Сохранения симуляции: OK индикатор=" + indicator.circles +
      " окружностей, действий=" + ["load", "overwrite", "delete"].length);
  }
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Сохранения симуляции: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}
