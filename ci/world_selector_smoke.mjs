// Селектор [МИР][КАМПАНИЯ][меню] в главном окне.
//
// Смысл проверки — стык Host ↔ Web: селектор рисуется ТОЛЬКО из сообщения Host
// (`world_selection`), а выбор уходит обратно действием. Расхождение этих двух
// сторон невозможно заметить ни сборкой, ни чтением исходников, поэтому
// монтируется настоящий main.js и проверяется реальная разметка.
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const mainHtml = read("src/AssistQuestEditor.App/Web/main.html");
const mainJs = read("src/AssistQuestEditor.App/Web/main.js");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

// --- 1. Разметка: обе подписи и кнопки ---
check(/id="worldSelect"/.test(mainHtml), "В шапке нет списка миров.");
check(/id="campaignSelect"/.test(mainHtml), "В шапке нет списка кампаний.");
check(/>МИР</.test(mainHtml), "У списка миров нет подписи МИР.");
check(/>КАМПАНИЯ</.test(mainHtml), "У списка кампаний нет подписи КАМПАНИЯ.");
check(/id="worldFolder"/.test(mainHtml), "Нет кнопки папки мира.");
check(/id="worldMenu"/.test(mainHtml), "Нет кнопки меню.");
// Кнопка обязана БЫТЬ ПОДПИСАНА словом, а не одним глифом ▾: по треугольнику
// меню не находят — экспорт и импорт искали глазами по всей панели. Проверяется
// видимый ТЕКСТ кнопки (глиф исключён из accessible name через aria-hidden).
check(/id="worldMenu"[^>]*>Действия<span aria-hidden="true">▾<\/span>/.test(mainHtml),
  "Кнопка меню должна называть себя словом (например «Действия»), а не только знаком ▾.");
check(/class="worldSelectorButton" id="worldMenu"/.test(mainHtml),
  "Кнопка меню должна нести стиль остальных кнопок шапки: подпись без стиля выбьет высоту строки.");
// Селектор стоит ПЕРВЫМ среди элементов шапки: он отвечает на вопрос «что я
// редактирую», и без этого его искали бы глазами по всей панели.
check(mainHtml.indexOf('id="worldSelector"') < mainHtml.indexOf('id="simulatorState"'),
  "Селектор мира должен идти перед чипами состояния.");
