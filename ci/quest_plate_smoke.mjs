// Читаемость текста на плашке квеста в Симуляторе.
//
// Симптом, который здесь сторожится: название квеста на карте выглядит чёрным
// и «выключенным». Причина была в сочетании цветов: фон плашки задавался как
// акцентный оранжевый с прозрачностью .25, а текст — чистым чёрным (#000000).
// Полупрозрачный оранжевый смешивается с тёмной картой и даёт почти чёрный фон,
// поэтому чёрный текст на нём не читается.
//
// Проверка меряет не константы, а РЕАЛЬНЫЕ пиксели canvas: текст должен быть
// светлее фона плашки на заметную величину. Так ломается и смена цвета текста,
// и смена прозрачности фона — то есть обе половины бага.
import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

const simulatorJs = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "simulator.js"),
  "utf8"
);

const { browser } = await openBrowser();

try {
  const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>
        html,body{height:100%;margin:0;overflow:hidden;background:#0d1014}
        #mapCanvas{display:block;width:900px;height:700px}
      </style></head>
      <body>
        <header><div id="hud"></div>
          <button id="openCampaigns"></button>
          <button id="reloadCatalog"></button>
          <button id="simulationToggle"></button>
          <button id="reset"></button>
        </header>
        <main style="display:flex">
          <aside id="runtimeSide"></aside>
          <section id="mapWrap" style="width:900px;height:700px">
            <canvas id="mapCanvas"></canvas>
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

  // Радиус 0 — у квеста не рисуется пунктирная зона триггера. Это важно:
  // её цвет тоже акцентный, и она сбила бы поиск маркера по цвету.
  const pushSnapshot = (active, title) => page.evaluate(({ active, title }) => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
          world: {
            coordinateSystem: "ETS2 X/Y/Z",
            categories: [],
            points: [{
              id: "sdo:cache",
              name: "Тайник",
              category: "camping",
              position: { x: 0, y: 0, z: 0 },
              color: "#5fb0ff",
              triggerRadius: 35,
              editable: false
            }]
          },
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
        questCatalog: [{
          campaignId: "sibirmap",
          campaignName: "SibirMap",
          quests: [{
            questId: "sibirmap_city_cache",
            questTitle: title,
            worldPointId: "sdo:cache",
            // Радиус 0: зона триггера не рисуется, маркер ищется по цвету.
            radius: 0,
            status: "Available",
            statusLabel: "Доступен",
            step: "available",
            stepTitle: "Доступен",
            active: active,
            runtimeActive: false,
            order: 1,
            fileName: "sibirmap_city_cache.aqquest"
          }]
        }],
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
  }, { active, title });

  /**
   * Меряет контраст текста на плашке.
   *
   * Маркер квеста ищется по акцентному цвету (ромб), плашка лежит прямо под ним
   * и центрирована по горизонтали, поэтому область текста известна без DOM.
   */
  const measurePlate = title => page.evaluate((title) => {
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const context = canvas.getContext("2d");
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;

    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [pixels[i], pixels[i + 1], pixels[i + 2]];
    };
    const luma = ([r, g, b]) => r * 0.299 + g * 0.587 + b * 0.114;

    // 1. Центр маркера = центр акцентного ромба.
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const [r, g, b] = at(x, y);
        // #fab003 и его тень; текст плашки (#ffce6a) исключён по g < 190.
        if (r > 225 && g > 150 && g < 190 && b < 60) {
          if (x < minX) minX = x;
          if (x > maxX) maxX = x;
          if (y < minY) minY = y;
          if (y > maxY) maxY = y;
        }
      }
    }

    if (!Number.isFinite(minX)) return { error: "маркер квеста не найден на canvas" };

    const marker = { x: (minX + maxX) / 2, y: (minY + maxY) / 2 };

    // 2. Область ПЕРВОЙ строки названия, узко и намеренно.
    //
    //   плашка: сверху marker.y + 16 (радиус 8 + отступ 8), слева marker.x - 60
    //   padding 6px, строка названия 12px, текст выровнен по левому краю.
    //
    // Окно берётся с запасом внутрь строки и не доходит до краёв плашки и до
    // разделителя (он лежит на marker.y + 37). Раньше окно было шире, и замер
    // ловил оранжевую рамку с разделителем — из-за этого «чёрный текст на
    // тёмной плашке» ошибочно проходил проверку контраста.
    const textLeft = marker.x - 54;
    const textRight = marker.x + 46;
    const textTop = marker.y + 23;
    const textBottom = marker.y + 31;

    const band = [];
    for (let y = Math.max(0, Math.round(textTop)); y <= Math.min(h - 1, Math.round(textBottom)); y++) {
      for (let x = Math.max(0, Math.round(textLeft)); x <= Math.min(w - 1, Math.round(textRight)); x++) {
        band.push(luma(at(x, y)));
      }
    }

    if (!band.length) return { error: "область плашки вне canvas" };

    // Фон плашки — мода распределения: заливка однородная, а глифы занимают
    // лишь часть строки, поэтому самый частый уровень яркости и есть фон.
    const histogram = new Map();
    for (const value of band) {
      const bucket = Math.round(value / 4) * 4;
      histogram.set(bucket, (histogram.get(bucket) || 0) + 1);
    }
    let background = 0;
    let backgroundHits = -1;
    for (const [bucket, hits] of histogram) {
      if (hits > backgroundHits) {
        backgroundHits = hits;
        background = bucket;
      }
    }

    const sorted = band.slice().sort((a, b) => a - b);
    const brightest = sorted[sorted.length - 1];
    const lightPixels = sorted.filter(value => value >= 120).length;

    return {
      marker,
      title,
      background: Math.round(background * 10) / 10,
      brightest: Math.round(brightest * 10) / 10,
      lightPixels,
      contrast: Math.round((brightest - background) * 10) / 10
    };
  }, title);

  // Активный квест: включён в кампании. Именно его плашку видел пользователь.
  await pushSnapshot(true, "Тайник в Челябинске");
  await page.waitForTimeout(250);

  const active = await measurePlate("Тайник в Челябинске");
  if (active.error) {
    check(false, "Активный квест: " + active.error);
  } else {
    check(
      active.lightPixels >= 20,
      "На плашке активного квеста почти нет светлых пикселей текста (" +
      active.lightPixels + "): текст сливается с фоном. " +
      "фон=" + active.background + " яркость=" + active.brightest
    );
    check(
      active.contrast >= 60,
      "Контраст текста активного квеста слишком низкий: " + active.contrast +
      " (фон=" + active.background + ", текст=" + active.brightest + ")"
    );
  }

  // Неактивный квест обязан оставаться читаемым: он не исчезает с карты.
  await pushSnapshot(false, "Отключённый тайник");
  await page.waitForTimeout(250);

  const inactive = await measurePlate("Отключённый тайник");
  if (inactive.error) {
    check(false, "Неактивный квест: " + inactive.error);
  } else {
    check(
      inactive.lightPixels >= 20,
      "На плашке неактивного квеста почти нет светлых пикселей текста (" +
      inactive.lightPixels + "). фон=" + inactive.background +
      " яркость=" + inactive.brightest
    );
    check(
      inactive.contrast >= 60,
      "Контраст текста неактивного квеста слишком низкий: " + inactive.contrast +
      " (фон=" + inactive.background + ", текст=" + inactive.brightest + ")"
    );
  }

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));

  if (!failures.length) {
    console.log(
      "Плашка квеста: OK active(фон=" + active.background + ", текст=" + active.brightest +
      ", контраст=" + active.contrast + ") inactive(фон=" + inactive.background +
      ", текст=" + inactive.brightest + ", контраст=" + inactive.contrast + ")"
    );
  }
} finally {
  await closeBrowser(browser);
}

if (failures.length) {
  console.log("Плашка квеста: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}
