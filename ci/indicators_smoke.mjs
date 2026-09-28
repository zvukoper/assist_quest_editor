// Проверка окна «Монитор показателей».
//
// ЧТО СТЕРЕЖЁТ. Окно показывает по каждой шкале полный разбор её формулы:
// текущее / максимум и перечень факторов, влияющих на скорость изменения. Два
// дефекта здесь невидимы статической сверке строк:
//
//   1) окно не подключено к данным — шкал нет вовсе;
//   2) окно рисует только ЗНАЧЕНИЯ, но не факторы — тогда «= формула» — пустая
//      формальность, и объяснения, ради которого окно и делалось, нет.
//
// Проверка считает РЕАЛЬНЫЙ DOM после снимка: заголовки шкал, числа «тек./макс.»
// и наличие строк-факторов. Мутация «факторы не перечислены» обязана её уронить.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-indicators-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "vitals.js", "indicators.js", "web_log.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

// Цвет из getComputedStyle приходит как "rgb(r, g, b)". Красный текста требует
// заметного преобладания красного канала — иначе «красноватый» серый пройдёт
// проверку, хотя глаз его красным не увидит.
const parseRgb = value => {
  const match = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(String(value || ""));
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
};
const isRed = value => {
  const rgb = parseRgb(value);
  return !!rgb && rgb[0] >= 150 && rgb[0] - rgb[1] >= 60 && rgb[0] - rgb[2] >= 60;
};
// «Жёлтый» = и красный, и зелёный высокие, а синий заметно ниже: так жёлтый
// отличается и от оранжевого акцента (синий ещё ниже), и от белого (все высокие).
const isYellowish = value => {
  const rgb = parseRgb(value);
  return !!rgb && rgb[0] >= 150 && rgb[1] >= 150 && rgb[2] + 60 <= Math.min(rgb[0], rgb[1]);
};
// LIME из темы — (17, 251, 6): зелёный канал резко доминирует.
const isLime = value => {
  const rgb = parseRgb(value);
  return !!rgb && rgb[1] >= 180 && rgb[1] - rgb[0] >= 60 && rgb[1] - rgb[2] >= 60;
};

// Страница повторяет настоящую, но с заглушкой канала WebView2: окно обязано
// просить снимок при готовности и уметь его применить.
const html = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "indicators.html"), "utf8");
fs.writeFileSync(path.join(tmp, "Web", "i.html"), html
  .replace(/\?v=[0-9a-f]+/g, "")
  .replace("</head>", `<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script></head>`));

