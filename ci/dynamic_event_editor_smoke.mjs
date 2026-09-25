import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

// Smoke-проверка нового Authoring слоя Dynamic Event:
// Definition отделён от runtime instance, Location выбирается ссылкой,
// read-only режим не открывает операции записи, а «Создать сейчас» отправляет
// именно диагностическую команду Dispatcher.
const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

// Контрактные проверки исходников собираются, а не падают по первой: цель —
// увидеть все расхождения за один прогон. Итог подводится в конце файла.
const failures = [];
function check(condition, message) {
  if (!condition) failures.push(message);
}

const editorJs = read("src/AssistQuestEditor.App/Web/editor.js");
const sceneJs = read("src/AssistQuestEditor.App/Web/sceneEditor.js");
const dialogueJs = read("src/AssistQuestEditor.App/Web/dialogueWorkspace.js");
const locationJs = read("src/AssistQuestEditor.App/Web/locationEditor.js");
const dynamicJs = read("src/AssistQuestEditor.App/Web/dynamicEventEditor.js");
// Действие активации отправляет симулятор, а не редактор события: редактор
// описывает Definition, активирует её уже runtime.
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
check(/DynamicEventDiscovery/.test(dynamicJs),
  "Dynamic Event editor must expose DynamicEventDiscovery trigger.");
check(/dynamicEventSourceDefinition/.test(dynamicJs),
  "Dynamic Event editor must expose discovery source DefinitionId.");
check(/spawnOnSimulationStart/.test(dynamicJs) && /respawnOnExpired/.test(dynamicJs),
  "Dynamic Event editor must expose first-spawn and expiry-respawn policies.");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}#root{height:100%;min-height:0}" +
    "</style></head><body><header><strong id='editorTitle'></strong></header>" +
    "<main id='root'></main></body></html>"
  );
  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
    window.__hostListeners = [];
    window.chrome = {
      webview: {
        addEventListener: (type, handler) => {
          if (type === "message") window.__hostListeners.push(handler);
        },
        postMessage() {}
      }
    };
    window.__deliverFromHost = data => {
      for (const handler of window.__hostListeners) {
        handler({ data: typeof data === "string" ? data : JSON.stringify(data) });
      }
    };
  });

  await page.addScriptTag({ content: sceneJs });
  await page.addScriptTag({ content: dialogueJs });
  await page.addScriptTag({ content: locationJs });
  await page.addScriptTag({ content: dynamicJs });
  await page.addScriptTag({ content: editorJs });

  await page.evaluate(() => {
    window.__deliverFromHost({ type: "active_pane", pane: "events" });
    window.__deliverFromHost({
      type: "dynamic_event_editor_state",
      definition: {
        id: "cache",
        name: "Тайник",
        description: "Тест",
        locationId: "cache.location",
        questId: "",
        triggerRadius: 35,
        category: "Cache",
        trigger: {
          type: "DistanceTravelled",
          minDistanceMeters: 5000,
          maxDistanceMeters: 10000,
          minGameHours: null,
          maxGameHours: null,
          minRealHours: null,
          maxRealHours: null,
          eventType: null,
          eventPayload: {}
        },
        spawnPolicy: {
          maxActiveInstances: 2,
          spawnChance: 1,
          cooldownGameHours: null,
          cooldownRealHours: null,
          lifetimeGameHours: 24,
          lifetimeRealHours: null,
          removeOnCompleted: true
        },
        presentation: { discovery: "Hidden" }
      },
      documentPath: "",
      documentDirty: false,
      readOnly: false,
      events: [{ id: "cache", name: "Тайник", locationId: "cache.location", trigger: "DistanceTravelled" }],
      locations: [{ id: "cache.location", name: "Точки тайников", mode: "Dynamic" }],
      runtime: { instances: [], schedules: [] }
    });
  });

  await page.waitForTimeout(100);

  if (await page.locator("#dynamicEventId").inputValue() !== "cache")
    throw new Error("Dynamic Event Editor не восстановил ID из Host state.");

  const locationOptions = await page.locator("#dynamicEventLocation option").count();
  if (locationOptions !== 2)
    throw new Error("Каталог Location не пришёл в Dynamic Event Editor: " + locationOptions);

  await page.locator("#dynamicEventMinDistance").fill("7000");
  await page.waitForTimeout(50);
  const dirty = await page.evaluate(() =>
    window.__messages.filter(message => message.action === "dynamic_event_mark_dirty"));
  if (dirty.length === 0 || dirty.at(-1)?.definition?.trigger?.minDistanceMeters !== 7000)
    throw new Error("Изменение Trigger не отправилось через dynamic_event_mark_dirty.");

  await page.evaluate(() => { window.__messages.length = 0; });
  await page.locator("#spawnDynamicEvent").click();
  const spawned = await page.evaluate(() =>
    window.__messages.filter(message => message.action === "dynamic_event_spawn"));
  if (spawned.length !== 1)
    throw new Error("Кнопка «Создать сейчас» не отправила dynamic_event_spawn.");

  await page.evaluate(() => {
    window.__deliverFromHost({
      type: "dynamic_event_editor_state",
      definition: {
        id: "cache", name: "Тайник", locationId: "cache.location",
        trigger: { type: "Manual" },
        spawnPolicy: { maxActiveInstances: 1, spawnChance: 1 },
        presentation: {}
      },
      readOnly: true,
      documentPath: "C:/readonly/cache.aqevent",
      documentDirty: false,
      events: [{ id: "cache", name: "Тайник", locationId: "cache.location", trigger: "Manual" }],
      locations: [{ id: "cache.location", name: "Точки тайников", mode: "Dynamic" }],
      runtime: {
        instances: [{
          instanceId: "cache#1",
          definitionId: "cache",
          status: "Active",
          point: { id: "wp1", name: "Точка", position: { x: 1, y: 2, z: 3 }, triggerRadius: 35 }
        }],
        schedules: []
      }
    });
  });
  await page.waitForTimeout(50);

  for (const id of ["newDynamicEvent", "openDynamicEvent", "saveDynamicEvent", "saveDynamicEventAs", "deleteDynamicEvent"]) {
    if (!(await page.locator("#" + id).isDisabled()))
      throw new Error("Read-only режим не заблокировал кнопку #" + id);
  }
  if (await page.locator("#dynamicEventMinDistance").isEnabled())
    throw new Error("Read-only режим не заблокировал поля Dynamic Event.");

  const runtimeText = await page.locator("#inspector").innerText();
  if (!runtimeText.includes("cache#1") || !runtimeText.includes("Точка"))
    throw new Error("Runtime Inspector не показал экземпляр Dynamic Event.");

  if (errors.length)
    throw new Error("Dynamic Event Editor дал ошибки браузера: " + errors.join(" | "));

  console.log("Dynamic Event Editor smoke: OK");
} finally {
  await closeBrowser(browser);
}

check(/activate_dynamic_event/.test(simulatorJs),
  "Simulator must expose explicit dynamic event activation action.");

if (failures.length) {
  console.error("Dynamic Event Editor smoke: FAIL");
  for (const failure of failures) console.error("  - " + failure);
  process.exit(1);
}
