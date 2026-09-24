// Инвентарь: отдельное окно, сетка 6×3, ячейки вчетверо меньше.
//
// Проверка ПОВЕДЕНЧЕСКАЯ в той части, где дело касается раскладки: размер ячейки
// задают ДВА места — CSS для страницы и InventoryLayoutRules для окна WinForms.
// Разойдись они, и содержимое либо не влезет (появится полоса прокрутки там, где
// сетка рассчитана на весь размер окна), либо останется пустая полоса.
// Такое расхождение видно только сравнением, а не чтением кода.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { chromium } from "playwright";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const themeCss = read("src/AssistQuestEditor.App/Web/theme.css");
const inventoryJs = read("src/AssistQuestEditor.App/Web/inventory.js");
const inventoryHtml = read("src/AssistQuestEditor.App/Web/inventory.html");
const simulatorHtml = read("src/AssistQuestEditor.App/Web/simulator.html");
const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
const layout = read("src/AssistQuestEditor.Domain/InventoryLayoutRules.cs");
const inventoryForm = read("src/AssistQuestEditor.App/Host/InventoryForm.cs");
const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");

// --- 1. Отдельное ОКНО, а не панель поверх карты ---

check(/class InventoryForm : WebViewForm/.test(inventoryForm),
  "Инвентарь должен быть отдельным окном (наследником WebViewForm), а не панелью.");
check(/inventory\.html/.test(inventoryForm),
  "Окно инвентаря должно открывать свою страницу.");
// Размер окна берётся из правила, а не задан числом: иначе окно и сетка разойдутся.
check(/InventoryLayoutRules\.WindowWidth/.test(inventoryForm) &&
      /InventoryLayoutRules\.WindowHeight/.test(inventoryForm),
  "Размер окна инвентаря не берётся из правила раскладки.");
