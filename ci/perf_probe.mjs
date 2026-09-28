// Замер производительности карты симулятора.
//
// ЭТО НЕ PASS/FAIL ПРОВЕРКА. Скрипт печатает отчёт и НИКОГДА не валит прогон:
// пороги производительности зависят от машины, а «медленно/быстро» — вопрос
// сравнения двух замеров, а не однозначного критерия. Поэтому он не входит в
// `ci/suites.json` (fast-гейт) и запускается отдельно:
//
//   node ci/perf_probe.mjs                    # краткий отчёт в stdout
//   node ci/perf_probe.mjs --json             # машинночитаемый JSON
//   node ci/perf_probe.mjs --scenario=pan     # только панорамирование
//
// Что измеряется:
//  1. панорамирование карты (перетаскивание) — «карта тяжело перемещается мышкой»;
//  2. кадры при включённой симуляции (сообщения live_state 4 раза в секунду);
//  3. кадры при выключенных дорогах (вклад слоя дорог в кадр);
//  4. вклад каждой секции отрисовки — по накопителю window.AssistPerf.
//
// Как это связано с задачей: сначала нужно было РАЗДЕЛИТЬ нагрузку на
// компоненты и измерить их по очереди. Секции в отчёте — это и есть разделение,
// а сценарии — поочерёдное отключение (дороги вкл/выкл, симуляция вкл/выкл).
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const args = process.argv.slice(2);
const jsonMode = args.includes("--json");
const scenarioArg = args.find(item => item.startsWith("--scenario="));
const onlyScenario = scenarioArg ? scenarioArg.split("=")[1] : null;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-perf-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "simulator.js", "vitals.js", "web_log.js", "perf.js", "interface.js", "playerPanels.js", "inventory.js"]) {
  const source = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(source)) fs.copyFileSync(source, path.join(tmp, "Web", name));
}

const roadsPath = path.join(root, "data", "world", "roads.json");
const roads = fs.existsSync(roadsPath)
  ? JSON.parse(fs.readFileSync(roadsPath, "utf8")).segments
  : [];

fs.writeFileSync(path.join(tmp, "Web", "perf.html"), `<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><link rel="stylesheet" href="theme.css">
<style>
  html,body{margin:0;height:100%;overflow:hidden}
  .mapSurface{position:relative;width:1200px;height:800px}
  #mapCanvas{display:block;width:100%;height:100%}
</style></head>
<body>
<div id="hud"></div><aside id="side"></aside><aside id="runtimeSide"></aside>
<section class="mapSurface" id="mapWrap"><canvas id="mapCanvas"></canvas></section>
<div class="mapContextMenu" id="mapContextMenu" hidden><button id="movePlayerHere"></button></div>
<div id="mapStatusBar">
  <input type="checkbox" id="onlyQuestsToggle">
  <input type="checkbox" id="citiesToggle">
  <input type="checkbox" id="roadsToggle" checked>
  <span id="mapStatusHint"></span>
</div>
<button id="backpackButton"></button><div id="inventoryNotifications"></div>
<div id="playerOverlay"><section id="inventoryPanel"></section>
<section class="gamePanel characterPanel" id="characterPanel"></section></div>
<script>
  // perf=1 включает счётчики ДО загрузки simulator.js: счётчики выключены в
  // рабочей сборке, поэтому включаем их здесь же, а не правкой продакшена.
  window.AQ_PERF = true;
  window.chrome = { webview: { listeners: new Map(),
    addEventListener(type, handler) { const list = this.listeners.get(type) || []; list.push(handler); this.listeners.set(type, list); },
    dispatch(type, data) { (this.listeners.get(type) || []).forEach(handler => handler({ data: JSON.stringify(data) })); },
    postMessage(message) { (window.__sent = window.__sent || []).push(message); } } };
</script>
<script src="perf.js"></script>
<script src="web_log.js"></script><script src="vitals.js"></script>
<script src="playerPanels.js"></script><script src="inventory.js"></script>
<script src="simulator.js"></script>
</body></html>`);

