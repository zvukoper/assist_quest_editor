import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

// Проверка карты Симулятора на живых событиях указателя, а не на вызовах
// функций из консоли. Так нашлась реальная поломка, которую статическая
// проверка пропустила бы: меню ПКМ лежит ПОВЕРХ канваса, поэтому переход
// курсора с карты на пункт меню вызывает `pointerleave` канваса. Обработчик
// сбрасывал цель ПКМ, и пункт «Переместить игрока сюда» молча ничего не делал —
// со стороны это выглядело как неработающая кнопка.

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-mapmenu-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

for (const name of ["theme.css", "simulator.js", "web_log.js", "playerPanels.js", "inventory.js"]) {
  const source = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(source)) fs.copyFileSync(source, path.join(tmp, "Web", name));
}

// Разметка повторяет существенное из simulator.html: .mapSurface{position:relative}
// оборачивает канвас, а меню лежит внутри него, поверх карты.
fs.writeFileSync(path.join(tmp, "Web", "map.html"), `<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><link rel="stylesheet" href="theme.css">
<style>
  html,body{margin:0}
  .mapSurface{position:relative;overflow:hidden;width:900px;height:640px}
  #mapCanvas{display:block;width:100%;height:100%;position:absolute;inset:0;background:#0d1014}
</style></head>
<body>
<div id="hud"></div><aside id="side"></aside><aside id="runtimeSide"></aside>
<section class="mapSurface" id="mapWrap">
  <canvas id="mapCanvas"></canvas>
  <div class="mapContextMenu" id="mapContextMenu" hidden>
    <button type="button" id="movePlayerHere">Переместить игрока сюда</button>
  </div>
</section>
<button id="backpackButton"></button><div id="inventoryNotifications"></div>
<div id="playerOverlay">
  <section id="inventoryPanel"></section>
  <section class="gamePanel characterPanel" id="characterPanel"></section>
</div>
<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(type, handler) {
    const list = this.listeners.get(type) || [];
    list.push(handler);
    this.listeners.set(type, list);
  },
  dispatch(type, data) {
    (this.listeners.get(type) || []).forEach(handler => handler({ data: JSON.stringify(data) }));
  },
  postMessage(message) { (window.__sent = window.__sent || []).push(message); } } };</script>
<script src="web_log.js"></script><script src="playerPanels.js"></script>
<script src="inventory.js"></script><script src="simulator.js"></script>
</body></html>`);

const snapshot = {
  player: { position: { x: 40, y: 12, z: 60 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
  world: { coordinateSystem: "ETS2", points: [], activeLocation: "t" },
  selection: { point: null, source: "" },
  facts: { values: {} },
  questStatuses: { quests: [] },
  states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
  playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
  playerProgress: { money: 1500, experience: 0, reserve: 0 },
  character: { stats: { strength: 5 }, skills: [] },
  inventory: { items: {}, newItemIds: [] },
  reputation: { entries: {} },
  conditions: {
    cumulativeHealth: 0, cumulativeEnergy: 0, cumulativeHydration: 0,
    stress: 0, cumulativeStress: 0, cumulativeFatigue: 0,
    criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0, effects: []
  },
  telemetry: {},
  environment: {},
  system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.goto("file:///" + path.join(tmp, "Web", "map.html").replace(/\\/g, "/"));
  await page.evaluate(value => {
    window.chrome.webview.dispatch("message", { type: "snapshot", version: "smoke", snapshot: value });
  }, snapshot);
  await page.waitForTimeout(500);

  // Канвас обязан быть в видимой области: координаты мыши задаются от границы
  // канваса, и при прокрученной странице клик ушёл бы мимо окна.
  await page.locator("#mapCanvas").scrollIntoViewIfNeeded();
  await page.waitForTimeout(100);
  const canvas = await page.locator("#mapCanvas").boundingBox();
  const openMenu = async (localX, localY) => {
    await page.mouse.move(canvas.x + localX, canvas.y + localY);
    await page.mouse.down({ button: "right" });
    await page.mouse.up({ button: "right" });
    await page.waitForTimeout(150);
  };

  // 1. ПКМ открывает меню в точке курсора.
  await openMenu(300, 200);
  const menu = await page.evaluate(() => {
    const element = document.getElementById("mapContextMenu");
    return element && !element.hidden
      ? { left: element.style.left, top: element.style.top }
      : null;
  });
  check(menu !== null, "ПКМ по карте не открывает контекстное меню");
  check(!!menu && menu.left === "300px" && menu.top === "200px",
    `меню открылось не в точке курсора: ${JSON.stringify(menu)}`);

  // 2. Клик по пункту меню перемещает игрока — ключевой сценарий: курсор
  //    плавно уходит с канваса на кнопку, порождая pointerleave карты.
  await page.evaluate(() => { window.__sent = []; });
  const button = await page.locator("#movePlayerHere").boundingBox();
  check(button !== null, "пункт меню «Переместить игрока сюда» недоступен");

  if (button) {
    await page.mouse.move(button.x + 10, button.y + 10, { steps: 12 });
    await page.mouse.down({ button: "left" });
    await page.mouse.up({ button: "left" });
    await page.waitForTimeout(200);
  }

  const sent = await page.evaluate(() =>
    (window.__sent || []).filter(message => message.action === "set_player_position"));
  check(sent.length === 1,
    `пункт меню не отправил set_player_position (получено ${sent.length}): ` +
    JSON.stringify(sent));
  check(!!sent[0] && Number.isFinite(sent[0].x) && Number.isFinite(sent[0].z),
    "set_player_position отправлен без числовых координат: " + JSON.stringify(sent[0]));
  // Координата должна быть той, где вызвано меню, а не центром карты: центр
  // совпадает с начальной камерой и маскировал бы ошибку чтения курсора.
  check(!!sent[0] && !(sent[0].x === 0 && sent[0].z === 0),
    "координата перемещения совпала с началом мира: цель ПКМ не прочитана");
  check(await page.evaluate(() => document.getElementById("mapContextMenu").hidden),
    "меню осталось открытым после выбора пункта");

  // 3. Клик мимо меню закрывает его, не трогая игрока.
  await openMenu(340, 260);
  await page.evaluate(() => { window.__sent = []; });
  await page.mouse.click(canvas.x + 700, canvas.y + 500, { button: "left" });
  await page.waitForTimeout(150);
  check(await page.evaluate(() => document.getElementById("mapContextMenu").hidden),
    "ЛКМ мимо меню не закрыла его");
  const strayMoves = await page.evaluate(() =>
    (window.__sent || []).filter(message => message.action === "set_player_position").length);
  check(strayMoves === 0, "ЛКМ мимо меню переместила игрока");

  // 4. Esc закрывает меню.
  await openMenu(360, 300);
  await page.keyboard.press("Escape");
  await page.waitForTimeout(120);
  check(await page.evaluate(() => document.getElementById("mapContextMenu").hidden),
    "Esc не закрывает контекстное меню карты");

  check(pageErrors.length === 0, "ошибки страницы: " + pageErrors.join(" | "));

  if (failures.length) {
    throw new Error("Map context menu smoke: " + failures.join("; "));
  }

  console.log("Map context menu smoke: OK");
} finally {
  await closeBrowser(browser);
  fs.rmSync(tmp, { recursive: true, force: true });
}
