// Проверка окна «Перки, баффы, скиллы».
//
// ЧТО СТЕРЕЖЁТ. Окно показывает четыре раздела с описанием каждого пункта и
// помечает действующие. Три дефекта здесь невидимы статической сверке строк:
//
//   1) у пункта нет описания — окно не отвечает на вопрос «как это работает»;
//   2) действующий пункт не выделен — игрок не отличает своё от доступного;
//   3) нажатие не отправляет запрос — окно выглядит живым, но ничего не меняет.
//
// Проверка считает РЕАЛЬНЫЙ DOM: разделы, пометки, цвет выделения и отправленные
// сообщения. Мутация «убрать выделение активного» обязана её уронить.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-perks-"));
fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });

for (const name of ["theme.css", "vitals.js", "perks.js", "web_log.js"]) {
  const src = path.join(root, "src", "AssistQuestEditor.App", "Web", name);
  if (fs.existsSync(src)) fs.copyFileSync(src, path.join(tmp, "Web", name));
}

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const parseRgb = value => {
  const match = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(String(value || ""));
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
};

// Жёлтый = оба канала высокие, синий заметно ниже. Так он отличается и от
// белого (все высокие), и от синего акцента подсветки.
const isYellowish = value => {
  const rgb = parseRgb(value);
  return !!rgb && rgb[0] >= 140 && rgb[1] >= 120 && rgb[2] + 30 <= Math.min(rgb[0], rgb[1]);
};

const html = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "perks.html"), "utf8");
fs.writeFileSync(path.join(tmp, "Web", "p.html"), html
  .replace(/\?v=[0-9a-f]+/g, "")
  .replace("</head>", `<script>window.chrome = { webview: { listeners: new Map(),
  addEventListener(t, h) { const l = this.listeners.get(t) || []; l.push(h); this.listeners.set(t, l); },
  dispatch(type, data) { (this.listeners.get(type) || []).forEach(h => h({ data: JSON.stringify(data) })); },
  postMessage(m) { (window.__sent = window.__sent || []).push(m); } } };</script></head>`));

