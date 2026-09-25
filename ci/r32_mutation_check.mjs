// Негативные контроли для правок раунда R32 (инвентарь, дерево, плашка времени).
//
// Смысл: КАЖДАЯ новая проверка обязана упасть на «мутанте» — исходном коде с
// возвращённым дефектом. Иначе проверка не доказывает ничего: она проходит и
// тогда, когда дефект на месте. Мутант вносится во ВРЕМЕННУЮ копию дерева:
// рабочие файлы не трогаются.
//
// Проверка построена так: копия каталога исходников → мутация → прогон
// соответствующей пробы → ОЖИДАЕТСЯ ПАДЕНИЕ. Копия удаляется в любом случае.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const failures = [];
const results = [];

// Мутации описывают: файл, замену и какая проверка обязана это поймать.
const mutations = [
  {
    name: "инвентарь без динамических рядов",
    file: "src/AssistQuestEditor.App/Web/inventory.js",
    probe: "inventory",
    from: "var rows = Math.max(ROWS, Math.ceil(items.length / COLUMNS));",
    to: "var rows = ROWS;",
    why: "девятнадцатый предмет должен исчезать"
  },
  {
    name: "инвентарь с прежним размером ячейки",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "inventory",
    from: "--inventory-cell:39px",
    to: "--inventory-cell:78px",
    why: "ячейка обязана быть 39 px"
  },
  {
    name: "окно инвентаря без проверки сохранённой геометрии",
    file: "src/AssistQuestEditor.App/Host/InventoryForm.cs",
    probe: "inventory",
    from: 'if (!WindowGeometryStore.HasSaved("inventory"))',
    to: "if (true)",
    why: "размер и место обязаны сохраняться"
  },
  {
    name: "инвентарь без обработки закрытия",
    file: "src/AssistQuestEditor.App/Host/InventoryForm.cs",
    probe: "inventory",
    from: 'case "close_inventory":',
    to: 'case "close_inventory_disabled":',
    why: "клавиша I внутри окна обязана закрывать его"
  },
  {
    name: "плашка времени со старым зелёным «идёт»",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    from: '[data-sim-state="running"]{color:var(--accent);',
    to: '[data-sim-state="running"]{color:#11fb06;',
    why: "идёт симуляция — оранжевая, а не зелёная"
  },
  {
    name: "плашка времени с текстом состояния",
    file: "src/AssistQuestEditor.App/Web/simulator.js",
    probe: "environment",
    from: '    const autoSave = document.getElementById("simAutoSave");',
    to: '    const autoSave = document.getElementById("simAutoSave");\n    document.getElementById("simStatusText").textContent = simulationPaused ? "На паузе" : "Идет симуляция";',
    why: "подпись плашки не должна меняться по состояниям"
  },
  {
    name: "плашка паузы без пульсации",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    from: "animation:simPausePulse 1500ms ease-in-out infinite;",
    to: "animation:none;",
    why: "пауза обязана отличаться пульсацией"
  },
  {
    name: "обводка строки квеста прямоугольная",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "using var path = RoundedPath(bounds, ButtonRadius);",
    to: "using var path = new System.Drawing.Drawing2D.GraphicsPath();",
    why: "обводка выделенной строки должна быть скруглённой"
  },
  {
    name: "кампании без отступа вложенности",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "Margin = new Padding(TreeLevelIndent, 0, 0, 8)",
    to: "Margin = new Padding(0, 0, 0, 8)",
    why: "уровни дерева обязаны читаться по отступу слева"
  },
  {
    name: "строка квеста с подобранными отступами",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 7, 0)",
    to: "Margin = new Padding(0, 14, 7, 0)",
    why: "вертикаль обязана задаваться якорем, а не отступом"
  },
  {
    name: "выключенные кнопки без своей отрисовки",
    file: "src/AssistQuestEditor.App/Host/DarkFlatButton.cs",
    probe: "contrast",
    // Мутация обязана КОМПИЛИРОВАТЬСЯ: `if (true)` даёт CS0162 (недостижимый
    // код), а проект собирается с TreatWarningsAsErrors — контроль тогда сообщал
    // бы «копия не собралась», что не является поимкой проверки.
    from: "if (Enabled)",
    to: "if (Enabled || !Enabled)",
    why: "выключенный текст обязан оставаться читаемым, а не системно-серым"
  },
  {
    name: "выключенный текст тёмным цветом",
    file: "src/AssistQuestEditor.App/Host/DarkFlatButton.cs",
    probe: "contrast",
    // Проверяется ИМЕННО читаемость: тёмный текст на тёмном фоне — тот самый
    // дефект. Замена класса кнопки на обычный `Button` такой мутацией НЕ была бы:
    // обычная кнопка рисуется темой Windows (серый фон, серый текст), и контраст
    // внутри неё остаётся достаточным — это несогласованность оформления, а не
    // нечитаемость, и требовать её поймать значило бы требовать не того.
    from: "Color.FromArgb(170, 178, 190)",
    to: "Color.FromArgb(20, 22, 25)",
    why: "выключенный текст обязан быть светлым: иначе он не читается на тёмном фоне"
  },
  {
    name: "кнопка меню без подписи",
    file: "src/AssistQuestEditor.App/Web/main.html",
    probe: "selector",
    from: "aria-expanded=\"false\">Действия<span aria-hidden=\"true\">▾</span></button>",
    to: "aria-expanded=\"false\">▾</button>",
    why: "по треугольнику меню не находят: экспорт и импорт искали глазами"
  },
  {
    name: "шапка без контекста наложения",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "selector",
    // Возвращается ровно тот дефект, который был: без контекста наложения в
    // шапке z-index меню остаётся внутри `.worldSelector`, и карточки редакторов
    // рисуются поверх меню — нажать в нём нечего.
    from: "backdrop-filter:blur(5px);position:relative;z-index:40}",
    to: "backdrop-filter:blur(5px)}",
    why: "меню действий обязано открываться поверх плашек редакторов"
  },
  {
    name: "кнопки первого мира в одной точке",
    file: "src/AssistQuestEditor.App/Host/WorldChooserForm.cs",
    probe: "contrast",
    // «Пропустить» возвращается на место «Создать мир»: она шире и добавлена
    // позже, поэтому закрывает «Создать мир» целиком — мышью нажать нельзя.
    from: "                Location = new Point(18, 244),\n                Size = new Size(330, 38),",
    to: "                Location = new Point(18, 198),\n                Size = new Size(330, 38),",
    why: "кнопки диалога не должны занимать одну и ту же область"
  },
  {
    name: "выключенный текст на акценте приглушённо-серым",
    file: "src/AssistQuestEditor.App/Host/DarkFlatButton.cs",
    probe: "contrast",
    // Цвет снова берётся независимо от фона: на оранжевой кнопке это «серое на
    // жёлтом» — тот самый нечитаемый вид.
    from: "Luma(background) > LightBackgroundLuma ? DarkDisabledTextColor : DisabledTextColor;",
    to: "DisabledTextColor;",
    why: "на акцентном фоне выключенный текст обязан быть тёмным, а не серым"
  },
  {
    name: "остановка без сброса ускорения времени",
    file: "src/AssistQuestEditor.Domain/QuestRuntimeCoordinator.cs",
    probe: "environment",
    // Ускорение остаётся после «Стоп»: часы оранжевые, кратность ×20 на месте,
    // хотя симуляции нет.
    from: "        if (!running)\n            SetSimulationSpeed(1d);\n",
    to: "",
    why: "«Стоп» обязан сбрасывать ускорение и его подсветку"
  },
  {
    name: "выключенная акцентная кнопка приглушена прозрачностью",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "contrast",
    // Возвращается ровно тот дефект, который был: `opacity:.45` из общего правила
    // продолжает приглушать и ТЕКСТ, и оранжевый фон смешивается с тёмной панелью
    // в грязно-оливковый — контраст падает до 2,7:1.
    from: "  opacity:1;\n  background:linear-gradient(rgba(11,13,17,.30)",
    to: "  background:linear-gradient(rgba(11,13,17,.30)",
    why: "у выключенной акцентной кнопки прозрачность обязана быть снята, иначе текст гаснет"
  },
  {
    name: "выключенная акцентная кнопка без приглушения",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "contrast",
    // Заливка убрана: кнопка снова приглушается прозрачностью, то есть дефект на месте.
    from: "background:linear-gradient(rgba(11,13,17,.30),rgba(11,13,17,.30)),var(--accent);",
    to: "background:var(--accent);",
    why: "приглушать выключенный акцент надо заливкой поверх фона, а не прозрачностью"
  },
  {
    name: "выбранный вариант интерфейса приглушён прозрачностью",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "contrast",
    // `.interfaceChoice` — это <button>, и общее правило снова глушит акцентный
    // кружок с номером: он уходит в 2,8:1.
    from: "  opacity:1;\n  background:linear-gradient(rgba(11,13,17,.35)",
    to: "  background:linear-gradient(rgba(11,13,17,.35)",
    why: "акцентный кружок с номером обязан оставаться читаемым в выключенном варианте"
  },
  {
    name: "акцентная кнопка со светлым текстом",
    file: "src/AssistQuestEditor.App/Host/ResourceCreateForm.cs",
    probe: "contrast",
    // Тот самый дефект, который автор увидел: серый (здесь светлый) на оранжевом.
    from: "            BackColor = Color.FromArgb(250, 176, 3),\n            ForeColor = Color.FromArgb(20, 20, 20),",
    to: "            BackColor = Color.FromArgb(250, 176, 3),\n            ForeColor = Color.FromArgb(231, 237, 244),",
    why: "на оранжевой кнопке текст обязан быть тёмным, а не светлым"
  },
  {
    name: "проба без счётчика акцентных поверхностей",
    file: "src/AssistQuestEditor.App/Program.cs",
    probe: "contrast",
    // Счётчик убран: проверка «нет серого на оранжевом» осталась бы верной и на
    // ПУСТОМ списке акцентных поверхностей, то есть перестала бы что-либо стеречь.
    from: "            lines.Add(\"accent surfaces measured: \" + lines.Count(line =>\n                line.StartsWith(\"accent: \", StringComparison.Ordinal)));",
    to: "",
    why: "без счётчика акцентных поверхностей проверка проходит и при нулевом охвате"
  },
  {
    name: "поверхность заставки без сохранения альфы",
    file: "src/AssistQuestEditor.App/SplashForm.cs",
    probe: "splash",
    // Обычный режим наложения подмешивает фон к сглаженным краям: мягкая тень
    // логотипа становится грязной, а прозрачность по краям — неполной.
    from: "        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;\n",
    to: "",
    why: "поверхность заставки обязана рисоваться в режиме SourceCopy, иначе альфа портится"
  },
  {
    name: "заставка без применения прозрачности",
    file: "src/AssistQuestEditor.App/SplashForm.cs",
    probe: "splash",
    // Прозрачность не применяется вовсе: логотип снова стоит на тёмном квадрате.
    // `= false` вместо вызова: простое удаление строки даёт CS0649 (поле нигде не
    // присваивается), а проект собирается с TreatWarningsAsErrors — контроль тогда
    // сообщал бы «копия не собралась», что не является поимкой проверки.
    from: "        _transparencyApplied = WindowTransparency.Apply(this, _surface);\n",
    to: "        _transparencyApplied = false;\n",
    why: "без применения прозрачности заставка снова рисует фон под прозрачными пикселями"
  },
  {
    name: "заставка с растянутым BackgroundImage",
    file: "src/AssistQuestEditor.App/SplashForm.cs",
    probe: "splash",
    // Возврат к исходной (сломанной) отрисовке: форма рисует BackColor под каждым
    // прозрачным пикселем — тот самый «чёрный квадрат».
    from: "            ClientSize = ScaledSize(_originalSize);\n",
    to: "            ClientSize = ScaledSize(_originalSize);\n            BackgroundImage = _image;\n",
    why: "BackgroundImage на форме не доносит альфу до окна — логотип встанет на чёрный фон"
  },
  {
    name: "шапка Симулятора в прежние три строки",
    file: "src/AssistQuestEditor.App/Web/simulator.html",
    probe: "environment",
    // Возвращается прежнее имя первой строки: строк снова три, и требование
    // «ровно две строки с заданным порядком» нарушено.
    from: '    <div class="simTopRow simTopMain">\n',
    to: '    <div class="simTopRow simTopHead">\n',
    why: "шапка Симулятора обязана состоять из двух строк simTopMain/simTopStatus"
  },
  {
    name: "шапка Симулятора: транспорт перед селектором мира",
    file: "src/AssistQuestEditor.App/Web/simulator.html",
    probe: "environment",
    // Транспорт появляется РАНЬШЕ селектора мира: порядок слева направо нарушен,
    // хотя все нужные элементы на месте. Именно такой случай проверка порядка и
    // обязана ловить — по составу разметки он неотличим от правильного.
    from: '    <div class="simTopRow simTopMain">\n      <div class="worldSelector simWorldSelector"',
    to: '    <div class="simTopRow simTopMain">\n      <div class="simTransport" role="group"></div>\n      <div class="worldSelector simWorldSelector"',
    why: "порядок первой строки: мир и кампания → транспорт → автосохранение"
  },
  {
    name: "название вернулось внутрь строки",
    file: "src/AssistQuestEditor.App/Web/simulator.html",
    probe: "environment",
    // Блок названия снова ВНУТРИ ряда: ряда-сетки больше нет, колонка названия
    // исчезает, и строка сведений начинает выравниваться не по селектору.
    // Самый важный контроль: он ломает именно ТО, что проверяется замером.
    from: '    <div class="simTitleBlock">\n      <div class="simTitle">Assist Quest Editor</div>',
    to: '    <div class="simTopRow simTopMain"><div class="simTitleBlock">\n      <div class="simTitle">Assist Quest Editor</div>',
    why: "название обязано быть соседом строки по сетке, а не её элементом"
  },
  {
    name: "первая строка шапки переносит содержимое",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    // Перенос выключен: при тесноте блоки выдавливаются за правый край вместо
    // того, чтобы уйти вниз.
    from: ".simTopMain{grid-column:2;grid-row:1;flex-wrap:wrap;gap:6px}",
    to: ".simTopMain{grid-column:2;grid-row:1;flex-wrap:nowrap;gap:6px}",
    why: "первая строка обязана переносить содержимое при нехватке места"
  },
  {
    name: "кнопки первой строки без компактных отступов",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    // Полноразмерные отступы выдавливают действия за край: именно поэтому
    // требование «сделать компактнее паддинг» и появилось.
    from: ".simTopMain .toolButton{padding:6px 8px;",
    to: ".simTopMain .toolButton{padding:10px 16px;",
    why: "в шапке Симулятора отступы кнопок обязаны быть компактными"
  },
  {
    name: "панель Симулятора снова flex-колонка",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    // Сетка возвращается к колонке: ряды снова начинаются от левого края окна,
    // и строка сведений оказывается под названием, а не под селектором.
    from: "  display:grid;\n  /* Первая колонка — по содержимому",
    to: "  display:flex;flex-direction:column;\n  /* Первая колонка — по содержимому",
    why: "выравнивание рядов держит сетка, а не подобранный отступ"
  },
  {
    name: "строка сведений в первой колонке",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    // Строка сведений уезжает в колонку названия: выравнивание по левому краю
    // селектора ломается на ровном месте — при верной сетке и верном порядке.
    from: ".simTopStatus{grid-column:2;grid-row:2;",
    to: ".simTopStatus{grid-column:1;grid-row:2;",
    why: "плашки сведений обязаны выравниваться по левому краю селектора"
  },
  {
    name: "блок автосохранения без ограничения ширины",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    // Ограничение снято: длинная подсказка растягивает ряд и выдавливает
    // действия за правый край окна.
    from: ".simAutoSaveBlock{display:flex;flex-direction:column;line-height:1.25;min-width:0;max-width:270px}",
    to: ".simAutoSaveBlock{display:flex;flex-direction:column;line-height:1.25;min-width:0}",
    why: "блок автосохранения обязан ограничивать ширину, иначе ряд уезжает за край"
  },
  {
    name: "подпись авторства Симулятора без канала Host",
    file: "src/AssistQuestEditor.App/Host/MainForm.cs",
    probe: "storage",
    // Подпись рисует Web, но имя живёт в Host: без этой строки в шапке
    // Симулятора навсегда останется прочерк, хотя в главном окне имя есть.
    from: "            _simulator?.SetAuthorJson(payload);\n",
    to: "",
    why: "смена имени обязана доходить до Симулятора, а не только до главного окна"
  },
  {
    name: "заставка без удержания минимума",
    file: "src/AssistQuestEditor.App/Program.cs",
    probe: "splash",
    // Удержание не вызывается вовсе: главное окно создаётся сразу, и заставка
    // мелькает вместо того, чтобы показаться четыре секунды.
    from: "            WaitForSplashMinimum();\n",
    to: "",
    why: "заставка обязана показываться 4 секунды до создания главного окна"
  },
  {
    name: "заставка удерживается меньше четырёх секунд",
    file: "src/AssistQuestEditor.App/Program.cs",
    probe: "splash",
    from: "private const int SplashMinimumDisplayMs = 4000;",
    to: "private const int SplashMinimumDisplayMs = 1500;",
    why: "минимум показа заставки — ровно 4000 мс"
  },
  {
    name: "удержание заставки до запуска слушателя",
    file: "src/AssistQuestEditor.App/Program.cs",
    probe: "splash",
    // Удержание уезжает ВЫШЕ запуска слушателя канала: второй экземпляр ждёт
    // канал 1.5 с, а его ещё 4 секунды никто не слушает — запрос теряется.
    from: "            singleInstance.StartListening();\n\n            var splashPath",
    to: "            WaitForSplashMinimum();\n            singleInstance.StartListening();\n\n            var splashPath",
    why: "слушатель канала обязан запускаться ДО удержания заставки"
  }
];

