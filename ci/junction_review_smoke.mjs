import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

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
//
// Правило живёт в ДОМЕНЕ (JunctionReview.cs), а файл — в App: правило проверяется
// тестами домена, работа с файлом остаётся в приложении. Проверять правило надо
// там, где оно лежит, иначе проверка прошла бы по одному факту наличия имени.
{
  const domain = read("src/AssistQuestEditor.Domain/JunctionReview.cs");
  const store = read("src/AssistQuestEditor.App/JunctionReviewStore.cs");

  if (!/Apply\(IReadOnlyList<JunctionPoint>/.test(domain))
    throw new Error("В JunctionReview нет метода Apply: правки не применяются к списку перекрёстков.");
  if (!/IsExcluded\(/.test(domain))
    throw new Error("В JunctionReview нет проверки исключения узла: файл исключений ни на что не влияет.");
  if (!/IsAdded\(/.test(domain))
    throw new Error("В JunctionReview нет проверки добавленного узла: приоритет " +
      "добавленного над исключением проверить нечем.");

  // Одна копия правила, а не две: запись файла и логика правки больше не в одном
  // файле, и второй экземпляр JunctionReview разошёлся бы с первым при правке.
  if (/record JunctionReview\(/.test(store))
    throw new Error("JunctionReview объявлен дважды (в App и в Domain): " +
      "две копии правила разойдутся, и правки начнут применяться по-разному.");

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

const { browser } = await openBrowser();
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

  // --- Режимы клика: радиобокс ---
  //
  // Режим меняет СМЫСЛ клика по карте, поэтому проверяется не «радиокнопки есть
  // в разметке», а поведение каждого режима. Ошибка здесь тихая: клик просто
  // делает не то, а панель выглядит исправной.

  // Разметка: три режима и кнопки полигона.
  for (const value of ["polygon", "delete", "add"]) {
    if (!junctionsHtml.includes(`value="${value}"`))
      throw new Error(`В junctions.html нет режима «${value}» в радиобоксе.`);
  }

  if (!junctionsHtml.includes('id="junctionPolygonClose"') ||
      !junctionsHtml.includes('id="junctionPolygonClear"'))
    throw new Error("В junctions.html нет кнопок полигона «Замкнуть» и «Очистить полигон».");

  // По умолчанию — «Добавление»: окно открывается ради проверки списка, и
  // случайный клик в режиме удаления уносил бы перекрёсток молча.
  //
  // Проверка статическая И поведенческая: браузерная ловит снятый checked у
  // разметки, а статическая — подмену на другой режим (когда `checked` стоит не
  // у «Добавления», браузерный запрос всё равно вернёт какой-то режим, и без
  // явного сравнения с разметкой проверка пропустила бы перенос галочки).
  if (!/name="junctionMode" value="add" checked/.test(junctionsHtml))
    throw new Error("В разметке режим «Добавление» не отмечен по умолчанию: " +
      "случайный клик в режиме удаления исключил бы перекрёсток незаметно.");

  const defaultMode = await page.evaluate(() =>
    document.querySelector('input[name="junctionMode"]:checked')?.value);
  if (defaultMode !== "add")
    throw new Error("По умолчанию выбран режим «" + defaultMode + "», а должно быть «add»: " +
      "случайный клик в режиме удаления исключил бы перекрёсток незаметно.");

  const stateDefault = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (stateDefault.mode !== "add")
    throw new Error("Состояние окна не отражает режим по умолчанию: " + stateDefault.mode);

  // Свежий прогон: исключения и добавления от предыдущих шагов сбрасываются
  // повторной подачей данных, иначе счётчики смешаются.
  await page.evaluate(p => {
    window.chrome.webview.listeners.get("message")({ data: JSON.stringify(p) });
  }, payload);
  await page.waitForTimeout(120);
  await page.locator("#junctionFitAll").click();
  await page.waitForTimeout(150);

  /**
   * Жёлтые кластеры (узлы) на канвасе.
   *
   * Скан по цвету, а не пересчёт worldToScreen: модуль не экспортирует камеру, и
   * любой внешний расчёт разошёлся бы с ним после fitAll. Кластеры нужны как
   * настоящие точки попадания для кликов.
   */
  const scanClusters = () => page.evaluate(() => {
    const canvas = document.getElementById("junctionCanvas");
    const w = canvas.width, h = canvas.height;
    const data = canvas.getContext("2d").getImageData(0, 0, w, h).data;
    const dpr = window.devicePixelRatio || 1;
    const points = [];
    for (let y = 0; y < h; y += 2) {
      for (let x = 0; x < w; x += 2) {
        const i = (y * w + x) * 4;
        if (Math.abs(data[i] - 255) < 40 && Math.abs(data[i + 1] - 204) < 40 && Math.abs(data[i + 2]) < 60) {
          const near = points.find(p => Math.hypot(p.x - x / dpr, p.y - y / dpr) < 18);
          if (near) { near.n++; } else { points.push({ x: x / dpr, y: y / dpr, n: 1 }); }
        }
      }
    }
    return points;
  });

  let freshClusters = await scanClusters();

  // Второй проход после повторного «Вся карта»: навигация в предыдущих шагах
  // приближала камеру, и часть узлов могла уехать за кадр.
  if (freshClusters.length !== 3) {
    await page.locator("#junctionFitAll").click();
    await page.waitForTimeout(220);
    freshClusters = await scanClusters();
  }

  if (freshClusters.length !== 3)
    throw new Error("Перед проверкой режимов на канвасе не 3 жёлтых кластера: " +
      freshClusters.length);

  // Точка попадания — центр кластера.
  const hit = i => ({ x: rect.left + freshClusters[i].x, y: rect.top + freshClusters[i].y });

  // Габарит узлов: по нему строятся вершины полигона с запасом, поэтому контур
  // заведомо накрывает все три узла. Жёсткие координаты углов («30, 30» и т. п.)
  // НЕ годятся: после fitAll узлы лежат вокруг центра канваса, и фиксированный
  // прямоугольник оказывается в пустом месте — проверка тогда падала бы на
  // «выделено 0» при исправном коде.
  const bounds = freshClusters.reduce((acc, point) => ({
    minX: Math.min(acc.minX, point.x), maxX: Math.max(acc.maxX, point.x),
    minY: Math.min(acc.minY, point.y), maxY: Math.max(acc.maxY, point.y)
  }), { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity });

  const margin = 40;
  const cornersOnCanvas = [
    { x: bounds.minX - margin, y: bounds.minY - margin },
    { x: bounds.maxX + margin, y: bounds.minY - margin },
    { x: bounds.maxX + margin, y: bounds.maxY + margin },
    { x: bounds.minX - margin, y: bounds.maxY + margin }
  ].map(point => ({
    // Углы прижимаются к видимой части канваса: клик за его пределами
    // не доходит до обработчика и вершина не ставится.
    x: Math.max(4, Math.min(900, point.x)),
    y: Math.max(4, Math.min(600, point.y))
  }));

  const corners = cornersOnCanvas.map(point => ({
    x: rect.left + point.x, y: rect.top + point.y
  }));

  // --- Хувер: курсор и подсветка ---
  //
  // Без подсветки автор не знает, какая из плотно стоящих точек отзовётся на
  // клик, а в режиме удаления это означает потерю не той точки.
  await page.mouse.move(hit(0).x, hit(0).y);
  await page.waitForTimeout(120);

  const hovered = await page.evaluate(() => window.__assistJunctionReview.getState().hovered);
  if (hovered < 0)
    throw new Error("Наведение на узел не подсвечивает его: hovered=" + hovered);

  const hoverCursor = await page.evaluate(() =>
    document.getElementById("junctionCanvas").style.cursor);
  if (hoverCursor !== "pointer")
    throw new Error("Курсор над узлом не меняется на «pointer»: " + JSON.stringify(hoverCursor));

  // Красная подсветка обязана быть видна ПИКСЕЛЯМИ: одно состояние hovered
  // прошло бы и при подсветке, которая ничего не рисует.
  const hoverRed = await countColor({ r: 255, g: 123, b: 114 });
  if (hoverRed < 20)
    throw new Error("Подсветка узла под курсором не нарисована: " + hoverRed + " пикселей красного.");

  // Уход курсора с КАНВАСА снимает подсветку. Именно с канваса, а не на пустое
  // место картины: перемещение внутри канваса уже ловится pointermove, поэтому
  // такой шаг не измерил бы обработчик pointerleave вообще. Уводим курсор на
  // панель — там pointermove на канвасе не случается, и без pointerleave
  // подсвеченная точка осталась бы красной и выглядела бы выбранной.
  await page.mouse.move(rect.left + 5, rect.top - 30);
  await page.waitForTimeout(150);

  const afterLeave = await page.evaluate(() => window.__assistJunctionReview.getState().hovered);
  if (afterLeave !== -1)
    throw new Error("Уход курсора с канваса не снял подсветку: hovered=" + afterLeave);

  // Возвращаем курсор на канвас: следующие шаги кликают по карте, и уход
  // курсора на панель оставил бы его вне холста.
  await page.mouse.move(hit(0).x, hit(0).y);
  await page.waitForTimeout(100);

  // --- Режим удаления: клик по узлу исключает сразу, без кнопок ---
  await page.evaluate(() => window.__assistJunctionReview.setMode("delete"));
  await page.waitForTimeout(80);

  // Радиокнопка обязана показывать ТОТ ЖЕ режим, по которому работает клик.
  // Расхождение — тихая ложь интерфейса: автор видит «Добавление», а клик
  // исключает узлы. Проверяется и для программной установки режима, и для
  // пользовательской (ниже, кликом по радиокнопке).
  const deleteChecked = await page.evaluate(() =>
    document.querySelector('input[name="junctionMode"]:checked')?.value);
  if (deleteChecked !== "delete")
    throw new Error("После setMode(\"delete\") радиокнопка показывает «" + deleteChecked +
      "»: панель расходится с режимом, по которому работает клик.");

  const beforeDelete = await page.evaluate(() => window.__assistJunctionReview.getState().excluded);
  await page.mouse.click(hit(0).x, hit(0).y);
  await page.waitForTimeout(120);

  const afterDelete = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (afterDelete.excluded !== beforeDelete + 1)
    throw new Error("Режим удаления не исключил узел кликом: было " + beforeDelete +
      ", стало " + afterDelete.excluded);

  // Клик по ПУСТОМУ месту в режиме удаления не должен добавлять узел: автор
  // промахнулся мимо точки, а не просил создать новую.
  const addedBefore = afterDelete.added;
  await page.mouse.click(rect.left + 60, rect.top + 60);
  await page.waitForTimeout(120);

  const afterEmptyDelete = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (afterEmptyDelete.added !== addedBefore)
    throw new Error("В режиме удаления клик по пустому месту добавил узел: " +
      addedBefore + " -> " + afterEmptyDelete.added);

  // --- Режим выделения многоугольником ---
  await page.evaluate(() => window.__assistJunctionReview.setMode("polygon"));
  await page.waitForTimeout(80);

  // Кнопки полигона видны только в своём режиме.
  const actionsVisible = await page.evaluate(() =>
    document.getElementById("junctionPolygonActions").classList.contains("visible"));
  if (!actionsVisible)
    throw new Error("В режиме выделения кнопки полигона не показаны.");

  // Многоугольник вокруг ВСЕХ узлов: углы посчитаны по габариту узлов с запасом,
  // поэтому контур заведомо накрывает все три.
  for (const corner of corners) {
    await page.mouse.click(corner.x, corner.y);
    await page.waitForTimeout(60);
  }

  const polygonState = await page.evaluate(() => window.__assistJunctionReview.getState());

  // Проверка самозамыкания идёт ПЕРВОЙ: если многоугольник замкнулся сам после
  // третьей вершины, четвёртый клик уже не добавит вершину, и проверка числа
  // вершин свалилась бы на другой причине, замаскировав настоящий дефект.
  if (polygonState.polygonClosed)
    throw new Error("Полигон замкнулся сам, без клика по первой вершине: " +
      "выделение сработает, пока автор ещё ставит вершины.");
  if (polygonState.polygonVertices !== 4)
    throw new Error("Клики в режиме выделения не поставили 4 вершины полигона: " +
      polygonState.polygonVertices);
  if (polygonState.selected !== 0)
    throw new Error("Незамкнутый полигон уже что-то выделил: " + polygonState.selected);

  // Замыкание: клик по первой вершине. Здесь выделяются все узлы внутри.
  await page.mouse.click(corners[0].x, corners[0].y);
  await page.waitForTimeout(150);

  const closedState = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (!closedState.polygonClosed)
    throw new Error("Клик по первой вершине не замкнул полигон.");
  if (closedState.polygonVertices !== 4)
    throw new Error("Замыкание добавило лишнюю вершину: " + closedState.polygonVertices);
  if (closedState.selected !== 3)
    throw new Error("Замыкание полигона выделило не все 3 узла: " + closedState.selected);

  // Очистка полигона снимает и контур, и выделение.
  //
  // Проверяется ДО пакетного исключения намеренно: после «Исключить» выделение
  // пусто по построению, и сброс в «Очистить» остался бы непроверенным.
  await page.locator("#junctionPolygonClear").click();
  await page.waitForTimeout(150);

  const cleared = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (cleared.polygonVertices !== 0 || cleared.polygonClosed || cleared.selected !== 0)
    throw new Error("«Очистить полигон» не сбросил контур и выделение: " +
      JSON.stringify(cleared));

  // Очистка полигона не трогает РАБОТУ автора: исключённые узлы и добавленные
  // точки обязаны остаться. Кнопка называется «Очистить полигон», а не
  // «отменить всё», и тихое стирание правок здесь было бы потерей работы.
  if (cleared.excluded !== 1)
    throw new Error("«Очистить полигон» тронул исключённые узлы: было 1, стало " +
      cleared.excluded);

  // Пакетное исключение: то, ради чего режим и заведён. Контур рисуется заново,
  // потому что предыдущий только что очищен.
  for (const corner of corners) {
    await page.mouse.click(corner.x, corner.y);
    await page.waitForTimeout(50);
  }
  await page.mouse.click(corners[0].x, corners[0].y);
  await page.waitForTimeout(150);

  const reselected = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (reselected.selected !== 3)
    throw new Error("Повторное замыкание полигона выделило не все 3 узла: " +
      reselected.selected);

  const excludedBefore = reselected.excluded;
  await page.locator("#junctionExclude").click();
  await page.waitForTimeout(150);

  const bulk = await page.evaluate(() => window.__assistJunctionReview.getState());

  // Ровно +2, а не +3: один узел уже исключён в проверке режима удаления, и
  // повторное исключение не должно добавлять дубль. Точное число проверяет СРАЗУ
  // два инварианта — пакетное исключение работает и защита от двойного счёта
  // держится (иначе в файле правок оказались бы две записи об одном узле).
  if (bulk.excluded !== excludedBefore + 2)
    throw new Error("Пакетное исключение по полигону: ожидалось +2 (третий узел уже " +
      "был исключён ранее, дубль не должен появиться), было " + excludedBefore +
      ", стало " + bulk.excluded);

  // Смена режима убирает кнопки полигона И сбрасывает контур с выделением:
  // незамкнутый контур в другом режиме не имеет смысла, а оставшееся выделение
  // выглядело бы как результат нового режима.
  for (const corner of corners) {
    await page.mouse.click(corner.x, corner.y);
    await page.waitForTimeout(50);
  }
  await page.mouse.click(corners[0].x, corners[0].y);
  await page.waitForTimeout(150);

  const beforeSwitch = await page.evaluate(() => window.__assistJunctionReview.getState());
  if (!beforeSwitch.polygonClosed || beforeSwitch.polygonVertices !== 4)
    throw new Error("Перед сменой режима полигон не нарисован: " +
      JSON.stringify(beforeSwitch));

  // Режим меняется КЛИКОМ по радиокнопке — так, как это делает автор. Это
  // проверяет и обработчик change, и то, что панель остаётся согласованной.
  await page.locator('input[name="junctionMode"][value="add"]').click();
  await page.waitForTimeout(120);

  const backToAdd = await page.evaluate(() => ({
    visible: document.getElementById("junctionPolygonActions").classList.contains("visible"),
    checked: document.querySelector('input[name="junctionMode"]:checked')?.value,
    state: window.__assistJunctionReview.getState()
  }));

  // Порядок ассертов: СНАЧАЛА причина, потом следствие. Смена режима — причина,
  // видимость кнопок полигона — следствие. При обратном порядке сломанный
  // обработчик change сообщал бы «кнопки остались видны», и автор искал бы
  // дефект в CSS вместо обработчика.
  if (backToAdd.checked !== "add" || backToAdd.state.mode !== "add")
    throw new Error("Клик по радиокнопке «Добавление» не переключил режим: " +
      JSON.stringify(backToAdd.checked) + " / " + backToAdd.state.mode);
  if (backToAdd.visible)
    throw new Error("Кнопки полигона остались видны вне режима выделения.");
  if (backToAdd.state.polygonVertices !== 0 || backToAdd.state.polygonClosed)
    throw new Error("Смена режима не сбросила полигон: " + JSON.stringify(backToAdd.state));
  if (backToAdd.state.selected !== 0)
    throw new Error("Смена режима оставила выделение: " + backToAdd.state.selected);

  // --- Цвета исключённых узлов: красный до сохранения, серый после ---
  //
  // Без этого все точки жёлтые, и автор путается, где уже вычищено. Проверяются
  // ЧЕТЫРЕ состояния: жёлтая (не проверена), красная (исключена, не записана),
  // серая (записана) и зелёная (добавлена вручную).
  {
    // ОТДЕЛЬНАЯ фикстура: узел (300, 0) уже исключён в файле, а (−200, 100) —
    // добавлен вручную. Основная фикстура намеренно без правок: на ней
    // проверяются счётчики других разделов, и подмешивать туда исключение с
    // добавлением значило бы пересчитывать их все.
    const payloadWithSaved = {
      ...payload,
      excluded: [{ x: 300, z: 0 }],
      added: [{ x: -200, z: 100 }],
      excludedCount: 1,
      addedCount: 1
    };

    await page.evaluate(p => {
      window.chrome.webview.listeners.get("message")({ data: JSON.stringify(p) });
    }, payloadWithSaved);
    await page.waitForTimeout(150);
    await page.locator("#junctionFitAll").click();
    await page.waitForTimeout(150);

    const beforeColours = await page.evaluate(() => window.__assistJunctionReview.getState());
    if (beforeColours.freshExcluded !== 0 || beforeColours.savedExcluded !== 1)
      throw new Error("Перед проверкой цветов состояние неверно: свежих " +
        beforeColours.freshExcluded + ", сохранённых " + beforeColours.savedExcluded +
        " (ожидалось 0 и 1: один узел исключён ранее и пришёл от Host).");

    // Серые точки обязаны быть НАРИСОВАНЫ: одно состояние savedExcluded прошло бы
    // и при полностью жёлтой карте.
    //
    // Порог скромный (точка радиусом 5 — это ~70 пикселей, минус крестик внутри),
    // потому что важен факт наличия, а не точное число. Ложных срабатываний нет:
    // светлее дорог (102,113,127) этот серый на 51 по каналу, и допуск ±24 не
    // дотягивается до цвета дорог.
    const greyBefore = await countColor({ r: 154, g: 164, b: 178 });
    if (greyBefore < 15)
      throw new Error("Исключённые ранее узлы не нарисованы серыми: " + greyBefore +
        " пикселей. Автор не увидит, что уже вычищено.");

    // Исключаем узел в текущем проходе: он обязан стать КРАСНЫМ.
    await page.evaluate(() => window.__assistJunctionReview.setMode("delete"));
    await page.waitForTimeout(80);

    const clustersForColour = await scanClusters();
    if (clustersForColour.length === 0)
      throw new Error("Перед проверкой цветов не найдено ни одного жёлтого узла.");

    await page.mouse.click(rect.left + clustersForColour[0].x, rect.top + clustersForColour[0].y);
    await page.waitForTimeout(150);

    const afterExclude = await page.evaluate(() => window.__assistJunctionReview.getState());
    if (afterExclude.freshExcluded !== 1)
      throw new Error("Свежее исключение не отмечено как несохранённое: " +
        afterExclude.freshExcluded);

    const redAfter = await countColor({ r: 255, g: 77, b: 77 });
    if (redAfter < 15)
      throw new Error("Исключённый в текущем проходе узел не нарисован красным: " +
        redAfter + " пикселей. Автор не увидит, где уже не нужно исключать.");

    // Сохранение переводит красные в серые.
    await page.evaluate(() => {
      window.chrome.webview.listeners.get("message")({
        data: JSON.stringify({ type: "junction_review_saved", excludedCount: 2, addedCount: 1 })
      });
    });
    await page.waitForTimeout(150);

    const afterSave = await page.evaluate(() => window.__assistJunctionReview.getState());
    if (afterSave.freshExcluded !== 0)
      throw new Error("После сохранения остались несохранённые исключения: " +
        afterSave.freshExcluded + " — красные точки показывали бы «не записано» там, " +
        "где уже записано.");
    if (afterSave.savedExcluded !== 2)
      throw new Error("После сохранения сохранённых исключений не 2, а " +
        afterSave.savedExcluded);

    const redAfterSave = await countColor({ r: 255, g: 77, b: 77 });
    if (redAfterSave >= 15)
      throw new Error("После сохранения на карте остались красные точки: " + redAfterSave +
        " пикселей. Красный обязан смениться на серый.");

    const greyAfterSave = await countColor({ r: 154, g: 164, b: 178 });
    if (greyAfterSave < 20)
      throw new Error("После сохранения серых точек не прибавилось: " + greyAfterSave +
        " пикселей серого.");

    // Зелёная добавленная точка остаётся зелёной — это третье состояние, и его
    // нельзя перекрасить ни в красный, ни в серый.
    const greenAfterSave = await countColor({ r: 84, g: 209, b: 106 });
    if (greenAfterSave < 15)
      throw new Error("Зелёная добавленная точка не сохранила свой цвет после записи: " +
        greenAfterSave + " пикселей зелёного.");
  }

  // --- Добавленный узел ставится ПОВЕРХ вычищенного кольца ---
  //
  // Главный сценарий автора: кольцо вычищено (серые точки стоят сплошняком), и
  // туда надо поставить ОДИН зелёный узел для квеста. Проверка «рядом уже есть
  // найденный узел» не должна запрещать это — она для промаха мимо ЖЁЛТОЙ точки.
  {
    // Фикстура с СОХРАНЁННЫМ исключением: серый узел должен быть на карте, иначе
    // проверять «добавление поверх вычищенного» не по чему.
    const payloadWithSaved = {
      ...payload,
      excluded: [{ x: 300, z: 0 }],
      excludedCount: 1
    };

    await page.evaluate(p => {
      window.chrome.webview.listeners.get("message")({ data: JSON.stringify(p) });
    }, payloadWithSaved);
    await page.waitForTimeout(150);
    await page.locator("#junctionFitAll").click();
    await page.waitForTimeout(150);

    await page.evaluate(() => window.__assistJunctionReview.setMode("add"));
    await page.waitForTimeout(80);

    const stateAdd = await page.evaluate(() => window.__assistJunctionReview.getState());
    const addedBefore = stateAdd.added;

    if (stateAdd.savedExcluded !== 1)
      throw new Error("В фикстуре нет сохранённого исключения: " + stateAdd.savedExcluded);

    // Позиция сохранённого исключения — берём её из состояния, а не угадываем:
    // в фикстуре исключён узел (300, 0), и он рисуется СЕРОЙ точкой.
    //
    // Скан идёт ПО КАЖДОМУ пикселю, а не через два: у серой точки всего ~23
    // пикселя (радиус 5 плюс тёмный крестик внутри), и шаг в 2 пикселя по обеим
    // осям пропускал их все — проверка падала на «точка не найдена» при
    // исправном коде.
    const excludedScreen = await page.evaluate(() => {
      const canvas = document.getElementById("junctionCanvas");
      const w = canvas.width, h = canvas.height;
      const data = canvas.getContext("2d").getImageData(0, 0, w, h).data;
      const dpr = window.devicePixelRatio || 1;

      // Собираем серые пиксели и берём их центр: одиночный пиксель может попасть
      // на крестик внутри точки, а центр устойчив.
      const hits = [];
      for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
          const i = (y * w + x) * 4;
          if (Math.abs(data[i] - 154) < 24 && Math.abs(data[i + 1] - 164) < 24 &&
              Math.abs(data[i + 2] - 178) < 24) {
            hits.push({ x: x / dpr, y: y / dpr });
          }
        }
      }

      if (!hits.length) return null;

      return {
        x: hits.reduce((sum, p) => sum + p.x, 0) / hits.length,
        y: hits.reduce((sum, p) => sum + p.y, 0) / hits.length,
        count: hits.length
      };
    });

    if (!excludedScreen)
      throw new Error("Серая точка исключённого узла не найдена на канвасе.");

    // Клик РОВНО по серой точке: раньше добавление здесь запрещалось.
    await page.mouse.click(rect.left + excludedScreen.x, rect.top + excludedScreen.y);
    await page.waitForTimeout(150);

    const stateAfterAdd = await page.evaluate(() => window.__assistJunctionReview.getState());
    if (stateAfterAdd.added !== addedBefore + 1)
      throw new Error("Поверх вычищенного узла нельзя поставить новый: added " +
        addedBefore + " -> " + stateAfterAdd.added + ". Автор не сможет вернуть " +
        "перекрёсток в вычищенном месте.");

    const greenOnGrey = await countColor({ r: 84, g: 209, b: 106 });
    if (greenOnGrey < 15)
      throw new Error("Зелёная точка поверх вычищенного места не нарисована.");

    // Убираем добавленное, чтобы не влиять на итоговый подсчёт.
    await page.mouse.click(rect.left + excludedScreen.x, rect.top + excludedScreen.y);
    await page.waitForTimeout(120);

    // И РЯДОМ с серой точкой — на несколько пикселей в стороне, но в пределах
    // 10 м по миру. Здесь работает проверка «нет ли рядом найденного узла», и она
    // тоже не должна мешать: у вычищенного кольца точки стоят сплошняком, и автор
    // ставит свой узел не пиксель-в-пиксель.
    const beforeNear = await page.evaluate(() => window.__assistJunctionReview.getState());
    await page.mouse.click(rect.left + excludedScreen.x + 6, rect.top + excludedScreen.y + 6);
    await page.waitForTimeout(150);

    const afterNear = await page.evaluate(() => window.__assistJunctionReview.getState());
    if (afterNear.added !== beforeNear.added + 1)
      throw new Error("Рядом с вычищенным узлом нельзя поставить новый: added " +
        beforeNear.added + " -> " + afterNear.added + ". У вычищенного кольца " +
        "точки стоят сплошняком, и автор ставит свой узел не пиксель-в-пиксель.");

    await page.mouse.click(rect.left + excludedScreen.x + 6, rect.top + excludedScreen.y + 6);
    await page.waitForTimeout(120);
  }

  if (errors.length)
    throw new Error("pageerror: " + errors.join(" | "));

  console.log("Окно проверки перекрёстков: OK (узлы, порядок, мультивыбор, исключение, добавление)");
  console.log("Режимы клика: OK (выделение полигоном, удаление кликом, добавление)");
  console.log("Хувер: OK (курсор pointer, красная подсветка, снятие при уходе)");
  console.log("Цвета исключённых: OK (красный до записи, серый после, зелёный остаётся)");
  console.log("Добавление поверх вычищенного: OK (приоритет ручного узла)");
} finally {
  await closeBrowser(browser);
}