const snapshot = {
  player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 72, heading: 0, paused: false, inCab: true },
  daylight: { gameDateLabel: "01.01.2026", gameClockLabel: "20:00:00", gameTimeLabel: "20:00" },
  playerVitals: {
    health: 8000, maxHealth: 10000, energy: 5500, maxEnergy: 10000,
    hydration: 7000, maxHydration: 10000, fatigue: 4000, maxFatigue: 10000,
    resilience: 6000, metabolism: 6000
  },
  // Истощение усталости 100%: формула обязана показать коэффициент ×13 (3+20·0,5).
  conditions: {
    cumulativeHealth: 1000, cumulativeEnergy: 2000, cumulativeHydration: 500,
    stress: 3000, cumulativeStress: 800, cumulativeFatigue: 10000,
    criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0,
    // Переваривание: пока в желудке есть неусвоенное, монитор обязан показать
    // ПУНКТ съеденного предмета в разделе «Энергия».
    lastConsumedItemId: "food.meal",
    stomach: {
      energyRemaining: 1500, hydrationRemaining: 500,
      energyPerGameSecond: 1500 / (40 * 60), hydrationPerGameSecond: 500 / (20 * 60)
    },
    effects: [
      { id: "bull", name: "Бык", remainingRealSeconds: 3600, isDebuff: false },
      { id: "unkempt", name: "Неопрятный", remainingRealSeconds: 3600, isDebuff: true }
    ]
  },
  conditionRates: { health: 0, energy: -41.67, hydration: -83.33, fatigue: 92.6, stress: 0, resilience: 0, metabolism: 0 }
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));
  await page.goto("file:///" + path.join(tmp, "Web", "i.html").replace(/\\/g, "/"));

  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "i", snapshot: v, conditionRates: v.conditionRates,
    itemCatalog: [{ id: "food.meal", name: "Паёк", description: "Нормальная еда" }],
    simulationRunning: true, simulationPaused: false
  }), snapshot);
  await page.waitForTimeout(400);

  const read = await page.evaluate(() => {
    const cards = [...document.querySelectorAll("#indicatorsBody .indicatorCard")];
    return cards.map(card => ({
      name: card.querySelector(".indicatorName")?.textContent?.trim() || "",
      value: card.querySelector(".indicatorValue")?.textContent?.trim() || "",
      classes: card.className,
      facts: [...card.querySelectorAll(".indicatorFact")].map(n => n.textContent.trim()),
      // Строки, помеченные жёлтым (истощение), и строка динамики читаются
      // ОТДЕЛЬНО: без этого проверка «выделено жёлтым» свелась бы к поиску
      // слова по тексту и прошла бы на невыделенном блоке.
      exhaustionRows: [...card.querySelectorAll(".indicatorExhaustionRow")]
        .map(n => n.textContent.trim()),
      dynamicRow: (() => {
        const row = card.querySelector(".indicatorDynamicRow");
        if (!row) return null;
        const strong = row.querySelector("strong");
        const badge = row.querySelector(".indicatorDynamicGood,.indicatorDynamicBad,.indicatorDynamicFlat");
        return {
          label: strong ? strong.textContent.trim() : "",
          className: badge ? badge.className : "",
          text: row.textContent.trim(),
          color: badge ? getComputedStyle(badge).color : ""
        };
      })()
    }));
  });

  check(errors.length === 0, "ошибки страницы: " + errors.join(" | "));

  // Все семь шкал плюс карточка активных эффектов.
  const scaleNames = ["Здоровье", "Стресс", "Энергия", "Жидкость", "Усталость", "Устойчивость", "Метаболизм"];
  const names = read.map(card => card.name);
  for (const name of scaleNames) {
    check(names.includes(name), "в мониторе нет шкалы «" + name + "»: " + names.join(", "));
  }
  check(names.includes("Активные эффекты"), "монитор не показал активные эффекты.");

  // Формат «текущее / максимум = процент».
  const withFormula = read.filter(card => /^\d+\s*\/\s*\d+\s*=\s*\d+%$/.test(card.value));
  check(withFormula.length >= 7,
    "строка значения обязана иметь вид «тек. / макс. = %»: " +
    read.map(card => card.name + "=" + card.value).join("; "));

  // По каждой шкале обязаны быть ПЕРЕЧИСЛЕНЫ факторы — иначе окно лишь повторяет
  // бары сайдбара, и объяснения в нём нет.
  for (const card of read.filter(card => scaleNames.includes(card.name))) {
    check(card.facts.length >= 2,
      "у шкалы «" + card.name + "» не перечислены факторы: " + card.facts.length);
  }

  // Усталость: коэффициент накопления кумулятивной усталости при 100% истощения
  // равен 3 + 20·0,5 = 13 — он обязан быть назван в факторах.
  const fatigue = read.find(card => card.name === "Усталость");
  check(fatigue && fatigue.facts.some(text => /коэффициент\s+×13\b/.test(text)),
    "формула усталости обязана называть коэффициент ×13 при 100% истощения: " +
    (fatigue ? fatigue.facts.join(" | ") : "нет карточки"));

  // Ускоренный рост усталости ночью (20:00 → ×1,5) и активные эффекты.
  check(fatigue && fatigue.facts.some(text => /×1,5/.test(text)),
    "формула усталости обязана учитывать время суток (20:00 → ×1,5).");
  const effects = read.find(card => card.name === "Активные эффекты");
  check(effects && effects.facts.some(text => /Бык/.test(text)) &&
        effects.facts.some(text => /Неопрятный/.test(text)),
    "карточка эффектов обязана перечислить бафф и дебафф: " +
    (effects ? effects.facts.join(" | ") : "нет карточки"));

  // Окно просит снимок при готовности: без этого оно открылось бы пустым до
  // следующего обновления мира, которое можно ждать сколько угодно.
  const sent = await page.evaluate(() => window.__sent || []);
  check(sent.some(message => message.action === "indicators_ready"),
    "окно обязано сообщать о готовности, чтобы получить снимок: " + JSON.stringify(sent));

  // --- Динамика показателей ---
  // Снимок даёт энергии −41,67 ед./мин: строка «Текущая динамика» обязана
  // назвать и направление, и скорость, а окрасить её нужно КРАСНЫМ — падение
  // энергии вредно игроку. Проверка красного идёт по ВЫЧИСЛЕННОМУ цвету, а не
  // по классу: класс легко сохранить, сняв правило.
  const energy = read.find(card => card.name === "Энергия");
  check(energy && energy.dynamicRow && energy.dynamicRow.label.includes("Текущая динамика"),
    "у шкалы обязана быть жирная строка «Текущая динамика»: " +
    (energy ? JSON.stringify(energy.dynamicRow) : "нет карточки «Энергия»"));
  check(energy && energy.dynamicRow && /падает/.test(energy.dynamicRow.text),
    "падение энергии обязано быть названо: " +
    (energy && energy.dynamicRow ? energy.dynamicRow.text : "нет строки"));
  check(energy && energy.dynamicRow && /41[,.]7/.test(energy.dynamicRow.text),
    "строка динамики обязана называть СКОРОСТЬ изменения: " +
    (energy && energy.dynamicRow ? energy.dynamicRow.text : "нет строки"));
  check(energy && energy.dynamicRow &&
        energy.dynamicRow.className.includes("indicatorDynamicBad"),
    "падение энергии (негатив для игрока) обязано быть красным: " +
    (energy && energy.dynamicRow ? energy.dynamicRow.className : "нет строки"));
  check(energy && energy.dynamicRow && isRed(energy.dynamicRow.color),
    "красный обязан быть ВЫЧИСЛЕННЫМ цветом строки динамики: " +
    (energy && energy.dynamicRow ? energy.dynamicRow.color : "нет строки"));

  // Нулевая скорость — это «без изменений», а не «растёт на 0»: иначе игрок
  // видит движение там, где шкала стоит.
  const health = read.find(card => card.name === "Здоровье");
  check(health && health.dynamicRow && /без изменений/.test(health.dynamicRow.text),
    "нулевая скорость обязана читаться как «без изменений»: " +
    (health && health.dynamicRow ? health.dynamicRow.text : "нет строки"));

  // --- Истощение ПЕРЕРАСПРЕДЕЛЕНО по блокам шкал ---
  // Требование автора: отдельного блока «Истощение» быть не должно — запись
  // истощения обязана стоять в блоке СВОЕЙ шкалы (энергия → истощение энергии,
  // стресс → кумулятивный стресс). Иначе число и его причина читаются в разных
  // местах, и связь между ними теряется.
  const oldExhaustion = read.find(card => card.classes.includes("indicatorExhaustionCard"));
  check(!oldExhaustion,
    "отдельного блока «Истощение» быть не должно: записи переехали в блоки шкал.");

  const exhaustionByScale = await page.evaluate(() => {
    const cards = [...document.querySelectorAll("#indicatorsBody .indicatorCard")];
    const out = {};
    cards.forEach(card => {
      out[card.querySelector(".indicatorName")?.textContent?.trim() || ""] =
        [...card.querySelectorAll(".indicatorExhaustionRow")].map(row => ({
          field: row.dataset.exhaustion || "",
          text: row.textContent.trim(),
          classes: row.className,
          color: getComputedStyle(row).color
        }));
    });
    return out;
  });

  // Каждой шкале — СВОЙ вид истощения, и ни одного чужого.
  const expectedExhaustion = {
    "Энергия": "cumulativeEnergy",
    "Жидкость": "cumulativeHydration",
    "Усталость": "cumulativeFatigue",
    "Стресс": "cumulativeStress"
  };
  for (const [scaleName, field] of Object.entries(expectedExhaustion)) {
    const rows = exhaustionByScale[scaleName] || [];
    check(rows.length === 1,
      "блок «" + scaleName + "» обязан содержать ровно одну строку истощения: " + rows.length);
    check(rows[0] && rows[0].field === field,
      "у блока «" + scaleName + "» должно быть истощение «" + field + "», а не «" +
      (rows[0] ? rows[0].field : "нет") + "»");
  }

  // В снимке истощение ЕСТЬ по всем четырём шкалам — значит все четыре строки
  // обязаны быть КРАСНЫМИ. Красный, а не жёлтый: жёлтый зарезервирован за
  // кумулятивными шкалами и шкалой форсажа, а истощение — накопленный вред.
  const presentRows = Object.values(exhaustionByScale).flat()
    .filter(row => row.classes.includes("indicatorExhaustionPresent"));
  check(presentRows.length >= 4,
    "при накопленном истощении все четыре строки обязаны помечаться: " +
    presentRows.length);
  check(presentRows.every(row => isRed(row.color)),
    "накопленное истощение обязано быть КРАСНЫМ вычисленным цветом: " +
    presentRows.map(row => row.color).join(", "));
  check(presentRows.some(row => /Текущая динамика/.test(row.text)),
    "строка накопленного истощения обязана называть динамику: " +
    presentRows.map(row => row.text).join(" | "));
  check(presentRows.some(row => /ед\.\/час/.test(row.text)),
    "динамика истощения обязана называть скорость в единицах в час.");

  // А БЕЗ истощения строка обязана остаться, но стать приглушённо-белой: игрок
  // должен видеть, что механизм есть и что он в норме, а не гадать, почему
  // раздела нет.
  const cleanExhaustion = JSON.parse(JSON.stringify(snapshot));
  cleanExhaustion.conditions.cumulativeEnergy = 0;
  cleanExhaustion.conditions.cumulativeHydration = 0;
  cleanExhaustion.conditions.cumulativeFatigue = 0;
  cleanExhaustion.conditions.cumulativeStress = 0;
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "i-clean", snapshot: v, conditionRates: v.conditionRates,
    itemCatalog: [{ id: "food.meal", name: "Паёк", description: "Нормальная еда" }],
    simulationRunning: true, simulationPaused: false
  }), cleanExhaustion);
  await page.waitForTimeout(300);

  const cleanRows = await page.evaluate(() => {
    const rows = [...document.querySelectorAll("#indicatorsBody .indicatorExhaustionRow")];
    return rows.map(row => ({
      text: row.textContent.trim(),
      classes: row.className,
      color: getComputedStyle(row).color
    }));
  });

  check(cleanRows.length >= 4,
    "строки истощения обязаны остаться и при нулевом истощении: " + cleanRows.length);
  check(cleanRows.every(row => row.classes.includes("indicatorExhaustionNone")),
    "при нулевом истощении строка обязана помечаться как «норма»: " +
    cleanRows.map(row => row.classes).join(" | "));
  check(cleanRows.every(row => !isRed(row.color)),
    "при нулевом истощении строки НЕ должны быть красными: " +
    cleanRows.map(row => row.color).join(", "));
  check(cleanRows.some(row => /нет/.test(row.text)),
    "при нулевом истощении строка обязана прямо говорить «нет».");

  // Возвращаем снимок с истощением: следующие проверки опираются на него.
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "i-back", snapshot: v, conditionRates: v.conditionRates,
    itemCatalog: [{ id: "food.meal", name: "Паёк", description: "Нормальная еда" }],
    simulationRunning: true, simulationPaused: false
  }), snapshot);
  await page.waitForTimeout(300);

  // --- Эффекты: кликабельные, окрашенные, с подсказкой ---
  // Требование автора: баффы красить позитивно, дебаффы негативно; имя
  // кликабельно, есть тултип, а клик открывает окно перков с подсветкой.
  const effectChips = await page.evaluate(() => {
    const chips = [...document.querySelectorAll("#indicatorsBody .indicatorEffect")];
    return chips.map(chip => ({
      text: chip.textContent.trim(),
      tag: chip.tagName,
      classes: chip.className,
      link: chip.dataset.perkLink || "",
      kind: chip.dataset.perkKind || "",
      tooltip: chip.dataset.gameTooltip || "",
      color: getComputedStyle(chip).color,
      background: getComputedStyle(chip).backgroundColor
    }));
  });

  check(effectChips.length >= 2,
    "активные эффекты обязаны быть показаны чипами: " + effectChips.length);

  // Кнопка, а не span: кликабельность должна быть доступна с клавиатуры.
  check(effectChips.every(chip => chip.tag === "BUTTON"),
    "имена эффектов обязаны быть КНОПКАМИ (доступность с клавиатуры): " +
    effectChips.map(chip => chip.tag).join(", "));

  const buffChip = effectChips.find(chip => chip.kind === "buff");
  const debuffChip = effectChips.find(chip => chip.kind === "debuff");

  check(!!buffChip && !!debuffChip,
    "снимок даёт и бафф, и дебафф — оба обязаны быть среди чипов: " +
    effectChips.map(chip => chip.text + "/" + chip.kind).join(", "));

  // Позитивный цвет баффа: зелёный канал доминирует.
  check(buffChip && isLime(buffChip.color),
    "бафф обязан быть окрашен ПОЗИТИВНЫМ (зелёным) цветом: " +
    (buffChip ? buffChip.color : "нет чипа"));

  // Негативный цвет дебаффа: красный канал доминирует.
  check(debuffChip && isRed(debuffChip.color),
    "дебафф обязан быть окрашен НЕГАТИВНЫМ (красным) цветом: " +
    (debuffChip ? debuffChip.color : "нет чипа"));

  check(effectChips.every(chip => chip.link.length > 0),
    "у каждого чипа обязан быть Id эффекта для открытия окна перков.");

  check(effectChips.every(chip => chip.tooltip.length > 0),
    "у каждого чипа обязана быть подсказка: " +
    effectChips.map(chip => chip.text + "=" + chip.tooltip).join(" | "));

  check(buffChip && /Бык/.test(buffChip.tooltip),
    "подсказка баффа обязана описывать его действие: " +
    (buffChip ? buffChip.tooltip : "нет чипа"));

  // Клик по чипу просит Хост открыть окно перков и называет пункт для подсветки.
  await page.evaluate(() => {
    window.__sent = [];
    document.querySelector("#indicatorsBody .indicatorEffect").click();
  });
  await page.waitForTimeout(200);

  const perkRequest = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "open_perks");

  check(!!perkRequest,
    "клик по имени эффекта обязан просить открыть окно перков: " +
    JSON.stringify(await page.evaluate(() => window.__sent || [])));
  check(perkRequest && String(perkRequest.perkId || "").length > 0,
    "запрос обязан называть Id пункта для подсветки: " +
    JSON.stringify(perkRequest));
  check(perkRequest && String(perkRequest.perkKind || "").length > 0,
    "запрос обязан называть вид пункта (бафф/дебафф): " +
    JSON.stringify(perkRequest));

  const formulaExhaustion = read.filter(card => scaleNames.includes(card.name))
    .reduce((sum, card) => sum + card.facts.filter(text => /истощ/i.test(text)).length, 0);
  check(formulaExhaustion >= 4,
    "в формулах шкал должны быть строки про истощение: " + formulaExhaustion);

  // Помечена обязана быть ровно ОДНА строка на шкалу — своя строка истощения.
  // Упоминания истощения внутри формул НЕ окрашиваются: иначе красным стало бы
  // полблока, и по цвету нельзя было бы понять, где истощение накоплено.
  const markedInFormulas = read.filter(card => scaleNames.includes(card.name))
    .reduce((sum, card) => sum + card.exhaustionRows.length, 0);
  check(markedInFormulas >= 4 && markedInFormulas <= 6,
    "помечено обязано быть по одной строке истощения на шкалу, а не полблока: " +
    markedInFormulas);

  // --- Насыщение: энергия получает ПОЗИТИВНУЮ динамику → LIME ---
  // Пока еда переваривается, домен отдаёт энергии положительную скорость.
  // Строка «Текущая динамика» обязана позеленеть: это единственный признак,
  // по которому игрок видит, что еда УЖЕ действует.
  const satiated = JSON.parse(JSON.stringify(snapshot));
  satiated.conditionRates = Object.assign({}, snapshot.conditionRates, { energy: 12.5 });
  await page.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "snapshot", version: "i2", snapshot: v, conditionRates: v.conditionRates,
    itemCatalog: [{ id: "food.meal", name: "Паёк", description: "Нормальная еда" }],
    simulationRunning: true, simulationPaused: false
  }), satiated);
  await page.waitForTimeout(300);

  const satietyRow = await page.evaluate(() => {
    const cards = [...document.querySelectorAll("#indicatorsBody .indicatorCard")];
    const energy = cards.find(card =>
      card.querySelector(".indicatorName")?.textContent?.trim() === "Энергия");
    if (!energy) return null;
    const row = energy.querySelector(".indicatorDynamicRow");
    const badge = row?.querySelector(".indicatorDynamicGood,.indicatorDynamicBad,.indicatorDynamicFlat");
    return {
      text: row ? row.textContent.trim() : "",
      className: badge ? badge.className : "",
      color: badge ? getComputedStyle(badge).color : ""
    };
  });
  check(satietyRow && /растёт/.test(satietyRow.text),
    "при насыщении динамика энергии обязана читаться как рост: " +
    (satietyRow ? satietyRow.text : "нет строки"));
  check(satietyRow && satietyRow.className.includes("indicatorDynamicGood"),
    "рост энергии (польза игроку) обязан быть зелёным: " +
    (satietyRow ? satietyRow.className : "нет строки"));
  check(satietyRow && isLime(satietyRow.color),
    "зелёный обязан быть ВЫЧИСЛЕННЫМ цветом строки динамики: " +
    (satietyRow ? satietyRow.color : "нет строки"));

  // --- Переваривание: пункт съеденного предмета в разделе шкалы ---
  // Автор просил: пока употреблённое переваривается, в соответствующем разделе
  // мониторинга должен появиться ПУНКТ предмета и его воздействия с количеством
  // и цветом по знаку эффекта.
  const digestion = await page.evaluate(() => {
    const rows = [...document.querySelectorAll("#indicatorsBody .indicatorDigestionRow")];
    return rows.map(row => {
      const value = row.querySelector(".indicatorDynamicGood,.indicatorDynamicBad,.indicatorDynamicFlat");
      return {
        key: row.dataset.digestion,
        text: row.textContent.trim(),
        className: value ? value.className : "",
        color: value ? getComputedStyle(value).color : ""
      };
    });
  });
  check(digestion.length >= 2,
    "пока идёт переваривание, оба раздела (энергия и жидкость) обязаны " +
    "показать пункт предмета: " + digestion.length);
  const energyDigestion = digestion.find(row => row.key === "energy");
  check(!!energyDigestion && /Паёк/.test(energyDigestion.text),
    "пункт переваривания обязан НАЗЫВАТЬ съеденный предмет: " +
    (energyDigestion ? energyDigestion.text : "нет пункта"));
  check(!!energyDigestion && /ед\.\/мин/.test(energyDigestion.text) &&
        /осталось/.test(energyDigestion.text),
    "пункт переваривания обязан показывать КОЛИЧЕСТВО: " +
    (energyDigestion ? energyDigestion.text : "нет пункта"));
  check(!!energyDigestion && energyDigestion.className.includes("indicatorDynamicGood") &&
        isLime(energyDigestion.color),
    "прирост от переваривания обязан быть зелёным вычисленным цветом: " +
    (energyDigestion ? energyDigestion.className + " / " + energyDigestion.color : "нет пункта"));

  // --- Пустое окно при live_state ПЕРВЫМ сообщением ---
  // Дефект из журнала: Симулятор шлёт live_state четыре раза в секунду, и окно
  // получало его раньше полного снимка. Тогда страница не могла инициализироваться
  // и оставалась ПУСТОЙ — в мониторинге состояний «ничего нет».
  // Здесь это проверяется отдельной страницей: первым и единственным сообщением
  // приходит live_state, и шкалы обязаны появиться.
  const liveOnlyPage = await browser.newPage({ viewport: { width: 1200, height: 900 } });
  const liveOnlyErrors = [];
  liveOnlyPage.on("pageerror", e => liveOnlyErrors.push(String(e)));
  await liveOnlyPage.goto("file:///" + path.join(tmp, "Web", "i.html").replace(/\\/g, "/"));

  await liveOnlyPage.evaluate(v => window.chrome.webview.dispatch("message", {
    type: "live_state",
    player: v.player,
    playerVitals: v.playerVitals,
    conditions: v.conditions,
    conditionRates: v.conditionRates,
    daylight: v.daylight,
    simulationRunning: true,
    simulationPaused: false
  }), snapshot);
  await liveOnlyPage.waitForTimeout(300);

  const liveOnly = await liveOnlyPage.evaluate(() => ({
    cards: document.querySelectorAll("#indicatorsBody .indicatorCard").length,
    text: (document.getElementById("indicatorsBody")?.textContent || "").trim().length
  }));
  await liveOnlyPage.close();

  check(liveOnlyErrors.length === 0,
    "ошибки страницы при live_state первым сообщением: " + liveOnlyErrors.join(" | "));
  check(liveOnly.cards >= 7,
    "live_state первым сообщением обязан нарисовать шкалы, а не оставить окно пустым: " +
    "карточек " + liveOnly.cards + ", текста " + liveOnly.text + " символов");

  if (failures.length) {
    console.error("Indicators smoke: FAIL");
    failures.forEach(message => console.error("  ✗ " + message));
    process.exitCode = 1;
  } else {
    console.log("Indicators smoke: OK (" + read.length + " карточек, " +
      read.reduce((sum, card) => sum + card.facts.length, 0) + " строк-факторов)");
  }
} finally {
  await closeBrowser(browser);
}