/**
 * Какие мутации правят C#. Их копия обязана БЫТЬ СОБРАНА: проба читает
 * ИСПОЛНЯЕМЫЙ файл, а каталог сборки подключён junction-ссылкой на настоящее
 * дерево, поэтому без своей сборки проба запускала бы неизменённый exe и
 * «мутант не пойман». Ровно это и выяснилось на прогоне: JS-мутации ловились,
 * а обе C# — нет.
 */
const csharpMutation = file => file.endsWith(".cs");

/**
 * Готовит временную копию дерева для мутации.
 *
 * Копируется только то, что читают пробы, и БЕЗ каталогов сборки и node_modules:
 * их перенос занял бы минуты, а нужны они только для запуска. Вместо копирования
 * они подключаются junction-ссылкой — на Windows она создаётся без прав
 * администратора, в отличие от символической.
 *
 * Прежняя версия копировала один `src`, и пробы падали на `import "playwright"`
 * ещё до своих проверок: ненулевой код возврата выглядел как «мутант пойман»,
 * хотя на самом деле проверка не запускалась.
 */
function stageCopy() {
  const copy = fs.mkdtempSync(path.join(os.tmpdir(), "aq-mutant-"));
  const skip = new Set(["node_modules", ".git", "bin", "obj", "ci-results", "LOGS"]);

  fs.cpSync(root, copy, {
    recursive: true,
    filter: source => !skip.has(path.basename(source))
  });

  // Библиотеки запуска подключаются ссылкой: их копирование заняло бы минуты,
  // а нужны они только для запуска. Каталоги сборки НЕ отдаются ссылкой: проба
  // запускает exe, и ссылка вернула бы НЕизменённую сборку — C#-мутация тогда
  // «не ловится». Для C#-правок своя сборка делается после подмены (см. ниже).
  fs.symlinkSync(path.join(root, "node_modules"), path.join(copy, "node_modules"), "junction");

  return copy;
}

