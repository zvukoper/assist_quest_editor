// Инвентарь: ОДНО окно игрока — сетка 6×3 слева, персонаж и репутация справа.
//
// Проверка ПОВЕДЕНЧЕСКАЯ в той части, где дело касается раскладки: размер ячейки
// задают ДВА места — CSS для страницы и InventoryLayoutRules для окна WinForms.
// Разойдись они, и содержимое либо не влезет, либо останется пустая полоса.
// Такое расхождение видно только сравнением, а не чтением кода.
//
// Размер окна сверяется с КЛИЕНТСКОЙ областью, а не с шириной сетки: ширина 800
// рассчитана на ДВЕ колонки окна игрока (инвентарь и сведения об игроке) плюс
// зазор между ними, поэтому сетка заведомо уже окна.
// Лишние предметы обязаны давать НОВЫЙ РЯД и вертикальную прокрутку — прежде
// девятнадцатый предмет просто исчезал.
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
const playerPanelsJs = read("src/AssistQuestEditor.App/Web/playerPanels.js");
const inventoryForm = read("src/AssistQuestEditor.App/Host/InventoryForm.cs");
const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
// Базовый класс окон: именно он подключает хранилище геометрии по ключу окна.
const webViewForm = read("src/AssistQuestEditor.App/Host/WebViewForm.cs");

// --- 1. Отдельное ОКНО, а не панель поверх карты ---

check(/class InventoryForm : WebViewForm/.test(inventoryForm),
  "Инвентарь должен быть отдельным окном (наследником WebViewForm), а не панелью.");
check(/inventory\.html/.test(inventoryForm),
  "Окно инвентаря должно открывать свою страницу.");
