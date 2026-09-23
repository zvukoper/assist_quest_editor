// Обновление каталога квестов Симулятора после правки файла.
//
// Симптом, который здесь сторожится: пользователь меняет точку активации в
// квесте, сохраняет файл, но квест остаётся на прежнем месте на карте. Причина
// не в отрисовке: карта строит маркеры из КЭШИРОВАННОГО каталога кампании
// (BuildSimulatorCatalog читает файлы и держит результат в памяти), поэтому без
// перечитывания файлов новый worldPointId не появляется ни в снимке, ни на карте.
//
// Проверка статическая (связка Host → Web) плюс поведенческая (кнопка шлёт
// действие) — именно так ловится «молчаливая» поломка: отсутствие хотя бы одной
// из трёх частей не даёт ни ошибки компиляции, ни падения других проверок.
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
const editorForm = read("src/AssistQuestEditor.App/Host/EditorForm.cs");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
const simulatorHtml = read("src/AssistQuestEditor.App/Web/simulator.html");

// 1. Host принимает действие перечитывания каталога и реально ходит на диск.
check(/case\s+"reload_catalog"/.test(simulatorForm),
  "SimulatorForm должен обрабатывать действие reload_catalog.");
check(/public\s+void\s+ReloadCatalog\(/.test(simulatorForm),
  "SimulatorForm должен иметь публичный ReloadCatalog для автоматического вызова.");
check(/_campaignStore\.Reload\(\)/.test(simulatorForm),
  "ReloadCatalog обязан вызвать _campaignStore.Reload(): без него файлы не перечитываются.");

// 2. Маркер квеста строится из активации документа, а не из параметра ноды.
// Параметр worldPointId у ноды Interaction — цель Runtime; если однажды сюда
// подставят его, квест снова начнёт «не сдвигаться» после правки активации.
//
// В присваивании допускается предварительное разрешение Dynamic Location
// (resolvedPoint), поэтому `quest.Activation` стоит не сразу после `=`.
// Инвариант тот же и проверяется как «в правой части есть quest.Activation»:
// подстановка параметра ноды по-прежнему валит проверку.
check(/worldPointId\s*=[^\n]*quest\.Activation\?\.WorldPointId/.test(simulatorForm),
  "BuildQuestCatalog должен брать точку из quest.Activation?.WorldPointId.");

// Точка маркера обязана разрешаться из активации: Dynamic Location ищется по
// activation.LocationId, а worldPointId — только резервный путь. Если сюда
// попадёт параметр ноды, проверка снова упадёт.
check(/ResolveActivationPoint\(\s*quest\.Activation\s*,/.test(simulatorForm),
  "BuildQuestCatalog обязан искать точку через ResolveActivationPoint(quest.Activation, ...).");

// 3. Редактор сообщает о записи файла, Host на это подписывается.
check(/public\s+event\s+EventHandler<string>\?\s+DocumentSaved/.test(editorForm),
  "EditorForm должен поднимать событие DocumentSaved после записи документа.");
check(/PublishDocumentSaved\(path!\)/.test(editorForm),
  "Путь сохранения Quest обязан вызывать PublishDocumentSaved.");
check(/form\.DocumentSaved\s*\+=\s*Editor_DocumentSaved/.test(mainForm),
  "MainForm должен подписываться на DocumentSaved.");
check(/form\.DocumentSaved\s*-=\s*Editor_DocumentSaved/.test(mainForm),
  "MainForm должен отписываться от DocumentSaved при закрытии редактора.");
check(/ReloadCatalog\(/.test(mainForm),
  "MainForm должен вызывать ReloadCatalog при сохранении квеста.");

// 4. Кнопка есть в разметке и отправляет действие.
check(/id="reloadCatalog"/.test(simulatorHtml),
  "simulator.html должен содержать кнопку reloadCatalog.");
check(/getElementById\("reloadCatalog"\)/.test(simulatorJs) &&
  /action:\s*"reload_catalog"/.test(simulatorJs),
  "simulator.js должен отправлять action reload_catalog по кнопке.");

// Поведенческая часть: клик по кнопке в реальном simulator.js должен дать
// сообщение Host'у. Статическая проверка выше не поймает, например, ранний
// return или удаление обработчика.
const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>html,body{height:100%;margin:0;overflow:hidden}</style></head>
      <body>
        <header><div id="hud"></div>
          <button id="openCampaigns">Кампании</button>
          <button id="reloadCatalog">Обновить квесты</button>
          <button id="simulationToggle">Пуск</button>
          <button id="reset">Сброс</button>
        </header>
        <main style="display:flex;height:80vh">
          <aside id="runtimeSide"></aside>
          <section id="mapWrap" style="width:700px;height:600px">
            <canvas id="mapCanvas" style="width:700px;height:600px"></canvas>
            <div id="mapStatusBar"><span id="mapStatusHint"></span></div>
            <button id="backpackButton"></button>
            <div id="inventoryNotifications"></div>
            <div id="playerOverlay" aria-hidden="true">
              <section id="inventoryPanel"></section>
              <section id="characterPanel">
                <div id="characterTabs"></div>
                <div id="characterTabBody"></div>
              </section>
            </div>
          </section>
          <aside id="side"></aside>
        </main>
        <script>
          window.__sent = [];
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

  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
          world: { coordinateSystem: "ETS2 X/Y/Z", points: [], categories: [] },
          selection: { point: null },
          facts: { values: {} },
          flags: { values: {} },
          variables: { values: {} },
          questStatuses: { quests: [] },
          states: { states: [] },
          inventory: { items: [] },
          reputation: { entries: {} },
          telemetry: {}, environment: {}, vitals: {}, progress: {}, character: {}
        },
        questCatalog: [],
        runtime: { questId: "sibirmap_city_cache", currentNodeId: null, status: "Stopped", waitingFor: "", message: "" },
        simulationRunning: false,
        enabledQuestIds: [],
        selectedQuest: { campaignId: "", questId: "" },
        itemCatalog: { items: [] },
        npcCatalog: { npcs: [] },
        reputationViews: {},
        journalDetached: false
      })
    });
  });

  await page.click("#reloadCatalog");
  const sent = await page.evaluate(() => window.__sent);
  check(
    sent.some(message => message && message.action === "reload_catalog"),
    "Клик по «Обновить квесты» не отправил reload_catalog: " + JSON.stringify(sent)
  );

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Обновление каталога квестов: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Обновление каталога квестов: OK");
