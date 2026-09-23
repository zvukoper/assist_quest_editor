import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка редактора локаций: он «молча не работал» четырьмя разными способами,
// и каждый раз это выглядело как «кнопка не активна» / «поиск ничего не ищет».
//
//  0. Модуль слушал сообщения Host только на window, а WebView2 доставляет их
//     через chrome.webview. Панель не получала НИЧЕГО: ни списка мира, ни
//     выбранной точки. Форма при этом рисовалась (render() сам создаёт пустое
//     определение), поэтому симптом выглядел как поломка кнопки и поиска.
//     Harness доставляет сообщения только через chrome.webview, как приложение.
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
    // Подменяем chrome.webview так, как его видит приложение: сообщения Host
    // доставляет ИМЕННО он (Host использует PostWebMessageAsJson). Если
    // модуль слушает только window, он не получит ничего — и проверка ниже
    // это поймает. Раньше harness сам рассылал сообщения через window, поэтому
    // молча пропускал именно этот дефект.
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
      // window-канал НЕ используется: только chrome.webview, как в WebView2.
      for (const handler of window.__hostListeners) {
        handler({ data: typeof data === "string" ? data : JSON.stringify(data) });
      }
    };
  });
  await page.addScriptTag({ content: sceneJs });
  await page.addScriptTag({ content: dialogueJs });
  await page.addScriptTag({ content: editorJs });

  // Модуль локаций обязан подписаться на канал chrome.webview: без этого Host не
  // может доставить ни список мира, ни выбранную точку. Считается именно ПРИРОСТ
  // слушателей: соседние модули тоже подписываются, поэтому «их вообще есть»
  // ничего не доказывает (такая проверка прошла бы на сломанном файле).
  const listenersBefore = await page.evaluate(() => window.__hostListeners.length);
  await page.addScriptTag({ content: locationJs });
  const listenersAfter = await page.evaluate(() => window.__hostListeners.length);
  if (listenersAfter <= listenersBefore)
    throw new Error(
      "locationEditor.js не подписан на chrome.webview: сообщения Host " +
      "(список точек, выбранная точка) до панели не доходят.");

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
    window.__deliverFromHost({ type: "active_pane", pane: "locations" });
    window.__deliverFromHost({
      type: "location_editor_state",
      definition: d,
      documentPath: "",
      documentDirty: false,
      readOnly: false,
      locations: [],
      worldPoints: pts
    });
    if (c) {
      window.__deliverFromHost(c);
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
    window.__deliverFromHost({
      type: "location_test",
      result: { locationId: "location_fixture", supported: true, requestedRounds: 1,
        candidates: [], diagnostics: ["Подходящих кандидатов нет."] }
    });
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

  // --- 4. Количество раундов сохраняется между перерисовками ---
  //
  // Симптом был такой: вводишь 20, нажимаешь «Тест», панель перерисовывается
  // ответом Host, и поле снова показывает 8 — то есть значение сбрасывалось.
  {
    const roundsDefinition = { ...definition, mode: "Dynamic",
      query: { criteria: [], history: {} } };
    await loadState(roundsDefinition, null);

    const roundsInput = page.locator("#locationRounds");
    if (!(await roundsInput.count()))
      throw new Error("Поле количества раундов отсутствует у Dynamic Location.");

    await roundsInput.fill("20");
    await page.evaluate(() => { window.__messages.length = 0; });
    await page.locator("#testLocation").click();
    await page.waitForTimeout(150);

    const sentTest = await send("location_test");
    if (sentTest.length !== 1 || sentTest[0].rounds !== 20)
      throw new Error("Тест отправлен не с введённым числом раундов: " +
        JSON.stringify(sentTest.map(m => m.rounds)));

    // Ответ Host перерисовывает панель — значение обязано остаться.
    await page.evaluate(() => {
      window.__deliverFromHost({
        type: "location_test",
        result: { locationId: "location_fixture", supported: true, requestedRounds: 20,
          candidates: [], diagnostics: [] }
      });
    });
    await page.waitForTimeout(150);

    const shownRounds = await page.evaluate(() =>
      document.getElementById("locationRounds")?.value);
    if (shownRounds !== "20")
      throw new Error("Введённое число раундов сбросилось после теста: " + shownRounds);

    // «Показать в симуляторе» использует то же значение, а не 8.
    await page.evaluate(() => { window.__messages.length = 0; });
    await page.locator("#showLocationInSimulator").click();
    await page.waitForTimeout(150);
    const sentVisual = await send("location_show_in_simulator");
    if (sentVisual.length !== 1 || sentVisual[0].rounds !== 20)
      throw new Error("Режим визуализации отправлен не с введённым числом раундов: " +
        JSON.stringify(sentVisual.map(m => m.rounds)));

    // Значение не должно теряться и при полной перезагрузке состояния панели.
    await loadState(roundsDefinition, null);
    const afterReload = await page.evaluate(() =>
      document.getElementById("locationRounds")?.value);
    if (afterReload !== "20")
      throw new Error("Число раундов потерялось при обновлении состояния панели: " + afterReload);
  }

  // --- 5. Новые критерии присутствуют и дают нужные поля ---
  {
    const criteriaDefinition = { ...definition, mode: "Dynamic",
      query: { criteria: [{ type: "NearbyCategory", parameters: { value: "cat", meters: "500" }, negate: false }], history: {} } };
    await loadState(criteriaDefinition, null);

    const options = await page.evaluate(() =>
      [...document.querySelectorAll("[data-criterion-type] option")].map(o => o.value));

    for (const required of ["NearbyCategory", "NoNearbyCategory", "MinDistanceBetweenCandidates"])
      if (!options.includes(required))
        throw new Error("В списке критериев нет «" + required + "»: " + JSON.stringify(options));

    // Критерий соседства обязан показать категорию и радиус.
    const nearbyFields = await page.evaluate(() => {
      const row = document.querySelector("[data-criterion]");
      return [...row.querySelectorAll("[data-criterion-parameter]")]
        .map(input => input.dataset.criterionParameter);
    });
    if (!nearbyFields.includes("value") || !nearbyFields.includes("meters"))
      throw new Error("Критерий соседства не даёт поля категории и радиуса: " +
        JSON.stringify(nearbyFields));

    // Минимальная дистанция — только расстояние, и у неё нет «НЕ»: это стратегия
    // отбора раундов, а не условие на точку, инвертировать её нечего.
    const distanceDefinition = { ...definition, mode: "Dynamic",
      query: { criteria: [{ type: "MinDistanceBetweenCandidates", parameters: { meters: "5000" }, negate: false }], history: {} } };
    await loadState(distanceDefinition, null);

    const distanceFields = await page.evaluate(() => {
      const row = document.querySelector("[data-criterion]");
      return {
        parameters: [...row.querySelectorAll("[data-criterion-parameter]")]
          .map(input => input.dataset.criterionParameter),
        negate: !!row.querySelector("[data-criterion-negate]")
      };
    });
    if (distanceFields.parameters.join(",") !== "meters")
      throw new Error("У минимальной дистанции должны быть только метры: " +
        JSON.stringify(distanceFields.parameters));
    if (distanceFields.negate)
      throw new Error("У минимальной дистанции не должно быть чекбокса «НЕ»: она не условие на точку.");

    // Смена типа критерия перерисовывает строку — проверяем, что сборка не падает
    // и коллекция параметров уходит в Host целиком, включая meters.
    await loadState(criteriaDefinition, null);
    await page.evaluate(() => { window.__messages.length = 0; });
    await page.locator("#saveLocation").click();
    await page.waitForTimeout(150);

    const saved = await send("location_save");
    const savedCriteria = saved[0]?.definition?.query?.criteria || [];
    if (savedCriteria.length !== 1 || savedCriteria[0].type !== "NearbyCategory" ||
        savedCriteria[0].parameters?.value !== "cat" ||
        String(savedCriteria[0].parameters?.meters) !== "500")
      throw new Error("Критерий соседства не собрался в определение корректно: " +
        JSON.stringify(savedCriteria));
  }

  // --- 6. Кнопка захвата выбранной точки у поля «Точка» в критерии ---
  //
  // Раньше кнопка была только у Fixed-точки, а в критерии «В радиусе точки»
  // приходилось выбирать точку из списка на 5000+ пунктов вручную. Точка в
  // критерии — та же точка мира, поэтому захват должен работать одинаково.
  {
    const pointCriterion = { ...definition, mode: "Dynamic",
      query: { criteria: [{ type: "WithinDistanceOfPoint",
        parameters: { pointId: "", meters: "500" }, negate: false }], history: {} } };

    // Без выбора в Simulator искать нечего: кнопка обязана быть неактивной.
    await loadState(pointCriterion, {
      type: "simulator_context", player: null, selection: { point: null }
    });
    const capture = page.locator("[data-criterion-capture-point='pointId']");
    if (!(await capture.count()))
      throw new Error("У поля «Точка» в критерии нет кнопки захвата выбранной точки.");
    if (!(await capture.isDisabled()))
      throw new Error("Захват точки в критерии активен без выбранной точки Simulator.");

    // Временная точка должна подставляться своим ID: её нет в списке мира, и
    // имя «Временная точка» у нескольких таких точек ввело бы в заблуждение.
    await loadState(pointCriterion, {
      type: "simulator_context",
      player: null,
      selection: {
        point: { id: "temporary:crit", name: "Временная точка", category: "Temporary",
          position: { x: 50, y: 1, z: 60 }, isCity: false },
        source: "Временная точка Simulator"
      }
    });
    if (await capture.isDisabled())
      throw new Error("Захват точки в критерии не активен при выбранной временной точке.");

    await page.evaluate(() => { window.__messages.length = 0; });
    await capture.click();
    await page.waitForTimeout(150);

    const captured = await send("location_mark_dirty");
    const capturedCriteria = captured.at(-1)?.definition?.query?.criteria || [];
    if (capturedCriteria[0]?.parameters?.pointId !== "temporary:crit")
      throw new Error("Захват точки в критерии не записал её ID в параметр pointId: " +
        JSON.stringify(capturedCriteria));

    // Пользователь обязан УВИДЕТЬ подставленное значение, а не только отправку.
    const shownPoint = await page.evaluate(() =>
      document.querySelector("[data-criterion-parameter='pointId']")?.value);
    if (shownPoint !== "temporary:crit")
      throw new Error("Захваченная в критерий точка не отобразилась в поле: " + shownPoint);

    // Существующая точка мира подставляется ИМЕНЕМ, а в файл всё равно уходит ID —
    // так поле остаётся читаемым, а параметр стабильным.
    await loadState(pointCriterion, {
      type: "simulator_context",
      player: null,
      selection: {
        point: { id: "sdo:test:0x7", name: "Пятёрочка Урал", category: "camping",
          position: { x: 1, y: 2, z: 3 }, isCity: false },
        source: "Карта симулятора"
      }
    });
    await page.evaluate(() => { window.__messages.length = 0; });
    await page.locator("[data-criterion-capture-point='pointId']").click();
    await page.waitForTimeout(150);

    const named = await page.evaluate(() =>
      document.querySelector("[data-criterion-parameter='pointId']")?.value);
    if (named !== "Пятёрочка Урал")
      throw new Error("Точка мира подставилась не именем: " + named);

    const namedSaved = await send("location_mark_dirty");
    if (namedSaved.at(-1)?.definition?.query?.criteria?.[0]?.parameters?.pointId !== "sdo:test:0x7")
      throw new Error("Имя точки мира не перевелось обратно в канонический ID: " +
        JSON.stringify(namedSaved.at(-1)?.definition?.query?.criteria));

    // Город — справочная точка карты, а не СДО: захвату не подлежит, как и в
    // режиме Fixed.
    await loadState(pointCriterion, {
      type: "simulator_context",
      player: null,
      selection: {
        point: { id: "city:chelyabinsk", name: "Челябинск", category: "Города",
          position: { x: 0, y: 0, z: 0 }, isCity: true },
        source: "Карта симулятора"
      }
    });
    if (!(await page.locator("[data-criterion-capture-point='pointId']").isDisabled()))
      throw new Error("Захват точки в критерии активен для города: город не является СДО.");
  }

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Location editor smoke: OK");
} finally {
  await browser.close();
}
