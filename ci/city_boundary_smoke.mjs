import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка окна рисования черт городов и связки критериев с панелью локаций.
//
// Что проверяется и почему:
//  1. Окно ПРИНИМАЕТ сообщения Host через chrome.webview и слушает window.
//     Модуль, подписанный только на один канал, выглядит рабочим (карта
//     нарисуется фоном), но данные до него не доходят — ошибка уже была в этом
//     репозитории у locationEditor.js и interface.js.
//  2. Клик по карте ставит ВЕРШИНУ контура, а не выбирает точку: единственное
//     взаимодействие с картой в этом окне — рисование многоугольника.
//  3. Контур замыкается кликом по первой вершине и уходит в Host ТОЛЬКО
//     замкнутым: незамкнутый контур — не область, и сохранять его нельзя.
//  4. Точки НЕ выделяются: клик не отправляет никаких действий выбора.
//  5. Панель локаций знает новые критерии, и ЦЕПОЧКА имён согласована:
//     список CRITERIA → ветка параметров → SupportedCriteria → case → предикат.
//     Расхождение даёт «критерий молча не поддерживается» без единой ошибки.
//  6. Критерий «В черте города» предлагает СПИСОК городов: без подсказок автор
//     вводит название по памяти, опечатка даёт «у города нет черты».

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const boundariesJs = read("src/AssistQuestEditor.App/Web/cityBoundaries.js");
const boundariesHtml = read("src/AssistQuestEditor.App/Web/cityBoundaries.html");
const locationEditorJs = read("src/AssistQuestEditor.App/Web/locationEditor.js");
const resolverCs = read("src/AssistQuestEditor.Domain/LocationResolver.cs");
const indexCs = read("src/AssistQuestEditor.Domain/CityBoundaryIndex.cs");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

// --- Статическая часть: окно ---

for (const channel of ["chrome.webview.addEventListener", 'window.addEventListener("message"']) {
  if (!boundariesJs.includes(channel))
    throw new Error(`cityBoundaries.js не слушает ${channel}: сообщения Host не дойдут, ` +
      "и карта останется пустой без единой ошибки на странице.");
}

if (!boundariesHtml.includes("cityBoundaries.js"))
  throw new Error("cityBoundaries.html не подключает cityBoundaries.js.");

for (const id of ["boundaryClose", "boundarySave", "boundaryUndo", "boundaryClear", "boundaryFitAll"]) {
  if (!boundariesHtml.includes(`id="${id}"`))
    throw new Error(`В cityBoundaries.html нет кнопки #${id}.`);
}

// Единственное взаимодействие с картой — рисование: в модуле не должно быть ни
// выбора точек, ни отправки действий выбора. Иначе окно начнёт спорить с задачей.
for (const forbidden of ["select_point", "toggleJunction", "selected.add", "POINT_RADIUS"]) {
  if (boundariesJs.includes(forbidden))
    throw new Error(`cityBoundaries.js содержит «${forbidden}»: окно должно ТОЛЬКО ` +
      "рисовать контур, точки не выделяются.");
}

// Контур замыкается флагом, а не совпадением координат: иначе контур, у которого
// последняя вершина случайно легла на первую, считался бы замкнутым без ведома автора.
if (!/let closed = false/.test(boundariesJs))
  throw new Error("cityBoundaries.js не хранит признак замкнутости: контур нельзя " +
    "отличить от незамкнутого, и в Host уйдёт область с дырой.");

if (!/action: "save_city_boundary"/.test(boundariesJs))
  throw new Error("cityBoundaries.js не отправляет save_city_boundary: черту негде сохранить.");

// Host обязан определять город внутри контура, а не доверять названию от окна:
// у него полный список точек мира.
{
  const formCs = read("src/AssistQuestEditor.App/Host/CityBoundaryForm.cs");
  if (!/point\.IsCity/.test(formCs))
    throw new Error("CityBoundaryForm не ищет город по признаку IsCity: название черты " +
      "окажется произвольным.");
  if (!/inside\.Length > 1/.test(formCs))
    throw new Error("CityBoundaryForm не отвергает контур с НЕСКОЛЬКИМИ городами внутри: " +
      "название черты станет неоднозначным.");
  if (!/inside\.Length == 0/.test(formCs))
    throw new Error("CityBoundaryForm не отвергает пустой контур: появится черта без названия, " +
      "которую не найдёт ни один критерий.");
  if (!/MissingCities/.test(read("src/AssistQuestEditor.App/Host/CityBoundaryForm.cs")))
    throw new Error("CityBoundaryForm не сообщает о городах без черты: после расширения карты " +
      "пропущенные города останутся незамеченными.");
}

