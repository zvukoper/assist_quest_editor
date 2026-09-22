// Обводка текста на карте не должна давать «пиков» над буквами.
//
// Симптом: из вершин букв («М», «А», «Ц») выходят чёрные шипы, заметно длиннее
// самой обводки. Причина — canvas по умолчанию соединяет линии «митрой»:
// lineJoin="miter" при miterLimit=10. На острых углах глифов митра вытягивается
// далеко за пределы буквы, и при толщине обводки 4.5px шип становится длиннее
// символа. Лечится lineJoin="round" + miterLimit=2.
//
// Проверка меряет не настройки, а РЕАЛЬНЫЕ пиксели: сколько чёрного выходит за
// верхнюю границу глифов. Так ловится и смена соединения, и рост толщины
// обводки, и появление нового прямого вызова strokeText без помощника.
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

const simulatorJs = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "simulator.js"),
  "utf8"
);

// 1. Ни одна обводка текста не рисуется напрямую.
//
// Именно прямой вызов и давал «пики»: помощник ограничивает соединение, а
// обходной вызов нет. Ищем вызовы strokeText, кроме единственного в помощнике.
// «strokeTextOutline(...)» под шаблон не подпадает: после strokeText идёт
// «Outline», а не «(».
const stroketextCalls = simulatorJs
  .split("\n")
  .map((line, index) => ({ line: index + 1, text: line.trim() }))
  .filter(item => /strokeText\s*\(/.test(item.text) && !/^(\/\/|\*)/.test(item.text));

check(
  stroketextCalls.length === 1,
  "Обводка текста должна идти только через strokeTextOutline (найдено вызовов strokeText: " +
  stroketextCalls.length + "): " + stroketextCalls.map(item => item.line).join(", ")
);

check(
  /function strokeTextOutline\(ctx, text, x, y\)/.test(simulatorJs) &&
  /ctx\.lineJoin = "round"/.test(simulatorJs) &&
  /ctx\.miterLimit = 2/.test(simulatorJs),
  "strokeTextOutline обязан задавать lineJoin=\"round\" и miterLimit=2."
);

const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>
        html,body{height:100%;margin:0;overflow:hidden;background:#0d1014}
        #mapCanvas{display:block;width:800px;height:600px}
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
          <section id="mapWrap" style="width:800px;height:600px">
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

  // Точка с названием из букв с острыми вершинами: именно они давали «пики».
  // Ярлык СДО рисуется обводкой толщиной 4.5px — самым толстым случаем.
  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 4000, y: 0, z: 4000 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
          world: {
            coordinateSystem: "ETS2 X/Y/Z",
            categories: [],
            points: [{
              id: "sdo:men",
              name: "ММАЦ",
              category: "cargo",
              position: { x: 0, y: 0, z: 0 },
              color: "#c07ce8",
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
        questCatalog: [],
        runtime: { questId: "", currentNodeId: null, status: "Stopped", waitingFor: "", message: "" },
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

  // Камера ставится на точку: после первого снимка карта зовёт fitWorld(),
  // поэтому попадание в нужную точку проще гарантировать масштабом и центром.
  await page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    // Клик по точке центрирует и выделяет её: подпись становится крупнее.
    const rect = canvas.getBoundingClientRect();
    canvas.dispatchEvent(new PointerEvent("pointerdown", {
      bubbles: true, clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2, button: 0
    }));
    canvas.dispatchEvent(new PointerEvent("pointerup", {
      bubbles: true, clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2, button: 0
    }));
  });
  await page.waitForTimeout(350);

  const probe = await page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    const pixels = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;

    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [pixels[i], pixels[i + 1], pixels[i + 2]];
    };
    const luma = ([r, g, b]) => r * 0.299 + g * 0.587 + b * 0.114;

    // Глифы ищутся по СВОЕМУ цвету, а не по яркости: подпись СДО рисуется
    // darkenColor(point.color), то есть #c07ce8 → #906dae. Яркостный порог
    // цеплял сетку, тень точки и рамку карты и давал bbox в весь канвас.
    const glyphHit = ([r, g, b]) =>
      Math.abs(r - 144) < 24 && Math.abs(g - 109) < 24 && Math.abs(b - 174) < 24;

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        if (!glyphHit(at(x, y))) continue;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }

    if (!Number.isFinite(minX)) return { error: "подпись СДО не найдена на canvas" };

    const glyph = { left: minX, top: minY, right: maxX, bottom: maxY };
    const glyphWidth = glyph.right - glyph.left;
    const glyphHeight = glyph.bottom - glyph.top;

    // «Пик» — узкий чёрный выступ НАД верхней границей глифов.
    //
    // Фон карты #0d1014 имеет яркость ≈ 16, обводка — rgba(0,0,0,.98), то есть
    // почти 0. Порог 8 отделяет обводку от фона. Обводка толщиной 4.5px
    // обходит контур глифа, поэтому нормальный выступ — 2-3px; митра давала
    // шипы в разы длиннее. Узость (не шире 3px) важна: широкая полоса означала
    // бы, что замер поймал не шип, а что-то другое.
    let spikeHeight = 0;
    for (let y = Math.max(0, Math.round(glyph.top) - 14); y < Math.round(glyph.top); y++) {
      let darkInRow = 0;
      for (let x = Math.round(glyph.left) - 2; x <= Math.round(glyph.right) + 2; x++) {
        if (luma(at(x, y)) < 8) darkInRow++;
      }
      if (darkInRow > 0 && darkInRow <= 3) {
        spikeHeight = Math.max(spikeHeight, Math.round(glyph.top) - y);
      }
    }

    return {
      glyph: { width: glyphWidth, height: glyphHeight },
      spikeHeight
    };
  });

  if (probe.error) {
    check(false, probe.error);
  } else {
    check(
      probe.glyph.width > 10 && probe.glyph.height > 6,
      "Подпись СДО слишком мала для замера: " + JSON.stringify(probe.glyph)
    );
    check(
      probe.spikeHeight <= 4,
      "Над буквами чёрные «пики» высотой " + probe.spikeHeight +
      "px: обводка текста использует митру вместо скруглённого соединения."
    );
  }

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));

  if (!failures.length) {
    console.log(
      "Обводка текста: OK глиф=" + probe.glyph.width + "x" + probe.glyph.height +
      " выступ над буквами=" + probe.spikeHeight + "px"
    );
  }
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Обводка текста: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}