const snapshot = {
  player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
  world: {
    coordinateSystem: "ETS2", activeLocation: "perf",
    points: [
      { id: "sdo:1", name: "Склад", category: "cargo", position: { x: 120, y: 0, z: 160 }, color: "#78c8f0", triggerRadius: 35, editable: false },
      { id: "sdo:2", name: "Руслан", category: "quest", position: { x: -260, y: 0, z: -90 }, color: "#ff7a50", triggerRadius: 35, editable: false },
      { id: "sdo:3", name: "Заправка", category: "fuel", position: { x: 340, y: 0, z: -210 }, color: "#8ad07a", triggerRadius: 35, editable: false },
      { id: "city:1", name: "Город", category: "city", isCity: true, position: { x: 500, y: 0, z: 300 }, color: "#ffd400", triggerRadius: 0, editable: false }
    ]
  },
  selection: { point: null, source: "" },
  facts: { values: {} }, questStatuses: { quests: [] },
  states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
  playerVitals: { health: 10000, maxHealth: 10000, energy: 10000, maxEnergy: 10000, hydration: 10000, maxHydration: 10000, fatigue: 0, maxFatigue: 10000 },
  playerProgress: { money: 0, experience: 0, reserve: 0 },
  character: { stats: { strength: 5 }, skills: [] },
  inventory: { items: {}, newItemIds: [] },
  reputation: { entries: {} },
  conditions: { cumulativeHealth: 0, cumulativeEnergy: 0, cumulativeHydration: 0, stress: 0, cumulativeStress: 0, cumulativeFatigue: 0, criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0, effects: [] },
  telemetry: {}, environment: {},
  system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" },
  route: {
    enabled: false, editing: false, editingAllowed: true, defaultSpeedKmh: 60,
    selectedWaypointId: null, stoppedWaypointIndex: null, currentTargetWaypointIndex: null,
    travelTimeRealSeconds: 0, travelTimeGameSeconds: 0, totalDistanceMeters: 0,
    distanceFromFirstWaypointMeters: 0, waypoints: [], legs: [], errors: []
  }
};

const results = [];