// Проба запускается как отдельный процесс: её код читает файлы из cwd, поэтому
// подменять нужно ИМЕННО рабочий каталог процесса.
const probeFile = {
  inventory: "ci/inventory_smoke.mjs",
  environment: "ci/environment_clock_smoke.mjs",
  storage: "ci/world_storage_smoke.mjs",
  selector: "ci/world_selector_smoke.mjs",
  contrast: "ci/contrast_smoke.mjs",
  splash: "ci/splash_dialog_smoke.mjs"
};
/**
 * Прогон пробы в заданном каталоге.
 *
 * Возвращает разбор по УТВЕРЖДЕНИЯМ, а не «упало/не упало». Без этого негативный
 * контроль неотличим от собственной поломки проверки: синтаксическая ошибка в
 * пробе тоже дала бы ненулевой код, и мутант был бы «пойман» ни за что. Признаком
 * пойманного дефекта считается только собственное сообщение пробы о провале —
 * и то, что провалено ИМЕННО утверждение, а не весь файл целиком.
 */
function runProbe(directory, probe) {
  let stdout = "";
  let code = 0;

  try {
    stdout = execFileSync(process.execPath, [probeFile[probe]], {
      cwd: directory, stdio: "pipe", timeout: 180000, encoding: "utf8"
    });
  } catch (error) {
    code = error.status ?? 1;
    stdout = String(error.stdout ?? "");
  }

  return { code, stdout, failed: code !== 0 && /FAIL/.test(stdout) };
}

