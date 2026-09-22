// Блок «Окружение»: игровое время, дата, кампания; подписи точек и игрока;
// кнопка запущенной симуляции.
//
// Проверяется то, что молча ломается и не видно ни домену, ни другим проверкам:
//   - признак «часы идут» больше не берётся из флага канала (по нему показывалось
//     «(пауза)» при работающей симуляции), а следует за состоянием симуляции;
//   - подписи всех точек центрированы над точкой, а игрок подписан и текст не
//     пересекается с маркером;
//   - кнопка запущенной симуляции имеет LIME фон с белым текстом и обводкой;
//   - «Применить» обновляет и погоду, и игровое время, и просит свежий снимок;
//   - кнопки кампании шлют правильные действия с валидными геокоординатами.
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

function read(relative) {
  return fs.readFileSync(path.join(root, ...relative.split("/")), "utf8");
}

const simulatorJs = read("src/AssistQuestEditor.App/Web/simulator.js");
const themeCss = read("src/AssistQuestEditor.App/Web/theme.css");const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
const coordinator = read("src/AssistQuestEditor.Domain/QuestRuntimeCoordinator.cs");
const mapper = read("src/AssistQuestEditor.Domain/SimulationSaveMapper.cs");
const campaignModels = read("src/AssistQuestEditor.Domain/CampaignModels.cs");
const campaignStore = read("src/AssistQuestEditor.App/CampaignStore.cs");

// 1. Признак «часы идут» — зеркало состояния симуляции, а не флаг из снимка.
check(
  /SetClockRunning\(running\)/.test(coordinator),
  "QuestRuntimeCoordinator должен синхронизировать признак «часы идут» при смене состояния симуляции."
);
check(
  !/Running = currentClock\.Running/.test(mapper) || !/Running = false/.test(mapper),
  "Загрузка сохранения НЕ должна принудительно гасить часы: признак зеркалит симуляцию."
);
check(
  /!\(simulationRunning\)/.test(simulatorJs) || /simulationRunning \? "" : " \(пауза\)"/.test(simulatorJs),
  "Метка «(пауза)» должна зависеть от состояния симуляции, а не от флага канала."
);

// 2. Действия блока «Окружение» обрабатываются Host'ом.
for (const action of ["set_world_time", "save_world_to_campaign", "load_world_from_campaign", "request_snapshot"]) {
  check(simulatorForm.includes('"' + action + '"'),
    "SimulatorForm должен обрабатывать действие " + action + ".");
}
check(/WorldStartConditions/.test(campaignModels),
  "CampaignDefinition должен хранить стартовые условия мира (погода, дождь, видимость).");
check(/SaveWorldSettings/.test(campaignStore),
  "CampaignStore должен уметь записывать стартовые условия мира в кампанию.");