// Название черты собирается в домене: его видят окно, панель и журнал.
if (!/Черта города /.test(indexCs))
  throw new Error("CityBoundaryIndex не собирает название черты «Черта города X».");

// --- Цепочка имён критериев ---

// Имя берётся ИЗ СТРОКИ СПИСКА панели, а не ищется по файлу: поиск «имя есть
// где-то в файле» проходит на сломанном коде (имя остаётся в ветке параметров).
const panelNames = [];
for (const match of locationEditorJs.matchAll(/\["([A-Za-z]+)",\s*"([^"]+)"\]/g)) {
  panelNames.push({ type: match[1], label: match[2] });
}

const cityPanel = panelNames.find(item => item.label === "В любом городе");
const boundaryPanel = panelNames.find(item => item.label === "В черте города");

if (!cityPanel) throw new Error("В списке CRITERIA панели локаций нет критерия «В любом городе».");
if (!boundaryPanel) throw new Error("В списке CRITERIA панели локаций нет критерия «В черте города».");

// Оба имени из СПИСКА обязаны иметь ветку в switch домена и предикат окружения.
for (const literal of ["inanycity", "incityboundary"]) {
  if (!resolverCs.includes(`case "${literal}"`))
    throw new Error(`В LocationResolver нет ветки case "${literal}": критерий дойдёт ` +
      "до default и молча объявит себя неподдерживаемым.");
}

if (!/IsCityCriterion/.test(resolverCs))
  throw new Error("В LocationResolver нет предиката IsCityCriterion: отсутствие черт " +
    "даст по диагностике на каждую точку мира вместо одного сообщения.");

// Белый список проверяется ВНУТРИ СВОЕГО массива, а не поиском имени по файлу:
// те же литералы есть ещё и в ветках switch, поэтому поиск «где-то в файле»
// проходил бы и после удаления критерия из SupportedCriteria — то есть проверка
// не измеряла бы ничего (та же ловушка, что уже была с критерием NearbyRoad).
{
  const start = resolverCs.indexOf("SupportedCriteria =");
  if (start < 0)
    throw new Error("В LocationResolver нет массива SupportedCriteria.");

  // Конец — закрывающая скобка массива, а не конец файла: иначе срез захватил бы
  // ветки switch и проверка снова стала бы бессмысленной.
  const end = resolverCs.indexOf("];", start);
  if (end < 0)
    throw new Error("Не найден конец массива SupportedCriteria.");

  const supported = resolverCs.slice(start, end);

  for (const literal of ["inanycity", "incityboundary"]) {
    if (!supported.includes(`"${literal}"`))
      throw new Error(`Критерий ${literal} отсутствует в SupportedCriteria домена: ` +
        "поиск объявит его неподдерживаемым.");
  }
}

// Проверка параметра города ищется ВНУТРИ CriterionParameterProblem: текст
// сообщения есть ещё и в ветке switch, поэтому поиск по всему файлу не заметил бы
// удаления самой проверки.
{
  const start = resolverCs.indexOf("CriterionParameterProblem(LocationCriterion");
  if (start < 0)
    throw new Error("В LocationResolver нет метода CriterionParameterProblem.");

  const end = resolverCs.indexOf("private static bool IsUnsupportedCriterion", start);
  const body = resolverCs.slice(start, end > start ? end : undefined);

  if (!/IsCitySpecificCriterion\(criterion\)/.test(body) ||
      !/не задан город city/.test(body))
    throw new Error("Критерий «В черте города» не проверяет параметр city в " +
      "CriterionParameterProblem: пустое поле даст молчаливый пустой результат " +
      "вместо понятного сообщения.");
}

