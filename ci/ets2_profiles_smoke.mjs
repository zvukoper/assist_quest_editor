// Проверка окна «Профили ETS2».
//
// ЧТО СТЕРЕЖЁТ. Окно — единственное место, где видно, ЧЕМ играет пользователь:
// язык интерфейса, подключённые моды, карты, DLC и сохранения. Данные целиком
// собирает Хост из файлов игры, поэтому три дефекта здесь невидимы статической
// сверке строк:
//
//   1) селект профилей открывается с ВЫБРАННЫМ профилем, хотя автор требовал
//      «по умолчанию без выбора» — данные чужого профиля показываются как свои;
//   2) моды, карты и DLC слиты в один список или перепутаны — теряется ответ на
//      вопрос «что из этого карта, а что докуплено»;
//   3) язык интерфейса нарисован не рядом с селектом, а в другом месте шапки —
//      подпись перестаёт читаться как ответ на выбор профиля.
//
// Проверка считает РЕАЛЬНЫЙ DOM и координаты: положение языка проверяется
// СРАВНЕНИЕМ прямоугольников, а не порядком узлов в разметке. Мутация «поставить
// язык после кнопки обновления» обязана её уронить.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-ets2-profiles-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "web_log.js", "ets2Profiles.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

// Заглушка WebView2. Слушатель регистрируется САМА страница, поэтому диспетчер
// общий: он шлёт сообщение всем подписчикам так же, как это делает WebView2.
const html = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "ets2Profiles.html"), "utf8");
fs.writeFileSync(path.join(tmp, "Web", "p.html"), html
  .replace(/\?v=[0-9a-f]+/g, "")
  .replace("</head>", `<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script></head>`));

// Каталог: два профиля, ни один из них НЕ выбран заранее. Второй помечен активным
// в игре — окно обязано подписать это в списке, но не выбирать его само.
//
// Место чтения у профилей РАЗНОЕ намеренно: облачный режим хранит свежие данные в
// Steam Cloud, и профиль без данных в документах игры обязан показываться с
// подписью «облако Steam», а не пустой карточкой без объяснения.
const catalog = {
  type: "ets2_profiles",
  gameRoot: "E:\\Users\\Docs\\Euro Truck Simulator 2",
  steamRoot: "E:\\Steam",
  steamCloudRoot: "E:\\Steam\\userdata\\40536066\\227300\\remote\\profiles",
  activeHexFolder: "445A527C4D696B6861696C31",
  activeProfileName: "Ruslan",
  profiles: [
    { area: "steam_profiles", hexFolder: "445A527C4D696B6861696C31", name: "Ruslan", saveCount: 3,
      lastWrite: "2024-05-01T10:00:00+03:00", isActive: true, source: "steam_cloud",
      sourceLabel: "облако Steam" },
    { area: "steam_profiles", hexFolder: "4162636465", name: "Тест", saveCount: 0,
      lastWrite: null, isActive: false, source: "documents", sourceLabel: "документы игры" }
  ],
  warnings: ["Профиль облака не читается."]
};

