import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка окна ручной валидации перекрёстков.
//
// Что проверяется и почему:
//  1. Окно ПРИНИМАЕТ сообщения Host через chrome.webview. Модуль, подписанный
//     только на window, выглядит рабочим (карта рисуется фоном), но данные до
//     него не доходят — в этом репозитории такая ошибка уже была у locationEditor.js.
//  2. Узлы рисуются ЖЁЛТЫМИ точками, добавленные — зелёными. Разный цвет
//     обязателен: иначе автор не отличит свою находку от результата поиска.
//  3. Кнопки «следующий/предыдущий» идут В ПОРЯДКЕ СПИСКА, а не в произвольном.
//  4. Мультивыбор и «Исключить» работают: выбранное уходит в список исключений.
//  5. Клик по пустому месту создаёт зелёную точку, повторный клик убирает её.
//  6. Сохранение отправляет Host и исключённые, и добавленные.
//
// Дороги в фикстуре не 98 341 отрезок, а несколько: проверяется ОТРИСОВКА и
// логика окна, а не содержимое реальных данных (оно проверяется в road_layer_smoke).

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");
const junctionsJs = read("src/AssistQuestEditor.App/Web/junctions.js");
const junctionsHtml = read("src/AssistQuestEditor.App/Web/junctions.html");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

// --- Статическая часть ---

// Окно обязано подписываться на chrome.webview: Host доставляет сообщения именно
// туда, а не в window.
if (!junctionsJs.includes("chrome.webview.addEventListener"))
  throw new Error("junctions.js не слушает chrome.webview: сообщения Host не дойдут, " +
    "и карта останется пустой без единой ошибки на странице.");

if (!junctionsJs.includes('window.addEventListener("message"'))
  throw new Error("junctions.js не слушает window: смоук-проверки не смогут " +
    "доставить сообщение.");

// Страница окна существует и подключает модуль.
if (!junctionsHtml.includes("junctions.js"))
  throw new Error("junctions.html не подключает junctions.js.");

// Кнопки панели: без них окно неработоспособно.
for (const id of ["junctionPrev", "junctionNext", "junctionExclude", "junctionSave"]) {
  if (!junctionsHtml.includes(`id="${id}"`))
    throw new Error(`В junctions.html нет кнопки #${id}.`);
}

// Цвета точек должны различаться: жёлтый — найденный, зелёный — добавленный.
{
  const accent = junctionsJs.match(/const ACCENT = "([^"]+)"/);
  if (!accent) throw new Error("В junctions.js не найден цвет найденного перекрёстка.");
  if (!/fillStyle = "#54d16a"/.test(junctionsJs))
    throw new Error("Добавленные вручную перекрёстки не рисуются зелёным цветом: " +
      "автор не отличит свою находку от результата поиска.");
  if (accent[1].toLowerCase() === "#54d16a")
    throw new Error("Найденные и добавленные перекрёстки одного цвета.");
}

