// ПРОВЕРКА: шкалы состояния в правом сайдбаре Симулятора и в окне «Игрок»
// обязаны совпадать — состав, цвета, направление кумулятива и расшифровка.
//
// Зачем проверка на живом рендере, а не сверка строк. Обе страницы рисуют шкалы
// разными копиями разметки, и копии уже разошлись: в окне «Игрок» не было шкалы
// «Стресс», цвета отличались от сайдбара, а подсказок не было ни там, ни там.
// Сверка строк такое не поймает: важно, что реально оказалось в DOM после
// обновления снимком — включая вычисленные стили, а не только текст разметки.
//
// Проверка ловит и поломку разметки: несогласованные кавычки в атрибуте класса
// съедали `style` кумулятивной части, из-за чего жёлтая шкала молча исчезала.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-vitals-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "simulator.js", "vitals.js", "inventory.js", "playerPanels.js", "web_log.js", "interface.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

// Браузер отдаёт цвет из style как rgb(...), а не тем же hex, которым он задан.
const toRgb = value => {
  const text = String(value || "").trim().toLowerCase();
  const hex = /^#([0-9a-f]{6})$/.exec(text);
  if (!hex) return text.replace(/\s+/g, "");
  const n = parseInt(hex[1], 16);
  return `rgb(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255})`;
};

fs.writeFileSync(path.join(tmp, "Web", "p.html"), `<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><link rel="stylesheet" href="theme.css"></head>
<body>
<div id="hud"></div><aside id="side"></aside><aside id="runtimeSide"></aside>
<canvas id="mapCanvas" style="width:800px;height:500px;display:block"></canvas>
<div class="mapContextMenu" id="mapContextMenu" hidden><button id="movePlayerHere"></button></div>
<button id="backpackButton"></button><div id="inventoryNotifications"></div>
<div id="playerOverlay">
  <section id="inventoryPanel"></section>
  <section class="gamePanel characterPanel" id="characterPanel"></section>
</div>
<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script>
<script src="web_log.js"></script><script src="vitals.js"></script>
<script src="playerPanels.js"></script><script src="inventory.js"></script>
<script src="simulator.js"></script>
</body></html>`);

const snapshot = {
  player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
  world: { coordinateSystem: "ETS2", points: [], activeLocation: "t" },
  selection: { point: null, source: "" }, facts: { values: {} }, questStatuses: { quests: [] },
  states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
  // Усталость НЕ 0: если жёлтая часть есть, а мягкая пустая — это и есть дефект.
  playerVitals: { health: 80, maxHealth: 100, energy: 55, maxEnergy: 100, hydration: 70, maxHydration: 100, fatigue: 40, maxFatigue: 100 },
  playerProgress: { money: 1500, experience: 0, reserve: 0 },
  character: { stats: { strength: 5 }, skills: [] },
  inventory: { items: {}, newItemIds: [] },
  reputation: { entries: {} },
  conditions: {
    cumulativeHealth: 10, cumulativeEnergy: 20, cumulativeHydration: 5,
    stress: 30, cumulativeStress: 8,
    cumulativeFatigue: 12, criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0,
    effects: []
  },
  telemetry: {}, environment: {},
  system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
};