function pushResult(name, report) {
  results.push({ scenario: name, ...report });
}

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));
  // console.error превращаем в ошибку страницы: web_log.js глушит их иначе.
  page.on("console", message => {
    if (message.type() === "error") pageErrors.push(message.text());
  });

  await page.goto("file:///" + path.join(tmp, "Web", "perf.html").replace(/\\/g, "/"));
  await page.waitForFunction(() => !!window.AssistPerf, null, { timeout: 5000 });

  // Дороги идут отдельным сообщением — как в приложении.
  await page.evaluate(segments => {
    window.chrome.webview.dispatch("message", {
      type: "roads", segments, segmentCount: segments.length / 4
    });
  }, roads);

  const pushSnapshot = overrides => page.evaluate(({ value, extra }) => {
    window.chrome.webview.dispatch("message", {
      type: "snapshot", version: "perf", snapshot: value,
      questCatalog: [], simulationRunning: extra.simulationRunning,
      simulationPaused: false, simulationSpeed: 1,
      selectedQuest: { campaignId: "", questId: "" },
      reputationViews: {}, worldSettings: null, daylight: null,
      runtime: { status: "Stopped", currentNodeId: null }
    });
  }, { value: snapshot, extra: overrides });

  await pushSnapshot({ simulationRunning: false });
  await page.waitForTimeout(400);

  /** Обнуляет накопитель перед сценарием. */
  const resetPerf = () => page.evaluate(() => window.AssistPerf.reset());

  /**
   * Сценарий панорамирования: реальное перетаскивание СКМ по карте.
   *
   * Именно этот жест назван в задаче («карта тоже тяжело перемещается мышкой»),
   * поэтому он измеряется живыми событиями указателя, а не вызовами функций.
   */
  async function scenarioPan() {
    await resetPerf();
    const box = await page.locator("#mapCanvas").boundingBox();
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;

    // Панорама идёт по СРЕДНЕЙ кнопке мыши. `page.mouse` с кнопкой middle в
    // Playwright не порождает корректную последовательность pointer-событий,
    // поэтому события отправляются прямо в канвас: обработчики те же самые, а
    // жест воспроизводится предсказуемо. Между кадрами обязательна пауза —
    // перерисовка коалесцируется через requestAnimationFrame.
    await page.evaluate(async ({ cx, cy }) => {
      const canvas = document.getElementById("mapCanvas");
      const rect = canvas.getBoundingClientRect();
      const local = { x: cx - rect.left, y: cy - rect.top };

      const emit = (type, x, y, buttons) => canvas.dispatchEvent(new PointerEvent(type, {
        bubbles: true, cancelable: true, pointerId: 1, pointerType: "mouse",
        button: 1, buttons, clientX: x, clientY: y
      }));

      const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

      emit("pointerdown", cx, cy, 4);
      for (let step = 0; step < 50; step++) {
        const x = cx + Math.sin(step / 6) * 260;
        const y = cy + Math.cos(step / 7) * 180;
        emit("pointermove", x, y, 4);
        await sleep(8);
      }
      emit("pointerup", cx, cy, 0);
      void local;
    }, { cx, cy });

    await page.waitForTimeout(150);
    return page.evaluate(() => window.AssistPerf.report());
  }

  /**
   * Сценарий симуляции: 4 сообщения live_state в секунду (как Host на тике
   * 250 мс) плюс редкие полные snapshot.
   */
  async function scenarioSimulation() {
    await pushSnapshot({ simulationRunning: true });
    await page.waitForTimeout(120);
    await resetPerf();
    for (let tick = 0; tick < 40; tick++) {
      await page.evaluate(({ index, route }) => {
        window.chrome.webview.dispatch("message", {
          type: "live_state",
          player: { position: { x: index * 3, y: 0, z: index * 2 }, speedKmh: 42, heading: 90, paused: false, inCab: true },
          playerVitals: { health: 10000, maxHealth: 10000, energy: 8000, maxEnergy: 10000, hydration: 7000, maxHydration: 10000, fatigue: 1200, maxFatigue: 10000 },
          conditions: { cumulativeHealth: 0, cumulativeEnergy: 0, cumulativeHydration: 0, stress: 400, cumulativeStress: 400, cumulativeFatigue: 0, criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0, effects: [] },
          runtime: { status: "Waiting", currentNodeId: "n1" },
          simulationRunning: true, simulationPaused: false, simulationSpeed: 1,
          route
        });
      }, { index: tick, route: snapshot.route });
      await page.waitForTimeout(25);
    }
    await page.waitForTimeout(150);
    const report = await page.evaluate(() => window.AssistPerf.report());
    await pushSnapshot({ simulationRunning: false });
    await page.waitForTimeout(120);
    return report;
  }

  /** Сценарий «без дорог»: тот же зум и кадры, слой дорог выключен. */
  async function scenarioRoadsOff() {
    await page.evaluate(() => {
      const toggle = document.getElementById("roadsToggle");
      toggle.checked = false;
      toggle.dispatchEvent(new Event("change"));
    });
    await page.waitForTimeout(120);
    await resetPerf();
    for (let step = 0; step < 25; step++) {
      await page.evaluate(({ index, route }) => {
        window.chrome.webview.dispatch("message", {
          type: "live_state",
          player: { position: { x: index, y: 0, z: 0 }, speedKmh: 10, heading: 0, paused: false, inCab: true },
          runtime: { status: "Waiting", currentNodeId: null },
          simulationRunning: true, simulationPaused: false, simulationSpeed: 1,
          route
        });
      }, { index: step, route: snapshot.route });
      await page.waitForTimeout(25);
    }
    await page.waitForTimeout(150);
    const report = await page.evaluate(() => window.AssistPerf.report());
    await page.evaluate(() => {
      const toggle = document.getElementById("roadsToggle");
      toggle.checked = true;
      toggle.dispatchEvent(new Event("change"));
    });
    return report;
  }

  /**
   * Сценарий плотной карты: много точек и квестов вне экрана.
   *
   * Смысл — показать ВКЛАД ОТСЕЧЕНИЯ: при большом каталоге точек в кадре
   * должны остаться только видимые. Если отсечение сломать, время «Точки мира»
   * вырастет пропорционально общему числу точек, а не видимых.
   */
  async function scenarioDensePoints() {
    const dense = JSON.parse(JSON.stringify(snapshot));
    dense.world.points = [];
    for (let index = 0; index < 4000; index++) {
      dense.world.points.push({
        id: "sdo:" + index,
        name: "СДО " + index,
        category: "cargo",
        position: { x: (index % 80) * 40 - 1600, y: 0, z: Math.floor(index / 80) * 40 - 1000 },
        color: "#78c8f0",
        triggerRadius: 35,
        editable: false
      });
    }

    await page.evaluate(value => {
      window.chrome.webview.dispatch("message", {
        type: "snapshot", version: "perf-dense", snapshot: value,
        questCatalog: [], simulationRunning: false, simulationPaused: false, simulationSpeed: 1,
        selectedQuest: { campaignId: "", questId: "" }, reputationViews: {},
        worldSettings: null, daylight: null, runtime: { status: "Stopped", currentNodeId: null }
      });
    }, dense);
    await page.waitForTimeout(300);

    await resetPerf();
    for (let step = 0; step < 25; step++) {
      await page.evaluate(({ index, route }) => {
        window.chrome.webview.dispatch("message", {
          type: "live_state",
          player: { position: { x: index * 10, y: 0, z: 0 }, speedKmh: 30, heading: 0, paused: false, inCab: true },
          runtime: { status: "Waiting", currentNodeId: null },
          simulationRunning: true, simulationPaused: false, simulationSpeed: 1,
          route
        });
      }, { index: step, route: snapshot.route });
      await page.waitForTimeout(25);
    }
    await page.waitForTimeout(150);
    const report = await page.evaluate(() => window.AssistPerf.report());

    // Возвращаем обычный мир.
    await page.evaluate(value => {
      window.chrome.webview.dispatch("message", {
        type: "snapshot", version: "perf", snapshot: value,
        questCatalog: [], simulationRunning: false, simulationPaused: false, simulationSpeed: 1,
        selectedQuest: { campaignId: "", questId: "" }, reputationViews: {},
        worldSettings: null, daylight: null, runtime: { status: "Stopped", currentNodeId: null }
      });
    }, snapshot);
    await page.waitForTimeout(150);
    return report;
  }

  const scenarios = {
    pan: { title: "Панорамирование (симуляция выключена)", run: scenarioPan },
    simulation: { title: "Симуляция включена (live_state 4 Гц)", run: scenarioSimulation },
    densePoints: { title: "Плотная карта: 4000 точек, отсечение", run: scenarioDensePoints },
    roadsOff: { title: "Дороги выключены (вклад слоя)", run: scenarioRoadsOff }
  };

  for (const [key, scenario] of Object.entries(scenarios)) {
    if (onlyScenario && onlyScenario !== key) continue;
    const report = await scenario.run();
    pushResult(scenario.title, report);
  }

  if (pageErrors.length) {
    console.error("Ошибки страницы во время замера: " + pageErrors.join(" | "));
  }
} finally {
  await closeBrowser(browser);
}

