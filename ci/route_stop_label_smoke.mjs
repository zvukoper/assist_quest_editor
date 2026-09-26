// ПРОВЕРКА: подпись путевой точки со скоростью 0 — «Остановка» (пульсирующая,
// оранжевая), а не «0 км/ч». Проверяются реальные пиксели: canvas не даёт
// инспектировать разметку, а подпись рисуется именно на нём.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-stoplabel-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "simulator.js", "vitals.js", "web_log.js", "interface.js", "playerPanels.js", "inventory.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

fs.writeFileSync(path.join(tmp, "Web", "s.html"), `<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><link rel="stylesheet" href="theme.css">
<style>body{margin:0}#mapCanvas{display:block;width:900px;height:640px}</style></head>
<body>
<div id="hud"></div><aside id="side"></aside><aside id="runtimeSide"></aside>
<canvas id="mapCanvas"></canvas>
<div class="mapContextMenu" id="mapContextMenu" hidden><button id="movePlayerHere"></button></div>
<button id="backpackButton"></button><div id="inventoryNotifications"></div>
<div id="playerOverlay"><section id="inventoryPanel"></section>
<section class="gamePanel characterPanel" id="characterPanel"></section></div>
<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script>
<script src="web_log.js"></script><script src="vitals.js"></script>
<script src="playerPanels.js"></script><script src="inventory.js"></script>
<script src="simulator.js"></script>
</body></html>`);

// Три точки: со скоростью 60 (подпись со скоростью), со скоростью 0 и с
// ненулевой скоростью, на которой игрок стоит (остановка).
const snapshot = {
  player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
  world: { coordinateSystem: "ETS2", points: [], activeLocation: "t" },
  selection: { point: null, source: "" }, facts: { values: {} }, questStatuses: { quests: [] },
  states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
  playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
  playerProgress: { money: 0, experience: 0, reserve: 0 },
  character: { stats: {}, skills: [] }, inventory: { items: {}, newItemIds: [] },
  reputation: { entries: {} },
  conditions: { cumulativeHealth: 0, cumulativeEnergy: 0, cumulativeHydration: 0, stress: 0, cumulativeStress: 0, cumulativeFatigue: 0, criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0, effects: [] },
  telemetry: {}, environment: {},
  system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" },
  route: {
    enabled: false, editing: false, editingAllowed: true, defaultSpeedKmh: 60,
    selectedWaypointId: null, stoppedWaypointIndex: 1,
    currentTargetWaypointIndex: 1,
    travelTimeRealSeconds: 0, travelTimeGameSeconds: 0,
    totalDistanceMeters: 200, distanceFromFirstWaypointMeters: 50,
    waypoints: [
      { id: "a", index: 1, x: -100, y: 0, z: 0, speedKmh: 60, isOffRoad: false, distanceFromFirstMeters: 0, estimatedArrivalGameTime: "—", countdownRealSeconds: null },
      { id: "b", index: 2, x: 0, y: 0, z: 0, speedKmh: 0, isOffRoad: false, distanceFromFirstMeters: 100, estimatedArrivalGameTime: "—", countdownRealSeconds: null },
      { id: "c", index: 3, x: 120, y: 0, z: 0, speedKmh: 45, isOffRoad: false, distanceFromFirstMeters: 220, estimatedArrivalGameTime: "—", countdownRealSeconds: null }
    ],
    legs: [],
    errors: []
  }
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1000, height: 720 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));
  await page.goto("file:///" + path.join(tmp, "Web", "s.html").replace(/\\/g, "/"));
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "s", snapshot: v, route: v.route,
    // Симуляция НЕ останавливается на точке со скоростью 0 — выключается только
    // «Движение по маршруту». Пульсация рисуется кадровым циклом маршрута,
    // который живёт именно при идущей симуляции.
    simulationRunning: true, simulationPaused: false
  }), snapshot);
  await page.waitForTimeout(600);

  // Пульсация: два кадра с разницей во времени должны дать разную прозрачность
  // оранжевой подписи. Образец берётся в полосе НАД маркером нулевой точки
  // (там рисуется подпись), а не по всей карте: по всей карте в счёт попадал бы
  // оранжевый маркер игрока и «перекрест», которые не пульсируют.
  const sample = () => page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    const data = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [data[i], data[i + 1], data[i + 2]];
    };

    // Центр маркера нулевой точки — по его заливке #12abe5 = rgb(18,171,229).
    let mx = 0, my = 0, found = 0;
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const [r, g, b] = at(x, y);
        if (r < 60 && g > 140 && g < 200 && b > 200) { mx += x; my += y; found++; }
      }
    }
    if (!found) return { found: false, sum: 0 };

    const cx = Math.round(mx / found), cy = Math.round(my / found);

    // Полоса подписи: выше маркера, но ниже строки ETA.
    let sum = 0;
    for (let y = cy - 42; y <= cy - 12; y++) {
      for (let x = cx - 90; x <= cx + 90; x++) {
        if (y < 0 || x < 0 || y >= h || x >= w) continue;
        const [r, g, b] = at(x, y);
        // Оранжевый #ff9f1a с учётом пульсации: R заметно выше B.
        if (r > 90 && r > b + 30) sum += r - b;
      }
    }
    return { found: true, cx, cy, sum };
  });

  const samples = [];
  for (let i = 0; i < 8; i++) {
    samples.push(await sample());
    await page.waitForTimeout(170);
  }

  const first = samples[0];
  check(first.found, "на карте не найден маркер нулевой точки");
  check(first.sum > 0, "над нулевой точкой нет оранжевой подписи «Остановка»");

  // Пульсация обязана менять яркость: берём размах по кадрам.
  const sums = samples.filter(s => s.found).map(s => s.sum);
  const spread = Math.max(...sums) - Math.min(...sums);
  check(spread > 20,
    "подпись «Остановка» не пульсирует: значения по кадрам " + JSON.stringify(sums));

  check(errors.length === 0, "ошибки страницы: " + errors.join(" | "));

  if (failures.length) throw new Error("Route stop label smoke: " + failures.join("; "));
  console.log("Route stop label smoke: OK (размах пульсации " + spread + ")");
} finally {
  await closeBrowser(browser);
  fs.rmSync(tmp, { recursive: true, force: true });
}