// Панель обязана РАЗБИРАТЬ параметры обоих критериев: без такой ветки поле не
// появится, и критерий станет невыполнимым. Имена берутся из списка CRITERIA,
// а не пишутся литералом — иначе проверка разошлась бы с панелью при переименовании.
for (const type of [cityPanel.type, boundaryPanel.type]) {
  if (!locationEditorJs.includes(`type === "${type}"`))
    throw new Error(`Панель не разбирает параметры критерия ${type}: поле не появится, ` +
      "и критерий будет невыполним.");
}

// Подсказки городов обязаны строиться из ТОГО ЖЕ источника, по которому фильтрует
// критерий: другой источник предложил бы города, для которых черта не найдётся.
if (!/function worldCities\(/.test(locationEditorJs))
  throw new Error("В панели локаций нет worldCities(): поле города останется голым, " +
    "и опечатка будет выглядеть как «у города нет черты».");

if (!/\.filter\(point => point\.isCity\)/.test(locationEditorJs))
  throw new Error("worldCities() не ограничивается городскими точками: подсказки предложат " +
    "точки, которые не могут стать городом черты.");

// --- Черты обновляются без перезапуска приложения ---

// Черты рисует автор ВРУЧНУЮ и уже во время работы. Если критерии держат индекс,
// прочитанный на старте, то только что обведённый город не находится до
// перезапуска — а выглядит это как поломка геометрии, хотя причина в снимке.
{
  const editorCs = read("src/AssistQuestEditor.App/Host/EditorForm.cs");
  const runtimeCs = read("src/AssistQuestEditor.App/LocationRuntimeResolver.cs");
  const programCs = read("src/AssistQuestEditor.App/Program.cs");

  if (!/ICityBoundarySource/.test(editorCs))
    throw new Error("EditorForm держит не источник черт, а снимок: черта, нарисованная " +
      "после открытия окна, не будет найдена до перезапуска приложения.");

  // Проверка ищется именно в TestLocation: поле может объявляться и передаваться
  // куда угодно, а важно только то, что индекс читается В МОМЕНТ проверки.
  {
    const start = editorCs.indexOf("private void TestLocation(");
    if (start < 0)
      throw new Error("В EditorForm нет TestLocation.");

    const end = editorCs.indexOf("private WorldCoordinate? PlayerPosition()", start);
    const body = editorCs.slice(start, end > start ? end : undefined);

    if (!/_cityBoundaries\.Current/.test(body))
      throw new Error("TestLocation не берёт индекс черт из источника: проверка пойдёт " +
        "по устаревшему снимку и сообщит «ни одна черта города не нарисована».");
  }

  // Кэш разрешённых локаций помнит точку, найденную по ПРЕЖНЕЙ черте. Без сброса
  // при смене индекса симуляция продолжала бы ходить в старую точку.
  //
  // Проверка ищется ВНУТРИ Resolve: `_lastCities` объявлено полем, а
  // `_resolved.Clear()` есть ещё и в Reset(), поэтому поиск этих имён по файлу
  // проходил и после удаления самого сброса — то есть не измерял ничего.
  {
    const start = runtimeCs.indexOf("public WorldPoint? Resolve(string locationId)");
    if (start < 0)
      throw new Error("В LocationRuntimeResolver нет Resolve.");

    const end = runtimeCs.indexOf("public double? ResolveTriggerRadius", start);
    const body = runtimeCs.slice(start, end > start ? end : undefined);

    if (!/_lastCities/.test(body) || !/_resolved\.Clear\(\)/.test(body))
      throw new Error("LocationRuntimeResolver не сбрасывает кэш разрешений при смене черт: " +
        "симуляция останется на точке, найденной по старой черте.");
  }

  // Читаться черты обязаны при КАЖДОМ обращении с проверкой метки файла, иначе
  // правка файла снаружи приложения не подхватится.
  if (!/class CityBoundaryFileSource/.test(read("src/AssistQuestEditor.App/CityBoundaryFileSource.cs")))
    throw new Error("Нет CityBoundaryFileSource: черты не перечитываются с диска.");

  if (!/new CityBoundaryFileSource\(\)/.test(programCs))
    throw new Error("Program.cs не создаёт живой источник черт: критерии получат снимок " +
      "и не увидят нарисованную черту до перезапуска.");
}

// --- Пустой результат объясняется, а не просто «кандидатов нет» ---

// Симптом, ради которого это добавлено: автор вводит «охрана» (русское ИМЯ
// объекта) вместо категории «ohrana», черта нарисована верно, точки внутри есть,
// а поиск молчит «Подходящих кандидатов нет». Категории в данных — латинские
// внутренние идентификаторы игры, и без подсказки причину не найти.
{
  const start = resolverCs.indexOf("private static string? ZeroMatchDiagnostic(");
  if (start < 0)
    throw new Error("В LocationResolver нет ZeroMatchDiagnostic: пустой результат ничем " +
      "не объясняется, и автор идёт проверять данные мира вместо своего критерия.");

  const end = resolverCs.indexOf("private static bool Matches(", start);
  const body = resolverCs.slice(start, end > start ? end : undefined);

  if (!/IsCategoryCriterion\(criterion\)/.test(body))
    throw new Error("ZeroMatchDiagnostic не различает критерий по категории: подсказка о " +
      "латинском написании категории не появится.");

  if (!/отбраковал все точки мира/.test(body))
    throw new Error("ZeroMatchDiagnostic не называет виновника: автор не поймёт, какой " +
      "критерий отсеял все точки.");

  // Обвинять критерий можно ТОЛЬКО при полном отсеве: иначе подсказка отправит
  // автора править исправный критерий.
  if (!/rejections\[position\] < worldPoints/.test(body))
    throw new Error("ZeroMatchDiagnostic не проверяет, что критерий отбраковал ВСЕ точки: " +
      "подсказка обвинит исправный критерий.");

  // Счётчик отсева обязан заполняться в самой фильтрации — иначе он вечно пуст
  // и подсказка никогда не сработает.
  if (!/rejections\[position\]\+\+/.test(resolverCs))
    throw new Error("Счётчик отсева не заполняется в MatchesAll: подсказка не сможет " +
      "определить виновника.");

  if (!/var culprit = ZeroMatchDiagnostic\(/.test(resolverCs))
    throw new Error("ZeroMatchDiagnostic не вызывается при пустом результате.");
}

// --- Динамическая часть ---

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  const errors = [];
  const sent = [];

  page.on("pageerror", e => {
    // setPointerCapture на синтетическом pointerdown бросает NotFoundError —
    // это не дефект страницы.
    if (String(e).includes("NotFoundError")) return;
    errors.push(String(e));
  });

  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}" +
    "#boundaryCanvas{display:block;width:100%;height:100%}" +
    "</style></head><body>" +
    boundariesHtml.slice(boundariesHtml.indexOf("<body>") + 6)
  );
  await page.addStyleTag({ content: theme });

  // Харнесс повторяет схему других smoke: заглушка chrome.webview ставится ДО
  // загрузки модуля (он подписывается на неё при инициализации), обработчики
  // складываются в Map, и сообщение доставляется ИМЕННО через них, как это
  // делает WebView2. Пустая заглушка addEventListener сделала бы модуль глухим.
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

  // Скрипт подключается ЯВНО: `<script src=...>` из setContent не загрузится
  // (относительный путь от about:blank), и модуль просто не выполнится.
  await page.addScriptTag({ content: boundariesJs });
  await page.waitForTimeout(150);

  if (!(await page.evaluate(() => !!window.__assistCityBoundary)))
    throw new Error("cityBoundaries.js не выполнился: window.__assistCityBoundary не создан.");

  const payload = {
    type: "city_boundary_review",
    reason: "проверка",
    roads: [
      0, -100, 0, 100,
      -100, 0, 100, 0,
      200, -50, 200, 50
    ],
    cities: [{ name: "Казань", x: 0, z: 0 }, { name: "Арск", x: 400, z: 0 }],
    worldPoints: [
      { name: "Дом", category: "house", x: 10, z: 10, isCity: false },
      { name: "Казань", category: "Города", x: 0, z: 0, isCity: true },
      { name: "Дрова", category: "firewood", x: 900, z: 900, isCity: false }
    ],
    boundaries: [
      { cityId: "city:kazan", cityName: "Казань", title: "Черта города Казань", points: [-50, -50, 50, -50, 50, 50, -50, 50] }
    ],
    missingCount: 1,
    missingCities: ["Арск"],
    boundaryPath: "C:\\test\\city_boundaries.json"
  };

  await page.evaluate(data => {
    window.chrome.webview.listeners.get("message")({ data: JSON.stringify(data) });
  }, payload);

  const state = await page.evaluate(() => window.__assistCityBoundary?.getState());
  if (!state) throw new Error("cityBoundaries.js не выставил window.__assistCityBoundary.");

  if (state.boundaries !== 1)
    throw new Error(`Окно не приняло черты: ${state.boundaries} вместо 1. ` +
      "Сообщение Host не дошло.");
  if (state.worldPoints !== 3)
    throw new Error(`Окно не приняло точки мира: ${state.worldPoints} вместо 3.`);
  if (state.cities !== 2)
    throw new Error(`Окно не приняло города: ${state.cities} вместо 2.`);
  if (state.missingCount !== 1)
    throw new Error(`Окно не показывает число городов без черты: ${state.missingCount} вместо 1.`);

  // Клик по карте ставит вершину контура.
  const box = await page.locator("#boundaryCanvas").boundingBox();
  const clickAt = (dx, dy) => page.mouse.click(box.x + dx, box.y + dy);

  await clickAt(400, 300);
  await clickAt(520, 300);
  await clickAt(520, 420);

  let after = await page.evaluate(() => window.__assistCityBoundary.getState());
  if (after.vertices !== 3)
    throw new Error(`Клики по карте не поставили вершины контура: ${after.vertices} вместо 3.`);
  if (after.closed)
    throw new Error("Контур считается замкнутым до замыкания автором.");

  // Незамкнутый контур НЕ сохраняется: сохранять его нельзя, это не область.
  await page.evaluate(() => { window.__sent = []; });
  await page.locator("#boundarySave").click();

  const premature = await page.evaluate(() => window.__sent.slice());
  if (premature.some(item => item.action === "save_city_boundary"))
    throw new Error("Незамкнутый контур ушёл в Host на сохранение.");

  // Замыкание контура: клик рядом с первой вершиной.
  await clickAt(400, 300);
  after = await page.evaluate(() => window.__assistCityBoundary.getState());
  if (!after.closed)
    throw new Error("Клик по первой вершине не замкнул контур.");
  if (after.vertices !== 3)
    throw new Error(`Замыкание добавило лишнюю вершину: ${after.vertices} вместо 3.`);

  // Сохранение замкнутого контура уходит в Host с координатами.
  await page.evaluate(() => { window.__sent = []; });
  await page.locator("#boundarySave").click();

  const saveMessage = (await page.evaluate(() => window.__sent.slice()))
    .find(item => item.action === "save_city_boundary");

  if (!saveMessage)
    throw new Error("Замкнутый контур не отправлен на сохранение.");
  if (!Array.isArray(saveMessage.points) || saveMessage.points.length !== 3)
    throw new Error("В сообщении сохранения нет трёх вершин контура: " +
      JSON.stringify(saveMessage.points));

  // Точки НЕ выделяются: за всё время работы окно не отправило ни одного действия выбора.
  const anySelection = (await page.evaluate(() => window.__sent.slice()))
    .some(item => /select|toggle|focus/i.test(String(item.action || "")));
  if (anySelection)
    throw new Error("Окно отправляет действия выбора: точки не должны выделяться.");

  // Ответ Host о сохранении показывается автору по имени черты, а не молча.
  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "city_boundary_saved",
        cityName: "Казань",
        title: "Черта города Казань",
        vertexCount: 3
      })
    });
  });

  const messageText = await page.locator("#boundaryMessage").textContent();
  if (!messageText.includes("Черта города Казань"))
    throw new Error(`Окно не показало название сохранённой черты: «${messageText}».`);

  // Отклонённый контур объясняется автором понятным текстом.
  await page.evaluate(() => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "city_boundary_rejected",
        message: "Внутри контура нет ни одного города. Обведите город целиком."
      })
    });
  });

  const rejectedText = await page.locator("#boundaryMessage").textContent();
  if (!rejectedText.includes("нет ни одного города"))
    throw new Error(`Окно не показало причину отказа: «${rejectedText}».`);

  if (errors.length)
    throw new Error("Ошибки на странице: " + errors.join("; "));

  console.log("Окно черт городов: OK (приём данных, контур, замыкание, сохранение, отказ)");
  console.log("Критерии города: OK (цепочка имён, подсказки городов, проверка параметра)");
} finally {
  await browser.close();
}
