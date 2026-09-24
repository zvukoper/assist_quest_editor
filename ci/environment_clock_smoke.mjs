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
const themeCss = read("src/AssistQuestEditor.App/Web/theme.css");
const simulatorHtml = read("src/AssistQuestEditor.App/Web/simulator.html");
const simulatorForm = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
const coordinator = read("src/AssistQuestEditor.Domain/QuestRuntimeCoordinator.cs");
const mapper = read("src/AssistQuestEditor.Domain/SimulationSaveMapper.cs");
const campaignModels = read("src/AssistQuestEditor.Domain/CampaignModels.cs");
const worldClock = read("src/AssistQuestEditor.Domain/WorldClock.cs");
const geoType = campaignModels + worldClock;
const campaignContentCheck = read("ci/check_campaigns.mjs");
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
  /simulationRunning/.test(simulatorJs) &&
  /simulationPaused/.test(simulatorJs) &&
  /data-sim-state/.test(simulatorJs),
  "Метка состояния должна зависеть от SimulationRunning/SimulationPaused, а не от WorldClock.Running."
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

// Гео-координата: пустое поле не должно превращаться в 0.
// Number("") === 0, и 0 проходит проверку диапазона — именно так координата
// 55.1644 была записана в файл кампании как 0.
const saveGeoBody = simulatorJs.slice(
  simulatorJs.indexOf('#saveWorldToCampaign'),
  simulatorJs.indexOf('#loadWorldFromCampaign')
);
check(!/Number\(side\.querySelector\("#worldLatitude"\)\?\.value\)/.test(saveGeoBody),
  "Гео-координата не должна читаться через Number(): пустое поле даёт 0 и портит файл.");
check(/parseCoordinate/.test(saveGeoBody),
  "Гео-координата должна разбираться с различением «пусто» и «ноль».");
check(/=== null/.test(saveGeoBody),
  "Пустое поле гео-координаты должно отвергаться, а не сохраняться как 0.");

// Host: гео опциональна и проверяется, иначе пустое поле снова обнулит файл.
check(/OptionalDouble\(root, "latitude"\)/.test(simulatorForm),
  "Host должен читать широту как необязательное значение (null вместо 0).");
check(/candidate\.IsValid/.test(simulatorForm),
  "Host должен проверять допустимость гео-координаты перед записью.");