// Пункты покрывают все четыре раздела и все четыре вида действий: без этого
// проверка не отличила бы «кнопка есть» от «кнопка правильная».
const payload = {
  type: "perks",
  entries: [
    {
      id: "perk.discharged", category: "perk",
      name: "Уволен", displayName: "Уволен (В разработке) ",
      description: "Даётся за провал рабочего поручения.",
      affects: "Репутация у работодателей, доступные квесты.",
      implemented: false, durationLabel: "", maxLevel: 0, level: 0, active: false
    },
    {
      id: "perk.breadwinner", category: "perk",
      name: "Кормилец", displayName: "Кормилец (активен)",
      description: "Даётся за регулярные поставки еды на базу.",
      affects: "Репутация у жителей, бонус к опыту.",
      implemented: true, durationLabel: "", maxLevel: 0, level: 0, active: true
    },
    {
      id: "skill.mechanics", category: "skill",
      name: "Механик", displayName: "Механик (активен)",
      description: "Практика полевого ремонта.",
      affects: "Скорость ремонта транспорта.",
      implemented: true, durationLabel: "", maxLevel: 5, level: 3, active: true
    },
    {
      id: "bull", category: "buff",
      name: "Бык", displayName: "Бык",
      description: "Даётся за дисциплину в спорте.",
      affects: "Негатив не накапливается вовсе.",
      implemented: true, durationLabel: "2 ч игрового времени",
      maxLevel: 0, level: 0, active: false
    },
    {
      id: "rested", category: "buff",
      name: "Отдохнувший", displayName: "Отдохнувший (активен)",
      description: "Выдаётся, когда усталость доходит до нуля.",
      affects: "Опыт +25%, стресс копится медленнее.",
      implemented: true, durationLabel: "25 мин реального времени",
      maxLevel: 0, level: 0, active: true
    },
    {
      id: "burnout", category: "debuff",
      name: "Выгорание", displayName: "Выгорание (активен)",
      description: "Выдаётся разово при входе стресса в критическую зону.",
      affects: "Усталость копится на 10% быстрее.",
      implemented: true, durationLabel: "15 мин реального времени",
      maxLevel: 0, level: 0, active: true
    }
  ]
};

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.goto("file://" + path.join(tmp, "Web", "p.html").replace(/\\/g, "/"));

  // Страница обязана попросить данные сама: окно, открытое после последнего
  // обновления мира, иначе показывало бы пустой список.
  const ready = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "perks_ready");
  check(!!ready, "страница обязана сообщать о готовности (perks_ready).");

  await page.evaluate(data => window.chrome.webview.dispatch("message", data), payload);
  await page.waitForTimeout(120);

  // ── Разделы ───────────────────────────────────────────────────────────────
  const sections = await page.$$eval(".perksCard .perksCardTitle span:first-child",
    nodes => nodes.map(node => node.textContent.trim()));
  for (const title of ["Перки", "Скиллы", "Баффы", "Дебаффы"]) {
    check(sections.includes(title), "нет раздела «" + title + "»: " + JSON.stringify(sections));
  }

  // ── Описания ──────────────────────────────────────────────────────────────
  const entries = await page.$$eval(".perkEntry", nodes => nodes.map(node => ({
    id: node.getAttribute("data-perk-entry"),
    text: node.textContent,
    active: node.classList.contains("active"),
    highlighted: node.classList.contains("highlight"),
    name: (node.querySelector(".perkEntryName") || {}).textContent || ""
  })));

  check(entries.length === payload.entries.length,
    "показаны не все пункты: " + entries.length + " из " + payload.entries.length);

  for (const entry of entries) {
    const source = payload.entries.find(item => item.id === entry.id);
    check(entry.text.includes(source.description),
      "у пункта «" + entry.id + "» нет описания.");
    // «Влияет» — второй вопрос автора; описание без него неполно.
    check(entry.text.includes(source.affects),
      "у пункта «" + entry.id + "» не сказано, на что он влияет.");
  }

  // ── Пометки и выделение ───────────────────────────────────────────────────
  const activeEntries = entries.filter(entry => entry.active);
  check(activeEntries.length === 4,
    "выделено активных пунктов: " + activeEntries.length + ", ожидалось 4.");
  check(activeEntries.every(entry => /\(активен\)/.test(entry.name)),
    "у действующего пункта нет пометки «(активен)»: " +
    JSON.stringify(activeEntries.map(entry => entry.name)));

  const unfinished = entries.find(entry => entry.id === "perk.discharged");
  check(!unfinished.active, "незаконченный перк не должен быть активным.");
  check(/\(В разработке\)/.test(unfinished.name),
    "у незаконченного пункта нет пометки «(В разработке)»: " + unfinished.name);

  // Активный пункт обязан быть ЖЁЛТЫМ: автор просил именно жёлтое выделение.
  for (const entry of activeEntries) {
    const style = await page.$eval(
      "[data-perk-entry='" + entry.id + "']",
      node => {
        const computed = getComputedStyle(node);
        return { border: computed.borderColor, background: computed.backgroundColor };
      });
    check(isYellowish(style.border),
      "рамка активного пункта «" + entry.id + "» не жёлтая: " + style.border);
  }

  // ── Действия ──────────────────────────────────────────────────────────────
  const buffButton = await page.$eval(
    "[data-perk-entry='bull'] [data-perk-action]",
    node => ({ action: node.getAttribute("data-perk-action"), label: node.textContent.trim() }));
  check(buffButton.action === "activate",
    "у неактивного баффа кнопка не «Активировать»: " + JSON.stringify(buffButton));

  const restedButton = await page.$eval(
    "[data-perk-entry='rested'] [data-perk-action]",
    node => ({ action: node.getAttribute("data-perk-action"), label: node.textContent.trim() }));
  check(restedButton.action === "deactivate",
    "у активного баффа кнопка не «Деактивировать»: " + JSON.stringify(restedButton));

  const perkButton = await page.$eval(
    "[data-perk-entry='perk.breadwinner'] [data-perk-action]",
    node => ({ action: node.getAttribute("data-perk-action"), label: node.textContent.trim() }));
  check(perkButton.action === "remove",
    "у полученного перка кнопка не «Убрать»: " + JSON.stringify(perkButton));

  // Незаконченный перк: кнопка видна, но НЕ работает — спрятать её значило бы
  // выдать «в разработке» за отсутствие возможности.
  const disabled = await page.$eval(
    "[data-perk-entry='perk.discharged'] [data-perk-action]",
    node => node.hasAttribute("disabled"));
  check(disabled, "кнопка незаконченного перка обязана быть отключена.");

  // Умение: поле очков с текущим уровнем и максимумом.
  const skillInput = await page.$eval(
    "[data-perk-entry='skill.mechanics'] [data-perk-level]",
    node => ({ value: node.value, max: node.getAttribute("max") }));
  check(skillInput.value === "3" && skillInput.max === "5",
    "поле очков умения неверно: " + JSON.stringify(skillInput));

  // ── Отправка правок ───────────────────────────────────────────────────────
  await page.evaluate(() => { window.__sent.length = 0; });

  await page.click("[data-perk-entry='bull'] [data-perk-action='activate']");
  const activate = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_effect");
  check(!!activate, "клик по «Активировать» не отправил set_effect.");
  check(activate && activate.effectId === "bull" && activate.enabled === true,
    "set_effect отправлен неверно: " + JSON.stringify(activate));

  await page.click("[data-perk-entry='rested'] [data-perk-action='deactivate']");
  const deactivate = (await page.evaluate(() => window.__sent || []))
    .filter(message => message && message.action === "set_effect")
    .pop();
  check(deactivate && deactivate.effectId === "rested" && deactivate.enabled === false,
    "set_effect на деактивацию отправлен неверно: " + JSON.stringify(deactivate));

  await page.click("[data-perk-entry='perk.breadwinner'] [data-perk-action='remove']");
  const perk = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_perk");
  check(perk && perk.perkId === "perk.breadwinner" && perk.enabled === false,
    "set_perk отправлен неверно: " + JSON.stringify(perk));

  // Очки умения: только по Enter или уходу фокуса, а не на каждый символ —
  // «1» это префикс «12», и отправка по символу переписывала бы уровень в наборе.
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.fill("[data-perk-entry='skill.mechanics'] [data-perk-level]", "1");
  await page.locator("[data-perk-entry='skill.mechanics'] [data-perk-level]").press("Enter");
  const level = (await page.evaluate(() => window.__sent || []))
    .find(message => message && message.action === "set_skill_level");
  check(!!level, "Enter в поле очков не отправил set_skill_level.");
  check(level && level.skillId === "skill.mechanics" && level.level === 1,
    "set_skill_level отправлен неверно: " + JSON.stringify(level));

  // ── Подсветка по ссылке ───────────────────────────────────────────────────
  //
  // Сравниваем РАМКУ подсвеченного пункта с рамкой обычного, а не с жёстко
  // зашитым цветом: акцент темы может меняться, и проверка, привязанная к его
  // оттенку, ломалась бы при смене палитры, ничего не сообщая о подсветке.
  const plainBorder = await page.$eval("[data-perk-entry='bull']",
    node => getComputedStyle(node).borderColor);

  await page.evaluate(() => window.chrome.webview.dispatch("message", {
    type: "highlight", perkId: "burnout"
  }));
  await page.waitForTimeout(80);

  const highlightedEntry = await page.$eval("[data-perk-entry='burnout']",
    node => ({
      border: getComputedStyle(node).borderColor,
      background: getComputedStyle(node).backgroundColor,
      marked: node.classList.contains("highlight")
    }));

  check(highlightedEntry.marked,
    "ссылка на пункт не пометила его классом подсветки.");
  check(highlightedEntry.border !== plainBorder,
    "рамка подсвеченного пункта не отличается от обычной: " + highlightedEntry.border);
  check(highlightedEntry.background !== "rgba(0, 0, 0, 0)",
    "у подсвеченного пункта нет более светлого фона: " + highlightedEntry.background);

  if (errors.length) throw new Error("pageerror: " + errors.join(" | "));

  if (failures.length) {
    console.error("Perks smoke: ошибок " + failures.length);
    for (const failure of failures) console.error("  • " + failure);
    process.exitCode = 1;
  } else {    console.log("Окно перков: OK (" + sections.length + " разделов, " +
      entries.length + " пунктов, " + activeEntries.length + " активных)");
  }
} finally {
  await closeBrowser(browser);
}