// Подписи капитальными: рядом два списка, и без подписей их не различить.
check(/\.worldSelectorLabel\{[\s\S]{0,160}font-weight:800/.test(theme),
  "Подписи МИР/КАМПАНИЯ должны быть заметно выделены.");

// --- 2. Стили: меню поверх содержимого, ширина ограничена ---
check(/\.worldMenu\{[\s\S]{0,200}position:absolute/.test(theme),
  "Меню должно быть поверх содержимого: иначе оно сдвигало бы карточки при раскрытии.");
check(/\.worldMenu\[hidden\]\{display:none\}/.test(theme),
  "Закрытое меню не должно показываться.");
check(/\.worldSelectorSelect\{[\s\S]{0,200}max-width/.test(theme),
  "Ширина списков должна быть ограничена: длинное имя мира выдавит чипы за край.");

// --- 3. Host отправляет состояние селектора ---
check(/type = "world_selection"/.test(mainForm),
  "Host обязан отправлять состояние селектора (world_selection).");
check(/PostWorldSelection\(\);/.test(mainForm),
  "Состояние селектора обязано отправляться при готовности окна: иначе он пуст.");
check(/ResourceSelectorRules\.ResolveCampaignId/.test(mainForm),
  "Активная кампания должна выбираться правилом домена, а не склейкой в форме.");

// --- 4. Действия селектора обрабатываются ---
for (const action of ["select_world", "select_campaign", "open_world_folder"]) {
  check(mainForm.includes(`case "${action}":`),
    `Host не обрабатывает действие селектора «${action}».`);
}

// Пункты меню обязаны отвечать, а не молчать: молчаливый no-op выглядит как
// сломанная кнопка.
check(/private void NotifyNotImplemented/.test(mainForm),
  "Необработанные пункты меню обязаны сообщать о себе, а не молчать.");
// Экспорт, «Ред.» и сведения уже реализованы — в списке «ещё не реализовано»
// они остаться не должны, иначе проверка закрепила бы заглушку как норму.
// Создающие пункты выделены акцентом — по ним меню и открывают чаще всего.
check(/\.worldMenuCreate\{color:var\(--accent\);font-weight:700\}/.test(theme),
  "Создающие пункты меню должны быть выделены акцентом.");
for (const action of ["create_world", "create_campaign"]) {
  check(mainForm.includes(`case "${action}":`),
    `Host не знает пункт меню «${action}».`);
}
for (const action of ["world_info", "world_edit", "campaign_info", "campaign_edit",
                      "export_world", "export_campaign", "import_archive"]) {
  check(!new RegExp(`case "${action}":[\\s\\S]{0,120}?NotifyNotImplemented`).test(mainForm),
    `Пункт «${action}» всё ещё заглушка, хотя должен быть реализован.`);
}

// --- 5. Поведение реального main.js ---
const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`<!doctype html><html lang="ru"><head>
    <style>html,body{height:100%;margin:0;overflow:hidden}</style>
    <style>${theme.replaceAll("</style", "<\\/style")}</style></head>
    <body>${mainHtml.slice(mainHtml.indexOf("<body>") + 6, mainHtml.indexOf("</body>"))}</body></html>`);

  await page.evaluate(() => {
    window.__sent = [];
    window.__assistSend = payload => window.__sent.push(payload);
    window.__hostListeners = [];
    window.chrome = {
      webview: {
        addEventListener: (type, handler) => {
          if (type === "message") window.__hostListeners.push(handler);
        },
        postMessage() {}
      }
    };
    window.__deliverFromHost = data => {
      for (const handler of window.__hostListeners) {
        handler({ data: typeof data === "string" ? data : JSON.stringify(data) });
      }
    };
  });

  await page.addScriptTag({ content: mainJs });

  // Подписка на канал обязательна: без неё состояние селектора не дойдёт.
  const listeners = await page.evaluate(() => window.__hostListeners.length);
  check(listeners > 0,
    "main.js не подписан на chrome.webview: состояние селектора до страницы не дойдёт.");

  // --- Состояние Host → селектор ---
  await page.evaluate(() => window.__deliverFromHost({
    type: "world_selection",
    worlds: [
      { id: "sibirmap", name: "Сибирская карта" },
      { id: "demo", name: "Демо Мир" }
    ],
    campaigns: [
      { id: "common", name: "Common" },
      { id: "story", name: "Сюжет" }
    ],
    worldId: "sibirmap",
    campaignId: "story"
  }));
  await page.waitForTimeout(80);

  const rendered = await page.evaluate(() => {
    const world = document.getElementById("worldSelect");
    const campaign = document.getElementById("campaignSelect");
    return {
      worldOptions: [...world.options].map(option => option.textContent),
      worldValue: world.value,
      worldDisabled: world.disabled,
      campaignOptions: [...campaign.options].map(option => option.textContent),
      campaignValue: campaign.value,
      campaignDisabled: campaign.disabled
    };
  });

  check(rendered.worldOptions.join("|").includes("Сибирская карта"),
    "Список миров не заполнился из состояния Host: " + rendered.worldOptions.join("|"));
  check(rendered.worldValue === "sibirmap",
    "Селектор должен показывать мир, открытый в Host: " + rendered.worldValue);
  check(rendered.campaignValue === "story",
    "Селектор должен показывать активную кампанию: " + rendered.campaignValue);
  // «Создать…» — ПОСЛЕДНИЙ пункт каждого списка.
  check(/Создать мир/.test(rendered.worldOptions[rendered.worldOptions.length - 1] || ""),
    "«Создать мир…» должен быть последним пунктом списка миров: " +
    rendered.worldOptions.join("|"));
  check(/Создать кампанию/.test(rendered.campaignOptions[rendered.campaignOptions.length - 1] || ""),
    "«Создать кампанию…» должен быть последним пунктом списка кампаний: " +
    rendered.campaignOptions.join("|"));

  // --- Выбор кампании уходит в Host ---
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.selectOption("#campaignSelect", "common");
  await page.waitForTimeout(80);

  const campaignAction = await page.evaluate(() =>
    window.__sent.find(item => item.action === "select_campaign"));
  check(campaignAction?.campaignId === "common",
    "Выбор кампании не ушёл в Host: " + JSON.stringify(campaignAction));

  // --- Пункт «создать» не залипает в списке ---
  await page.evaluate(() => { window.__sent.length = 0; });
  const createValue = await page.evaluate(() => {
    const select = document.getElementById("campaignSelect");
    const option = [...select.options].find(item => item.textContent.includes("Создать"));
    select.value = option.value;
    select.dispatchEvent(new Event("change", { bubbles: true }));
    return { sent: window.__sent.slice(), valueAfter: select.value };
  });

  check(createValue.sent.some(item => item.action === "create_campaign"),
    "Выбор «Создать кампанию…» не отправил действие: " + JSON.stringify(createValue.sent));
  check(createValue.valueAfter === "story",
    "После выбора «Создать» список должен вернуться к активной кампании, а не остаться " +
    "на пункте создания: " + createValue.valueAfter);

  // --- Кнопка папки ---
  await page.evaluate(() => { window.__sent.length = 0; });
  await page.click("#worldFolder");
  await page.waitForTimeout(80);
  const folderAction = await page.evaluate(() =>
    window.__sent.find(item => item.action === "open_world_folder"));
  check(folderAction !== undefined, "Кнопка папки мира не отправила действие.");

  // --- Меню: открывается, содержит пункты, закрывается по Escape ---
  await page.click("#worldMenu");
  await page.waitForTimeout(80);

  const opened = await page.evaluate(() => {
    const menu = document.getElementById("worldMenuList");
    return {
      hidden: menu.hidden,
      items: [...menu.querySelectorAll("button")].map(button => button.dataset.action),
      expanded: document.getElementById("worldMenu").getAttribute("aria-expanded")
    };
  });

  check(opened.hidden === false, "Меню не открылось по кнопке.");
  check(opened.expanded === "true", "aria-expanded не обновлён при открытии меню.");
  for (const required of ["world_edit", "campaign_info", "export_world", "export_campaign",
                         "import_archive", "create_world", "create_campaign"]) {
    check(opened.items.includes(required),
      `В меню нет пункта «${required}»: ` + opened.items.join(", "));
  }
  check(opened.items.includes("world_info") && opened.items.includes("campaign_info"),
    "В меню нет пунктов сведений (ℹ️): " + opened.items.join(", "));

  await page.keyboard.press("Escape");
  await page.waitForTimeout(80);
  check(await page.evaluate(() => document.getElementById("worldMenuList").hidden),
    "Меню не закрылось по Escape: оно перекрывало бы содержимое окна.");

  // --- Пункт меню уходит действием и закрывает меню ---
  await page.click("#worldMenu");
  await page.waitForTimeout(60);
  await page.evaluate(() => { window.__sent.length = 0; });

  const infoClick = await page.evaluate(() => {
    const button = [...document.querySelectorAll("#worldMenuList button")]
      .find(item => item.dataset.action === "world_info");
    button.click();
    return { sent: window.__sent.slice(), hidden: document.getElementById("worldMenuList").hidden };
  });

  check(infoClick.sent.some(item => item.action === "world_info"),
    "Пункт «Сведения о мире» не отправил действие: " + JSON.stringify(infoClick.sent));
  check(infoClick.hidden === true, "Меню не закрылось после выбора пункта.");

  // --- Мир без кампаний: список кампаний заблокирован ---
  await page.evaluate(() => window.__deliverFromHost({
    type: "world_selection",
    worlds: [{ id: "empty", name: "Пустой мир" }],
    campaigns: [],
    worldId: "empty",
    campaignId: ""
  }));
  await page.waitForTimeout(80);

  const emptyState = await page.evaluate(() => ({
    campaignDisabled: document.getElementById("campaignSelect").disabled,
    folderDisabled: document.getElementById("worldFolder").disabled,
    campaignOptions: document.getElementById("campaignSelect").options.length
  }));
  check(emptyState.folderDisabled === false,
    "Папка мира должна оставаться доступной даже без кампаний.");
  check(emptyState.campaignOptions >= 1,
    "Список кампаний должен оставаться с пунктом «Создать».");

  // --- Нет миров: селектор заблокирован, а не пуст ---
  await page.evaluate(() => window.__deliverFromHost({
    type: "world_selection", worlds: [], campaigns: [], worldId: "", campaignId: ""
  }));
  await page.waitForTimeout(80);

  const noWorlds = await page.evaluate(() => ({
    worldDisabled: document.getElementById("worldSelect").disabled,
    folderDisabled: document.getElementById("worldFolder").disabled
  }));
  check(noWorlds.worldDisabled, "Без миров селектор мира должен быть заблокирован.");
  check(noWorlds.folderDisabled, "Без мира папка мира должна быть заблокирована.");

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join(" | "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Селектор мир/кампания: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Селектор мир/кампания: OK списки из Host, «Создать…» последним, меню открывается и закрывается, действия уходят.");