// Минимальный размер равен расчётному: уже сетки окно быть не может.
check(/MinimumSize = new Size\([\s\S]{0,120}?InventoryLayoutRules\.WindowWidth/.test(inventoryForm),
  "Минимальный размер окна меньше сетки: часть ячеек уедет за край.");

// Панель инвентаря обязана УЙТИ с карты: иначе сумка и персонаж снова начнут
// делить место в оверлее, ради чего окно и делалось.
check(!/id="inventoryPanel"/.test(simulatorHtml),
  "Панель инвентаря осталась на карте: она должна переехать в отдельное окно.");
check(/id="characterPanel"/.test(simulatorHtml),
  "Панель персонажа должна остаться на карте: она читается вместе с地图.");
check(!/const inventoryPanel = document\.getElementById/.test(simulatorJs),
  "Симулятор всё ещё ищет панель инвентаря, которой больше нет.");

// --- 2. Клавиша I открывает и закрывает окно, и решение принимает Host ---

check(/INVENTORY_HOTKEY_CODES = new Set\(\["KeyI"\]\)/.test(simulatorJs),
  "Горячая клавиша инвентаря должна определяться по event.code: в русской раскладке key = «ш».");
check(/INVENTORY_HOTKEY_KEYS = new Set\(\["i", "ш"\]\)/.test(simulatorJs),
  "Горячая клавиша должна работать в обеих раскладках.");
check(/send\(\{ action: "toggle_inventory"/.test(simulatorJs),
  "Страница не запрашивает открытие окна инвентаря у Host: сама она форму создать не может.");
check(/case "toggle_inventory":/.test(simulatorForm),
  "Host не обрабатывает запрос открытия инвентаря.");
check(/OpenInventoryWindow\(\)/.test(simulatorForm) &&
      /CloseInventoryWindow\(\)/.test(simulatorForm),
  "Host не открывает и не закрывает окно инвентаря.");
// Повторное нажатие I обязано ПОДНИМАТЬ окно, а не создавать второе: два окна
// с одним содержимым рано или поздно покажут разное.
check(/private InventoryForm\? _inventoryForm;/.test(simulatorForm) &&
      /_inventoryForm is not null && !_inventoryForm\.IsDisposed/.test(simulatorForm),
  "Окно инвентаря не защищено от повторного создания: откроется второе окно.");
// Окно закрывается вместе с Симулятором: без хозяина оно осталось бы с
// устаревшими данными, обновлять которые некому.
check(/_inventoryForm\.Close\(\)[\s\S]{0,60}?_inventoryForm = null/.test(simulatorForm),
  "Окно инвентаря не закрывается вместе с Симулятором.");

// --- 3. Сетка 6×3 и четверть площади ячейки ---

check(/var COLUMNS = 6;/.test(inventoryJs) && /var ROWS = 3;/.test(inventoryJs),
  "Размер сетки инвентаря должен быть 6×3.");
check(/CAPACITY = COLUMNS \* ROWS/.test(inventoryJs),
  "Число ячеек должно считаться из формы сетки, а не задаваться отдельно.");
check(/--inventory-cell:39px/.test(themeCss),
  "Сторона ячейки в CSS должна быть 39px (четверть площади прежних 78px).");
check(/CellSize = 39;/.test(layout),
  "Сторона ячейки в правиле раскладки должна совпадать с CSS (39).");
// Прежний размер ячейки быть НЕ должен: четверть площади означает сторону вдвое
// меньше. Проверяются ОБЪЯВЛЕНИЯ, а не любые вхождения чисел: `78px` встречается
// ещё и в пояснительном комментарии к правилу, а `min-height:72px` относится к
// текстовому полю формы и к инвентарю отношения не имеет. Проверка по всему
// файлу падала бы на своём же тексте.
check(!/grid-auto-rows:minmax\(78px/.test(themeCss),
  "Строки сетки по-прежнему заданы прежним размером 78px: четверть площади не достигнута.");
check(!/\.inventorySlot\{[^}]*min-height:72px/.test(themeCss),
  "Ячейка инвентаря по-прежнему задана прежним размером 72px.");
// Ячейки квадратные.
check(/\.inventorySlot\{[\s\S]{0,200}?width:var\(--inventory-cell\);height:var\(--inventory-cell\)/.test(themeCss),
  "Ячейка инвентаря должна быть квадратной (ширина равна высоте).");
check(/grid-template-columns:repeat\(6/.test(themeCss),
  "В CSS должно быть ровно 6 столбцов сетки.");
// Подписи предметов не должны раздувать строку: иначе квадратность формы теряется.
check(/\.inventoryItemName\{[^}]*white-space:nowrap/.test(themeCss),
  "Подпись предмета переносится и увеличивает строку сетки: форма станет не 6×3.");

// --- 4. Прокрутки в окне быть не должно: размер подобран под сетку ---

check(/\.inventoryWindow \.inventoryGrid \{ overflow:hidden; \}/.test(inventoryHtml),
  "В окне инвентаря запрещена прокрутка: размер подобран под сетку, и полоса " +
  "означала бы, что размеры разъехались.");

// --- 5. Отрисовка одна на два места ---

// Модуль вызывается и страницей окна, и (для панели персонажа) остаётся общим.
check(/AssistInventory\.panelMarkup/.test(inventoryHtml),
  "Страница окна инвентаря не использует общий модуль отрисовки.");
// Копии разметки сетки быть НЕ должно: две реализации разошлись бы.
check(!/for \(let index = 0; index < 24/.test(simulatorJs),
  "В Симуляторе осталась собственная сетка инвентаря: разметка должна быть одна.");

// --- 6. Реальные размеры: число ячеек и размер окна ---

const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки размеров инвентаря.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-inventory-"));
  const report = path.join(workspace, "inventory.txt");

  try {
    execFileSync(exe, ["--inventory-probe", "--report", report],
      { stdio: "pipe", timeout: 120000 });
  } catch {
    // Код возврата сообщается текстом отчёта — читаем ниже.
  }

  const probe = fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  check(/Размеры инвентаря проверены/.test(probe), "Проба размеров не отработала: " + probe);

  const number = (key) => {
    const match = new RegExp(key + ":\\s*(\\d+)").exec(probe);
    return match ? Number(match[1]) : NaN;
  };

  const columns = number("columns");
  const rows = number("rows");
  const capacity = number("capacity");
  const cell = number("cell");
  const grid = /grid:\s*(\d+)x(\d+)/.exec(probe);
  const window = /window:\s*(\d+)x(\d+)/.exec(probe);

  check(columns === 6 && rows === 3, `Сетка должна быть 6×3, а не ${columns}×${rows}.`);
  check(capacity === 18, `Ячеек должно быть 18, а не ${capacity}.`);
  check(cell === 39, `Ячейка должна быть 39 px, а не ${cell} (четверть площади 78×78).`);

  // Размер окна обязан ВМЕЩАТЬ сетку, а не быть подобранным на глаз.
  if (grid && window) {
    const gridWidth = Number(grid[1]);
    const gridHeight = Number(grid[2]);
    const windowWidth = Number(window[1]);
    const windowHeight = Number(window[2]);

    check(gridWidth === 6 * 39 + 5 * 7 + 20,
      `Ширина сетки не сходится: ${gridWidth}.`);
    check(gridHeight === 3 * 39 + 2 * 7 + 20,
      `Высота сетки не сходится: ${gridHeight}.`);
    check(windowWidth >= gridWidth,
      `Окно (${windowWidth}) уже сетки (${gridWidth}): ячейки уедут за край.`);
    check(windowHeight >= gridHeight,
      `Окно (${windowHeight}) ниже сетки (${gridHeight}): ячейки уедут за край.`);
    // Правой панели в окне больше нет, поэтому окно не должно быть вдвое шире
    // сетки: это признак того, что размер считался под прежние две панели.
    check(windowWidth < gridWidth * 2,
      `Окно (${windowWidth}) вдвое шире сетки (${gridWidth}): размер считался ` +
      "под две панели, а в окне панель одна.");
  } else {
    failures.push("Проба не сообщила размеры сетки или окна: " + probe);
  }
}

// --- 7. Реальная отрисовка: 18 ячеек, все квадратные ---

const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 293, height: 323 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.goto("file:///" + path.join(root, "src", "AssistQuestEditor.App", "Web", "inventory.html")
    .replace(/\\/g, "/"));

  // Снимок подаётся ОТ Host, как в приложении: страница не читает данные сама.
  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 0, y: 0, z: 0 } },
          inventory: { items: { "ruslan.raw_meat": 3, "gosha.sausage": 7 }, newItemIds: ["gosha.sausage"] },
          playerVitals: { health: 80, maxHealth: 100, energy: 50, maxEnergy: 100, hydration: 60, maxHydration: 100, fatigue: 20, maxFatigue: 100 },
          playerProgress: { money: 12345, experience: 678, reserve: 9 }
        },
        itemCatalog: [
          { id: "ruslan.raw_meat", name: "Сырое мясо", description: "Мясо для шашлыка", color: "#8b4a3a" },
          { id: "gosha.sausage", name: "Домашняя колбаса", description: "Колбаса Гоши", color: "#a07040" }
        ]
      })
    }));
  });

  await page.waitForTimeout(250);

  const layoutReport = await page.evaluate(() => {
    const slots = [...document.querySelectorAll(".inventorySlot")];
    const rects = slots.map(slot => {
      const r = slot.getBoundingClientRect();
      return { w: Math.round(r.width), h: Math.round(r.height) };
    });

    const filled = document.querySelectorAll(".inventorySlot:not(.empty)").length;
    const square = rects.every(r => r.w === r.h && r.w > 0);
    const sizes = [...new Set(rects.map(r => r.w + "x" + r.h))];
    const grid = document.querySelector(".inventoryGrid");
    const gridStyle = grid ? getComputedStyle(grid) : null;

    return {
      slots: slots.length,
      filled,
      square,
      sizes,
      columns: gridStyle ? gridStyle.gridTemplateColumns.split(" ").length : 0,
      scrollWidth: grid ? grid.scrollWidth : 0,
      clientWidth: grid ? grid.clientWidth : 0,
      scrollHeight: grid ? grid.scrollHeight : 0,
      clientHeight: grid ? grid.clientHeight : 0,
      hasQty: document.querySelectorAll(".inventoryItemQty").length,
      notificationsPresent: !!document.getElementById("inventoryNotifications")
    };
  });

  check(layoutReport.slots === 18,
    `Ячеек должно быть 18 (6×3), отрисовано ${layoutReport.slots}.`);
  check(layoutReport.columns === 6,
    `Столбцов должно быть 6, отрисовано ${layoutReport.columns}.`);
  check(layoutReport.square,
    "Ячейки инвентаря не квадратные: " + layoutReport.sizes.join(", "));
  check(layoutReport.sizes.length === 1 && /^39x39$/.test(layoutReport.sizes[0]),
    "Сторона ячейки должна быть 39 px: " + layoutReport.sizes.join(", "));
  check(layoutReport.filled === 2,
    "Заполненных ячеек должно быть 2, отрисовано " + layoutReport.filled + ".");
  check(layoutReport.hasQty === 2,
    "У обоих предметов должно показываться количество.");
  // Прокрутки быть не должно: размер окна подобран под сетку.
  check(layoutReport.scrollWidth <= layoutReport.clientWidth + 1,
    `Появилась горизонтальная прокрутка: ${layoutReport.scrollWidth} > ${layoutReport.clientWidth}.`);
  check(layoutReport.scrollHeight <= layoutReport.clientHeight + 1,
    `Появилась вертикальная прокрутка: ${layoutReport.scrollHeight} > ${layoutReport.clientHeight}.`);
  check(layoutReport.notificationsPresent,
    "Контейнер уведомлений инвентаря отсутствует: выдача предмета не покажется.");

  check(pageErrors.length === 0, "Ошибки страницы инвентаря: " + pageErrors.join("; "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Инвентарь: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Инвентарь: OK отдельное окно, сетка 6x3, ячейка 39px, прокрутки нет, разметка одна.");

function findExecutable() {
  const base = path.join(root, "src", "AssistQuestEditor.App", "bin");
  if (!fs.existsSync(base)) return null;

  // Берётся САМЫЙ СВЕЖИЙ exe: в дереве лежат сборки Debug и Release, и
  // произвольная из них оказалась бы устаревшей.
  const found = [];
  const stack = [base];
  while (stack.length) {
    const current = stack.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (entry.name === "AssistQuestEditor.exe") found.push(full);
    }
  }

  return found.sort((left, right) =>
    fs.statSync(right).mtimeMs - fs.statSync(left).mtimeMs)[0] ?? null;
}
