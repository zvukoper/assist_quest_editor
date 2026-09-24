import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const simulatorJs = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "simulator.js"),
  "utf8"
);

const browser = await chromium.launch({ headless: true });
const failures = [];
let page;

try {
  page = await browser.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <body>
        <div id="hud"></div>
        <button id="backpackButton"></button>
        <div id="playerOverlay">
          <section id="characterPanel"></section>
        </div>
        <div id="inventoryNotifications"></div>
        <aside id="runtimeSide"></aside>
        <aside id="side"></aside>
        <canvas id="mapCanvas"></canvas>
        <button id="reset"></button>
        <script>
          window.__sent = [];
          window.chrome = {
            webview: {
              listeners: new Map(),
              addEventListener(type, handler) { this.listeners.set(type, handler); },
              postMessage(raw) { try { window.__sent.push(JSON.parse(raw)); } catch (e) { window.__sent.push(raw); } }
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
    player: { position: { x: 0, y: 10, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
    world: { coordinateSystem: "ETS2 X/Y/Z", points: [point], activeLocation: "test" },
    selection: { point, source: "Карта симулятора" },
    facts: { values: {} },
    questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    playerVitals: {
      health: 100, maxHealth: 100, energy: 100, maxEnergy: 100,
      hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100
    },
    playerProgress: { money: 1500, experience: 0, reserve: 0 },
    character: { stats: { strength: 5 }, skills: [] },
    inventory: { items: {}, newItemIds: [] },
    reputation: { entries: { gosha: { npcId: "gosha", value: 400, contacted: true } } },
    npcCatalog: [{ id: "gosha", name: "Гоша", avatar: "data/images/avatar_placeholder.png", speakerAliases: ["Гоша"] }],
    reputationViews: { gosha: { value: 400, valueLabel: "+400", rangeName: "Неопасный", rangeColor: "#00a7bd", fillColor: "#44ff00", progressPercent: 4, tooltip: "Репутация: +400 из 10000 (Неопасный)" } },
    telemetry: {
      speedKmh: 0, engineRpm: 0, throttle: 0, brake: 0, steering: 0,
      fuelPercent: 100, engineTemperature: 90, cabinTemperature: 20,
      damageCabPercent: 0, damageEnginePercent: 0, damageTransmissionPercent: 0,
      damageWheelPercent: 0, hornPressed: false
    },
    environment: { weather: "clear", rainPercent: 0, gameTime: "12:00", visibilityMeters: 10000 },
    system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
  };

  await page.evaluate(snapshotValue => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({ type: "snapshot", version: "test", snapshot: snapshotValue })
    });
  }, snapshot);

  await page.waitForTimeout(50);

  // Инвентарь теперь ОТДЕЛЬНОЕ ОКНО, и создаёт его Host: страница не может
  // сделать форму Windows. Поэтому проверяется не класс на оверлее, а ЗАПРОС
  // к Host — toggle_inventory. Прежняя проверка «оверлей стал visible»
  // фиксировала снятое поведение и падала бы всегда, хотя клавиша работает.
  const inventoryRequests = () => page.evaluate(() =>
    (window.__sent || []).filter(item => item && item.action === "toggle_inventory").length);

  const dispatch = async (code, key) => {
    const before = await inventoryRequests();
    await page.evaluate(({ code: c, key: k }) => {
      document.dispatchEvent(new KeyboardEvent("keydown", {
        code: c, key: k, bubbles: true, cancelable: true
      }));
    }, { code, key });
    await page.waitForTimeout(20);
    return (await inventoryRequests()) > before;
  };

  // 1. English layout: physical I -> key "i".
  if (!await dispatch("KeyI", "i")) failures.push("Английская раскладка (KeyI/i) не запросила инвентарь.");
  await dispatch("KeyI", "i");

  // 2. Russian layout: same physical key -> key "ш".
  if (!await dispatch("KeyI", "ш")) failures.push("Русская раскладка (KeyI/ш) не запросила инвентарь.");
  await dispatch("KeyI", "ш");

  // 3. Other layout where code is unavailable but key is the latin char.
  if (!await dispatch("", "i")) failures.push("Раскладка без code (i) не запросила инвентарь.");
  await dispatch("", "i");

  // 4. Input fields must still swallow the hotkey.
  const beforeField = await inventoryRequests();
  await page.evaluate(() => {
    const field = document.createElement("input");
    field.id = "probeInput";
    document.body.appendChild(field);
    field.focus();
    field.dispatchEvent(new KeyboardEvent("keydown", {
      code: "KeyI", key: "i", bubbles: true, cancelable: true
    }));
  });
  await page.waitForTimeout(20);
  if ((await inventoryRequests()) > beforeField)
    failures.push("Горячая клавиша не должна срабатывать при вводе текста в поле.");

  if (pageErrors.length) failures.push("pageerror: " + pageErrors.join(" | "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.error("Hotkey probe: FAIL");
  for (const failure of failures) console.error("- " + failure);
  process.exit(1);
}

console.log("Hotkey probe: OK");
