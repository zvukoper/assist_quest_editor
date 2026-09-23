import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка редактора локаций: он «молча не работал» тремя разными способами, и
// каждый раз это выглядело как «кнопка не активна» / «поиск ничего не ищет».
//
//  1. markDirty вызывал render() без аргументов → TypeError на ws.innerHTML.
//     Значение уходило в Host, но панель не перерисовывалась, поэтому захват
//     выбранной точки выглядел как «ничего не произошло».
//  2. Поиск точки опирался на <datalist>: у 5000+ пунктов он давал
//     непредсказуемый результат, а имя и ID нельзя было различить.
//  3. Подпись «Нет точек для отображения» рисовалась SVG-текстом с классом,
//     который задаёт CSS color, а в SVG нужен fill → чёрный текст на тёмной
//     карте (контраст ~0).

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");
const editorJs = read("src/AssistQuestEditor.App/Web/editor.js");
const sceneJs = read("src/AssistQuestEditor.App/Web/sceneEditor.js");
const dialogueJs = read("src/AssistQuestEditor.App/Web/dialogueWorkspace.js");
const locationJs = read("src/AssistQuestEditor.App/Web/locationEditor.js");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}#root{height:100%;min-height:0}" +
    "</style></head><body>" +
    "<header><strong id='editorTitle'></strong></header><main id='root'></main>" +
    "</body></html>"
  );
  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });
  await page.addScriptTag({ content: sceneJs });
  await page.addScriptTag({ content: dialogueJs });
  await page.addScriptTag({ content: locationJs });
  await page.addScriptTag({ content: editorJs });

  // Мир: 50 СДО с узнаваемым именем у одной из них + город (не СДО).
  const points = [];
  for (let i = 0; i < 50; i++) {
    points.push({
      id: "sdo:test:0x" + i,
      name: i === 7 ? "Пятёрочка Урал" : "Точка " + i,
      category: "camping",
      position: { x: 1000 + i * 500, y: 120, z: -2000 + i * 700 },
      triggerRadius: 35,
      editable: true,
      color: "#78c8f0",
      isCity: false
    });
  }
  points.push({
    id: "city:chelyabinsk",
    name: "Челябинск",
    category: "Города",
    position: { x: 0, y: 0, z: 0 },
    triggerRadius: 35,
    editable: false,
    isCity: true
  });

  const definition = {
    id: "location_fixture",
    name: "Тестовая локация",
    description: "",
    mode: "Fixed",
    worldPointId: "",
    triggerRadius: 35,
    query: { criteria: [], history: {} }
  };

  const loadState = (def, context) => page.evaluate(([d, c, pts]) => {
    window.dispatchEvent(new MessageEvent("message", {
      data: { type: "active_pane", pane: "locations" }
    }));
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "location_editor_state",
        definition: d,
        documentPath: "",
        documentDirty: false,
        readOnly: false,
        locations: [],
        worldPoints: pts
      }
    }));
    if (c) {
      window.dispatchEvent(new MessageEvent("message", { data: c }));
    }
  }, [def, context, points]);

  const send = action => page.evaluate(
    a => window.__messages.filter(m => m.action === a),
    action);

  // --- 1. Кнопка захвата выбранной точки ---
  await loadState(definition, { type: "simulator_context", player: null, selection: { point: null } });
  if (!(await page.locator("#useSelectedSimulatorPoint").isDisabled()))
    throw new Error("Кнопка захвата активна без выбранной точки Simulator.");
  if (await send("location_mark_dirty").then(m => m.length))
    throw new Error("Панель отправила dirty без действий пользователя.");

  // Временная точка: пользователь создаёт её кликом по пустому месту и ждёт,
  // что «Взять выбранную» её захватит.
  await loadState(definition, {
    type: "simulator_context",
    player: null,
    selection: {
      point: {
        id: "temporary:probe",
        name: "Временная точка",
        category: "Temporary",
        position: { x: 111, y: 5, z: 222 },
        triggerRadius: 35,
        isCity: false
      },
      source: "Временная точка Simulator"
    }
  });
  if (await page.locator("#useSelectedSimulatorPoint").isDisabled())
    throw new Error("Кнопка захвата не активна при выбранной временной точке.");

  await page.locator("#useSelectedSimulatorPoint").click();
  await page.waitForTimeout(150);

  const captured = await send("location_mark_dirty");
  if (captured.length !== 1 || captured[0].definition.worldPointId !== "temporary:probe")
    throw new Error("Захват выбранной точки не отправил её ID: " + JSON.stringify(captured));
  if (errors.length)
    throw new Error("Захват точки уронил панель: " + errors.join(" | "));

  // Панель обязана ПОКАЗАТЬ захваченное значение, а не только отправить.
  const shown = await page.evaluate(() =>
    document.querySelector("[data-location-field='worldPointId']")?.value);
  if (shown !== "temporary:probe")
    throw new Error("Захваченный ID не отобразился в панели: " + shown);

  // Город — справочная точка карты, захвату не подлежит.
  await loadState(definition, {
    type: "simulator_context",
    player: null,
    selection: {
      point: { id: "city:chelyabinsk", name: "Челябинск", category: "Города",
        position: { x: 0, y: 0, z: 0 }, isCity: true },
      source: "Карта симулятора"
    }
  });
  if (!(await page.locator("#useSelectedSimulatorPoint").isDisabled()))
    throw new Error("Кнопка захвата активна для города: город не является СДО.");

  // --- 2. Живой поиск по имени и по ID ---
  await loadState(definition, null);
  const search = page.locator("#locationPointSearch");
  if (!(await search.count()))
    throw new Error("Поле поиска точки отсутствует: список должен быть явным, а не datalist.");

  await search.click();
  await search.fill("Пятёрочка");
  await page.waitForTimeout(150);
  let results = await page.evaluate(() =>
    [...document.querySelectorAll("[data-point-result]")].map(b => b.dataset.pointResult));
  if (results.length !== 1 || results[0] !== "sdo:test:0x7")
    throw new Error("Поиск по имени не нашёл точку: " + JSON.stringify(results));

  await page.locator("[data-point-result]").first().click();
  await page.waitForTimeout(150);
  const afterPick = await send("location_mark_dirty");
  if (afterPick.at(-1)?.definition.worldPointId !== "sdo:test:0x7")
    throw new Error("Клик по результату поиска не подставил ID: " + JSON.stringify(afterPick));
  if (await page.evaluate(() => document.querySelector("[data-location-field='worldPointId']")?.value)
      !== "sdo:test:0x7")
    throw new Error("Клик по результату поиска не обновил панель.");

  await search.fill("0x8");
  await page.waitForTimeout(150);
  results = await page.evaluate(() =>
    [...document.querySelectorAll("[data-point-result]")].map(b => b.dataset.pointResult));
  if (results.length !== 1 || results[0] !== "sdo:test:0x8")
    throw new Error("Поиск по ID не нашёл точку: " + JSON.stringify(results));

  await search.fill("нет-такой-точки");
  await page.waitForTimeout(150);
  const empty = await page.evaluate(() =>
    document.querySelector("#locationPointResults .notice")?.textContent || "");
  if (!empty.includes("Ничего не найдено"))
    throw new Error("Пустой результат поиска не объяснён пользователю: " + empty);

  // --- 3. Читаемость подписи «Нет точек» ---
  await loadState(definition, null);
  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "location_test",
        result: { locationId: "location_fixture", supported: true, requestedRounds: 1,
          candidates: [], diagnostics: ["Подходящих кандидатов нет."] }
      }
    }));
  });
  await page.waitForTimeout(150);

  const contrast = await page.evaluate(() => {
    const text = document.querySelector("#locationMap text");
    if (!text) return null;
    const fill = getComputedStyle(text).fill;
    const match = fill.match(/rgba?\((\d+)[,\s]+(\d+)[,\s]+(\d+)/);
    if (!match) return { fill };
    // Фон карты — #111419 (17,20,25): текст обязан быть заметно светлее.
    const luma = Number(match[1]) * 0.299 + Number(match[2]) * 0.587 + Number(match[3]) * 0.114;
    return { fill, luma };
  });
  if (!contrast || !(contrast.luma > 100))
    throw new Error("Подпись «Нет точек» нечитаема (нужен fill, а не CSS color): " +
      JSON.stringify(contrast));

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Location editor smoke: OK");
} finally {
  await browser.close();
}