// Заголовок окна называет ИГРОКА, а не сумку: в одном окне лежат инвентарь,
// персонаж и репутация, и «Инвентарь» обещало бы только содержимое сумки.
check(/base\(\s*"Игрок",\s*"inventory\.html"/.test(inventoryForm),
  "Окно игрока обязано называться «Игрок»: оно показывает не только сумку.");
// Размер окна берётся из правила, а не задан числом: иначе окно и сетка разойдутся.
check(/InventoryLayoutRules\.WindowWidth/.test(inventoryForm) &&
      /InventoryLayoutRules\.WindowHeight/.test(inventoryForm),
  "Размер окна инвентаря не берётся из правила раскладки.");
// Минимальный размер равен расчётному: уже сетки окно быть не может.
check(/MinimumSize = new Size\([\s\S]{0,120}?InventoryLayoutRules\.WindowWidth/.test(inventoryForm),
  "Минимальный размер окна меньше сетки: часть ячеек уедет за край.");

// ОБЕ панели обязаны УЙТИ с карты: инвентарь, персонаж и репутация теперь живут
// в одном отдельном окне игрока (inventory.html), а оверлей на карте спорил
// с ней за место. Требование изменилось осознанно: раньше на карте оставалась
// панель персонажа, но с появлением окна игрока это стало ДВУМЯ местами с одними
// и теми же данными, которые неизбежно разошлись бы.
check(!/id="inventoryPanel"/.test(simulatorHtml),
  "Панель инвентаря осталась на карте: она должна переехать в отдельное окно.");
check(!/id="characterPanel"/.test(simulatorHtml),
  "Панель персонажа осталась на карте: она тоже переехала в окно игрока.");
check(!/id="playerOverlay"/.test(simulatorHtml),
  "Оверлей игрока остался на карте: панели переехали в окно игрока целиком.");
check(!/const inventoryPanel = document\.getElementById/.test(simulatorJs),
  "Симулятор всё ещё ищет панель инвентаря, которой больше нет.");
check(/id="inventoryPanel"/.test(inventoryHtml) && /id="playerInfoPanel"/.test(inventoryHtml),
  "Окно игрока обязано содержать обе колонки: инвентарь и сведения об игроке.");
// Персонаж и репутация — ОБЩИЙ модуль, а не копия разметки: те же данные
// рисуются из одного места, иначе окно игрока и карта показывали бы разное.
check(/AssistPlayerPanels\.renderTabs/.test(inventoryHtml),
  "Окно игрока не использует общий модуль персонажа и репутации.");
check(/window\.AssistPlayerPanels\s*=/.test(playerPanelsJs) &&
      /function renderTabs\(container, snapshot, activeTab, onTabChanged\)/.test(playerPanelsJs),
  "Модуль playerPanels.js обязан быть готовым к подключению и отдавать renderTabs.");

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

// Окно страницы просит закрыть себя тем же способом, каким сообщает о
// просмотренном предмете: страница не может закрыть окно сама, решение
// принимает Host. Это нужно для клавиши I, нажатой ВНУТРИ окна: иначе клавиша
// работала бы только в Симуляторе, а в самом инвентаре — нет.
check(/case "close_inventory":/.test(inventoryForm) &&
      /CloseRequested\?\.Invoke/.test(inventoryForm),
  "Окно инвентаря не обрабатывает запрос close_inventory: клавиша I внутри окна не закроет его.");
check(/CloseRequested \+=/.test(simulatorForm) && /CloseInventoryWindow\(\)/.test(simulatorForm),
  "Симулятор не подписан на закрытие окна инвентаря: Escape внутри окна не сработает.");
check(/KeyI/.test(inventoryHtml) && /close_inventory/.test(inventoryHtml),
  "Страница инвентаря должна отправлять close_inventory по клавише I.");

// Размер и положение окна ОБЯЗАНЫ сохраняться. Прежде базовый конструктор
// восстанавливал запомненные размеры, а следующий за ним PlaceOnSecondaryScreen
// их перезаписывал — поэтому окно каждый раз открывалось заново в углу.
// Ключ геометрии — "inventory": по нему запись ищется при следующем открытии.
check(/WindowGeometryStore\.HasSaved\("inventory"\)/.test(inventoryForm),
  "Окно инвентаря не проверяет сохранённую геометрию перед установкой позиции по умолчанию.");
// Проверяется САМА конструкция «если не сохранено — поставить по умолчанию».
// Сравнение позиций в файле не годится: имя метода упоминается ещё и в
// пояснении к правке, и пояснение оказалось бы «раньше» вызова.
check(/if \(!WindowGeometryStore\.HasSaved\("inventory"\)\)[\r\n\s]*PlaceOnSecondaryScreen\(\)/.test(inventoryForm),
  "Установка позиции по умолчанию должна выполняться ТОЛЬКО при отсутствии сохранённой геометрии.");
// Подключение к хранилищу делает БАЗОВЫЙ класс по ключу, переданному в base(...).
// Поэтому проверяется передача ключа, а не вызов Attach в самой форме: искать
// его здесь значило бы требовать дублирования уже сделанного базой.
check(/"inventory"\)/.test(inventoryForm) && /WindowGeometryStore\.Attach/.test(webViewForm),
  "Окно инвентаря не подключено к хранилищу геометрии: размер не сохранится.");

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

// --- 4. Прокрутка: ВЕРТИКАЛЬНАЯ разрешена, ГОРИЗОНТАЛЬНАЯ запрещена ---

// Раньше здесь требовалось отсутствие любой прокрутки. Требование автора
// изменилось: если предметов больше, чем ячеек, снизу добавляется ряд и
// появляется прокрутка. Горизонтальная остаётся запрещённой: она означала бы,
// что сетка шире окна, и часть ячеек уехала бы за край.
const gridRules = (() => {
  const start = themeCss.indexOf(".inventoryGrid{");
  return start < 0 ? "" : themeCss.slice(start, themeCss.indexOf("}", start));
})();
check(gridRules.length > 0, "В theme.css должно быть правило .inventoryGrid.");
check(/overflow-y:auto/.test(gridRules),
  "Сетка инвентаря должна прокручиваться по вертикали: " + gridRules);
check(!/overflow-y:hidden/.test(gridRules),
  "Вертикальная прокрутка сетки запрещена, а без неё лишние ряды недоступны: " + gridRules);
check(/overflow-x:hidden/.test(gridRules),
  "Горизонтальная прокрутка сетки должна быть запрещена: " + gridRules);
// Число столбцов в CSS и в правиле раскладки обязано совпадать: расхождение
// дало бы либо пустую колонку, либо перенос строки и лишний ряд.
const cssColumns = /grid-template-columns:repeat\((\d+)/.exec(gridRules);
const ruleColumns = /Columns = (\d+);/.exec(layout);
check(cssColumns && ruleColumns && cssColumns[1] === ruleColumns[1],
  "Число столбцов в CSS и в InventoryLayoutRules разошлось: " +
  `${cssColumns?.[1]} против ${ruleColumns?.[1]}.`);

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
  const client = /client:\s*(\d+)x(\d+)/.exec(probe);
  const window = /window:\s*(\d+)x(\d+)/.exec(probe);
  const minimum = number("minimum-window-height");

  check(columns === 6 && rows === 3, `Сетка должна быть 6×3, а не ${columns}×${rows}.`);
  check(capacity === 18, `Ячеек должно быть 18, а не ${capacity}.`);
  check(cell === 39, `Ячейка должна быть 39 px, а не ${cell} (четверть площади 78×78).`);
  // Согласованность размеров проверяется в самой пробе: она сравнивает
  // клиента с сеткой и минимум с составом окна.
  check(/consistency: ok/.test(probe),
    "Размеры инвентаря несогласованы: " + (/consistency:.*/.exec(probe) || ["нет строки"])[0]);

  // Размер окна обязан ВМЕЩАТЬ сетку, а не быть подобранным на глаз.
  if (grid && window && client) {
    const gridWidth = Number(grid[1]);
    const gridHeight = Number(grid[2]);
    const clientWidth = Number(client[1]);
    const clientHeight = Number(client[2]);
    const windowWidth = Number(window[1]);
    const windowHeight = Number(window[2]);

    check(gridWidth === 6 * 39 + 5 * 7 + 20,
      `Ширина сетки не сходится: ${gridWidth}.`);
    check(gridHeight === 3 * 39 + 2 * 7 + 20,
      `Высота сетки не сходится: ${gridHeight}.`);
    // Клиент считается под ДВЕ колонки окна игрока: инвентарь слева,
    // персонаж и репутация справа. 800 задано правилом раскладки явно,
    // а не выведено из сетки: колонка сведений занимает фиксированные 290 px.
    check(clientWidth === 800 && clientHeight === 392,
      `Клиентская область должна быть 800×392 (инвентарь + сведения об игроке): ` +
      `${clientWidth}×${clientHeight}.`);
    check(clientWidth >= gridWidth,
      `Клиент (${clientWidth}) уже сетки (${gridWidth}): появится горизонтальная прокрутка.`);
    check(clientHeight >= gridHeight,
      `Клиент (${clientHeight}) ниже сетки (${gridHeight}): ячейки уедут за край.`);
    check(windowWidth === clientWidth + 4 && windowHeight === clientHeight + 4 + 31,
      `Внешний размер окна не сходится с клиентом: ${windowWidth}×${windowHeight}.`);
    // Окно ДВУХКОЛОНОЧНОЕ, поэтому ширина — сетка плюс колонка сведений и зазор,
    // а не «сетка на всю ширину». Проверяется, что колонке сведений реально
    // хватает места: без него сведения сжались бы в ноль, а сетка осталась бы целой.
    const infoColumn = clientWidth - gridWidth;
    check(infoColumn >= 290,
      `Под сведения об игроке осталось ${infoColumn} px при клиенте ${clientWidth} ` +
      `и сетке ${gridWidth}: колонка сведений сжата.`);
    // Минимум — ровно одна строка сетки: меньше нельзя, больше запрещало бы
    // уменьшать окно, хотя прокрутка это уже позволяет.
    check(minimum > 0 && minimum < windowHeight,
      `Минимум высоты (${minimum}) должен быть меньше исходного размера (${windowHeight}).`);
  } else {
    failures.push("Проба не сообщила размеры сетки, клиента или окна: " + probe);
  }
}

// --- 7. Реальная отрисовка: 18 ячеек, все квадратные ---

const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 800, height: 392 } });
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

  check(layoutReport.slots >= 18,
    `Ячеек должно быть НЕ МЕНЬШЕ 18 (6×3), отрисовано ${layoutReport.slots}.`);
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
  // Прокрутка по вертикали ДОПУСКАЕТСЯ (предметов может быть больше), а по
  // горизонтали — нет: она означала бы, что сетка шире окна.
  check(layoutReport.scrollWidth <= layoutReport.clientWidth + 1,
    `Появилась горизонтальная прокрутка: ${layoutReport.scrollWidth} > ${layoutReport.clientWidth}.`);
  check(layoutReport.notificationsPresent,
    "Контейнер уведомлений инвентаря отсутствует: выдача предмета не покажется.");

  check(pageErrors.length === 0, "Ошибки страницы инвентаря: " + pageErrors.join("; "));

  // Предметов БОЛЬШЕ, чем ячеек: снизу добавляется ряд, появляется вертикальная
  // прокрутка. Раньше девятнадцатый предмет просто исчезал: сетка была жёстко
  // 6×3, и места под него не было.
  // Пять рядов (243 px) ещё УМЕЩАЮТСЯ в отведённую высоту, поэтому для
  // проверки прокрутки нужно больше предметов: 60 штук дают 10 рядов.
  const manyItems = {};
  for (let index = 0; index < 60; index += 1) manyItems["item." + index] = 1;
  await page.evaluate(items => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "snapshot",
        snapshot: { player: {}, inventory: { items }, itemCatalog: [] },
        itemCatalog: []
      })
    }));
  }, manyItems);
  await page.waitForTimeout(250);

  const overflowReport = await page.evaluate(() => {
    const grid = document.querySelector(".inventoryGrid");
    const slots = [...document.querySelectorAll(".inventorySlot")];
    const rows = slots.length / 6;
    return {
      slots: slots.length,
      rows,
      scrollHeight: grid ? grid.scrollHeight : 0,
      clientHeight: grid ? grid.clientHeight : 0,
      scrollWidth: grid ? grid.scrollWidth : 0,
      clientWidth: grid ? grid.clientWidth : 0
    };
  });

  check(overflowReport.slots >= 60,
    `При 60 предметах должно быть не меньше 60 ячеек, отрисовано ${overflowReport.slots}.`);
  check(overflowReport.rows > 3,
    `При 60 предметах рядов должно стать больше трёх, а не ${overflowReport.rows}.`);
  check(overflowReport.scrollHeight > overflowReport.clientHeight,
    "При 60 предметах должна появиться вертикальная прокрутка: " +
    `${overflowReport.scrollHeight} против ${overflowReport.clientHeight}.`);
  check(overflowReport.scrollWidth <= overflowReport.clientWidth + 1,
    `Горизонтальная прокрутка при 60 предметах: ${overflowReport.scrollWidth} > ${overflowReport.clientWidth}.`);
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Инвентарь: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Инвентарь: OK окно игрока (инвентарь + персонаж/репутация), сетка 6x3, ячейка 39px, " +
  "лишние ряды с прокруткой, разметка одна.");

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
