import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка маркера игрока на карте Simulator:
//   1. красная обводка + чёрная обводка снаружи неё;
//   2. смещённая вниз тень под маркером;
//   3. оранжевый перекрест во всю карту, проходящий через игрока.
// Canvas не даёт инспектировать DOM, поэтому проверяем реальные пиксели.

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

const simulatorJs = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "simulator.js"),
  "utf8"
);

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>
        body{margin:0;background:#0d1014}
        #mapCanvas{display:block;width:900px;height:640px}
        .hidden{display:none}
      </style></head>
      <body>
        <div id="hud" class="hidden"></div>
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

  const points = [
    { id: "sdo:a", name: "Склад", category: "cargo", position: { x: 120, y: 0, z: 160 }, color: "#78c8f0", triggerRadius: 35, editable: false },
    { id: "sdo:b", name: "Руслан", category: "quest", position: { x: -260, y: 0, z: -90 }, color: "#ff7a50", triggerRadius: 35, editable: false },
    { id: "sdo:c", name: "Заправка", category: "fuel", position: { x: 340, y: 0, z: -210 }, color: "#8ad07a", triggerRadius: 35, editable: false }
  ];

  const snapshot = {
    player: { position: { x: 40, y: 12, z: 60 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
    world: { coordinateSystem: "ETS2 X/Y/Z", points, activeLocation: "test" },
    selection: { point: null, source: "" },
    facts: { values: {} },
    questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
    playerProgress: { money: 1500, experience: 0, reserve: 0 },
    character: { stats: { strength: 5 }, skills: [] },
    inventory: { items: {}, newItemIds: [] },
    reputation: { entries: { gosha: { npcId: "gosha", value: 400, contacted: true } } },
    npcCatalog: [{ id: "gosha", name: "Гоша", avatar: "data/images/avatar_placeholder.png", speakerAliases: ["Гоша"] }],
    reputationViews: { gosha: { value: 400, valueLabel: "+400", rangeName: "Неопасный", rangeColor: "#00a7bd", fillColor: "#44ff00", progressPercent: 4, tooltip: "Репутация: +400 из 10000 (Неопасный)" } },
    telemetry: { speedKmh: 0, engineRpm: 0, throttle: 0, brake: 0, steering: 0, fuelPercent: 100, engineTemperature: 90, cabinTemperature: 20, damageCabPercent: 0, damageEnginePercent: 0, damageTransmissionPercent: 0, damageWheelPercent: 0, hornPressed: false },
    environment: { weather: "clear", rainPercent: 0, gameTime: "12:00", visibilityMeters: 10000 },
    system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
  };

  await page.evaluate(snapshotValue => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({ type: "snapshot", version: "test", snapshot: snapshotValue })
    });
  }, snapshot);

  await page.waitForTimeout(300);

  const probe = await page.evaluate(() => {
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    const image = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;

    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [image[i], image[i + 1], image[i + 2], image[i + 3]];
    };

    const isCrosshair = (x, y) => {
      const [r, g, b] = at(x, y);
      return r > 26 && r < 60 && g > 20 && g < 50 && b < 30;
    };

    // Оранжевая заливка маркера (#f59e0b) — берём bounding box для центра.
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let y = 2; y < h - 2; y++) {
      for (let x = 2; x < w - 2; x++) {
        const [r, g, b] = at(x, y);
        if (r > 230 && g > 140 && g < 180 && b < 40) {
          if (x < minX) minX = x;
          if (x > maxX) maxX = x;
          if (y < minY) minY = y;
          if (y > maxY) maxY = y;
        }
      }
    }

    const player = Number.isFinite(minX)
      ? { x: (minX + maxX) / 2, y: (minY + maxY) / 2 }
      : null;

    // Перекрест ищем по верхнему/левому краю карты.
    let vlineX = null;
    for (let x = 2; x < w - 2; x++) if (isCrosshair(x, 30)) { vlineX = x; break; }
    let hlineY = null;
    for (let y = 2; y < h - 2; y++) if (isCrosshair(30, y)) { hlineY = y; break; }


    // Полукольцо (side -1 сверху, +1 снизу): медиана яркости. Сетка и кольца
    // дистанции радиально симметричны, поэтому их вклад взаимно сокращается,
    // и разница изолирует смещённую вниз тень.
    const annulusMedian = (side) => {
      const values = [];
      for (let deg = 0; deg < 360; deg += 6) {
        const rad = deg * Math.PI / 180;
        const dy = Math.sin(rad);
        if (side < 0 ? dy > 0.25 : dy < -0.25) continue;
        for (const radius of [15, 19, 23]) {
          const x = player.x + Math.cos(rad) * radius;
          const y = player.y + dy * radius;
          if (x < 0 || y < 0 || x >= w || y >= h) continue;
          const s = at(x, y);
          values.push(s[0] * 0.299 + s[1] * 0.587 + s[2] * 0.114);
        }
      }
      if (!values.length) return null;
      values.sort((a, b) => a - b);
      return Math.round(values[Math.floor(values.length / 2)] * 10) / 10;
    };

    // Радиальный профиль: медианный цвет на каждом радиусе от центра маркера.
    // Позволяет проверить порядок слоёв (заливка → красная → чёрная → фон),
    // а не просто наличие отдельных цветов рядом.
    const radialProfile = () => {
      const profile = [];
      for (let radius = 0; radius <= 24; radius += 0.5) {
        const reds = [], greens = [], blues = [];
        for (let deg = 0; deg < 360; deg += 4) {
          const rad = deg * Math.PI / 180;
          const x = player.x + Math.cos(rad) * radius;
          const y = player.y + Math.sin(rad) * radius;
          if (x < 0 || y < 0 || x >= w || y >= h) continue;
          const s = at(x, y);
          reds.push(s[0]); greens.push(s[1]); blues.push(s[2]);
        }
        if (!reds.length) continue;
        const median = arr => {
          arr.sort((a, b) => a - b);
          return arr[Math.floor(arr.length / 2)];
        };
        profile.push({
          radius,
          r: median(reds),
          g: median(greens),
          b: median(blues)
        });
      }
      return profile;
    };

    const profile = player ? radialProfile() : [];

    // Классификация полосы: orange (заливка), red (обводка), black (обводка).
    // Фон карты #0d1014 = [13,16,20], а чёрная обводка rgba(0,0,0,.95) поверх
    // него даёт ~[1,1,1], поэтому порог «чёрного» должен быть ниже фона —
    // иначе фон сам классифицируется как обводка.
    const bandOf = band =>
      band.r > 200 && band.g > 120 && band.g < 200 && band.b < 70 ? "orange"
        : band.r > 120 && band.g < 120 && band.b < 120 ? "red"
          : band.r < 8 && band.g < 8 && band.b < 8 ? "black"
            : "other";

    // Сжимаем профиль в последовательность полос, убирая повторы.
    const bands = [];
    for (const band of profile) {
      const kind = bandOf(band);
      if (bands.length && bands[bands.length - 1].kind === kind) {
        bands[bands.length - 1].end = band.radius;
        continue;
      }
      bands.push({ kind, start: band.radius, end: band.radius });
    }

    return {
      player,
      vlineX,
      hlineY,
      fill: player ? at(player.x, player.y) : null,
      bands: bands.map(b => b.kind + ":" + b.start + "-" + b.end),
      crosshairFill: vlineX === null ? null : at(vlineX, 30),
      offLine: vlineX === null ? null : at(vlineX + 25, 30),
      crosshairThroughV: player && vlineX !== null ? Math.abs(player.x - vlineX) <= 2 : null,
      crosshairThroughH: player && hlineY !== null ? Math.abs(player.y - hlineY) <= 2 : null,
      shadowAbove: player ? annulusMedian(-1) : null,
      shadowBelow: player ? annulusMedian(1) : null
    };
  });

  check(probe.player !== null, "Маркер игрока не найден на карте.");
  check(probe.vlineX !== null, "Вертикальная линия перекреста не найдена.");
  check(probe.hlineY !== null, "Горизонтальная линия перекреста не найдена.");

  if (probe.player) {
    // 1. Заливка — оранжевая.
    const fill = probe.fill;
    check(
      fill && fill[0] > 220 && fill[1] > 130 && fill[1] < 190 && fill[2] < 60,
      "Заливка маркера игрока должна остаться оранжевой: " + JSON.stringify(fill)
    );

    // 2. Порядок слоёв по радиусу: orange → red → black → фон.
    // Проверяем именно последовательность, а не отдельные цвета: прежний blur
    // тоже давал тёмные пиксели рядом с маркером.
    const kinds = probe.bands.map(band => band.split(":")[0]);
    const orangeIndex = kinds.indexOf("orange");
    const redIndex = kinds.indexOf("red");
    const blackIndex = kinds.indexOf("black");

    check(
      orangeIndex >= 0,
      "Заливка маркера не найдена в радиальном профиле: " + probe.bands.join(", ")
    );
    check(
      redIndex > orangeIndex,
      "Красная обводка должна идти сразу после заливки: " + probe.bands.join(", ")
    );
    check(
      blackIndex > redIndex,
      "Чёрная обводка должна идти снаружи красной: " + probe.bands.join(", ")
    );

    // 3. Тень смещена вниз: область под маркером темнее области над ним.
    check(
      probe.shadowAbove !== null && probe.shadowBelow !== null &&
        probe.shadowBelow < probe.shadowAbove,
      "Тень не смещена вниз: above=" + probe.shadowAbove + ", below=" + probe.shadowBelow
    );
  }

  // 5. Перекрест проходит точно через игрока.
  check(probe.crosshairThroughV === true, "Вертикальная линия не проходит через игрока.");
  check(probe.crosshairThroughH === true, "Горизонтальная линия не проходит через игрока.");

  // 6. Линии оранжевые и почти прозрачные.
  const line = probe.crosshairFill;
  check(
    line && line[0] > 26 && line[0] < 60 && line[1] > 20 && line[1] < 50 && line[2] < 30,
    "Линии перекреста должны быть приглушённо-оранжевыми: " + JSON.stringify(line)
  );
  check(
    probe.offLine && Math.abs(probe.offLine[0] - 13) <= 2,
    "Линии перекреста должны быть почти прозрачными (рядом должен быть фон): " +
      JSON.stringify(probe.offLine)
  );

  check(pageErrors.length === 0, "pageerror при отрисовке маркера: " + pageErrors.join(" | "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.error("Player marker smoke: FAIL");
  for (const failure of failures) console.error("- " + failure);
  process.exit(1);
}

console.log("Player marker smoke: OK");
