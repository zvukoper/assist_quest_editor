// Проверка ПЛАВНОСТИ маркера игрока на карте Simulator.
//
// ПРОБЛЕМА, которую проверка стережёт: позиция игрока приходит сообщениями
// (live_state раз в 250 мс), поэтому между пакетами маркер не двигался, а на
// каждом новом прыгал на десятки метров пути. Со стороны это выглядело как
// «точка игрока движется рывками», хотя частота кадров страницы была ни при чём.
//
// ПРОВЕРКА. Маркер рисуется на canvas, поэтому инспектировать разметку нельзя —
// считаем реальные пиксели. Симуляция запускается, ставится ОДИН пакет с
// движением, и дальше пакеты НЕ приходят: если покадровый цикл работает, маркер
// продолжит смещаться вправо, если цикла нет (или окно экстраполяции нулевое) —
// центр маркера замрёт до следующего пакета.
//
// Проверка обязана ПАДАТЬ на коде без покадрового цикла: это негативный
// контроль, а не подтверждение «что-то нарисовано».
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-marker-anim-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of [
  "theme.css", "simulator.js", "vitals.js", "web_log.js",
  "interface.js", "playerPanels.js", "inventory.js"
]) {
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

// Маршрут не нужен: проверяется сглаживание маркера, а не подписи точек.
// Игрок ставится в центр карты и едет строго на восток (+X) со скоростью
// 72 км/ч = 20 м/с, то есть 5 м за пакет в 250 мс.
const snapshot = {
  player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 72, heading: 0, paused: false, inCab: true },
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
    selectedWaypointId: null, stoppedWaypointIndex: null, currentTargetWaypointIndex: null,
    travelTimeRealSeconds: 0, travelTimeGameSeconds: 0,
    totalDistanceMeters: 0, distanceFromFirstWaypointMeters: 0,
    waypoints: [], legs: [], errors: []
  }
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1000, height: 720 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.goto("file:///" + path.join(tmp, "Web", "s.html").replace(/\\/g, "/"));

  // Симуляция ВКЛ: без неё покадровый цикл маркера не имеет права работать.
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "s", snapshot: v, route: v.route,
    simulationRunning: true, simulationPaused: false, simulationSpeed: 1
  }), snapshot);

  // Пакетов больше НЕ будет. Ждём: покадровый цикл обязан двигать маркер сам.
  //
  // Замеры берутся РАНО (50 / 150 / 400 мс). Предел экстраполяции — 0.3 с, и к
  // этому моменту сглаженная позиция выходит на плато: поздние замеры показали
  // бы нулевое смещение на ИСПРАВНОМ коде и проверка ловила бы сама себя.
  await page.waitForTimeout(50);

  // Центр оранжевой заливки маркера ищется в области карты. Кольца дистанций
  // исключены из поиска: у них другой цвет и они тоньше.
  const markerX = () => page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    const data = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [data[i], data[i + 1], data[i + 2]];
    };

    // ACCENT_COLOR заливки маркера — #fab003 = rgb(250,176,3).
    let minX = Infinity, maxX = -Infinity, found = 0;
    for (let y = 2; y < h - 2; y++) {
      for (let x = 2; x < w - 2; x++) {
        const [r, g, b] = at(x, y);
        if (Math.abs(r - 250) > 20 || Math.abs(g - 176) > 20 || b > 30) continue;
        found++;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
      }
    }

    return found > 20 && Number.isFinite(minX)
      ? { center: (minX + maxX) / 2, found }
      : null;
  });

  const first = await markerX();
  await page.waitForTimeout(100);
  const second = await markerX();
  await page.waitForTimeout(250);
  const third = await markerX();

  check(first !== null, "маркер игрока не найден на canvas до начала проверки");
  check(second !== null, "маркер игрока не найден на canvas через 150 мс");
  check(third !== null, "маркер игрока не найден на canvas через 400 мс");

  if (first && second && third) {
    // Скорость 20 м/с: за 100 мс это 2 м пути. Даже с отставанием сглаживания
    // маркер обязан сдвинуться заметно, если покадровый цикл работает.
    const step1 = second.center - first.center;
    const step2 = third.center - second.center;

    check(step1 > 0.5,
      "между 50 и 150 мс маркер игрока почти не двинулся (" +
      step1.toFixed(2) + " px): покадровый цикл сглаживания не работает");
    check(step2 > 0.5,
      "между 150 и 400 мс маркер игрока почти не двинулся (" +
      step2.toFixed(2) + " px): движение оборвалось, хотя симуляция идёт");

    // Рывок означал бы СКАЧОК на пакете и замирание между ними. Здесь скачков
    // быть не должно: два подряд интервала обязаны быть сопоставимы.
    const ratio = Math.max(step1, step2) / Math.max(0.001, Math.min(step1, step2));
    check(ratio < 4,
      "движение маркера неравномерно (шаги " + step1.toFixed(2) + " и " +
      step2.toFixed(2) + " px, отношение " + ratio.toFixed(2) + "): похоже на рывки");
  }

  // Симуляция остановлена: покадровый цикл обязан завершиться, иначе карта
  // перерисовывается вечно.
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "s", snapshot: v, route: v.route,
    simulationRunning: false, simulationPaused: false, simulationSpeed: 1
  }), snapshot);

  await page.waitForTimeout(250);
  const stoppedFirst = await markerX();
  await page.waitForTimeout(400);
  const stoppedSecond = await markerX();

  if (stoppedFirst && stoppedSecond) {
    const drift = Math.abs(stoppedSecond.center - stoppedFirst.center);
    check(drift < 0.5,
      "после остановки симуляции маркер продолжает ползти (" +
      drift.toFixed(2) + " px): покадровый цикл не выключился");
  }

  check(errors.length === 0, "ошибки страницы: " + errors.join(" | "));

  if (failures.length) {
    console.error("Player marker smoothness smoke: FAIL");
    failures.forEach(message => console.error("  ✗ " + message));
    process.exitCode = 1;
  } else {
    console.log(
      "Player marker smoothness smoke: OK " +
      "(смещение без пакетов " + (second && first ? (second.center - first.center).toFixed(2) : "?") +
      " → " + (third && second ? (third.center - second.center).toFixed(2) : "?") + " px)");
  }
} finally {
  await closeBrowser(browser);
}