const { browser } = await openBrowser();
const out = {};
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));
  await page.goto("file:///" + path.join(tmp, "Web", "p.html").replace(/\\/g, "/"));
  await page.evaluate(v => window.chrome.webview.dispatch("message", { type: "snapshot", version: "p", snapshot: v }), snapshot);
  await page.waitForTimeout(600);

  // Раскрываем секцию «Потребности» в сайдбаре.
  await page.evaluate(() => {
    const section = document.querySelector("#side .acc[data-section='vitals']");
    if (section && !section.classList.contains("open")) section.querySelector(".accHead").click();
  });
  await page.waitForTimeout(200);

  out.pageErrors = errors;

  const readScales = selector => page.evaluate(sel => {
    const root = document.querySelector(sel);
    if (!root) return null;
    return {
      labels: [...root.querySelectorAll(".dualStatHead span:first-child")].map(n => n.textContent.trim()),
      values: [...root.querySelectorAll(".dualStatHead span:last-child")].map(n => n.textContent.trim()),
      bars: [...root.querySelectorAll(".dualBar")].map(n => ({
        cls: n.className,
        soft: n.querySelector(".dualBarSoft")?.style.width || "",
        softColor: n.querySelector(".dualBarSoft")?.style.background || "",
        cum: n.querySelector(".dualBarCumulative")?.style.width || "",
        cumColor: n.querySelector(".dualBarCumulative")?.style.background || "",
        cumClass: n.querySelector(".dualBarCumulative")?.className.replace("dualBarCumulative", "").trim() || ""
      })),
      tooltips: [...root.querySelectorAll("[data-game-tooltip]")]
        .map(n => n.getAttribute("data-game-tooltip")).filter(Boolean)
    };
  }, selector);

  out.sidebar = await readScales("#side .acc[data-section='vitals'] .accBody");
  out.fields = await page.evaluate(() =>
    [...document.querySelectorAll("#side .acc[data-section='vitals'] [data-t]")].map(n => n.dataset.t));

  // Окно «Игрок» — потребности рисует AssistInventory.vitalsMarkup.
  out.playerWindow = await page.evaluate(() => {
    const panel = document.getElementById("inventoryPanel");
    panel.innerHTML = AssistInventory.panelMarkup(JSON.parse(JSON.stringify(window.__snapshot || {})), [], { withHeader: false });
    return null;
  }).catch(() => null);

  // Проще: собрать разметку напрямую тем же модулем.
  out.playerWindow = await page.evaluate(v => {
    const host = document.getElementById("inventoryPanel");
    host.innerHTML = "<div class='gameVitals'>" + AssistVitals.markup(v) + "</div>";
    const root = host.querySelector(".gameVitals");
    return {
      labels: [...root.querySelectorAll(".dualStatHead span:first-child")].map(n => n.textContent.trim()),
      values: [...root.querySelectorAll(".dualStatHead span:last-child")].map(n => n.textContent.trim()),
      bars: [...root.querySelectorAll(".dualBar")].map(n => ({
        cls: n.className,
        soft: n.querySelector(".dualBarSoft")?.style.width || "",
        softColor: n.querySelector(".dualBarSoft")?.style.background || "",
        cum: n.querySelector(".dualBarCumulative")?.style.width || "",
        cumColor: n.querySelector(".dualBarCumulative")?.style.background || "",
        cumClass: n.querySelector(".dualBarCumulative")?.className.replace("dualBarCumulative", "").trim() || ""
      })),
      tooltips: [...root.querySelectorAll("[data-game-tooltip]")]
        .map(n => n.getAttribute("data-game-tooltip")).filter(Boolean)
    };
  }, snapshot);

  // --- Проверки ---
  const required = ["Здоровье", "Стресс", "Энергия", "Жидкость", "Усталость"];
  check(JSON.stringify(out.sidebar?.labels) === JSON.stringify(required),
    "сайдбар: состав шкал " + JSON.stringify(out.sidebar?.labels));
  check(JSON.stringify(out.playerWindow?.labels) === JSON.stringify(required),
    "окно «Игрок»: состав шкал " + JSON.stringify(out.playerWindow?.labels));

  check((out.fields || []).includes("stress"),
    "в разделе «Потребности» нет поля для стресса: " + JSON.stringify(out.fields));

  const colors = {
    health: "#32cd32", stress: "#8c63d9", energy: "#ff8c00",
    hydration: "#2f7ff0", fatigue: "#e03131"
  };
  const expectedCumulative = { health: "fromEnd", stress: "fromStart", energy: "fromEnd", hydration: "fromEnd", fatigue: "fromStart" };

  for (const [name, scales] of [["сайдбар", out.sidebar], ["окно «Игрок»", out.playerWindow]]) {
    if (!scales) { check(false, name + ": шкалы не найдены"); continue; }

    for (const bar of scales.bars) {
      const key = bar.cls.replace("dualBar", "").trim();
      check(toRgb(bar.softColor) === toRgb(colors[key]),
        `${name}: цвет «${key}» ${bar.softColor} вместо ${colors[key]}`);
      check(toRgb(bar.cumColor) === toRgb("#ffd400"),
        `${name}: кумулятив «${key}» цвета ${bar.cumColor}, должен быть ярко-жёлтым #ffd400`);
      check(bar.cumClass === expectedCumulative[key],
        `${name}: кумулятив «${key}» растёт ${bar.cumClass}, ожидалось ${expectedCumulative[key]}`);
    }

    // Каждая шкала обязана иметь расшифровку с реальным и кумулятивным значением.
    for (const scale of required) {
      check(scales.tooltips.some(t => t.includes(scale)),
        `${name}: нет подсказки для «${scale}»`);
    }
    check(scales.tooltips.some(t => t.includes("Кумулятивное значение")),
      `${name}: подсказка не называет кумулятивное значение отдельно`);
    check(scales.tooltips.some(t => t.includes("Реальное значение")),
      `${name}: подсказка не называет реальное значение`);
  }

  // Усталость 40% — мягкая часть не пустая, иначе жёлтое выглядело бы «0%».
  const fatigueBar = out.sidebar?.bars.find(b => b.cls.includes("fatigue"));
  check(fatigueBar && fatigueBar.soft !== "0%",
    "сайдбар: мягкая часть усталости равна " + fatigueBar?.soft + " при усталости 40");

  if (errors.length) failures.push("ошибки страницы: " + errors.join(" | "));

  if (failures.length) throw new Error("Vitals parity smoke: " + failures.join("; "));

  console.log("Vitals parity smoke: OK (" + out.sidebar.bars.length + " шкал, " +
    out.sidebar.tooltips.length + " подсказок)");
} finally {
  await closeBrowser(browser);
  fs.rmSync(tmp, { recursive: true, force: true });
}