// Данные профиля: разные разделы намеренно не пересекаются по именам, иначе
// проверка «мод не попал в карты» проходила бы при полностью перепутанных списках.
const profile = {
  type: "ets2_profile",
  avatarDataUrl: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==",
  profile: {
    area: "steam_profiles",
    hexFolder: "445A527C4D696B6861696C31",
    name: "Ruslan",
    profileName: "Ruslan",
    source: "steam_cloud",
    sourceLabel: "облако Steam",
    language: "ru_ru",
    languageLabel: "Русский",
    currency: "EUR",
    currencyLabel: "Евро",
    companyName: "Логистика",
    brand: "Volvo",
    truck: "Volvo FH",
    mapPath: "/map/europe",
    faceIndex: 3,
    male: true,
    distanceKm: 12345,
    experience: 39130,
    saveCount: 3,
    profileCreated: "2023-01-02T12:00:00+03:00",
    profileSaved: "2024-05-01T10:00:00+03:00",
    avatarPath: "C:\\нет\\такого.png",
    mods: [
      { id: "mod.trailers", name: "Прицепы ProMods", kind: "mod", kindLabel: "Мод" }
    ],
    maps: [
      { id: "promods.map", name: "ProMods", kind: "map", kindLabel: "Карта" }
    ],
    dlc: [
      { id: "dlc_iberia", name: "Iberia", kind: "dlc", kindLabel: "DLC" }
    ],
    saves: [
      { slot: "1", name: "Первый рейс", modifiedAt: "2024-05-01T10:00:00+03:00",
        inGameMinutes: 754, money: 250000, experience: 1200, visitedCities: 14,
        dependencyCount: 6, inGameTimeLabel: "12:34" }
    ],
    warnings: []
  }
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1100, height: 820 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.goto("file://" + path.join(tmp, "Web", "p.html").replace(/\\/g, "/"));

  // ── Окно обязано само попросить список профилей ───────────────────────────
  const request = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "ets2_request_profiles");
  check(!!request, "страница обязана запрашивать профили при открытии (ets2_request_profiles).");

  // ── Селект открывается БЕЗ выбора ─────────────────────────────────────────
  const initialValue = await page.$eval("#ets2ProfileSelect", node => node.value);
  check(initialValue === "", "селект профиля открылся с выбором («" + initialValue +
    "»); автор требовал «по умолчанию без выбора».");

  await page.evaluate(data => window.chrome.webview.dispatch("message", data), catalog);
  await page.waitForTimeout(120);

  const options = await page.$$eval("#ets2ProfileSelect option", nodes => nodes.map(node => ({
    value: node.value, label: node.textContent.trim(), selected: node.selected
  })));

  check(options.length === catalog.profiles.length + 1,
    "пунктов в селекте: " + options.length + ", ожидалось " +
    (catalog.profiles.length + 1) + " (с приглашением).");
  check(options[0].value === "", "первый пункт селекта не пустой: " + JSON.stringify(options[0]));
  check(options[0].selected && options.filter(option => option.selected).length === 1,
    "выбран не пункт-приглашение: " + JSON.stringify(options.filter(o => o.selected)));

  for (const summary of catalog.profiles) {
    const value = summary.area + "|" + summary.hexFolder;
    const option = options.find(item => item.value === value);
    check(!!option, "в селекте нет профиля «" + summary.name + "».");
    check(option && option.label.includes(summary.name),
      "подпись пункта не содержит имени профиля: " + JSON.stringify(option));
  }

  // Активный профиль в списке ОТМЕЧЕН, но не выбран: иначе окно показывало бы
  // данные профиля, который пользователь не выбирал.
  const activeOption = options.find(option => option.value.includes(catalog.activeHexFolder));
  check(activeOption && /сейчас в игре/.test(activeOption.label),
    "активный профиль не отмечен в списке: " + JSON.stringify(activeOption));

  // Место чтения названо у КАЖДОГО пункта: облачный профиль может не иметь данных
  // в документах игры, и пункт без подписи читался бы как сломанный.
  check(activeOption && /облако Steam/.test(activeOption.label),
    "у облачного профиля нет подписи места чтения: " + JSON.stringify(activeOption));
  const documentsOption = options.find(option => option.value.includes("4162636465"));
  check(documentsOption && /документы игры/.test(documentsOption.label),
    "у локального профиля нет подписи места чтения: " + JSON.stringify(documentsOption));

  // Папка облака показана в шапке: без неё неясно, откуда взялись данные.
  const rootText = await page.$eval("#ets2Root", node => node.textContent);
  check(rootText.includes(catalog.steamCloudRoot),
    "в шапке не показана папка облачного хранилища: «" + rootText + "»");

  // ── Язык интерфейса СРАЗУ СПРАВА от селекта ───────────────────────────────
  //
  // Проверяются КООРДИНАТЫ, а не порядок узлов: язык должен стоять правее селекта
  // и на той же строке, причём без органов управления между ними.
  const boxes = await page.evaluate(() => {
    const rect = selector => {
      const node = document.querySelector(selector);
      if (!node) return null;
      const box = node.getBoundingClientRect();
      return { left: box.left, right: box.right, top: box.top, bottom: box.bottom };
    };
    return {
      select: rect("#ets2ProfileSelect"),
      refresh: rect("#ets2Refresh"),
      language: rect("#ets2Language")
    };
  });

  check(!!boxes.select && !!boxes.language,
    "не найдены селект профиля или подпись языка: " + JSON.stringify(boxes));
  if (boxes.select && boxes.language) {
    check(boxes.language.left > boxes.select.right,
      "язык интерфейса не справа от селекта: язык left=" + boxes.language.left +
      ", селект right=" + boxes.select.right);
    const overlap = Math.min(boxes.select.bottom, boxes.language.bottom) -
      Math.max(boxes.select.top, boxes.language.top);
    check(overlap > 0, "язык интерфейса не на одной строке с селектом.");
  }
  if (boxes.refresh && boxes.language) {
    check(boxes.language.left < boxes.refresh.left,
      "между селектом и языком стоит другое управление: язык left=" +
      boxes.language.left + ", кнопка обновления left=" + boxes.refresh.left);
  }

  // ── Данные профиля: свойства ненумерованным списком ───────────────────────
  await page.evaluate(data => window.chrome.webview.dispatch("message", data), profile);
  await page.waitForTimeout(120);

  // Место чтения данных названо в карточке профиля: расхождение облачной и
  // локальной копий — свойство облачного режима игры, и без подписи оно выглядит
  // как ошибка AQE.
  const cardSub = await page.$eval(".ets2ProfileSub", node => node.textContent);
  check(/облако Steam/.test(cardSub), "в карточке профиля нет места чтения данных: «" + cardSub + "»");

  const listStyle = await page.$eval(".ets2Properties", node => getComputedStyle(node).listStyleType);
  check(listStyle === "none", "свойства профиля показаны НУМЕРОВАННЫМ списком: " + listStyle);

  const properties = await page.$eval(".ets2Properties", node => node.textContent);
  for (const expected of ["Ruslan", "Логистика", "Volvo FH", "39 130", "14"]) {
    check(properties.includes(expected),
      "в свойствах профиля нет «" + expected + "»: " + properties.slice(0, 200));
  }

  // ── Язык профиля попал рядом с селектом ───────────────────────────────────
  const languageText = await page.$eval("#ets2Language", node => node.textContent);
  check(languageText.includes("Русский"), "язык интерфейса профиля не показан: " + languageText);
  check(languageText.includes("ru_ru"), "код языка не показан: " + languageText);

  // ── Моды, карты и DLC — ТРЕМЯ списками, каждый со своим содержимым ───────
  const cards = await page.$$eval(".ets2Card", nodes => nodes.map(node => ({
    title: (node.querySelector(".ets2CardTitle span") || {}).textContent || "",
    count: (node.querySelector(".ets2Count") || {}).textContent || "",
    text: node.textContent
  })));

  const modCard = cards.find(card => card.title.includes("Подключённые моды"));
  const mapCard = cards.find(card => card.title.includes("Подключённые карты"));
  const dlcCard = cards.find(card => card.title.includes("Подключённые DLC"));

  check(!!modCard, "нет отдельного списка модов.");
  check(!!mapCard, "нет отдельного списка карт.");
  check(!!dlcCard, "нет отдельного списка DLC.");

  if (modCard && mapCard && dlcCard) {
    check(modCard.text.includes("Прицепы ProMods") && !mapCard.text.includes("Прицепы ProMods"),
      "мод не показан в списке модов: " + modCard.text.slice(0, 160));
    check(mapCard.text.includes("ProMods") && !mapCard.text.includes("Прицепы ProMods"),
      "карта не показана в списке карт: " + mapCard.text.slice(0, 160));
    check(dlcCard.text.includes("Iberia") && !dlcCard.text.includes("Прицепы ProMods"),
      "DLC не показано в списке DLC: " + dlcCard.text.slice(0, 160));
    check(modCard.count === "1" && mapCard.count === "1" && dlcCard.count === "1",
      "счётчики разделов неверны: " + JSON.stringify(
        [modCard.count, mapCard.count, dlcCard.count]));
  }

  // ── Аватар ────────────────────────────────────────────────────────────────
  const avatar = await page.$eval(".ets2Avatar", node => ({
    src: node.getAttribute("src") || "", width: node.naturalWidth
  })).catch(() => null);
  check(!!avatar && avatar.src.startsWith("data:image/png;base64,"),
    "аватар профиля не показан картинкой: " + JSON.stringify(avatar));
  check(!!avatar && avatar.width > 0, "картинка аватара не загрузилась.");

  // ── Сохранения ────────────────────────────────────────────────────────────
  const saveText = await page.$eval(".ets2Saves", node => node.textContent);
  check(saveText.includes("Первый рейс"), "сохранение профиля не показано: " + saveText);
  check(saveText.includes("12:34"), "игровое время сохранения не показано: " + saveText);

  // ── Выбор профиля отправляется Хосту целиком ──────────────────────────────
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.selectOption("#ets2ProfileSelect",
    catalog.profiles[0].area + "|" + catalog.profiles[0].hexFolder);

  const selected = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "ets2_select_profile");
  check(!!selected, "выбор профиля не отправил ets2_select_profile.");
  check(selected && selected.area === catalog.profiles[0].area &&
    selected.hexFolder === catalog.profiles[0].hexFolder,
    "ets2_select_profile отправлен неверно: " + JSON.stringify(selected));

  // ── Снятие выбора возвращает окно к подсказке ─────────────────────────────
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.selectOption("#ets2ProfileSelect", "");

  const cleared = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "ets2_select_profile");
  check(cleared && cleared.area === "" && cleared.hexFolder === "",
    "снятие выбора отправлено неверно: " + JSON.stringify(cleared));

  await page.evaluate(() => window.chrome.webview.dispatch("message",
    { type: "ets2_profile_cleared" }));
  await page.waitForTimeout(80);

  const afterClear = await page.$("#ets2Placeholder");
  check(!!afterClear, "после снятия выбора подсказка «выберите профиль» не вернулась.");
  const stale = await page.$(".ets2Properties");
  check(!stale, "после снятия выбора в окне остались данные прежнего профиля.");

  // ── Ошибка чтения показывается словами ────────────────────────────────────
  await page.evaluate(() => window.chrome.webview.dispatch("message",
    { type: "ets2_profiles_error", message: "Папка профилей недоступна" }));
  await page.waitForTimeout(80);

  const errorText = await page.$eval("#ets2Error", node => node.textContent);
  check(errorText.includes("Папка профилей недоступна"),
    "сообщение об ошибке чтения не показано: «" + errorText + "»");

  if (errors.length) throw new Error("pageerror: " + errors.join(" | "));

  if (failures.length) {
    console.error("ETS2 profiles smoke: ошибок " + failures.length);
    for (const failure of failures) console.error("  • " + failure);
    process.exitCode = 1;
  } else {
    console.log("Окно профилей ETS2: OK (" + options.length + " пунктов селекта, " +
      cards.length + " разделов, " + catalog.profiles.length + " профилей)");
  }
} finally {
  await closeBrowser(browser);
}