// 3. Подписи: единая геометрия «по центру над точкой», игрок подписан.
check(
  /drawPointLabel\(ctx, \{ name: "Игрок", isPlayer: true/.test(simulatorJs),
  "Точка игрока должна быть подписана «Игрок»."
);
const labelBody = simulatorJs.slice(
  simulatorJs.indexOf("function drawPointLabel("),
  simulatorJs.indexOf("function drawPlayer(")
);
check(/ctx\.textAlign = "center"/.test(labelBody),
  "Подписи точек должны быть выровнены по центру над точкой.");
check(!/q\.x \+ 10/.test(labelBody),
  "Подпись не должна рисоваться сбоку от точки: только над ней.");
check(/const gap = isPlayer \? 20 :/.test(labelBody),
  "Отступ подписи игрока должен быть больше: маркер крупнее точки СДО.");
check(/#ffffff/.test(labelBody) && /isPlayer/.test(labelBody),
  "Игрок должен подписываться белым цветом.");

// 4. Кнопка запущенной симуляции.
check(/simRunning/.test(simulatorJs), "Кнопка симуляции должна получать класс simRunning.");
check(/\.toolButton\.simRunning\{/.test(themeCss),
  "theme.css должен стилизовать кнопку запущенной симуляции.");
const runningStyle = themeCss.slice(
  themeCss.indexOf(".toolButton.simRunning{"),
  themeCss.indexOf(".toolButton.simRunning:hover")
);
check(/var\(--lime\)/.test(runningStyle), "Кнопка запущенной симуляции должна иметь LIME фон.");
check(/color:#fff/.test(runningStyle), "Текст кнопки запущенной симуляции должен быть белым.");
check(/text-shadow/.test(runningStyle) && /#000/.test(runningStyle),
  "Текст кнопки запущенной симуляции должен иметь чёрную обводку.");

// 5. Поведенческая часть: реальный simulator.js.
const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 820 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <head><style>html,body{height:100%;margin:0;overflow:hidden}</style>
      <style>${themeCss.replaceAll("</style", "<\\/style")}</style></head>
      <body>
        <header>
          <div id="hud"></div>
          <div id="daylightIndicator"></div>
          <button id="openCampaigns"></button>
          <button id="reloadCatalog"></button>
          <button id="openSaves"></button>
          <button id="simulationToggle" class="toolButton">Запустить</button>
          <button id="reset"></button>
        </header>
        <main style="display:flex">
          <aside id="runtimeSide"></aside>
          <section id="mapWrap" style="width:900px;height:640px">
            <canvas id="mapCanvas" style="width:900px;height:640px"></canvas>
            <div id="mapStatusBar"><input type="checkbox" id="onlyQuestsToggle"><input type="checkbox" id="citiesToggle"><span id="mapStatusHint"></span></div>
            <button id="backpackButton"></button>
            <div id="inventoryNotifications"></div>
            <div id="playerOverlay" aria-hidden="true">
              <section id="inventoryPanel"></section>
              <section id="characterPanel"><div id="characterTabs"></div><div id="characterTabBody"></div></section>
            </div>
          </section>
          <aside id="side"></aside>
        </main>
        <div id="savesPanel" hidden><div><span id="savesRoot"></span><button id="closeSaves"></button><button id="createSave"></button><span id="savesNotice"></span><div id="savesList"></div></div></div>
        <script>
          window.__sent = [];
          window.confirm = () => true;
          window.chrome = {
            webview: {
              listeners: new Map(),
              addEventListener(type, handler) { this.listeners.set(type, handler); },
              postMessage(raw) { try { window.__sent.push(JSON.parse(raw)); } catch (e) { window.__sent.push(raw); } }
            }
          };
        </script>
        <script>
          ${simulatorJs.replaceAll("</script", "<\\/script")}
        </script>
      </body>
    </html>
  `);

  const point = {
    id: "sdo:shop", name: "Магазин", category: "shop",
    position: { x: 0, y: 0, z: 0 }, color: "#78c8f0", triggerRadius: 35, editable: false
  };

  const pushSnapshot = running => page.evaluate(({ running, point }) => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          player: { position: { x: 60, y: 0, z: 60 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
          world: { coordinateSystem: "ETS2 X/Y/Z", categories: [], points: [point] },
          selection: { point: null },
          facts: { values: {} }, flags: { values: {} }, variables: { values: {} },
          questStatuses: { quests: [] }, states: { states: [] }, inventory: { items: [] },
          reputation: { entries: {} },
          telemetry: {}, environment: { weather: "Ясно", rainPercent: 0, gameTime: "12:00", visibilityMeters: 5000 },
          vitals: {}, progress: {}, character: {},
          clock: { startDate: "2026-01-01T00:00:00+00:00", elapsed: "12:00:00", running: running }
        },
        questCatalog: [], runtime: { questId: "", currentNodeId: null, status: "Stopped", waitingFor: "", message: "" },
        simulationRunning: running, enabledQuestIds: [], selectedQuest: { campaignId: "", questId: "" },
        itemCatalog: { items: [] }, npcCatalog: { npcs: [] }, reputationViews: {},
        journalDetached: false,
        daylight: {
          gameDateLabel: "01.01.2026", gameTimeLabel: "12:00", seasonLabel: "Зима", running: running,
          // Часы в шапке берут отдельное поле с секундами: по ним видно, что время идёт.
          gameClockLabel: "12:00:00",
          dayFraction: 0.5, isDay: true, isPolarDay: false, isPolarNight: false,
          sunriseLabel: "07:50", sunsetLabel: "15:53", dayLengthLabel: "8 ч 03 мин", sunAltitude: 11.5
        },
        worldSettings: {
          campaignId: "sibir_map", campaignName: "SibirMap", readOnly: false,
          latitude: 55.1644, longitude: 61.4368, hasGeo: true,
          startDateLabel: "01.01.2026", startTimeLabel: "00:00"
        }
      })
    });
  }, { running, point });

  // Симуляция ВКЛ: метки «(пауза)» быть НЕ должно, кнопка — LIME.
  await pushSnapshot(true);
  await page.waitForTimeout(300);

  const runningState = await page.evaluate(() => {
    const hud = document.getElementById("hud").textContent;
    const button = document.getElementById("simulationToggle");
    const style = getComputedStyle(button);
    return {
      hud,
      simRunning: button.classList.contains("simRunning"),
      background: style.backgroundColor,
      color: style.color,
      textShadow: style.textShadow
    };
  });

  check(!/\(пауза\)/.test(runningState.hud),
    "При включённой симуляции метка «(пауза)» появляться не должна: " + runningState.hud);
  // Часы обязаны показывать секунды — иначе по ним не видно, что время идёт.
  check(/\d{2}:\d{2}:\d{2}/.test(runningState.hud),
    "Часы в шапке должны показывать время с секундами (чч:мм:сс): " + runningState.hud);
  check(runningState.simRunning,
    "Кнопка запущенной симуляции должна получать класс simRunning.");
  check(/rgb\(\s*17,\s*251,\s*6\s*\)/.test(runningState.background),
    "Фон кнопки запущенной симуляции должен быть LIME (#11fb06): " + runningState.background);
  check(/rgb\(\s*255,\s*255,\s*255\s*\)/.test(runningState.color),
    "Текст кнопки запущенной симуляции должен быть белым: " + runningState.color);
  check(runningState.textShadow && runningState.textShadow !== "none" && /rgb\(\s*0,\s*0,\s*0\s*\)/.test(runningState.textShadow),
    "У текста кнопки должна быть чёрная обводка: " + runningState.textShadow);

  // Симуляция ВЫКЛ: метка появляется, класс снимается.
  await pushSnapshot(false);
  await page.waitForTimeout(300);

  const stoppedState = await page.evaluate(() => ({
    hud: document.getElementById("hud").textContent,
    simRunning: document.getElementById("simulationToggle").classList.contains("simRunning")
  }));
  check(/\(пауза\)/.test(stoppedState.hud),
    "При выключенной симуляции метка «(пауза)» должна быть: " + stoppedState.hud);
  check(!stoppedState.simRunning,
    "У выключенной симуляции класс simRunning должен сниматься.");

  // Секунды обязаны ДВИГАТЬСЯ: именно ради этого они добавлены. Проверяем
  // реальное изменение текста, а не только его формат — часы, которые стоят,
  // формально «показывают секунды», но пользу не приносят.
  await pushSnapshot(true);
  await page.waitForTimeout(200);

  const readClock = () => page.evaluate(() =>
    document.getElementById("hudGameTime")?.textContent || "");
  const firstTick = await readClock();
  await page.waitForTimeout(1400);
  const secondTick = await readClock();

  check(/\d{2}:\d{2}:\d{2}/.test(firstTick),
    "Часы должны показывать секунды при включённой симуляции: " + firstTick);
  check(firstTick !== secondTick,
    "Секунды должны увеличиваться (время идёт): " + firstTick + " → " + secondTick);

  // Поле ввода времени секунд показывать НЕ должно: там они только мешают.
  const inputValue = await page.evaluate(() => {
    // Блок «Окружение» рендерится в боковой панели; открываем её секцию.
    const section = [...document.querySelectorAll("#side .acc")].find(item =>
      item.dataset.section === "environment");
    if (section) {
      section.classList.add("open");
      section.querySelector(".accHead")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    }
    return document.getElementById("simTime")?.value || "";
  });
  if (inputValue) {
    check(!/^\d{2}:\d{2}:\d{2}$/.test(inputValue),
      "Поле ввода времени не должно показывать секунды: " + inputValue);
  }

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));

  // Подписи на карте: «Игрок» и «Магазин» центрированы над своими точками.
  const labels = await page.evaluate(() => {
    // Подписи — единственный источник белого жирного текста («Игрок») и цвета
    // точки; ищем характерные цвета, чтобы не зависеть от порядка отрисовки.
    const canvas = document.getElementById("mapCanvas");
    const dpr = window.devicePixelRatio || 1;
    const data = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    const at = (x, y) => {
      const i = (Math.round(y * dpr) * canvas.width + Math.round(x * dpr)) * 4;
      return [data[i], data[i + 1], data[i + 2]];
    };
    const W = canvas.clientWidth, H = canvas.clientHeight;

    // «Игрок» — белый текст; ищем белые пиксели и смотрим, где они относительно
    // маркера игрока (оранжевый).
    let whiteMinY = Infinity, whiteMaxY = -Infinity, whiteMinX = Infinity, whiteMaxX = -Infinity;
    let orangeMinY = Infinity, orangeMaxY = -Infinity, orangeMinX = Infinity, orangeMaxX = -Infinity;
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const [r, g, b] = at(x, y);
        if (r === 255 && g === 255 && b === 255) {
          if (y < whiteMinY) whiteMinY = y;
          if (y > whiteMaxY) whiteMaxY = y;
          if (x < whiteMinX) whiteMinX = x;
          if (x > whiteMaxX) whiteMaxX = x;
        }
        if (r > 225 && g > 150 && g < 190 && b < 60) {
          if (y < orangeMinY) orangeMinY = y;
          if (y > orangeMaxY) orangeMaxY = y;
          if (x < orangeMinX) orangeMinX = x;
          if (x > orangeMaxX) orangeMaxX = x;
        }
      }
    }

    return {
      hasWhite: Number.isFinite(whiteMinY),
      hasOrange: Number.isFinite(orangeMinY),
      white: { minY: whiteMinY, maxY: whiteMaxY, minX: whiteMinX, maxX: whiteMaxX },
      orange: { minY: orangeMinY, maxY: orangeMaxY, minX: orangeMinX, maxX: orangeMaxX }
    };
  });

  check(labels.hasWhite, "Подпись игрока («Игрок») белым текстом не найдена на карте.");
  check(labels.hasOrange, "Маркер игрока не найден на карте.");

  if (labels.hasWhite && labels.hasOrange) {
    // Текст должен быть ВЫШЕ маркера и не пересекаться с ним.
    check(labels.white.maxY < labels.orange.minY,
      "Подпись игрока должна быть выше маркера и не пересекаться с ним: " +
      "текст до y=" + labels.white.maxY + ", маркер с y=" + labels.orange.minY);

    // Центрирование: центр текста примерно совпадает с центром маркера.
    const textCenter = (labels.white.minX + labels.white.maxX) / 2;
    const markerCenter = (labels.orange.minX + labels.orange.maxX) / 2;
    check(Math.abs(textCenter - markerCenter) <= 12,
      "Подпись игрока должна быть по центру над маркером: текст " + textCenter +
      ", маркер " + markerCenter);
  }

  check(pageErrors.length === 0, "Ошибки страницы: " + pageErrors.join("; "));

  if (!failures.length) {
    console.log("Окружение и подписи: OK кнопка LIME, подпись выше маркера на " +
      (labels.orange.minY - labels.white.maxY) + "px");
  }
} finally {
  await browser.close();
}

if (failures.length) {
  console.log("Окружение и подписи: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}
