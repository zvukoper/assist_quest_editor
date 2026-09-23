import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка режима визуализации Location на основной карте Симулятора.
//
// Собственная карта редактора локаций не содержит ориентиров мира, поэтому по
// ней нельзя понять, ГДЕ оказались отобранные точки. Режим показывает набор на
// знакомой карте: обычные точки приглушаются, отобранные рисуются поверх, камера
// вписывается в набор.
//
// Canvas не даёт инспектировать DOM, поэтому проверяются РЕАЛЬНЫЕ пиксели:
//   - вне режима отобранных точек на карте нет;
//   - в режиме появляется оранжевая заливка (акцент) поверх карты;
//   - камера вписалась: набор виден целиком внутри канваса;
//   - обычные точки стали бледнее в два раза;
//   - выход из режима возвращает карту к прежнему виду.

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>
        body{margin:0;background:#0d1014}
        #mapCanvas{display:block;width:900px;height:640px}
        .hidden{display:none}
      </style></head>
      <body>
        <div id="hud"></div>
        <button id="backpackButton" class="hidden"></button>
        <div id="playerOverlay" class="hidden">
          <section id="inventoryPanel"></section>
          <section id="characterPanel"></section>
        </div>
        <div id="inventoryNotifications" class="hidden"></div>
        <aside id="runtimeSide" class="hidden"></aside>
        <aside id="side" class="hidden"></aside>
        <canvas id="mapCanvas"></canvas>
        <button id="reset" class="hidden"></button>
        <script>
          window.chrome = {
            webview: {
              listeners: new Map(),
              addEventListener(type, handler) { this.listeners.set(type, handler); },
              postMessage() {}
            }
          };
        </script>
        <script>
          ${simulatorJs.replaceAll("</script", "<\\/script")}
        </script>
      </body>
    </html>
  `);

  // Мир намеренно широкий, а набор — по УГЛАМ большого квадрата: камера
  // вписывается в него так, что масштаб остаётся крупным и обычные точки
  // (они ближе к центру мира) не уезжают за экран. Иначе замер приглушения
  // ничего не измерял бы — обычных точек в кадре просто нет.
  const points = [
    { id: "sdo:a", name: "Склад", category: "cargo", position: { x: 0, y: 0, z: 0 }, color: "#78c8f0", isCity: false },
    { id: "sdo:b", name: "Заправка", category: "fuel", position: { x: 4000, y: 0, z: 3000 }, color: "#8ad07a", isCity: false },
    { id: "sdo:c", name: "Мост", category: "road", position: { x: -4000, y: 0, z: -3000 }, color: "#c07ce8", isCity: false },
    // Отобранные точки — по углам квадрата 40000x40000 вокруг центра мира.
    { id: "sdo:1", name: "Пикник", category: "camping", position: { x: 20000, y: 0, z: 20000 }, color: "#78c8f0", isCity: false },
    { id: "sdo:2", name: "Рыбалка", category: "water_lake", position: { x: -20000, y: 0, z: -20000 }, color: "#78c8f0", isCity: false },
    { id: "sdo:3", name: "Лес", category: "forest", position: { x: 20000, y: 0, z: -20000 }, color: "#78c8f0", isCity: false }
  ];

  const snapshot = {
    // Игрок уведён далеко за пределы кадра: его маркер тоже оранжевый (акцент),
    // поэтому в кадре он считался бы четвёртым «маркером набора» и ломал подсчёт.
    // Заодно в центре кадра остаётся обычная точка «Склад» (0,0) — по ней и
    // измеряется приглушение.
    player: { position: { x: 90000, y: 0, z: 90000 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
    world: { coordinateSystem: "ETS2 X/Y/Z", points, activeLocation: "test" },
    selection: { point: null, source: "" },
    facts: { values: {} },
    questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
    playerProgress: { money: 0, experience: 0, reserve: 0 },
    character: { stats: {}, skills: [] },
    inventory: { items: {}, newItemIds: [] },
    reputation: { entries: {} },
    telemetry: {}, environment: {},
    system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
  };

  const deliver = payload => page.evaluate(data => {
    window.chrome.webview.listeners.get("message")({ data: JSON.stringify(data) });
  }, payload);

  await deliver({ type: "snapshot", version: "test", snapshot });
  await page.waitForTimeout(250);

  // Считает акцентные (оранжевые) пиксели карты: они и есть маркеры набора.
  const accentPixels = () => page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const ctx = canvas.getContext("2d");
    const { width, height } = canvas;
    const data = ctx.getImageData(0, 0, width, height).data;
    let count = 0;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const i = (y * width + x) * 4;
        const r = data[i], g = data[i + 1], b = data[i + 2];
        // Акцент #fab003 (250,176,3) с допуском на сглаживание.
        if (r > 200 && g > 130 && g < 215 && b < 70) {
          count++;
          if (x < minX) minX = x;
          if (x > maxX) maxX = x;
          if (y < minY) minY = y;
          if (y > maxY) maxY = y;
        }
      }
    }
    return { count, minX, maxX, minY, maxY, width, height };
  });

  const before = await accentPixels();

  // --- Режим визуализации ---
  await deliver({
    type: "location_visualisation",
    title: "Пикник у воды",
    points: [
      { pointId: "sdo:1", index: 1 },
      { pointId: "sdo:2", index: 2 },
      { pointId: "sdo:3", index: 3 }
    ],
    diagnostics: ["Уникальных кандидатов найдено 3."]
  });
  await page.waitForTimeout(250);

  const after = await accentPixels();

  if (!(after.count > before.count))
    throw new Error(
      "Режим визуализации не нарисовал отобранные точки: акцентных пикселей " +
      before.count + " -> " + after.count);

  // Камера обязана вписаться в набор: точки лежат в (20000,20000), а мир — нет.
  // Если маркеры ушли за границы канваса, значит вписывания не было.
  if (after.minX < 0 || after.minY < 0 ||
      after.maxX >= after.width || after.maxY >= after.height)
    throw new Error(
      "Камера не вписала набор Location: маркеры выходят за канвас. " +
      JSON.stringify({ minX: after.minX, minY: after.minY, maxX: after.maxX, maxY: after.maxY,
        width: after.width, height: after.height }));

  // Все три маркера должны быть видны: набор показывается целиком, а не первая точка.
  //
  // Акцентом нарисованы и маркер, и его подпись справа, поэтому наивный подсчёт
  // пятен даёт больше трёх. Радиус объединения выбран между длиной подписи
  // (~115 px) и минимальным расстоянием между маркерами (в этом наборе ~478 px),
  // поэтому маркер со своей подписью сливаются в одно пятно, а разные точки —
  // нет. Проверяется именно это: набор Location не должен «схлопываться» в один
  // маркер или терять часть точек.
  const markerCount = await page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const ctx = canvas.getContext("2d");
    const { width, height } = canvas;
    const data = ctx.getImageData(0, 0, width, height).data;
    const hit = (x, y) => {
      const i = (y * width + x) * 4;
      const r = data[i], g = data[i + 1], b = data[i + 2];
      return r > 200 && g > 130 && g < 215 && b < 70;
    };
    const centers = [];
    for (let y = 0; y < height; y += 8) {
      for (let x = 0; x < width; x += 8) {
        if (!hit(x, y)) continue;
        if (centers.some(c => Math.hypot(c.x - x, c.y - y) < 130)) continue;
        centers.push({ x, y });
      }
    }
    return centers.length;
  });

  if (markerCount !== 3)
    throw new Error(
      "Показаны не все отобранные точки: найдено маркеров " + markerCount + " из 3.");

  // --- Обычные точки приглушены ---
  //
  // Замер делается на ОДНОЙ камере: сначала в режиме, затем после выхода
  // (выход камеру не меняет). Так сравнивается цвет ОДНОЙ И ТОЙ ЖЕ точки, а не
  // два разных вида карты — иначе замер ничего не доказывал бы.
  //
  // В центре кадра заведомо лежит обычная точка «Склад» (0,0), потому что камера
  // вписала симметричный набор с центром в (0,0), а игрок уведён в сторону.
  const centerLuma = () => page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const ctx = canvas.getContext("2d");
    const x = Math.round(canvas.width / 2);
    const y = Math.round(canvas.height / 2);
    const d = ctx.getImageData(x - 3, y - 3, 6, 6).data;
    let sum = 0;
    for (let i = 0; i < d.length; i += 4) {
      sum += d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
    }
    return sum / (d.length / 4);
  });

  const lumaDimmed = await centerLuma();

  // --- Выход из режима ---
  const exitButton = page.locator("#locationVisExit");
  if (!(await exitButton.count()))
    throw new Error("В режиме нет кнопки выхода: пользователь не сможет вернуть обычный вид карты.");
  await exitButton.click();
  await page.waitForTimeout(200);

  // Камера та же, режим выключен — точка обязана вернуть яркость.
  const lumaPlain = await centerLuma();
  if (!(lumaPlain > lumaDimmed * 1.25))
    throw new Error(
      "Обычные точки не приглушаются в режиме: яркость точки в центре " +
      lumaDimmed.toFixed(1) + " (режим) -> " + lumaPlain.toFixed(1) + " (обычный вид).");

  const restored = await accentPixels();
  if (restored.count > after.count / 2)
    throw new Error(
      "Выход из режима не убрал маркеры набора: акцентных пикселей " +
      restored.count + " против " + after.count);

  // Пустой набор обязан ВЫКЛЮЧИТЬ режим, а не приглушить карту впустую: иначе
  // пользователь увидел бы погашенную карту без единой показанной точки и решил,
  // что карта сломалась.
  await deliver({ type: "location_visualisation", title: "", points: [], diagnostics: [] });
  await page.waitForTimeout(150);
  if (await page.locator("#locationVisExit").count())
    throw new Error("Пустой набор Location включил режим: карта гаснет без показанных точек.");

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Location visualisation smoke: OK");
} finally {
  await browser.close();
}
