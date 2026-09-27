// ПРОВЕРКА: раскрытые разделы правого сайдбара.
//
//   1. Список разделов приходит СНИМКОМ от Host и хранится в пользовательских
//      настройках (ui-settings.json) — как размеры и положение окон. Раньше он
//      жил в localStorage страницы, а тот привязан к профилю WebView2: профиль
//      меняется вместе с отпечатком сборки, поэтому после обновления версии
//      разделы оказывались закрытыми.
//   2. Переключение раздела отправляет список в Host — иначе он не сохранился бы.
//   3. Сайдбар НЕ пересобирается, когда содержимое не изменилось. Снимок приходит
//      4 раза в секунду; замена узлов сбрасывала :hover под курсором, из-за чего
//      кнопка «мигала» и нажималась не с первого раза, а у пульсирующего бейджа
//      «Остановка» не оставалось шанса доиграть анимацию.
//   4. Пока кнопка нажата (pointerdown без pointerup), разметка не пересобирается:
//      иначе click между нажатием и отпусканием не срабатывает.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-sidebar-sections-"));
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

function makeSnapshot(extra = {}) {
  return {
    player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
    world: { coordinateSystem: "ETS2", points: [], activeLocation: "t" },
    selection: { point: null, source: "" },
    facts: { values: {} },
    questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
    playerProgress: { money: 0, experience: 0, reserve: 0 },
    character: { stats: {}, skills: [] },
    inventory: { items: {}, newItemIds: [] },
    reputation: { entries: {} },
    conditions: { cumulativeHealth: 0, cumulativeEnergy: 0, cumulativeHydration: 0, stress: 0, cumulativeStress: 0, cumulativeFatigue: 0, criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0, effects: [] },
    telemetry: {},
    environment: { weather: "Ясно", rainPercent: 0, gameTime: "12:00", visibilityMeters: 5000 },
    system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" },
    route: {
      enabled: false, editing: false, editingAllowed: true, defaultSpeedKmh: 60,
      selectedWaypointId: null, stoppedWaypointIndex: null, currentTargetWaypointIndex: null,
      travelTimeRealSeconds: 0, travelTimeGameSeconds: 0,
      totalDistanceMeters: 0, distanceFromFirstWaypointMeters: 0,
      waypoints: [], legs: [], errors: []
    },
    ...extra
  };
}

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  const pageErrors = [];
  page.on("pageerror", e => pageErrors.push(String(e)));

  await page.goto("file:///" + path.join(tmp, "Web", "s.html").replace(/\\/g, "/"));

  const pushSnapshot = snapshotValue => page.evaluate(value => {
    window.chrome.webview.dispatch("message", {
      type: "snapshot", version: "s", snapshot: value, route: value.route,
      // Разделы хранит Host: страница их только рисует.
      sidebarSections: value.__sidebarSections || [],
      simulationRunning: false, simulationPaused: false, simulationSpeed: 1
    });
  }, snapshotValue);

  const openSections = () => page.evaluate(() =>
    [...document.querySelectorAll("#side .acc.open")].map(s => s.dataset.section));

  // --- 1. Список из снимка применяется ---
  const first = makeSnapshot({ __sidebarSections: ["route", "vitals"] });
  await pushSnapshot(first);
  await page.waitForTimeout(150);

  const opened = await openSections();
  check(opened.includes("route") && opened.includes("vitals"),
    "раскрытые разделы из снимка не применены: " + JSON.stringify(opened));
  check(!opened.includes("facts"),
    "раздел «facts» раскрыт, хотя его нет в списке: " + JSON.stringify(opened));

  // --- 2. Переключение отправляет список в Host ---
  await page.evaluate(() => { window.__sent = []; });
  await page.locator("#side .acc[data-section='facts'] .accHead").click();
  await page.waitForTimeout(120);

  const sentAfterToggle = await page.evaluate(() => window.__sent || []);
  const sectionsMessage = sentAfterToggle.find(m => m.action === "set_sidebar_sections");

  check(!!sectionsMessage,
    "переключение раздела не отправило set_sidebar_sections: " +
    JSON.stringify(sentAfterToggle));
  check(!!sectionsMessage && Array.isArray(sectionsMessage.sections) &&
        sectionsMessage.sections.includes("facts"),
    "список разделов отправлен без только что раскрытого: " +
    JSON.stringify(sectionsMessage && sectionsMessage.sections));

  // Host подтверждает список — повторный снимок с ним не должен ничего ломать.
  const confirmed = makeSnapshot({ __sidebarSections: sectionsMessage?.sections || [] });
  await pushSnapshot(confirmed);
  await page.waitForTimeout(150);

  const afterConfirm = await openSections();
  check(afterConfirm.includes("facts"),
    "раздел «facts» закрылся после подтверждения списка Host: " +
    JSON.stringify(afterConfirm));

  // --- 3. Без изменений DOM не пересобирается ---
  await page.evaluate(() => {
    window.__toggleNode = document.getElementById("routeToggle");
  });

  await pushSnapshot(confirmed);
  await page.waitForTimeout(150);

  const sameNode = await page.evaluate(() =>
    window.__toggleNode === document.getElementById("routeToggle"));

  check(sameNode,
    "сайдбар пересобрался на неизменившемся снимке: узел кнопки подменён, " +
    "из-за этого сбрасывается :hover и «мигают» кнопка и бейдж «Остановка»");

  // --- 4. Нажатая кнопка не подменяется до отпускания ---
  await page.evaluate(() => {
    window.__sent = [];
    window.__pressedNode = document.getElementById("routeToggle");
    window.__pressedNode.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0 }));
  });

  // Снимок с ИЗМЕНИВШИМСЯ содержимым: разметка кнопки стала бы другой.
  const changed = makeSnapshot({ __sidebarSections: confirmed.__sidebarSections });
  changed.route = { ...changed.route, enabled: true };
  changed.player = { ...changed.player, speedKmh: 42 };

  await pushSnapshot(changed);
  await page.waitForTimeout(150);

  const stillPressed = await page.evaluate(() =>
    window.__pressedNode === document.getElementById("routeToggle"));

  check(stillPressed,
    "во время нажатия кнопки разметка сайдбара пересобрана: click не сработает, " +
    "и кнопка «нажимается не с первого раза»");

  await page.evaluate(() => {
    document.getElementById("routeToggle")
      .dispatchEvent(new PointerEvent("pointerup", { bubbles: true, button: 0 }));
  });
  await page.locator("#side #routeToggle").click();
  await page.waitForTimeout(120);

  const sentAfterPress = await page.evaluate(() => window.__sent || []);
  check(sentAfterPress.some(m => m.action === "route_toggle"),
    "нажатие кнопки маршрута не дошло до Host после снимка во время нажатия: " +
    JSON.stringify(sentAfterPress));

  // После отпускания изменение содержимого обязано примениться.
  await pushSnapshot(changed);
  await page.waitForTimeout(150);

  const enabledNow = await page.evaluate(() =>
    document.getElementById("routeToggle")?.getAttribute("aria-pressed"));

  check(enabledNow === "true",
    "после отпускания кнопки новое состояние не применено: aria-pressed=" + enabledNow);

  check(pageErrors.length === 0, "ошибки страницы: " + pageErrors.join(" | "));

  if (failures.length) {
    console.error("Sidebar sections smoke: FAIL");
    failures.forEach(message => console.error("  ✗ " + message));
    process.exitCode = 1;
  } else {
    console.log(
      "Sidebar sections smoke: OK " +
      "(разделы из снимка, публикация в Host, без пересборки на повторе)");
  }
} finally {
  await closeBrowser(browser);
}
