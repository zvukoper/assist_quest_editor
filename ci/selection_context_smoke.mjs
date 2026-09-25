import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

const editorForm = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Host", "EditorForm.cs"),
  "utf8"
);
check(
  editorForm.includes("PropertyNamingPolicy = JsonNamingPolicy.CamelCase"),
  "EditorForm должен сериализовать simulator_context/coordinate в camelCase."
);

const simulatorJs = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "simulator.js"),
  "utf8"
);

const { browser } = await openBrowser();

try {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <body style="margin:0">
        <header><div id="hud"></div></header>
        <main style="display:flex">
          <section id="mapWrap" style="width:900px;height:700px">
            <canvas id="mapCanvas" style="width:900px;height:700px"></canvas>
          </section>
          <aside id="side"></aside>
        </main>
        <button id="reset">Сбросить</button>
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

  const point = {
    id: "sdo:shashlik:test",
    name: "Шашлык",
    category: "food",
    position: { x: 100, y: 20, z: 200 },
    color: "#ffaa00",
    triggerRadius: 35,
    editable: false
  };

  const snapshot = {
    player: {
      position: { x: 0, y: 10, z: 0 },
      speedKmh: 0,
      heading: 0,
      paused: false,
      inCab: true
    },
    world: {
      coordinateSystem: "ETS2 X/Y/Z",
      points: [point],
      activeLocation: "test"
    },
    selection: {
      point,
      source: "Карта симулятора"
    },
    facts: { values: {} },
    questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    inventory: { items: {} },
    reputation: { entries: { gosha: { npcId: "gosha", value: 400, contacted: true } } },
    npcCatalog: [{ id: "gosha", name: "Гоша", avatar: "data/images/avatar_placeholder.png", speakerAliases: ["Гоша"] }],
    reputationViews: { gosha: { value: 400, valueLabel: "+400", rangeName: "Неопасный", rangeColor: "#00a7bd", fillColor: "#44ff00", progressPercent: 4, tooltip: "Репутация: +400 из 10000 (Неопасный)" } },
    telemetry: {
      speedKmh: 0,
      engineRpm: 0,
      throttle: 0,
      brake: 0,
      steering: 0,
      fuelPercent: 100,
      engineTemperature: 90,
      cabinTemperature: 20,
      damageCabPercent: 0,
      damageEnginePercent: 0,
      damageTransmissionPercent: 0,
      damageWheelPercent: 0,
      hornPressed: false
    },
    environment: {
      weather: "clear",
      rainPercent: 0,
      gameTime: "12:00",
      visibilityMeters: 10000
    },
    system: {
      runtimeRunning: false,
      runtimeMode: "Simulator",
      lastEvent: "",
      lastTransition: ""
    }
  };

  await page.evaluate(snapshotValue => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        version: "test",
        snapshot: snapshotValue
      })
    });
  }, snapshot);

  await page.waitForTimeout(50);

  const sideText = await page.locator("#side").innerText();
  check(
    sideText.includes("Шашлык"),
    "После snapshot с выбранной точкой правая панель симулятора должна показывать «Шашлык»."
  );
  check(
    sideText.includes("X 100") && sideText.includes("Y 20") && sideText.includes("Z 200"),
    "Правая панель симулятора должна показывать координаты выбранной СДО."
  );
  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "event",
        event: {
          eventType: "CustomEvent",
          timestamp: null,
          source: "Test"
        }
      })
    });
  });

  const eventText = await page.locator("#side").innerText();
  check(
    eventText.includes("время неизвестно"),
    "Журнал событий должен безопасно обрабатывать отсутствующий timestamp."
  );
  check(
    pageErrors.length === 0,
    "Симулятор не должен выбрасывать pageerror при отображении выбранной СДО: " + pageErrors.join(" | ")
  );
} finally {
  await closeBrowser(browser);
}

if (failures.length) {
  console.error("Selection/context contract smoke: FAIL");
  for (const failure of failures) console.error("- " + failure);
  process.exit(1);
}

console.log("Selection/context contract smoke: OK");
