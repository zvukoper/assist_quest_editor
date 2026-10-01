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

for (const name of ["theme.css", "vitals.js", "items.js", "web_log.js", "dom_reconcile.js"]) {
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
      feeds: true, edible: true, grams: 500, milliliters: 500,
      kilocalories: 0, waterMilliliters: 500,
      energyPercent: 0, hydrationPercent: 3.33,
      proteinGrams: 0, fatGrams: 0, carbohydrateGrams: 0,
      quality: "Обычное", condition: "Свежее", quest: false, priceRubles: 45
    },
    {
      // ОБЪЁМ ПОРЦИИ (400) НАРОЧНО ОТЛИЧАЕТСЯ от содержимого ВОДЫ (250) —
      // это прямой сторож дефекта автора: «Банан 110 мл, а добавилось 150».
      // Окно печатало `waterMilliliters`, и объём в карточке не совпадал с тем,
      // что займёт порция. Показ 250 вместо 400 обязан валить проверку.
      id: "food.meal", name: "Паёк", description: "Нормальная еда: энергия и жидкость.",
      category: "Еда", color: "#b98a55", letter: "П", quantity: 0,
      feeds: true, edible: true, grams: 400, milliliters: 400,
      kilocalories: 600, waterMilliliters: 250,
      energyPercent: 12, hydrationPercent: 1.67,
      // БЖУ порции: 8,0/6,0/18,0 на 100 г × 4.
      proteinGrams: 32, fatGrams: 24, carbohydrateGrams: 72,
      quality: "Обычное", condition: "Свежее", quest: false, priceRubles: 320
    },
    {
      // Неедовый предмет: блока пищевой ценности быть НЕ должно — «0 ккал»
      // читалось бы как «еда, но бесполезная». ОБЪЁМ при этом показать надо:
      // это свойство предмета, а не еды. ЦЕНЫ нет (0) — и строки цены тоже быть
      // не должно: «0 ₽» читалось бы как «бесплатно».
      //
      // КВЕСТОВЫЙ: единственный предмет в пробе с `quest: true`. Он же — сторож
      // правила «квестовое никогда не продаётся»: `priceRubles` намеренно 0.
      id: "note", name: "Записка", description: "Короткая записка из тайника.",
      category: "Квестовый предмет", color: "#e7d58a", letter: "З", quantity: 1,
      feeds: false, edible: false, grams: 0, milliliters: 0,
      kilocalories: 0, waterMilliliters: 0,
      energyPercent: 0, hydrationPercent: 0, priceRubles: 0,
      proteinGrams: 0, fatGrams: 0, carbohydrateGrams: 0,
      quality: "Обычное", condition: "Без повреждений", quest: true
    },
    {
      // ПОЗИЦИЯ МЕНЮ КАФЕ: цена приходит от Хоста и обязана быть ВИДНА. Цена —
      // число автора (план кафе «У Дороги»), а не оценка рынка; окно её только
      // показывает, покупка появится в механиках квестов.
      id: "cafe.borscht", name: "Борщ со сметаной",
      description: "Первое блюдо. Ценность 100-80-90-0 (здоровье-голод-жажда-тонус).",
      category: "Кафе", color: "#a8324a", letter: "Б", quantity: 0,
      feeds: true, edible: true, grams: 400, milliliters: 400,
      kilocalories: 4000, waterMilliliters: 2700,
      energyPercent: 80, hydrationPercent: 90,
      priceRubles: 250,
      // БЖУ автора-меню: 3,2/4,5/3,8 на 100 г × 4.
      proteinGrams: 12.8, fatGrams: 18, carbohydrateGrams: 15.2,
      quality: "Обычное", condition: "Свежее", quest: false
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
    })(),
    badge: (() => {
      const badge = node.querySelector(".itemEdibleBadge");
      return badge ? badge.textContent.trim() : "";
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
  check(/Объём порции 500 мл/.test(water.text),
    "у воды не показан объём порции в миллилитрах: " + water.text);
  check(/3\.3%/.test(water.text), "у воды не показан процент шкалы.");

  const meal = cards.find(card => card.id === "food.meal");
  check(/600 ккал/.test(meal.text), "у пайка не показана калорийность.");
  check(/Энергия/.test(meal.text), "у пайка не сказано, что он даёт энергию.");
  // ПРЯМОЙ СТОРОЖ ДЕФЕКТА АВТОРА: объём порции (400), а НЕ содержимое воды (250).
  // Прежняя карточка печатала `waterMilliliters`, и «Паёк 250 мл» не совпадал с
  // тем, сколько паёк займёт в желудке (400).
  check(/Объём порции 400 мл/.test(meal.text),
    "у пайка показан не объём порции, а содержимое воды: " + meal.text);
  // Отрицание — по ПОДПИСИ ОБЪЁМА, а не по всей карточке: «Жидкость 250 мл» там
  // обязана быть, и проверка всей карточки ловила бы именно её.
  check(!/Объём порции 250 мл/.test(meal.text),
    "у пайка объём порции показан как содержимое воды (250 мл): " + meal.text);

  // Неедовый предмет: блока пищевой ценности быть не должно.
  const note = cards.find(card => card.id === "note");
  check(!/Пищевая ценность/.test(note.text),
    "у неедового предмета показана пищевая ценность: " + note.text);

  // ── Цена ──────────────────────────────────────────────────────────────────
  // Автор: «Цену показывать, но покупка будет совершаться только в определённых
  // ситуациях в новых механиках». Значит окно цену ПОКАЗЫВАЕТ и НЕ продаёт.
  const borscht = cards.find(card => card.id === "cafe.borscht");
  check(/Цена: 250 ₽/.test(borscht.text),
    "у позиции кафе не показана цена автора: " + borscht.text);

  // Нулевая цена — это «цены нет», и строки быть НЕ должно: «0 ₽» читалось бы
  // как «бесплатно», то есть как обещание.
  check(!/Цена/.test(note.text),
    "у предмета без цены нарисована строка цены: " + note.text);

  // Покупки в этом окне нет: цену показываем, но денег не списываем и предмет не
  // выдаём. Проверка держит это явно — иначе следующая правка легко приделает
  // сюда продажу, и «цена только для показа» перестанет быть правдой молча.
  check(!/Купить|Продать|Оплатить/i.test(borscht.text),
    "в карточке появилась кнопка покупки, которой быть не должно: " + borscht.text);

  // ── Салатовая плашка «съедобно» ───────────────────────────────────────────
  // Автор: «съедобные предметы пометить салатовой плашкой - "съедобно"».
  check(/съедобно/.test(water.badge), "у воды нет плашки «съедобно»: " + water.badge);
  check(/съедобно/.test(meal.badge), "у пайка нет плашки «съедобно»: " + meal.badge);
  check(note.badge === "",
    "у неедового предмета есть плашка «съедобно»: " + note.badge);
  // Плашка салатовая, а не какого-то другого цвета: зелёный — это и есть
  // «съедобно», и подмена цвета отняла бы смысл метки.
  const badgeColor = await page.$eval(".itemEdibleBadge", node => {
    const rgb = getComputedStyle(node).backgroundColor.match(/\d+/g).map(Number);
    return { r: rgb[0], g: rgb[1], b: rgb[2] };
  });
  check(badgeColor.g > badgeColor.r && badgeColor.g > badgeColor.b,
    "плашка «съедобно» не салатовая: rgb(" +
    badgeColor.r + "," + badgeColor.g + "," + badgeColor.b + ")");

  // ── Масса в граммах ───────────────────────────────────────────────────────
  // Автор: «добавить предметам в окне предметов не только объём, но и массу в
  // граммах». Это РАЗНЫЕ числа, и оба обязаны быть видны: у пайка объём 400 мл и
  // масса 400 г, но у банана они расходятся (110 мл воды против 150 г), поэтому
  // проверяется ИМЕННО наличие подписи «Масса», а не совпадение с объёмом.
  check(/Масса 500 г/.test(water.text),
    "у воды не показана масса в граммах: " + water.text);
  check(/Масса 400 г/.test(meal.text),
    "у пайка не показана масса в граммах: " + meal.text);

  // ── БЖУ ───────────────────────────────────────────────────────────────────
  // Автор: «зафиксировать для всех предметов БЖУ для будущих механик».
  // Печатается отдельной строкой от пищевой ценности: состав — это НЕ эффект.
  check(/БЖУ порции/.test(meal.text), "у пайка не показано БЖУ: " + meal.text);
  check(/б 32 г/.test(meal.text) && /ж 24 г/.test(meal.text) &&
    /у 72 г/.test(meal.text),
    "у пайка показано неверное БЖУ: " + meal.text);

  // У непродовольственного предмета строки БЖУ быть НЕ должно: «БЖУ: 0/0/0»
  // утверждало бы диетологический факт о том, что не едят.
  check(!/БЖУ/.test(note.text),
    "у неедового предмета показано БЖУ: " + note.text);

  // ── Качество изготовления и состояние ─────────────────────────────────────
  // Автор: «у всех предметов сделать свойство "Качество изготовления" и
  // "Состояние". По умолчанию качество "Обычное", состояние для съедобных
  // "Свежее", для несъедобных "Без повреждений". Это для будущих механик».
  check(/Качество: Обычное/.test(meal.text),
    "у еды не показано качество изготовления: " + meal.text);
  check(/Состояние: Свежее/.test(meal.text),
    "у съедобного не показано состояние «Свежее»: " + meal.text);
  check(/Состояние: Без повреждений/.test(note.text),
    "у несъедобного не показано состояние «Без повреждений»: " + note.text);
  check(/Качество: Обычное/.test(note.text),
    "у несъедобного не показано качество: " + note.text);
  // Свойства показываются у ВСЕХ, а не только у отклонившихся от умолчания:
  // иначе первая же механика порчи явила бы строку «из ниоткуда».
  check(/Качество: Обычное/.test(water.text),
    "у воды нет строки качества: " + water.text);

  // ── Галочка «только съедобное» ────────────────────────────────────────────
  // Автор: «сделать возле поиска галочку - "только съедобное" - которая скрывает
  // все несъедобные предметы из списка».
  const edibleFilter = await page.$("#itemsOnlyEdible");
  check(!!edibleFilter, "возле поиска нет галочки «только съедобное».");
  if (edibleFilter) {
    await page.check("#itemsOnlyEdible");
    await page.waitForTimeout(80);
    const filtered = await page.$$eval(".itemCard", nodes =>
      nodes.map(node => node.getAttribute("data-item-id")));
    check(!filtered.includes("note"),
      "галочка «только съедобное» не скрыла неедовый предмет: " + filtered.join(","));
    check(filtered.includes("water.bottle") && filtered.includes("food.meal"),
      "галочка «только съедобное» скрыла съедобные предметы: " + filtered.join(","));
    // Снятие галочки обязано вернуть весь каталог: фильтр — это ПОКАЗ, и он не
    // имеет права терять предметы навсегда.
    await page.uncheck("#itemsOnlyEdible");
    await page.waitForTimeout(80);
    const restored = await page.$$eval(".itemCard", nodes => nodes.length);
    check(restored === payload.entries.length,
      "снятие галочки не вернуло весь каталог: " + restored + " из " + payload.entries.length);
  }

  // ── Галочка «только несъедобное» ──────────────────────────────────────────
  // Автор: «Сделать галочку-фильтр "Только несъедобное"». Это ОБРАТНЫЙ фильтр, а
  // не тот же флаг с другим названием: он обязан оставить РОВНО непродовольствие.
  const inedibleFilter = await page.$("#itemsOnlyInedible");
  check(!!inedibleFilter, "нет галочки «только несъедобное».");
  if (inedibleFilter) {
    await page.check("#itemsOnlyInedible");
    await page.waitForTimeout(80);
    const filtered = await page.$$eval(".itemCard", nodes =>
      nodes.map(node => node.getAttribute("data-item-id")));
    check(filtered.includes("note"),
      "галочка «только несъедобное» скрыла неедовый предмет: " + filtered.join(","));
    check(!filtered.includes("water.bottle") && !filtered.includes("food.meal"),
      "галочка «только несъедобное» оставила съедобные предметы: " + filtered.join(","));

    await page.uncheck("#itemsOnlyInedible");
    await page.waitForTimeout(80);
  }

  // ── Галочка «квестовое» ───────────────────────────────────────────────────
  // Автор: «и "Квестовое"». Фильтр обязан отделять по признаку ХОСТА (`quest`), а
  // не по строке категории: правило «квестовое не продаётся» живёт в домене.
  const questFilter = await page.$("#itemsOnlyQuest");
  check(!!questFilter, "нет галочки «квестовое».");
  if (questFilter) {
    await page.check("#itemsOnlyQuest");
    await page.waitForTimeout(80);
    const filtered = await page.$$eval(".itemCard", nodes =>
      nodes.map(node => node.getAttribute("data-item-id")));
    check(filtered.length === 1 && filtered[0] === "note",
      "галочка «квестовое» показала не только квестовые предметы: " + filtered.join(","));

    await page.uncheck("#itemsOnlyQuest");
    await page.waitForTimeout(80);
  }

  // ── Взаимоисключающие фильтры ─────────────────────────────────────────────
  // «Только съедобное» + «только несъедобное» одновременно дают пустой список:
  // предмет не бывает едой и не-едой сразу. Пустой список ПРАВИЛЬНЫЙ, но он обязан
  // быть ПОДПИСАН: молчаливая пустота читалась бы как поломка каталога.
  if (edibleFilter && inedibleFilter) {
    await page.check("#itemsOnlyEdible");
    await page.check("#itemsOnlyInedible");
    await page.waitForTimeout(80);

    const conflict = await page.$$eval(".itemCard", nodes => nodes.length);
    check(conflict === 0,
      "взаимоисключающие галочки не дали пустой список: " + conflict);

    const conflictText = await page.$eval(".itemsEmpty", node => node.textContent);
    check(/исключают/.test(conflictText),
      "пустой список от противоречивых галочек не объяснён: " + conflictText);

    await page.uncheck("#itemsOnlyEdible");
    await page.uncheck("#itemsOnlyInedible");
    await page.waitForTimeout(80);

    const back = await page.$$eval(".itemCard", nodes => nodes.length);
    check(back === payload.entries.length,
      "снятие обеих галочек не вернуло каталог: " + back);
  }

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

  // ── Одна колонка и отсутствие горизонтальной прокрутки ────────────────────
  // Требование автора: «сортировать по алфавиту и сделать в одну колонку с
  // прокруткой. Ширину сократить в два раза. Горизонтальную прокрутку исключить,
  // текст переносить.» Проверяется ФАКТИЧЕСКАЯ раскладка: число дорожек сетки и
  // отсутствие горизонтального переполнения. Без этого возврат multi-column
  // `auto-fill` прошёл бы незамеченным (плитка и количество остались бы верны).
  const layout = await page.evaluate(() => {
    const body = document.getElementById("itemsBody");
    const style = getComputedStyle(body);
    const tracks = style.gridTemplateColumns.trim().split(/\s+/).filter(Boolean);
    return {
      tracks: tracks.length,
      scrollWidth: body.scrollWidth,
      clientWidth: body.clientWidth,
      overflowX: style.overflowX
    };
  });
  check(layout.tracks === 1,
    "каталог не в одну колонку: дорожек сетки " + layout.tracks + ".");
  check(layout.scrollWidth <= layout.clientWidth + 1,
    "горизонтальная прокрутка каталога: " + layout.scrollWidth + " > " + layout.clientWidth + ".");
  check(layout.overflowX === "hidden",
    "горизонтальная прокрутка не запрещена явно: overflow-x = " + layout.overflowX + ".");

  // Алфавитный порядок карточек: сортировка живёт в JS, и «выключить» её можно,
  // не тронув ни отрисовку, ни количество. Имена в фикстуре даны НЕ по алфавиту
  // («Вода», «Паёк», «Записка»), поэтому проверка осмысленна.
  const shownNames = cards.map(card => card.name);
  const alphabetical = shownNames.slice().sort((a, b) => a.localeCompare(b, "ru"));
  check(JSON.stringify(shownNames) === JSON.stringify(alphabetical),
    "предметы не по алфавиту: " + JSON.stringify(shownNames) + ".");

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

  // ── Фликер и тормоза при ЖИВЫХ обновлениях ────────────────────────────────
  //
  // Автор: «сильно тормозит список предметов. Прокрутка колесом или скроллом
  // тормозит. Если выключить симуляцию, всё плавно передвигается».
  //
  // Причина: список пересобирался через `innerHTML` на КАЖДОМ обновлении, то есть
  // все карточки заменялись новыми узлами четыре раза в секунду. Замена узла —
  // это снятие и вставка в документ: карточка под курсором теряет `:hover`
  // (мерцание плашек), выделение текста сбрасывается, а вёрстка получает
  // пересчёт ровно в тот момент, когда пользователь тянет прокрутку.
  //
  // Проверяются ДВА свойства, и оба обязаны держаться:
  //  1) повтор того же списка не трогает DOM ВООБЩЕ — ни одного узла;
  //  2) при настоящем изменении (правка количества) карточки остаются ТЕМИ ЖЕ
  //     узлами, а не пересоздаются.
  {
    // Повтор ТОГО ЖЕ списка — самый частый случай живого обновления.
    await page.evaluate(data => window.chrome.webview.dispatch("message", data), payload);
    await page.evaluate(() => {
      window.__mutations = { added: 0, removed: 0 };
      window.__observer = new MutationObserver(records => {
        for (const record of records) {
          window.__mutations.added += record.addedNodes.length;
          window.__mutations.removed += record.removedNodes.length;
        }
      });
      window.__observer.observe(document.getElementById("itemsBody"),
        { childList: true, subtree: true });
    });

    for (let tick = 0; tick < 4; tick++) {
      await page.evaluate(data => window.chrome.webview.dispatch("message", data), payload);
      await page.waitForTimeout(30);
    }

    const repeated = await page.evaluate(() => {
      window.__observer.disconnect();
      return window.__mutations;
    });
    check(repeated.added === 0 && repeated.removed === 0,
      "повтор того же списка предметов пересобирает разметку: добавлено узлов " +
      repeated.added + ", убрано " + repeated.removed + ". Каждая замена узла — это " +
      "потерянный :hover под курсором и пересчёт вёрстки при прокрутке");

    // Настоящее изменение: количество предмета. Узлы обязаны УЦЕЛЕТЬ.
    await page.evaluate(() => {
      window.__kept = {
        water: document.querySelector("[data-item-id='water.bottle']"),
        note: document.querySelector("[data-item-id='note']"),
        tile: document.querySelector("[data-item-id='water.bottle'] .itemTile"),
        minus: document.querySelector("[data-item-id='water.bottle'] .itemMicro")
      };
    });

    await page.evaluate(data => window.chrome.webview.dispatch("message", data), {
      type: "items",
      entries: payload.entries.map(entry =>
        entry.id === "water.bottle" ? { ...entry, quantity: 9 } : entry)
    });
    await page.waitForTimeout(80);

    const identity = await page.evaluate(() => ({
      water: window.__kept.water === document.querySelector("[data-item-id='water.bottle']"),
      note: window.__kept.note === document.querySelector("[data-item-id='note']"),
      tile: window.__kept.tile === document.querySelector("[data-item-id='water.bottle'] .itemTile"),
      minus: window.__kept.minus === document.querySelector("[data-item-id='water.bottle'] .itemMicro"),
      quantity: document.querySelector("[data-item-id='water.bottle'] [data-item-qty]").value
    }));

    check(identity.quantity === "9",
      "количество в карточке не обновилось при изменении списка: " + identity.quantity);
    check(identity.water,
      "карточка предмета подменена при изменении количества — она мерцает под курсором");
    check(identity.note,
      "соседняя карточка подменена при изменении количества другого предмета");
    check(identity.tile && identity.minus,
      "плитка или микрокнопка карточки пересозданы: выделение и курсор сбрасываются");

    // Ключ карточки обязан быть в разметке: без него сверка узлов ключует
    // карточки по ПОРЯДКУ, а порядок задаёт сортировка по названию — правка
    // количества переставляла бы карточки местами.
    const keyed = await page.$$eval(".itemCard",
      nodes => nodes.every(node => (node.getAttribute("data-key") || "").length > 0));
    check(keyed,
      "у карточек нет ключа data-key: сверка узлов считала бы их безымянными и " +
      "могла бы переставить карточки при правке количества");
  }

  if (errors.length) throw new Error("pageerror: " + errors.join(" | "));

  if (failures.length) {
    console.error("Items smoke: ошибок " + failures.length);
    for (const failure of failures) console.error("  • " + failure);
    process.exitCode = 1;
  } else {
    console.log("Окно предметов: OK (" + cards.length + " предметов, плитка 48×48, " +
      "пищевая ценность, масса и БЖУ, качество и состояние, цена, " +
      "три фильтра, количество и поиск)");
  }
} finally {
  await closeBrowser(browser);
}
