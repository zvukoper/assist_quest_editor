import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка дорожного слоя на карте Симулятора и того, что он НЕ едет в снимке.
//
// Зачем отдельная проверка:
//  1. Дорог ~98 000 отрезков (~3.5 МБ). Если положить их в точки мира, каждый
//     снимок карты вырос бы с ~0.9 МБ до ~19 МБ — карта заметно тормозила бы,
//     потому что снимки уходят часто (каждое событие, тик времени, выбор квеста).
//     Поэтому дороги грузятся отдельным файлом ОДИН раз, и это надо удержать.
//  2. Слой обязан РИСОВАТЬСЯ: галочка, отсечение по экрану, толщина по масштабу.
//     Проверка «файл загрузился» ничего не говорит о том, что видно на экране.

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

// --- Статическая часть: дороги не должны попасть в точки мира ---

// Загрузчик обязан ходить за отдельным файлом, а не ждать дороги в снимке.
if (!simulatorJs.includes("../data/world/roads.json"))
  throw new Error("simulator.js не загружает ../data/world/roads.json: " +
    "дороги должны приходить отдельным файлом, а не в снимке.");

// Дорожный слой обязан вызываться в отрисовке карты. Без этого геометрия
// загрузится и молча не покажется.
if (!/drawRoads\s*\(/.test(simulatorJs))
  throw new Error("simulator.js не вызывает drawRoads: слой дорог не рисуется.");

// Дороги рисуются ДО точек: иначе они закрыли бы СДО и квесты.
{
  // Границы берутся по телу функции: срез «до следующей известной функции»
  // захватывал и ОПРЕДЕЛЕНИЕ drawRoads, и тогда проверка порядка видела
  // «drawRoads» ниже точек даже после удаления вызова из drawMap. Это давало
  // сообщение про порядок там, где вызова не было вовсе.
  const start = simulatorJs.indexOf("function drawMap()");
  const end = simulatorJs.indexOf("\n  function ", start + 10);
  if (start < 0 || end < 0)
    throw new Error("Не найдено тело drawMap: проверка порядка дорог невозможна.");
  const drawMap = simulatorJs.slice(start, end);

  const roadsAt = drawMap.indexOf("drawRoads(");
  const pointsAt = drawMap.indexOf("drawWorldPoint(");
  // Отсутствие вызова и неверный порядок — разные поломки с разным лечением,
  // поэтому и сообщения разные: иначе автор чинил бы не то.
  if (roadsAt < 0)
    throw new Error("В drawMap нет вызова drawRoads: дорожный слой не рисуется.");
  if (pointsAt < 0)
    throw new Error("В drawMap нет отрисовки точек: дороги рисуются в пустоту.");
  if (roadsAt > pointsAt)
    throw new Error("Дороги рисуются ПОСЛЕ точек: они закрыли бы СДО и квесты. " +
      "Дороги — фон, а не объект поверх карты.");
}

// Данные дорог обязаны быть отдельным ресурсом рядом с данными мира.
{
  const roadsPath = path.join(root, "data", "world", "roads.json");
  if (!fs.existsSync(roadsPath))
    throw new Error("Файл data/world/roads.json отсутствует: слой дорог нечем нарисовать.");

  const payload = JSON.parse(fs.readFileSync(roadsPath, "utf8"));
  if (!Array.isArray(payload.segments))
    throw new Error("В roads.json нет массива segments.");
  if (payload.segments.length % 4 !== 0)
    throw new Error("Длина segments не кратна четырём числам: " +
      payload.segments.length);
  if (payload.segmentCount !== payload.segments.length / 4)
    throw new Error("segmentCount не совпадает с длиной массива: " +
      payload.segmentCount + " против " + payload.segments.length / 4);
  if (payload.layout !== "x1,z1,x2,z2")
    throw new Error("Неожиданная раскладка дорог: " + payload.layout);

  // Объём: файл не должен раздуться до исходных 45 МБ GeoJSON.
  const sizeMb = fs.statSync(roadsPath).size / 1048576;
  if (sizeMb > 12)
    throw new Error("roads.json вырос до " + sizeMb.toFixed(1) + " МБ: " +
      "компактный формат потерян.");

  console.log(`Дороги: ${payload.segmentCount} отрезков, ${sizeMb.toFixed(1)} МБ.`);
}

// Имя критерия обязано совпадать везде. Это не придирка к стилю: панель Location
// отправляет в определение строку из своего списка критериев, домен сверяет её
// со своим switch, а SupportedCriteria решает, поддерживается критерий или нет.
// Разойдись эти три места хоть на букву («nearroad» против «nearbyroad») — домен
// молча объявит критерий неподдерживаемым, и автор увидит «Не поддерживается»,
// хотя всё написано. Проверка держит имена связанными.
{
  const resolver = read("src/AssistQuestEditor.Domain/LocationResolver.cs");
  const editor = read("src/AssistQuestEditor.App/Web/locationEditor.js");
  const label = "Рядом с дорогой";

  // Имя критерия берётся из СПИСКА панели — именно оно уходит в определение.
  // Искать по «известному» написанию нельзя: переименование в списке тогда
  // осталось бы незамеченным, ведь искомое имя продолжало бы находиться в
  // другом месте файла.
  const listStart = editor.indexOf("const CRITERIA = [");
  const listEnd = editor.indexOf("];", listStart);
  if (listStart < 0 || listEnd < 0)
    throw new Error("В панели Location нет списка CRITERIA: критерии неоткуда взять.");
  const criteriaList = editor.slice(listStart, listEnd);

  const row = criteriaList.match(new RegExp(`\\["([A-Za-z]+)",\\s*"${label}"\\]`));
  if (!row)
    throw new Error("В списке CRITERIA панели нет критерия «" + label + "»: " +
      "автор не сможет его выбрать, а в домен уходит только то, что есть в списке.");
  const panelName = row[1];
  const domainName = panelName.toLowerCase();

  // Разбор параметров обязан знать ТО ЖЕ имя. Разойдись они — панель покажет
  // поля одного критерия, а в определение уйдёт другое имя; домен объявит его
  // неподдерживаемым, и критерий молча перестанет работать.
  if (!editor.includes(`type === "${panelName}"`))
    throw new Error("Имя критерия «" + panelName + "» есть в списке, но разбор " +
      "параметров его не знает: поля критерия не соберутся в определение.");

  const supported = resolver.slice(resolver.indexOf("SupportedCriteria"));
  if (!supported.toLowerCase().includes(domainName))
    throw new Error("Критерий «" + domainName + "» отсутствует в SupportedCriteria: " +
      "домен объявит его неподдерживаемым.");

  if (!new RegExp(`case\\s+"${domainName}"`, "i").test(resolver))
    throw new Error("В разборе критериев домена нет ветки «" + domainName + "».");

  // Ищем ОПРЕДЕЛЕНИЕ, а не первое упоминание: имя IsRoadCriterion встречается
  // ещё и в вызове criteria.Any(IsRoadCriterion) выше по файлу, и срез от него
  // не содержал бы тела метода.
  const roadCriterionAt = resolver.indexOf("bool IsRoadCriterion(");
  if (roadCriterionAt < 0)
    throw new Error("В домене нет метода IsRoadCriterion: дорожный критерий " +
      "неотличим от прочих, и без геометрии он получит общую диагностику.");

  const roadCriterionBody = resolver.slice(roadCriterionAt, roadCriterionAt + 600);
  if (!new RegExp(`"${domainName}"`, "i").test(roadCriterionBody))
    throw new Error("Распознавание дорожного критерия не знает «" + domainName + "»: " +
      "без геометрии критерий не получит понятной диагностики.");
}

// --- Динамическая часть: слой реально рисуется на canvas ---

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  // Фикстура повторяет разметку реальной страницы: без неё renderGameplayPanels
  // выходит раньше и панели не рисуются (проверено на других smoke).
  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}" +
    "#mapCanvas{width:100%;height:100%;display:block}" +
    "</style></head><body>" +
    "<canvas id='mapCanvas'></canvas><div id='side'></div><div id='hud'></div>" +
    "<div id='runtimeSide'></div><div id='playerOverlay'>" +
      "<div id='inventoryPanel'></div><div id='characterPanel'></div>" +
      "<div id='characterTabs'></div><div id='characterTabBody'></div>" +
    "</div>" +
    "<button id='backpackButton'></button><div id='inventoryNotifications'></div>" +
    "<div id='mapStatusBar'>" +
      "<input type='checkbox' id='onlyQuestsToggle'>" +
      "<input type='checkbox' id='citiesToggle'>" +
      "<input type='checkbox' id='roadsToggle'>" +
      "<span id='mapStatusHint'></span>" +
    "</div>" +
    "</body></html>"
  );
  await page.addStyleTag({ content: theme });

  // Дороги подменяются предсказуемым набором: проверяется ОТРИСОВКА, а не
  // содержимое 98 000 реальных отрезков. Через сеть файл не подгружаем, чтобы
  // тест не зависел от 3.5 МБ данных.
  const fixtureRoads = [
    -1000, 0, 1000, 0,
    0, -1000, 0, 1000
  ];

  await page.evaluate(roads => {
    window.__sent = [];
    // Харнесс повторяет схему других smoke: обработчики складываются в Map, и
    // снимок доставляется именно через них, как это делает WebView2. Пустая
    // заглушка addEventListener() сделала бы модуль глухим — карта не получила
    // бы ни снимка, ни данных, и проверка рисовала бы пустой канвас.
    window.chrome = {
      webview: {
        listeners: new Map(),
        addEventListener(type, handler) { this.listeners.set(type, handler); },
        postMessage(payload) { window.__sent.push(payload); }
      }
    };
    // Заглушка fetch: настоящий файл не нужен, важна отрисовка.
    window.fetch = async url => {
      if (String(url).includes("roads.json"))
        return { ok: true, json: async () => ({ segments: roads, segmentCount: roads.length / 4 }) };
      return { ok: false, status: 404, json: async () => ({}) };
    };
  }, fixtureRoads);

  await page.addScriptTag({ content: simulatorJs });

  // Снимок нужен, чтобы карта вообще нарисовалась.
  await page.evaluate(() => {
    const snapshot = {
      world: { coordinateSystem: "ETS2", points: [], activeLocation: "" },
      player: {
        position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0,
        paused: true, inCab: false
      },
      selection: { point: null, source: "" },
      playerVitals: {
        health: 100, maxHealth: 100, energy: 100, maxEnergy: 100,
        hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100
      },
      playerProgress: { money: 0, experience: 0, reserve: 0 },
      facts: { values: {} }, variables: { values: {} }, states: { flags: {} },
      inventory: { items: [] }, reputation: { entries: {} },
      questStatuses: { entries: [] }, telemetry: { entries: [] },
      environment: {}, journal: []
    };

    const payload = {
      type: "snapshot",
      snapshot,
      questCatalog: [],
      enabledQuestIds: [],
      journalDetached: false,
      runtime: { status: "Stopped", currentNodeId: null },
      simulationRunning: false,
      selectedQuest: { campaignId: "", questId: "" },
      reputationViews: {},
      worldSettings: null,
      daylight: null
    };

    // Тот же канал, что использует приложение: снимок приходит через chrome.webview.
    window.chrome.webview.listeners?.get?.("message")?.({ data: JSON.stringify(payload) });
  });

  // Загрузка дорог асинхронна — ждём, пока слой появится.
  await page.waitForTimeout(400);

  /**
   * Замер кадров — ЦЕЛИКОМ В СТРАНИЦЕ.
   *
   * Почему не через возврат в Node, как хотелось сначала: кадр 1200x800 — это
   * 3.84 млн чисел, и сериализация их через мост Playwright занимала ~25 секунд
   * из 30 на проверку. Хранить пиксели в window и сравнивать на месте — секунды.
   *
   * Почему сравнение кадров, а не подсчёт пикселей «нужного цвета»: дороги
   * рисуются линией толщиной от 0.6 px с прозрачностью .85 и сглаживанием. Почти
   * все пиксели линии получают смешанный цвет, и точное сравнение с
   * rgba(118,130,146) их отбрасывает; зато под допуск попадают посторонние серые
   * элементы (сетка, панели). Из-за этого подсчёт по цвету и «проходил», и падал
   * не по делу. Разница двух кадров измеряет ровно вклад слоя и не зависит ни от
   * оттенка, ни от прозрачности.
   */
  await page.evaluate(() => {
    window.__frames = new Map();

    /** Снимает текущий кадр карты под именем key. */
    window.__grabFrame = key => {
      const canvas = document.getElementById("mapCanvas");
      const img = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height);
      window.__frames.set(key, { w: canvas.width, h: canvas.height, d: img.data });
      return { w: canvas.width, h: canvas.height };
    };

    /** Число различимых пикселей между двумя снятыми кадрами. */
    window.__diffFrames = (keyA, keyB) => {
      const a = window.__frames.get(keyA);
      const b = window.__frames.get(keyB);
      if (!a || !b)
        throw new Error("Кадр не снят: " + keyA + " / " + keyB);
      if (a.w !== b.w || a.h !== b.h)
        throw new Error("Кадры разного размера: " +
          a.w + "x" + a.h + " против " + b.w + "x" + b.h);
      let count = 0;
      for (let i = 0; i < a.d.length; i += 4) {
        if (Math.abs(a.d[i] - b.d[i]) > 6 ||
            Math.abs(a.d[i + 1] - b.d[i + 1]) > 6 ||
            Math.abs(a.d[i + 2] - b.d[i + 2]) > 6) {
          count++;
        }
      }
      return count;
    };
  });

  const grabFrame = key => page.evaluate(name => window.__grabFrame(name), key);
  const setRoadsToggle = value => page.evaluate(checked => {
    const toggle = document.getElementById("roadsToggle");
    toggle.checked = checked;
    toggle.dispatchEvent(new Event("change"));
  }, value);

  await grabFrame("withRoads");

  // Галочка гасит слой. Кадр без слоя — опорная точка для измерения.
  await setRoadsToggle(false);
  await page.waitForTimeout(150);
  await grabFrame("withoutRoads");

  const roadPixels = await page.evaluate(() => window.__diffFrames("withRoads", "withoutRoads"));
  if (roadPixels < 200)
    throw new Error("Дорожный слой не нарисован: включение галочки меняет " +
      roadPixels + " пикселей. Геометрия загружена, но не отображается.");

  // Возврат галочки возвращает слой: кадр обязан совпасть с исходным.
  await setRoadsToggle(true);
  await page.waitForTimeout(150);
  await grabFrame("restored");

  const restored = await page.evaluate(() => window.__diffFrames("withRoads", "restored"));
  if (restored > roadPixels * 0.1)
    throw new Error("После возврата галочки дорожный слой не вернулся: " +
      "кадр отличается на " + restored + " пикселей.");

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Дорожный слой: OK, " + roadPixels + " пикселей дорог.");
} finally {
  await browser.close();
}