check(/PostSaveError\(/.test(simulatorForm.slice(
  simulatorForm.indexOf("private void SaveWorldToCampaign("),
  simulatorForm.indexOf("private void LoadWorldFromCampaign("))),
  "Сохранение мира должно сообщать об ошибке вместо записи мусора.");

// «Сброс» обязан возвращать мир к стартовым условиям кампании.
const resetBody = simulatorForm.slice(
  simulatorForm.indexOf('case "reset"'),
  simulatorForm.indexOf('case "reload_catalog"')
);
check(/ApplyWorldFromCampaign/.test(resetBody),
  "«Сбросить» должен приводить мир к стартовым условиям кампании: иначе " +
  "испорченная правка в Окружении не отменяется ни сбросом, ни загрузкой.");
check(!/SetSimulationRunning\(false\)/.test(resetBody),
  "«Сбросить» НЕ должен выключать симуляцию: сброс и остановка — разные действия.");

// Кампания: вычисляемое свойство не должно утекать в файл.
// Проверяется именно атрибут НА свойстве: простое вхождение строки "JsonIgnore"
// ничего не доказывает — слово встречается и в поясняющем комментарии.
check(/\[JsonIgnore\][\s\S]{0,120}public bool IsValid/.test(geoType),
  "Вычисляемое свойство GeoCoordinate.IsValid должно быть помечено [JsonIgnore]: " +
  "иначе оно попадает в файл кампании.");
check(/isValid leaked into campaign geo/.test(campaignContentCheck) &&
      /is zero: world lost its position/.test(campaignContentCheck),
  "ci/check_campaigns.mjs должен отвергать нулевую гео-координату в data/: " +
  "иначе испорченная кампания снова пройдёт проверку.");

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

// 4. Транспорт симуляции: play / stop / ff и плашка статуса.
//
// Плашка раньше была КНОПКОЙ; теперь она только показывает состояние, а
// управляют три кнопки слева. Отсюда три состояния вместо двух: одной кнопкой
// их выразить невозможно, а мир знает именно три (не запущено / идёт / пауза).
check(/simStatusPlate/.test(simulatorHtml), "Разметка должна содержать плашку статуса симуляции.");
check(/id="simPlay"/.test(simulatorHtml), "Разметка должна содержать кнопку play.");
check(/id="simStop"/.test(simulatorHtml), "Разметка должна содержать кнопку stop.");
check(/id="simFastForward"/.test(simulatorHtml), "Разметка должна содержать кнопку ff.");
// Лейаут: три строки вместо одного ряда — иначе верхние тексты срезались, а
// межстрочные расстояния задавались полями элементов и были слишком большими.
check(/class="simTopRow/.test(simulatorHtml),
  "Верхняя панель Симулятора должна быть разделена на строки (.simTopRow).");
check(/\.simTopRow\{/.test(themeCss), "theme.css должен стилизовать строки верхней панели.");
check(/\.simTop\{[\s\S]{0,260}flex-direction:column/.test(themeCss),
  "Верхняя панель должна быть flex-колонкой: только так три строки раскладываются без среза текста.");
check(/\.simTitle\{[^}]*white-space:normal/.test(themeCss),
  "Заголовок Симулятора должен переноситься, а не обрезаться.");
check(/\.simBody\{flex:1 1 auto/.test(themeCss),
  "Высота тела не должна быть константой: панель выросла до трёх строк.");

// Состояния плашки заданы атрибутом: цвета не дублируются в JS.
check(/data-sim-state/.test(simulatorJs),
  "Плашка должна получать состояние атрибутом data-sim-state.");
check(/\.simStatusPlate\[data-sim-state="running"\]/.test(themeCss),
  "theme.css должен оформлять состояние «идёт».");
check(/\.simStatusPlate\[data-sim-state="stopped"\]/.test(themeCss),
  "theme.css должен оформлять состояние «не запущено».");
// Требование автора: СИНИЙ — симуляции нет, ОРАНЖЕВЫЙ — идёт или пауза.
// Проверяются ВЫЧИСЛЕННЫЕ стили в браузере (ниже), а здесь — что оба правила
// вообще присутствуют и используют разные цвета.
const runningStyle = themeCss.slice(
  themeCss.indexOf('.simStatusPlate[data-sim-state="running"]'),
  themeCss.indexOf('.simStatusPlate[data-sim-state="paused"]')
);
const stoppedStyle = themeCss.slice(
  themeCss.indexOf('.simStatusPlate[data-sim-state="stopped"]'),
  themeCss.indexOf('.simStatusPlate[data-sim-state="running"]')
);
check(/var\(--accent\)/.test(runningStyle),
  "Идущая симуляция должна быть оранжевой (акцентной): " + runningStyle);
check(/var\(--blue\)/.test(stoppedStyle),
  "Состояние «симуляции нет» должно быть СИНИМ, а не серым: " + stoppedStyle);
// Пульсация — ТОЛЬКО у паузы: если она есть и у хода, состояния не отличить.
check(!/animation/.test(runningStyle),
  "Ход симуляции НЕ должен пульсировать: пульсация отличает паузу.");
const pausedStyle = themeCss.slice(
  themeCss.indexOf('.simStatusPlate[data-sim-state="paused"]'),
  themeCss.indexOf("@keyframes simPausePulse")
);
check(/animation:simPausePulse 1500ms/.test(pausedStyle),
  "Состояние «на паузе» должно пульсировать 1500 мс (требование пользователя).");
check(/var\(--accent\)/.test(pausedStyle),
  "Пульсация паузы должна быть оранжевой (акцентной).");

// В плашке НЕ должно быть собственной иконки, а также кратности: состояние
// выражено цветом и текстом, третий носитель того же смысла — шум. Кратность
// переехала к игровым часам в HUD (и только при ускорении).
check(!/simStatusIcon/.test(simulatorJs) && !/simStatusIcon/.test(simulatorHtml),
  "Плашка статуса не должна дублировать состояние иконкой.");
check(!/simStatusSpeed/.test(simulatorJs) && !/simStatusSpeed/.test(simulatorHtml),
  "Кратность не должна дублироваться в плашке: она показывается у игровых часов.");
// Кнопки транспорта не выделяются: состояние показывает плашка.
check(!/\.simTransportButton\.active\{/.test(themeCss),
  "Кнопки транспорта не должны иметь активного состояния — статуса достаточно.");
check(!/classList\.toggle\("active"/.test(simulatorJs.slice(
    simulatorJs.indexOf("function renderSimulationTransport()"),
    simulatorJs.indexOf("function formatSpeed("))),
  "JS не должен подсвечивать кнопку транспорта: это дублировало бы плашку.");

// Подпись автосохранения — отдельная строка сведений под плашкой.
check(/id="simAutoSave"/.test(simulatorHtml),
  "Под кнопкой запуска должна быть подпись «Автосохранение: дата и время».");
check(/autoSaveLabel/.test(simulatorForm) && /autoSaveLabel/.test(simulatorJs),
  "Подпись автосохранения должна приходить в снимке (Host → Web).");
// Подпись в плашке НЕ должна меняться по состояниям: именно смена подписи
// оставляла «На паузе» висеть после остановки. Проверяется ТЕЛО функции
// отрисовки транспорта — и БЕЗ КОММЕНТАРИЕВ: пояснение, описывающее дефект,
// само содержит те же слова, и поиск по тексту с комментариями ловил бы
// объяснение вместо кода (это уже случалось в этом проекте).
const stripComments = source => source
  .replace(/\/\*[\s\S]*?\*\//g, " ")
  .replace(/\/\/[^\n]*/g, " ");
const transportBody = stripComments(simulatorJs.slice(
  simulatorJs.indexOf("function renderSimulationTransport()"),
  simulatorJs.indexOf("function formatSpeed(")
));
check(transportBody.length > 0,
  "В simulator.js должна быть функция renderSimulationTransport().");
check(!/simStatusText/.test(transportBody),
  "Отрисовка транспорта не должна переписывать текст плашки.");
check(!/На паузе|Идет симуляция|не запущена/.test(transportBody),
  "Отрисовка транспорта не должна подставлять текст состояния: состояние — это цвет.");

// Вспышка автосохранения: LIME, ОДИН раз, 2 секунды.
const autoSaveStyle = themeCss.slice(
  themeCss.indexOf(".simAutoSave.autoSavePulse"),
  themeCss.indexOf("@keyframes simAutoSavePulse")
);
check(/animation:simAutoSavePulse 2000ms/.test(autoSaveStyle),
  "Вспышка автосохранения должна длиться 2000 мс.");
check(/\b1\b/.test(autoSaveStyle) && /both/.test(autoSaveStyle),
  "Вспышка автосохранения должна быть ОДНОразовой (iteration-count 1).");
const autoSaveKeyframes = themeCss.slice(themeCss.indexOf("@keyframes simAutoSavePulse"));
check(/var\(--lime\)/.test(autoSaveKeyframes),
  "Вспышка автосохранения должна быть цвета lime.");

// Ускоренное игровое время: часы оранжевые + кратность.
check(/hudAccelerated/.test(simulatorJs),
  "Web должен помечать ускоренное время классом на HUD.");
check(/#hud\.hudAccelerated #hudGameTime/.test(themeCss),
  "При ускорении игровые часы должны становиться оранжевыми.");
check(/hudClockSpeed/.test(simulatorJs) && /\.hudClockSpeed\{/.test(themeCss),
  "При ускорении рядом с часами должна показываться кратность.");
const speedStyle = themeCss.slice(
  themeCss.indexOf(".hudClockSpeed{"),
  themeCss.indexOf(".hudClockSpeed{") + 120
);
check(/var\(--accent\)/.test(speedStyle),
  "Кратность ускорения должна быть акцентного (оранжевого) цвета.");
// Реакция на смену кратности обязательна: без снимка в ответ UI не узнает о ней.
const speedCase = simulatorForm.slice(
  simulatorForm.indexOf('case "simulation_set_speed":'),
  simulatorForm.indexOf('case "simulation_set_speed":') + 620
);
check(/RequestSnapshot/.test(speedCase),
  "Смена кратности должна отвечать снимком: иначе UI о ней не узнает.");

// Кнопка инвентаря не должна перекрывать полосу состояния карты.
const backpackStyle = themeCss.slice(
  themeCss.indexOf(".backpackButton{"),
  themeCss.indexOf(".backpackButton:hover")
);
check(/bottom:calc\(26px/.test(backpackStyle),
  "Кнопка инвентаря должна отступать от полосы состояния карты (26px).");

// Кнопки строки управления НЕ переносятся. Симптом был конкретный: кнопки
// НАЕЗЖАЛИ друг на друга. Причина — `flex-wrap:wrap` при фиксированной высоте
// строки: содержимое занимало две строки, а строка имела одну.
//
// Срез берётся от объявления до его ЗАКРЫВАЮЩЕЙ скобки, а не окном в N символов:
// в правиле есть поясняющий комментарий, и окно в 320 символов не доходило до
// `flex-wrap`. Проверка падала на СВОЁМ тексте, а не на дефекте.
const controlsStyle = (() => {
  const start = themeCss.indexOf(".simTopControls{");
  if (start < 0) return "";
  const end = themeCss.indexOf("}", start);
  return end > start ? themeCss.slice(start, end + 1) : "";
})();
check(controlsStyle.length > 0, "theme.css должен стилизовать строку управления.");
check(/flex-wrap:nowrap/.test(controlsStyle),
  "Строка управления не должна переносить содержимое: при wrap кнопки наезжают " +
  "друг на друга, потому что высота строки фиксированная.");
check(!/flex-wrap:wrap/.test(controlsStyle),
  "Строка управления всё ещё разрешает перенос — кнопки будут перекрываться.");
// Кнопки не сжимаются: сжатие ломает квадратный транспорт и обрезает подписи.
check(/\.simTopControls \.toolButton\{flex:0 0 auto/.test(themeCss),
  "Кнопки строки управления не должны сжиматься: подписи обрезались бы по буквам.");
check(/\.simTopControls \.simTransport\b[^{]*\{[^}]*flex:0 0 auto/.test(themeCss) ||
      /\.simTopControls \.toolButton\{flex:0 0 auto/.test(themeCss),
  "Транспорт не должен ужиматься.");
// Сжимается ПЛАШКА, а не кнопки: у неё короткий текст, и многоточие читается
// лучше, чем перекрытые кнопки.
check(/\.simTopControls \.simStatusPlate\{flex:0 1 auto/.test(themeCss),
  "Плашка статуса должна быть первым кандидатом на сжатие, а не кнопки.");

// Индикатор фазы дня: внутри HUD между восходом и временем года, БЕЗ плашки,
// высотой с плашку-бейдж.
//
// В РАЗМЕТКЕ его нет и быть не должно: слот рисует HUD, потому что место
// индикатора определяется порядком подписей в снимке. Поэтому проверяется
// наличие в JS, а не в HTML.
check(/daylightIndicator/.test(simulatorJs),
  "Индикатор светового дня должен рисоваться JS (внутри HUD).");
// В строке управления его быть НЕ должно: там он был размером с кнопку и
// выталкивал кнопки на вторую строку.
const controlsRow = simulatorHtml.slice(
  simulatorHtml.indexOf('class="simTopRow simTopControls"'),
  simulatorHtml.indexOf('class="simTopRow simTopInfo"'));
check(controlsRow.length > 0, "Строка управления должна быть в разметке.");
check(!/daylightIndicator/.test(controlsRow),
  "Индикатор фазы дня не должен стоять в строке управления: он её раздувает.");
// Рисуется внутри HUD, а не отдельным элементом разметки.
const hudMarkupStart = simulatorJs.indexOf("hud.innerHTML = [");
check(hudMarkupStart >= 0, "HUD должен собираться из массива частей.");
const hudMarkup = simulatorJs.slice(hudMarkupStart, simulatorJs.indexOf("renderDaylight()", hudMarkupStart));
check(hudMarkup.length > 0 && /daylightIndicator/.test(hudMarkup),
  "Индикатор должен рисоваться внутри HUD (в ряду подписей), а не отдельно.");
// Порядок: восход/закат → индикатор → сезон.
check(/hudClockBadge/.test(hudMarkup) || /sunriseLabel/.test(hudMarkup),
  "Разметка HUD не содержит восход/закат: порядок индикатора непроверяем.");
check(hudMarkup.indexOf("sunriseLabel") < hudMarkup.indexOf("daylightIndicator") &&
      hudMarkup.indexOf("daylightIndicator") < hudMarkup.indexOf("seasonLabel"),
  "Индикатор должен стоять МЕЖДУ восходом/закатом и временем года.");
// Плашки нет: свой фон и рамка делали бы его кнопкой.
const daylightStyle = themeCss.slice(
  themeCss.indexOf(".daylightSlot{"), themeCss.indexOf(".daylightSlot{") + 320);
check(!/background/.test(daylightStyle) && !/border:/.test(daylightStyle),
  "У индикатора фазы дня не должно быть плашки (фона/рамки): это индикатор, а не кнопка.");
// Высота — как у плашки-бейджа, а не как у кнопки.
check(/\.daylightSlot\{[\s\S]{0,200}?height:22px/.test(themeCss),
  "Индикатор должен быть высотой с плашку-бейдж (22px), а не с кнопку (42px).");
check(/const size = 22;/.test(simulatorJs) &&
      !/const size = 42;/.test(simulatorJs),
  "SVG индикатора должен рисоваться в 22px: прежние 42px — это размер кнопки.");

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
        <header class="simTop">
          <div class="simTopRow simTopHead">
            <div class="simTitleBlock"><div class="simTitle">Симулятор</div></div>
            <div class="topSpacer"></div>
            <div class="hud" id="hud"></div>
          </div>
          <div class="simTopRow simTopControls">
            <div class="simTransport">
              <button id="simPlay" class="toolButton simTransportButton">▶</button>
              <button id="simStop" class="toolButton simTransportButton">⏹</button>
              <button id="simFastForward" class="toolButton simTransportButton">⏩</button>
            </div>
            <div class="simStatusPlate" id="simStatusPlate" data-sim-state="stopped">
              <span class="simStatusText" id="simStatusText">Игровое время</span>
            </div>
            <div class="topSpacer"></div>
            <button class="toolButton" id="openCampaigns">Кампании и квесты</button>
            <button class="toolButton" id="reloadCatalog">Обновить квесты</button>
            <button class="toolButton" id="openSaves">Сохранения</button>
            <button class="toolButton" id="reset">Сбросить</button>
          </div>
          <div class="simTopRow simTopInfo">
            <span class="simAutoSave" id="simAutoSave"></span>
            <span class="simAutoSaveHint" id="simAutoSaveHint"></span>
          </div>
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
          // Счётчик ЗАПУСКОВ вспышки автосохранения. Считать надо именно
          // animationstart, а не читать animationName: после завершения конечной
          // анимации getComputedStyle всё ещё отдаёт её имя, поэтому «вспыхнуло
          // ли снова» по стилю не отличить.
          window.__autoSavePulseStarts = 0;
          document.getElementById("simAutoSave").addEventListener("animationstart", event => {
            if (event.animationName === "simAutoSavePulse") window.__autoSavePulseStarts += 1;
          });
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

  const pushSnapshot = state => page.evaluate(({ state, point }) => {
    const running = !!state.running;
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
        simulationRunning: running,
        simulationPaused: !!state.paused,
        simulationSpeed: state.speed || 1,
        autoSaveLabel: state.autoSaveLabel || null,
        enabledQuestIds: [], selectedQuest: { campaignId: "", questId: "" },
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
  }, { state, point });

  const readTransport = () => page.evaluate(() => ({
    hud: document.getElementById("hud").textContent,
    state: document.getElementById("simStatusPlate").dataset.simState,
    text: document.getElementById("simStatusText").textContent,
    play: document.getElementById("simPlay").textContent,
    autoSave: document.getElementById("simAutoSave").textContent,
    clockSpeed: document.getElementById("hudClockSpeed")?.textContent || "",
    hudAccelerated: document.getElementById("hud").classList.contains("hudAccelerated")
  }));

  // Симуляция ВКЛ: метки «(пауза)» быть НЕ должно, плашка оранжевая, play — ⏸️.
  await pushSnapshot({ running: true });
  await page.waitForTimeout(300);

  const runningState = await readTransport();
  check(!/\(пауза\)/.test(runningState.hud),
    "При включённой симуляции метка «(пауза)» появляться не должна: " + runningState.hud);
  // Часы обязаны показывать секунды — иначе по ним не видно, что время идёт.
  check(/\d{2}:\d{2}:\d{2}/.test(runningState.hud),
    "Часы в шапке должны показывать время с секундами (чч:мм:сс): " + runningState.hud);
  check(runningState.state === "running",
    "При идущей симуляции плашка должна быть в состоянии running: " + runningState.state);
  // Подпись СТАТИЧЕСКАЯ и не сообщает состояние: состояние — цвет и значок.
  check(/Игровое время/.test(runningState.text),
    "Подпись плашки должна быть нейтральной («Игровое время»): " + runningState.text);
  check(runningState.play === "⏸️",
    "Иконка play при идущей симуляции должна стать ⏸️ (следующее действие — пауза): " + runningState.play);
  // Требование автора: идёт симуляция — оранжевая. Проверяем вычисленный
  // стиль, а не имя класса.
  const runningColor = await page.evaluate(() =>
    getComputedStyle(document.getElementById("simStatusPlate")).color);
  check(/rgb\(\s*250,\s*176,\s*3\s*\)/.test(runningColor),
    "Идущая симуляция должна быть оранжевой (#fab003): " + runningColor);
  const runningAnimation = await page.evaluate(() =>
    getComputedStyle(document.getElementById("simStatusPlate")).animationName);
  check(!/simPausePulse/.test(runningAnimation),
    "Ход НЕ должен пульсировать: пульсация отличает паузу: " + runningAnimation);

  // Пауза: оранжевая пульсация 1500 мс, статус «На паузе», play снова ▶️.
  await pushSnapshot({ running: false, paused: true, speed: 1 });
  await page.waitForTimeout(300);

  const pausedState = await readTransport();
  const pausedComputed = await page.evaluate(() => {
    const plate = document.getElementById("simStatusPlate");
    const style = getComputedStyle(plate);
    const animation = { name: style.animationName, duration: style.animationDuration };
    // Цвет читаем на ОСТАНОВЛЕННОЙ анимации: пульсация меняет цвет по кадрам, и
    // произвольный замер попал бы в середину цикла (#fbbd2e вместо акцента).
    plate.style.animation = "none";
    const color = getComputedStyle(plate).color;
    plate.style.animation = "";
    return { color, animationName: animation.name, duration: animation.duration };
  });
  check(pausedState.state === "paused",
    "Плашка должна быть в состоянии paused: " + pausedState.state);
  check(/Игровое время/.test(pausedState.text),
    "Подпись плашки не должна меняться на паузе: " + pausedState.text);
  check(pausedState.play === "▶️",
    "Иконка play на паузе должна быть ▶️ (следующее действие — продолжить): " + pausedState.play);
  check(/rgb\(\s*250,\s*176,\s*3\s*\)/.test(pausedComputed.color),
    "Статус паузы должен быть оранжевым (акцент #fab003): " + pausedComputed.color);
  check(pausedComputed.animationName === "simPausePulse" && pausedComputed.duration === "1.5s",
    "Пульсация паузы должна быть 1500 мс: " + pausedComputed.animationName + "/" + pausedComputed.duration);

  // Симуляция ВЫКЛ: СИНЯЯ плашка, подпись нейтральная, пульсации нет.
  await pushSnapshot({ running: false, paused: false, autoSaveLabel: "24.09.2026 11:37:05" });
  await page.waitForTimeout(300);

  const stoppedState = await readTransport();
  const stoppedComputed = await page.evaluate(() => ({
    color: getComputedStyle(document.getElementById("simStatusPlate")).color,
    animationName: getComputedStyle(document.getElementById("simStatusPlate")).animationName
  }));
  check(/пауза/.test(stoppedState.hud),
    "При выключенной симуляции метка «(пауза)» должна быть: " + stoppedState.hud);
  check(stoppedState.state === "stopped",
    "Плашка должна быть в состоянии stopped: " + stoppedState.state);
  check(/Игровое время/.test(stoppedState.text),
    "Подпись плашки должна остаться нейтральной после остановки: " + stoppedState.text);
  check(/rgb\(\s*18,\s*171,\s*229\s*\)/.test(stoppedComputed.color),
    "Состояние «симуляции нет» должно быть СИНИМ (#12abe5): " + stoppedComputed.color);
  check(!/simPausePulse/.test(stoppedComputed.animationName),
    "У незапущенной симуляции пульсации быть не должно.");

  // Подпись автосохранения: формат «дата и время», из данных Host.
  check(/Автосохранение: 24\.09\.2026 11:37:05/.test(stoppedState.autoSave),
    "Под плашкой должна быть дата автосохранения из снимка: " + stoppedState.autoSave);
  // Вспышка: LIME и ровно 2 секунды. Проверяется вычисленный стиль, а не класс:
  // класс может быть на месте, а анимация уже закончиться.
  const autoSavePulse = await page.evaluate(() => {
    const style = getComputedStyle(document.getElementById("simAutoSave"));
    return { name: style.animationName, duration: style.animationDuration, iterations: style.animationIterationCount };
  });
  check(autoSavePulse.name === "simAutoSavePulse",
    "Автосохранение должно пропульсировать цветом lime: " + autoSavePulse.name);
  check(autoSavePulse.duration === "2s" && autoSavePulse.iterations === "1",
    "Вспышка автосохранения должна быть однократной и длиться 2 секунды: " +
      autoSavePulse.duration + "/" + autoSavePulse.iterations);
  // Вспышка именно LIME, а не «какой-то цвет»: замер по кадру 15%, потому что
  // крайние кадры возвращают нейтральный цвет.
  const autoSavePeak = await page.evaluate(() => {
    const frames = [...document.styleSheets]
      .flatMap(sheet => { try { return [...sheet.cssRules]; } catch { return []; } })
      .find(rule => rule.type === CSSRule.KEYFRAMES_RULE && rule.name === "simAutoSavePulse");
    const peak = frames ? [...frames.cssRules].find(frame => frame.keyText.startsWith("15")) : null;
    return peak?.style?.color || null;
  });
  check(autoSavePeak && /var\(--lime\)/.test(autoSavePeak),
    "Пик вспышки автосохранения должен быть цвета lime: " + autoSavePeak);
  // Однократность по счётчику ЗАПУСКОВ: у одного автосохранения — ровно одна.
  const pulseStartsAfterFirst = await page.evaluate(() => window.__autoSavePulseStarts);
  check(pulseStartsAfterFirst === 1,
    "Автосохранение должно вспыхивать ровно один раз: " + pulseStartsAfterFirst);

  // При ×1 ускорения нет: часы нейтральные, кратность не показана.
  check(!stoppedState.hudAccelerated && !stoppedState.clockSpeed,
    "Без ускорения часы не должны становиться оранжевыми и показывать кратность: " +
      JSON.stringify(stoppedState));

  // Кратность: ff перебирает набор и отправляет новое значение в Host.
  await page.click("#simFastForward");
  await page.waitForTimeout(100);
  const speedMessage = await page.evaluate(() =>
    window.__sent.filter(item => item.action === "simulation_set_speed").pop());
  check(speedMessage && Number(speedMessage.speed) === 5,
    "ff с ×1 должен запросить ×5: " + JSON.stringify(speedMessage));

  await pushSnapshot({ running: true, speed: 20 });
  await page.waitForTimeout(200);
  const speedState = await readTransport();
  check(/×20/.test(speedState.clockSpeed),
    "При ускорении у игровых часов должна быть кратность: " + speedState.clockSpeed);
  check(speedState.hudAccelerated,
    "При ускорении HUD должен получать класс hudAccelerated.");
  const clockColor = await page.evaluate(() =>
    getComputedStyle(document.getElementById("hudGameTime")).color);
  check(/rgb\(\s*250,\s*176,\s*3\s*\)/.test(clockColor),
    "При ускорении игровые часы должны быть оранжевыми: " + clockColor);

  // Повторный снимок с ТЕМ ЖЕ автосохранением не должен вспыхивать снова:
  // снимки приходят постоянно, и вспышка на каждом мигала бы бесконечно.
  await pushSnapshot({ running: true, speed: 20, autoSaveLabel: "24.09.2026 11:37:05" });
  await page.waitForTimeout(200);
  const repeatedStarts = await page.evaluate(() => window.__autoSavePulseStarts);
  check(repeatedStarts === 1,
    "Повторный снимок с той же датой автосохранения не должен запускать вспышку: " +
      repeatedStarts);

  // А НОВОЕ автосохранение — должно.
  await pushSnapshot({ running: true, speed: 20, autoSaveLabel: "24.09.2026 12:05:44" });
  await page.waitForTimeout(200);
  const newStarts = await page.evaluate(() => window.__autoSavePulseStarts);
  check(newStarts === 2,
    "Новое автосохранение обязано пропульсировать: " + newStarts);

  // Клик по play при идущей симуляции — это ПАУЗА, а не остановка.
  await page.click("#simPlay");
  await page.waitForTimeout(100);
  const pauseAction = await page.evaluate(() => window.__sent[window.__sent.length - 1]);
  check(pauseAction?.action === "simulation_pause",
    "Клик по play при идущей симуляции должен ставить ПАУЗУ: " + JSON.stringify(pauseAction));

  // Клик по stop — выключение с автосохранением.
  await page.click("#simStop");
  await page.waitForTimeout(100);
  const stopAction = await page.evaluate(() => window.__sent[window.__sent.length - 1]);
  check(stopAction?.action === "simulation_stop",
    "Клик по stop должен выключать симуляцию: " + JSON.stringify(stopAction));

  // Секунды обязаны ДВИГАТЬСЯ: именно ради этого они добавлены. Проверяем
  // реальное изменение текста, а не только его формат — часы, которые стоят,
  // формально «показывают секунды», но пользу не приносят.
  await pushSnapshot({ running: true });
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
    console.log("Окружение и подписи: OK транспорт/плашка трёх состояний, автосохранение, " +
      "часы с секундами, подпись выше маркера на " +
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
