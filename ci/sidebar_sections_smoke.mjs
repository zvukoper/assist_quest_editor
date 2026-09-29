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
  "theme.css", "simulator.js", "vitals.js", "web_log.js", "dom_reconcile.js",
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
<script src="web_log.js"></script><script src="dom_reconcile.js"></script><script src="vitals.js"></script>
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
    playerVitals: { health: 10000, maxHealth: 10000, energy: 10000, maxEnergy: 10000, hydration: 10000, maxHydration: 10000, fatigue: 0, maxFatigue: 10000 },
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

  // --- 5. Кнопки инструментов — в ЛЕВОМ сайдбаре, первый раздел «Инструменты» ---
  //
  // Монитор показателей, перки и предметы — окна постоянного доступа, а не
  // настройка одного показателя. Автор потребовал вынести их в левый сайдбар
  // ПЕРВЫМ разделом: их место там, где всегда виден контекст квестов, а не среди
  // сворачиваемых разделов правой панели (там кнопку пришлось бы искать).
  const toolsInRuntime = await page.evaluate(() => {
    const runtime = document.getElementById("runtimeSide");
    const firstHead = runtime.querySelector("section .accHead strong");
    return {
      firstIsTools: (firstHead?.textContent || "").trim() === "Инструменты",
      inRuntime: !!runtime.querySelector("#openIndicators"),
      hasMonitor: !!runtime.querySelector("#openIndicators"),
      hasPerks: !!runtime.querySelector("#openPerks"),
      hasItems: !!runtime.querySelector("#openItems"),
      inRightSidebar: !!document.querySelector("#side #openIndicators")
    };
  });
  check(toolsInRuntime.firstIsTools,
    "первый раздел левого сайдбара не «Инструменты»");
  check(toolsInRuntime.hasMonitor && toolsInRuntime.hasPerks && toolsInRuntime.hasItems,
    "кнопки инструментов не в левом сайдбаре: " + JSON.stringify(toolsInRuntime));
  check(!toolsInRuntime.inRightSidebar,
    "кнопки инструментов продублированы в правом сайдбаре");

  await page.evaluate(() => { window.__sent = []; });
  await page.locator("#runtimeSide #openIndicators").click();
  await page.locator("#runtimeSide #openPerks").click();
  await page.locator("#runtimeSide #openItems").click();
  await page.waitForTimeout(120);

  const toolSent = await page.evaluate(() => window.__sent || []);
  check(toolSent.some(m => m.action === "open_indicators"),
    "кнопка монитора не отправляет open_indicators: " + JSON.stringify(toolSent));
  check(toolSent.some(m => m.action === "open_perks"),
    "кнопка перков не отправляет open_perks: " + JSON.stringify(toolSent));
  check(toolSent.some(m => m.action === "open_items"),
    "кнопка предметов не отправляет open_items: " + JSON.stringify(toolSent));

  // --- 6. Кнопки инструментов не пересобираются на неизменившемся снимке ---
  //
  // Левый сайдбар до этого пересобирался на КАЖДЫЙ снимок, то есть четыре раза
  // в секунду: innerHTML заменял все узлы, и у кнопки под курсором сбрасывался
  // :hover. Автор описал это как «кнопки постоянно мерцают то выделением, то без
  // него» — причём независимо от движения по маршруту, потому что пересборка от
  // маршрута не зависела вовсе. Проверка держит сам УЗЕЛ: подмена узла и есть
  // мерцание, тогда как сверка разметки его пропустила бы.
  await page.evaluate(() => {
    window.__toolsNode = document.getElementById("openIndicators");
  });

  await pushSnapshot(makeSnapshot({ __sidebarSections: confirmed.__sidebarSections }));
  await page.waitForTimeout(150);

  const toolsSameNode = await page.evaluate(() =>
    window.__toolsNode === document.getElementById("openIndicators"));

  check(toolsSameNode,
    "левый сайдбар пересобрался на неизменившемся снимке: узел кнопки «Монитор " +
    "показателей» подменён, значит сбрасывается :hover и кнопки «мерцают»");

  // --- 7. Изменение содержимого левого сайдбара обязано применяться ---
  //
  // Обратная сторона: «не пересобирать» не должно превратиться в «не обновлять».
  // Событие Runtime видно в сайдбаре, поэтому смена события — настоящая правка.
  const runtimeChanged = makeSnapshot({ __sidebarSections: confirmed.__sidebarSections });
  runtimeChanged.system = { ...runtimeChanged.system, lastEvent: "HornPressed" };

  await pushSnapshot(runtimeChanged);
  await page.waitForTimeout(150);

  const eventShown = await page.evaluate(() =>
    (document.getElementById("runtimeSide")?.textContent || "").includes("HornPressed"));

  check(eventShown,
    "новое событие Runtime не показано в левом сайдбаре: «не пересобирать» " +
    "превратилось в «не обновлять вовсе»");

  // --- 8. Кнопки инструментов ЖИВУТ через пересборку ---
  //
  // Даже когда содержимое сайдбара действительно меняется, узлы кнопок обязаны
  // остаться ТЕМИ ЖЕ. Раздел «Инструменты» статичен, поэтому он не пересоздаётся
  // вовсе: элемент лишь возвращается на первое место. Это последний рубеж против
  // мерцания — при пересборке содержимого узел не должен «мигнуть».
  const toolsSurvivedContentChange = await page.evaluate(() =>
    window.__toolsNode === document.getElementById("openIndicators") &&
    document.getElementById("runtimeSide").firstElementChild ===
      document.getElementById("openIndicators")?.closest("section"));

  check(toolsSurvivedContentChange,
    "кнопки «Инструменты» пересозданы при пересборке содержимого сайдбара или " +
    "потеряли первое место — на этом и было мерцание");

  // --- 9. Кнопка под курсором переживает СМЕНУ ДАННЫХ ---
  //
  // Главный дефект автора: «чуть реже мерцает любая кнопка в окне симулятора.
  // Мерцание совпадает со сменой координат игрока в разделе с координатами».
  //
  // Предикат «разметка изменилась» здесь бессилен: координаты меняются КАЖДУЮ
  // секунду, то есть разметка меняется на каждом обновлении, и прежняя подпись
  // всё равно пересобирала side.innerHTML целиком. Проверяем поэтому САМ УЗЕЛ
  // рядом с полем координаты: при смене числа X меняться должен ТОЛЬКО он, а
  // кнопки остаются теми же элементами и не «мигают».
  const coord1 = makeSnapshot({ __sidebarSections: ["player"] });
  await pushSnapshot(coord1);
  await page.waitForTimeout(150);

  await page.evaluate(() => {
    window.__coordField = document.querySelector("#side [data-player='x']");
    window.__nearbyButton = document.querySelector("#side [data-section='player'] button, #side #routeToggle");
  });

  const coord2 = makeSnapshot({ __sidebarSections: ["player"] });
  coord2.player = { ...coord2.player, position: { x: 1234, y: 5, z: -7 }, speedKmh: 63 };
  await pushSnapshot(coord2);
  await page.waitForTimeout(150);

  const coordSync = await page.evaluate(() => ({
    valueShown: document.querySelector("#side [data-player='x']")?.value,
    fieldKept: window.__coordField === document.querySelector("#side [data-player='x']"),
    buttonKept: window.__nearbyButton ===
      document.querySelector("#side [data-section='player'] button, #side #routeToggle")
  }));

  check(String(coordSync.valueShown) === "1234",
    "координата игрока не обновилась в сайдбаре при смене снимка: " +
    coordSync.valueShown);
  check(coordSync.fieldKept,
    "поле координаты подменено при смене ЧИСЛА: значит подменяются и кнопки, а под " +
    "курсором это и есть мерцание (автор: «мерцание совпадает со сменой координат»)");
  check(coordSync.buttonKept,
    "кнопка рядом с координатой подменена при смене числа — это и есть мерцание " +
    "кнопок в окне симулятора");

  // --- 10. Обработчик не удваивается при переиспользовании узлов ---
  //
  // Сверка узлов ПЕРЕИСПОЛЬЗУЕТ элементы, поэтому подписка на сами узлы после
  // каждой отрисовки дала бы ДВА обработчика на кнопке и два одинаковых
  // сообщения в Host на одно нажатие. Проверяем: одно нажатие — одно сообщение.
  // Раздел «Движение по маршруту» раскрыт: кнопка внутри закрытого раздела не
  // видна, и клик по ней был бы не проверкой, а таймаутом.
  const pressable = makeSnapshot({ __sidebarSections: ["player", "route"] });
  await pushSnapshot(pressable);
  await page.waitForTimeout(150);
  await page.evaluate(() => { window.__sent = []; });
  await pushSnapshot(pressable);
  await page.waitForTimeout(150);
  await pushSnapshot(pressable);
  await page.waitForTimeout(150);
  await page.locator("#side #routeToggle").click();
  await page.waitForTimeout(120);

  const singleClick = await page.evaluate(() =>
    (window.__sent || []).filter(m => m.action === "route_toggle").length);

  check(singleClick === 1,
    "одно нажатие кнопки отправило Host " + singleClick + " сообщений вместо 1: " +
    "обработчик навешен дважды на переиспользованный узел");

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