if (jsonMode) {
  console.log(JSON.stringify({
    generatedAt: new Date().toISOString(),
    // Roads влияют на кадр только при включённом слое; размер набора печатаем,
    // чтобы было видно, на каком объёме снят замер.
    roadSegments: roads.length / 4,
    results
  }, null, 2));
} else {
  console.log("=== Замер производительности карты симулятора ===\n");
  console.log("Отрезков дорог: " + (roads.length / 4) + "\n");
  console.log("ГЛАВНАЯ МЕТРИКА — «drawMap целиком»: среднее время отрисовки кадра.");
  console.log("FPS зависит от того, КАК ЧАСТО пробник присылает сообщения, и не");
  console.log("является свойством карты; сравнивайте в первую очередь мс.\n");
  for (const result of results) {
    console.log("### " + result.scenario);
    const whole = result.sections.find(section => section.section === "drawMap целиком");
    console.log("  отрисовка кадра: сред " + (whole ? whole.avgMs : "—") +
      " мс, макс " + (whole ? whole.maxMs : "—") + " мс");
    console.log("  кадров: " + result.frames + ", FPS пробника: " + result.fps +
      ", кадр: сред " + result.frameAvgMs + " мс, p50 " + result.frameP50Ms +
      ", p95 " + result.frameP95Ms + ", макс " + result.frameMaxMs + " мс");
    for (const section of result.sections) {
      if (section.section === "drawMap целиком") continue;
      console.log("    " + section.section.padEnd(30) +
        " вызовов " + String(section.calls).padStart(5) +
        "  сумма " + String(section.totalMs).padStart(9) + " мс" +
        "  сред " + String(section.avgMs).padStart(8) + " мс" +
        "  макс " + String(section.maxMs).padStart(8) + " мс");
    }
    console.log("");
  }
  console.log("Сравнивайте «сред» по секциям между сценариями: доля секции");
  console.log("в «drawMap целиком» показывает, какой слой съедает время кадра.");
}
