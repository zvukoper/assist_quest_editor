// Проверка окна «Предметы».
//
// ЧТО СТЕРЕЖЁТ. Окно показывает каталог: название, плитку 48×48, описание,
// пищевую ценность и количество с микрокнопками. Три дефекта здесь невидимы
// статической сверке строк:
//
//   1) плитка не 48×48 — «изображение» перестаёт быть изображением;
//   2) пищевая ценность не показана — окно не отвечает на вопрос «что даст еда»;
//   3) микрокнопки не отправляют правку — количество выглядит меняемым, но мир
//      не меняется.
//
// Проверка считает РЕАЛЬНЫЙ DOM: размеры плитки, текст карточек и отправленные
// сообщения. Мутация «микрокнопки ничего не шлют» обязана её уронить.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-items-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "vitals.js", "items.js", "web_log.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const html = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "items.html"), "utf8");
fs.writeFileSync(path.join(tmp, "Web", "it.html"), html
  .replace(/\?v=[0-9a-f]+/g, "")
  .replace("</head>", `<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script></head>`));

const payload = {
  type: "items",
  entries: [
    {
      id: "water.bottle", name: "Вода", description: "Бутылка воды. Восполняет жидкость.",
      category: "Напиток", color: "#4b8fe8", letter: "В", quantity: 3,
      feeds: true, grams: 500, kilocalories: 0, waterMilliliters: 500,
      energyPercent: 0, hydrationPercent: 3.33
    },
    {
      id: "food.meal", name: "Паёк", description: "Нормальная еда: энергия и жидкость.",
      category: "Еда", color: "#b98a55", letter: "П", quantity: 0,
      feeds: true, grams: 400, kilocalories: 600, waterMilliliters: 250,
      energyPercent: 12, hydrationPercent: 1.67
    },
    {
      // Неедовый предмет: блока пищевой ценности быть НЕ должно — «0 ккал»
      // читалось бы как «еда, но бесполезная».
      id: "note", name: "Записка", description: "Короткая записка из тайника.",
      category: "Квестовый предмет", color: "#e7d58a", letter: "З", quantity: 1,
      feeds: false, grams: 0, kilocalories: 0, waterMilliliters: 0,
      energyPercent: 0, hydrationPercent: 0
    }
  ]
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.goto("file://" + path.join(tmp, "Web", "it.html").replace(/\\/g, "/"));

  const ready = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "items_ready");
  check(!!ready, "страница обязана сообщать о готовности (items_ready).");

  await page.evaluate(data => window.chrome.webview.dispatch("message", data), payload);
  await page.waitForTimeout(120);

  const cards = await page.$$eval(".itemCard", nodes => nodes.map(node => ({
    id: node.getAttribute("data-item-id"),
    highlighted: node.classList.contains("itemHighlight"),
    name: (node.querySelector(".itemName") || {}).textContent || "",
    text: node.textContent,
    tile: (() => {
      const tile = node.querySelector(".itemTile");
      if (!tile) return null;
      const rect = tile.getBoundingClientRect();
      const computed = getComputedStyle(tile);
      return { width: Math.round(rect.width), height: Math.round(rect.height), letter: tile.textContent.trim() };
    })(),
    quantity: (() => {
      const input = node.querySelector("[data-item-qty]");
      return input ? input.value : null;
    })()
  })));

  check(cards.length === payload.entries.length,
    "показаны не все предметы: " + cards.length + " из " + payload.entries.length);

  // ── Плитка 48×48 ──────────────────────────────────────────────────────────
  // Автор задал размер явно, а отсутствие картинки компенсируется цветом с
  // буквой — но только при НАСТОЯЩЕМ размере плитки.
  for (const card of cards) {
    check(!!card.tile, "у предмета «" + card.id + "» нет плитки.");
    if (!card.tile) continue;
    check(card.tile.width === 48 && card.tile.height === 48,
      "плитка «" + card.id + "» не 48×48: " + card.tile.width + "×" + card.tile.height);
    check(card.tile.letter.length >= 1,
      "плитка «" + card.id + "» без буквы названия.");
  }

  // ── Описание у каждого предмета ───────────────────────────────────────────
  for (const card of cards) {
    const source = payload.entries.find(item => item.id === card.id);
    check(card.text.includes(source.description),
      "у предмета «" + card.id + "» нет описания.");
  }

  // ── Пищевая ценность ──────────────────────────────────────────────────────
  const water = cards.find(card => card.id === "water.bottle");
  check(/Пищевая ценность/.test(water.text),
    "у воды нет блока пищевой ценности.");
  check(/500 мл/.test(water.text), "у воды не показан объём.");
  check(/3\.3%/.test(water.text), "у воды не показан процент шкалы.");

  const meal = cards.find(card => card.id === "food.meal");
  check(/600 ккал/.test(meal.text), "у пайка не показана калорийность.");
  check(/Энергия/.test(meal.text), "у пайка не сказано, что он даёт энергию.");

  // Неедовый предмет: блока пищевой ценности быть не должно.
  const note = cards.find(card => card.id === "note");
  check(!/Пищевая ценность/.test(note.text),
    "у неедового предмета показана пищевая ценность: " + note.text);

  // ── Количество ────────────────────────────────────────────────────────────
  // Поле показывает ЗАПАС, а не ноль по умолчанию: 0 — это «предмета нет»,
  // и подставлять его поверх реального числа значило бы врать об инвентаре.
  check(water.quantity === "3", "у воды показано не её количество: " + water.quantity);
  check(meal.quantity === "0", "у отсутствующего пайка должно быть 0: " + meal.quantity);

  // «−» у нулевого количества отключена: изъять то, чего нет, невозможно.
  const minusDisabled = await page.$eval(
    "[data-item-id='food.meal'] [data-item-step='-1']",
    node => node.disabled);
  check(minusDisabled, "«−» у нулевого количества обязана быть отключена.");

  // ── Микрокнопки ───────────────────────────────────────────────────────────
  await page.evaluate(() => { window.__sent.length = 0; });

  await page.click("[data-item-id='water.bottle'] [data-item-step='1']");
  const added = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_item_quantity");
  check(!!added, "«+» не отправил set_item_quantity.");
  check(added && added.itemId === "water.bottle" && added.quantity === 4,
    "«+» отправил неверное количество: " + JSON.stringify(added));

  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click("[data-item-id='water.bottle'] [data-item-step='-1']");
  const removed = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_item_quantity");
  check(removed && removed.quantity === 2,
    "«−» отправил неверное количество: " + JSON.stringify(removed));

  // Поле количества: значение уходит по Enter, а не на каждый символ — «1» это
  // префикс «12», и отправка по символу переписывала бы запас в процессе набора.
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.fill("[data-item-id='note'] [data-item-qty]", "7");
  await page.locator("[data-item-id='note'] [data-item-qty]").press("Enter");
  const typed = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_item_quantity");
  check(!!typed, "Enter в поле количества не отправил set_item_quantity.");
  check(typed && typed.itemId === "note" && typed.quantity === 7,
    "поле количества отправило неверное значение: " + JSON.stringify(typed));

  // ── Поиск ─────────────────────────────────────────────────────────────────
  await page.fill("#itemsSearch", "паёк");
  await page.waitForTimeout(80);
  const filtered = await page.$$eval(".itemCard",
    nodes => nodes.map(node => node.getAttribute("data-item-id")));
  check(filtered.length === 1 && filtered[0] === "food.meal",
    "поиск не сузил список: " + JSON.stringify(filtered));

  // ── Подсветка по ссылке ───────────────────────────────────────────────────
  // Фильтр не должен прятать предмет, на который сослались из журнала: иначе клик
  // по названию не показал бы ничего и выглядел бы сломанной ссылкой.
  await page.evaluate(() => window.chrome.webview.dispatch("message", {
    type: "highlight", itemId: "water.bottle"
  }));
  await page.waitForTimeout(80);

  const afterHighlight = await page.$$eval(".itemCard", nodes => nodes.map(node => ({
    id: node.getAttribute("data-item-id"),
    marked: node.classList.contains("itemHighlight"),
    background: getComputedStyle(node).backgroundColor
  })));

  const highlighted = afterHighlight.find(card => card.id === "water.bottle");
  check(!!highlighted, "подсветка спрятала предмет за фильтром.");
  check(highlighted && highlighted.marked,
    "ссылка на предмет не пометила его классом подсветки.");
  check(highlighted && highlighted.background !== "rgba(0, 0, 0, 0)",
    "у подсвеченного предмета нет более светлого фона: " +
    (highlighted && highlighted.background));

  if (errors.length) throw new Error("pageerror: " + errors.join(" | "));

  if (failures.length) {
    console.error("Items smoke: ошибок " + failures.length);
    for (const failure of failures) console.error("  • " + failure);
    process.exitCode = 1;
  } else {
    console.log("Окно предметов: OK (" + cards.length + " предметов, плитка 48×48, " +
      "пищевая ценность, количество и поиск)");
  }
} finally {
  await closeBrowser(browser);
}
