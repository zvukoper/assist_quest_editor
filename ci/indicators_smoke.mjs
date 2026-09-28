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
  // Истощение усталости 100%: формула обязана показать коэффициент ×7 (2+20·0,25).
  conditions: {
    cumulativeHealth: 1000, cumulativeEnergy: 2000, cumulativeHydration: 500,
    stress: 3000, cumulativeStress: 800, cumulativeFatigue: 10000,
    criticalFatigueGameSeconds: 0, criticalStressGameSeconds: 0,
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
    simulationRunning: true, simulationPaused: false
  }), snapshot);
  await page.waitForTimeout(400);

  const read = await page.evaluate(() => {
    const cards = [...document.querySelectorAll("#indicatorsBody .indicatorCard")];
    return cards.map(card => ({
      name: card.querySelector(".indicatorName")?.textContent?.trim() || "",
      value: card.querySelector(".indicatorValue")?.textContent?.trim() || "",
      facts: [...card.querySelectorAll(".indicatorFact")].map(n => n.textContent.trim())
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
  // равен 2 + 20·0,25 = 7 — он обязан быть назван в факторах.
  const fatigue = read.find(card => card.name === "Усталость");
  check(fatigue && fatigue.facts.some(text => /коэффициент\s+×7\b/.test(text)),
    "формула усталости обязана называть коэффициент ×7 при 100% истощения: " +
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