// Правки должны применяться в домене, а не только в окне: иначе файл исключений
// ни на что не влияет и вся ручная работа пропадает.
{
  const store = read("src/AssistQuestEditor.App/JunctionReviewStore.cs");
  if (!/Apply\(IReadOnlyList<JunctionPoint>/.test(store))
    throw new Error("В JunctionReview нет метода Apply: правки не применяются к списку перекрёстков.");
  if (!/IsExcluded\(/.test(store))
    throw new Error("В JunctionReview нет проверки исключения узла: файл исключений ни на что не влияет.");
  if (!/public void Save\(JunctionReview/.test(store))
    throw new Error("JunctionReviewStore не умеет записывать правки в файл.");
}

// Критерий обязан знать про перекрёстки: без этого окно есть, а поиск их не видит.
{
  const resolver = read("src/AssistQuestEditor.Domain/LocationResolver.cs");
  if (!/IsJunctionCriterion/.test(resolver))
    throw new Error("В LocationResolver нет распознавания критерия перекрёстка: " +
      "без него критерий получит общую диагностику вместо понятной.");
  if (!/case "nearbyjunction"/.test(resolver))
    throw new Error("В LocationResolver нет ветки обработки критерия «в радиусе от перекрёстка».");
  if (!/DistanceToNearest/.test(read("src/AssistQuestEditor.Domain/JunctionIndex.cs")))
    throw new Error("JunctionIndex не считает расстояние до ближайшего перекрёстка.");
}

// Импортёр обязан воспроизводить проверенный метод, а не «примерно похожий».
{
  const importer = read("ci/import_junctions.mjs");

  // Ищем ИСПОЛЬЗОВАНИЕ, а не объявление: константа может остаться в файле, но
  // перестать применяться в расчёте — тогда сшивка к линии и схлопывание
  // направлений молча исчезнут, а проверка по одному имени этого не заметит.
  const steps = [
    [/\bp\.d\s*<=\s*SLACK\b/, "без сшивки «конец -> линия» теряются Т-образные примыкания"],
    [/<\s*mergeRad\b/, "без схлопывания направлений дубли полос дают ложные перекрёстки"],
    [/function nodeAt\(/, "без склейки концов дуг граф не собирается"],
    [/endsNear\(px, pz, VOTE_WINDOW\)/, "без направлений в окружении ячейки теряются съезды к заглушкам"]
  ];

  for (const [pattern, why] of steps) {
    if (!pattern.test(importer))
      throw new Error(`Импортёр перекрёстков не выполняет шаг «${pattern.source}»: ${why}.`);
  }

  // ОБРЫВЫ НЕ ОТСЕКАЮТСЯ. То, что выглядит тупиком, — перекрытый от проезда
  // съезд; дизайнер карты намеренно достраивает перекрёстки, а проезд закрывает
  // заглушкой. Такой съезд ценен как квестовая локация, поэтому его нельзя
  // «почистить» фильтром. Проверяем, что обрывы НЕ фильтруются ни по длине
  // дороги, ни по числу веток: любое отсечение вырезало бы часть съездов.
  for (const bad of [
    [/dangling\s*=\s*\[?\.\.\.?\s*endMap\.values\(\)\]?\.filter\([^)]*(?:length|len|deg|branch)/,
      "обрывы фильтруются по длине или числу веток — часть съездов будет вырезана"],
    [/dangling\.filter\(\s*e\s*=>\s*Math\.hypot[^)]*>/, "короткие обрывы отбрасываются"]
  ]) {
    if (bad[0].test(importer))
      throw new Error(`Импортёр перекрёстков отсекает обрывы (${bad[0].source}): ${bad[1]}.`);
  }
}

console.log("Статические проверки окна перекрёстков: OK");

// --- Динамическая часть ---

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}" +
    "#junctionCanvas{display:block;width:100%;height:100%}" +
    "</style></head><body>" + junctionsHtml.slice(junctionsHtml.indexOf("<body>") + 6)
  );
  await page.addStyleTag({ content: theme });

  // Харнесс повторяет схему других smoke: обработчики складываются в Map, и
  // сообщение доставляется ИМЕННО через них, как это делает WebView2. Пустая
  // заглушка addEventListener сделала бы модуль глухим.
  await page.evaluate(() => {
    window.__sent = [];
    window.chrome = {
      webview: {
        listeners: new Map(),
        addEventListener(type, handler) { this.listeners.set(type, handler); },
        postMessage(payload) { window.__sent.push(payload); }
      }
    };
  });

  await page.addScriptTag({ content: junctionsJs });
  await page.waitForTimeout(150);

  // Фикстура: дороги крестом, два города и три узла в известном порядке.
  const payload = {
    type: "junction_review",
    reason: "smoke",
    // Четыре отрезка: горизонтальная и вертикальная дороги.
    roads: [-1000, 0, 1000, 0, 0, -1000, 0, 1000],
    cities: [{ name: "Тестоград", x: 0, z: 0 }, { name: "Дальний", x: 900, z: 900 }],
    // ВАЖЕН ПОРЯДОК: кнопки обязаны идти по нему, а не по координатам.
    junctions: [{ x: 0, z: 0 }, { x: 300, z: 0 }, { x: -200, z: 100 }],
    detectedCount: 3,
    excluded: [],
    added: [],
    excludedCount: 0,
    addedCount: 0,
    reviewPath: "C:\\test\\junctions.json"
  };

  await page.evaluate(p => {
    window.chrome.webview.listeners.get("message")({ data: JSON.stringify(p) });
  }, payload);
  await page.waitForTimeout(200);

  const state1 = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (state1.junctions !== 3)
    throw new Error("Окно не приняло список перекрёстков через chrome.webview. Состояние: " +
      JSON.stringify(state1));

  const counter1 = await page.locator("#junctionCounter").textContent();
  if (!counter1.includes("/ 3"))
    throw new Error("Счётчик узлов не показывает 3: " + counter1);

  /** Считает пиксели заданного цвета на канвасе. */
  const countColor = rgb => page.evaluate(({ r, g, b }) => {
    const canvas = document.getElementById("junctionCanvas");
    const data = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (Math.abs(data[i] - r) < 40 && Math.abs(data[i + 1] - g) < 40 && Math.abs(data[i + 2] - b) < 40)
        count++;
    }
    return count;
  }, rgb);

  const yellow = await countColor({ r: 255, g: 204, b: 0 });
  if (yellow < 20)
    throw new Error("Жёлтые точки перекрёстков не нарисованы: найдено " + yellow + " пикселей.");

  const green0 = await countColor({ r: 84, g: 209, b: 106 });
  if (green0 > 5)
    throw new Error("Зелёные точки нарисованы до добавления: " + green0 + " пикселей.");

  // --- «Следующий» идёт по порядку списка ---
  await page.locator("#junctionNext").click();
  await page.waitForTimeout(100);
  const focus1 = await page.evaluate(() => window.__assistJunctionReview.getState().focused);
  if (focus1 !== 0)
    throw new Error("Первый «следующий» должен выбрать узел 0, выбрано: " + focus1);

  // Проверяем НЕ только индекс, но и координаты узла. Без этого подмена порядка
  // обхода (например, сортировка узлов по координатам) осталась бы незамеченной:
  // индекс по-прежнему шёл бы 0, 1, 2, а «следующий» вёл бы к другим узлам.
  // Узлы нарочно заданы НЕ по возрастанию X, чтобы сортировка их переставила.
  const focusCoords = await page.evaluate(() => {
    const state = window.__assistJunctionReview.getState();
    return { x: state.focusX, z: state.focusZ };
  });
  if (Math.abs(focusCoords.x - 0) > 1e-6 || Math.abs(focusCoords.z - 0) > 1e-6)
    throw new Error("Первый «следующий» привёл не к первому найденному узлу " +
      "(0, 0), а к (" + focusCoords.x + ", " + focusCoords.z + "): порядок обхода " +
      "не совпадает с порядком списка.");

  // Счётчик — это то, что автор видит глазами. Он обязан показывать положение
  // в порядке списка, а не что-то своё: по нему автор понимает, далеко ли до
  // конца проверки.
  const counterAfterNext = await page.locator("#junctionCounter").textContent();
  if (!counterAfterNext.includes("1 / 3"))
    throw new Error("Счётчик не показывает «1 / 3» после первого «следующий»: " + JSON.stringify(counterAfterNext));

  await page.locator("#junctionNext").click();
  await page.waitForTimeout(100);
  const focus2 = await page.evaluate(() => window.__assistJunctionReview.getState().focused);
  if (focus2 !== 1)
    throw new Error("Второй «следующий» должен выбрать узел 1 (порядок списка), выбрано: " + focus2);

  await page.locator("#junctionPrev").click();
  await page.waitForTimeout(100);
  const focus3 = await page.evaluate(() => window.__assistJunctionReview.getState().focused);
  if (focus3 !== 0)
    throw new Error("«Предыдущий» не вернулся к узлу 0: " + focus3);

  // С первого узла «предыдущий» обязан уйти на последний. Без замыкания автор
  // упирался бы в начало списка и не мог бы дойти до конца с другой стороны.
  await page.locator("#junctionPrev").click();
  await page.waitForTimeout(100);
  const focus4 = await page.evaluate(() => window.__assistJunctionReview.getState().focused);
  if (focus4 !== 2)
    throw new Error("«Предыдущий» с узла 0 должен уйти на последний узел (2), выбрано: " + focus4);

  // --- Мультивыбор и исключение ---
  //
  // ВАЖНО: перед сканированием канваса возвращаем вид «Вся карта». Кнопки
  // «следующий/предыдущий» приближают камеру к выбранному узлу, и без сброса
  // часть узлов уезжает за кадр — в первой версии проверки это дало «2 кластера
  // вместо 3», хотя узлов было ровно три.
  await page.locator("#junctionFitAll").click();
  await page.waitForTimeout(150);

  // Координаты узлов берутся СКАНИРОВАНИЕМ канваса по цвету, а не пересчётом
  // worldToScreen: модуль не экспортирует камеру, и любой внешний расчёт
  // разошёлся бы с ним после fitAll. Скан даёт настоящие пиксели узлов.
  const clusters = await page.evaluate(() => {
    const canvas = document.getElementById("junctionCanvas");
    const w = canvas.width, h = canvas.height;
    const data = canvas.getContext("2d").getImageData(0, 0, w, h).data;
    const dpr = window.devicePixelRatio || 1;
    const points = [];
    for (let y = 0; y < h; y += 2) {
      for (let x = 0; x < w; x += 2) {
        const i = (y * w + x) * 4;
        if (Math.abs(data[i] - 255) < 40 && Math.abs(data[i + 1] - 204) < 40 && Math.abs(data[i + 2]) < 60) {
          // Сливаем близкие точки в один кластер.
          const near = points.find(p => Math.hypot(p.x - x / dpr, p.y - y / dpr) < 18);
          if (near) {
            near.n++;
            near.x = (near.x * (near.n - 1) + x / dpr) / near.n;
            near.y = (near.y * (near.n - 1) + y / dpr) / near.n;
          } else {
            points.push({ x: x / dpr, y: y / dpr, n: 1 });
          }
        }
      }
    }
    return points;
  });

  if (clusters.length !== 3)
    throw new Error("На канвасе должно быть 3 жёлтых кластера (по узлу), найдено: " + clusters.length);

  const rect = await page.evaluate(() => {
    const r = document.getElementById("junctionCanvas").getBoundingClientRect();
    return { left: r.left, top: r.top };
  });

  // Клик по кластеру 1, затем Ctrl+клик по кластеру 2.
  await page.mouse.click(rect.left + clusters[1].x, rect.top + clusters[1].y);
  await page.waitForTimeout(80);
  await page.keyboard.down("Control");
  await page.mouse.click(rect.left + clusters[2].x, rect.top + clusters[2].y);
  await page.keyboard.up("Control");
  await page.waitForTimeout(80);

  const multi = await page.evaluate(() => window.__assistJunctionReview.getState().selected);
  if (multi !== 2)
    throw new Error("Мультивыбор не набрал 2 узла (Ctrl+клик), выбрано: " + multi);

  await page.locator("#junctionExclude").click();
  await page.waitForTimeout(120);

  const afterExclude = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (afterExclude.excluded !== 2)
    throw new Error("«Исключить» не записало 2 узла в исключения: " + afterExclude.excluded);

  // --- Клик по пустому месту создаёт зелёную точку, повторный убирает ---
  const emptyX = rect.left + 80, emptyY = rect.top + 80;
  await page.mouse.click(emptyX, emptyY);
  await page.waitForTimeout(120);

  const added1 = await page.evaluate(() => window.__assistJunctionReview.getState().added);
  if (added1 !== 1)
    throw new Error("Клик по пустому месту не создал узел: added=" + added1);

  const green1 = await countColor({ r: 84, g: 209, b: 106 });
  if (green1 < 15)
    throw new Error("Добавленный узел не нарисован зелёным: " + green1 + " пикселей.");

  await page.mouse.click(emptyX, emptyY);
  await page.waitForTimeout(120);

  const added2 = await page.evaluate(() => window.__assistJunctionReview.getState().added);
  if (added2 !== 0)
    throw new Error("Повторный клик не убрал добавленный узел: added=" + added2);

  // --- Сохранение отправляет и исключённые, и добавленные ---
  // Сначала снова добавим узел, чтобы в сообщении были оба списка.
  await page.mouse.click(emptyX, emptyY);
  await page.waitForTimeout(100);

  await page.locator("#junctionSave").click();
  await page.waitForTimeout(150);

  const saved = await page.evaluate(() =>
    window.__sent.filter(item => item.action === "save_junction_review"));
  if (saved.length !== 1)
    throw new Error("Сохранение не отправило ровно одно сообщение: " + saved.length);

  const message = saved[0];
  if (!Array.isArray(message.excluded) || message.excluded.length !== 2)
    throw new Error("В сообщении сохранения нет 2 исключённых узлов: " +
      JSON.stringify(message.excluded));

  if (!Array.isArray(message.added) || message.added.length !== 1)
    throw new Error("В сообщении сохранения нет 1 добавленного узла: " +
      JSON.stringify(message.added));

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Окно проверки перекрёстков: OK (узлы, порядок, мультивыбор, исключение, добавление)");
} finally {
  await browser.close();
}