// Базовая линия: пробы обязаны ПРОЙТИ на немодифицированном дереве. Иначе
// «пойманные мутанты» означали бы, что проверки сломаны в принципе.
for (const probe of new Set(mutations.map(item => item.probe))) {
  const baseline = runProbe(root, probe);
  results.push({ name: "базовая линия " + probe, caught: null, baseline: true });

  if (baseline.code !== 0 || baseline.failed) {
    // Вывод пробы печатается целиком: без него «базовая линия не проходит»
    // неотличимо от «проба не смогла запуститься» (окно не отрисовалось,
    // exe занят другим процессом), а чинятся эти случаи по-разному.
    failures.push(`базовая линия «${probe}» не проходит на чистом дереве: сама проверка сломана.\n` +
      baseline.stdout.split(/\r?\n/).filter(line => line.trim()).slice(-12)
        .map(line => "     " + line.trim()).join("\n"));
  }
}

for (const mutation of mutations) {
  const copy = stageCopy();
  try {
    const target = path.join(copy, ...mutation.file.split("/"));

    // Исходники в репозитории CRLF, а якоря в этом файле записаны через LF:
    // без нормализации `includes` не находит образец, подмена не применяется, и
    // контроль молча «проходит» ничего не измерив (этот промах уже случался).
    const source = fs.readFileSync(target, "utf8").replace(/\r\n/g, "\n");

    if (!source.includes(mutation.from)) {
      failures.push(`мутация «${mutation.name}»: образец не найден в ${mutation.file}`);
      continue;
    }

    fs.writeFileSync(target, source.split(mutation.from).join(mutation.to), "utf8");

    if (csharpMutation(mutation.file)) {
      // Своя сборка в копии: проба запускает exe, а не читает исходник.
      try {
        execFileSync("dotnet", [
          "build", "src/AssistQuestEditor.App/AssistQuestEditor.App.csproj",
          "-c", "Release", "--nologo", "-v", "q"
        ], { cwd: copy, stdio: "pipe", timeout: 300000 });
      } catch (error) {
        // Не собралось — это НЕ поимка: сообщаем отдельно, иначе «мутант
        // пойман» означало бы «проверка не запустилась».
        failures.push(`мутация «${mutation.name}»: копия не собралась — ` +
          String(error.stdout ?? "").slice(-400));
        continue;
      }
    }

    const result = runProbe(copy, mutation.probe);
    results.push({ name: mutation.name, caught: result.failed });

    if (!result.failed) {
      // Вывод пробы печатается целиком: без него непонятно, ПОЧЕМУ мутант не
      // пойман — «проверка не заметила» и «проверка не нашла нужный контрол»
      // выглядят одинаково, а чинятся по-разному.
      failures.push(`мутация «${mutation.name}» НЕ поймана (${mutation.why}): ` +
        `проверка проходит с дефектом (код ${result.code}).\n` +
        result.stdout.split(/\r?\n/).filter(line => line.trim()).slice(-12)
          .map(line => "     " + line.trim()).join("\n"));
    }
  } finally {
    fs.rmSync(copy, { recursive: true, force: true });
  }
}

const caughtCount = results.filter(item => item.caught === true).length;

if (failures.length) {
  console.log("Негативные контроли R32: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Негативные контроли R32: OK базовая линия пройдена, " + caughtCount +
  " из " + mutations.length + " мутаций пойманы.");
