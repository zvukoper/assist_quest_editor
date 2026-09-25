// Порядок закрытия заставки запуска относительно МОДАЛЬНЫХ диалогов.
//
// Баг: пользователь удалил пользовательскую папку в Документах, и при старте
// была видна только заставка — окно первичной настройки открывалось ПОД ней
// (заставка создана TopMost + ShowWithoutActivation и висит поверх всего), так
// что ни ввести псевдоним, ни отменить, ни выйти было нельзя. В превью таскбара
// окно при этом видно — отсюда симптом «приложение зависло на картинке».
//
// Проверка СТАТИЧЕСКАЯ по порядку вызовов, и это осознанный выбор: заставка и
// модальный диалог — WinForms-объекты, а «виден ли диалог поверх заставки»
// определяется порядком вызовов, а не значениями. Наблюдать это поведенчески
// можно только перечислив окна процесса через Win32, что для CI непереносимо и
// зависит от графической сессии. Порядок вызовов — ровно тот инвариант, который
// нарушался, и он проверяется надёжно.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";

const root = process.cwd();
const program = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Program.cs"), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

/** Тело метода по имени: от объявления до следующего члена класса. */
const methodBody = (name) => {
  const start = program.indexOf(name);
  if (start < 0) return "";
  const candidates = [
    program.indexOf("\n    private ", start + 10),
    program.indexOf("\n    /// ", start + 10)
  ].filter(index => index > 0);
  const stop = candidates.length ? Math.min(...candidates) : undefined;
  return program.slice(start, stop);
};

// --- 1. Заставка обязана закрываться отдельным идемпотентным методом ---

check(/private static SplashForm\? _splash;/.test(program),
  "Заставка должна быть ПОЛЕМ, а не локальной переменной: её закрывает не только Main, " +
  "но и вспомогательные методы фаз запуска.");

const closeBody = methodBody("private static void CloseSplash(string reason)");
check(closeBody.length > 0, "Не найден метод CloseSplash.");
check(/if \(_splash is null \|\| _splash\.IsDisposed\)/.test(closeBody),
  "CloseSplash обязан быть идемпотентным: заставку гасят из нескольких мест, " +
  "и обращение к освобождённой форме уронило бы запуск.");
check(/_splash = null;/.test(closeBody),
  "CloseSplash обязан обнулять поле: иначе следующий вызов увидит Disposed-форму.");

// --- 2. Заставка гасится ДО каждого модального диалога ---

// Первичная настройка. Это и есть исходный дефект.
const setupBody = methodBody("private static bool RunSetupPhase(");
check(setupBody.length > 0, "Не найден метод RunSetupPhase.");

const setupClose = setupBody.indexOf("CloseSplash(");
const setupDialog = setupBody.indexOf("ShowDialog()");
check(setupClose >= 0,
  "Перед первичной настройкой заставка не гасится: диалог окажется под ней и будет недоступен.");
check(setupDialog >= 0, "В фазе настройки не найден модальный ShowDialog().");
check(setupClose >= 0 && setupDialog >= 0 && setupClose < setupDialog,
  "Заставка гасится ПОСЛЕ открытия диалога настройки: он уже невидим.");

// Предупреждение о расхождении ресурсов — тоже модальное и открывается раньше.
const verifyBody = methodBody("private static bool VerifyResourcesAtStartup(");
check(verifyBody.length > 0, "Не найден метод VerifyResourcesAtStartup.");
if (/MessageBox\.Show/.test(verifyBody)) {
  check(verifyBody.indexOf("CloseSplash(") >= 0 &&
        verifyBody.indexOf("CloseSplash(") < verifyBody.indexOf("MessageBox.Show"),
    "Предупреждение о ресурсах показывается ДО закрытия заставки.");
} else {
  check(false, "VerifyResourcesAtStartup больше не предупреждает: проверка порядка стала бессмысленной.");
}

// Уведомление об обновлении кампаний — тот же класс.
const syncIndex = program.indexOf("using var notice = new CampaignSyncNoticeForm(");
check(syncIndex > 0, "Не найдено уведомление об обновлении кампаний.");
if (syncIndex > 0) {
  const before = program.slice(Math.max(0, syncIndex - 400), syncIndex);
  check(/CloseSplash\(/.test(before),
    "Уведомление об обновлении кампаний показывается ДО закрытия заставки.");
}

// --- 3. Заставка гасится и в аварийных путях ---

const catchIndex = program.indexOf("Необработанное исключение верхнего уровня.");
check(catchIndex > 0, "Не найден верхнеуровневый catch.");
if (catchIndex > 0) {
  check(/CloseSplash\(/.test(program.slice(catchIndex, catchIndex + 400)),
    "При исключении заставка обязана закрываться: иначе окно ошибки окажется под ней.");
}

// Срез берётся от `finally` ВПЕРЁД и до конца метода. Если смотреть назад, в срез
// попадает catch — и он со своим CloseSplash маскирует удаление вызова из
// finally (проверено негативным контролем: без этого уточнения подмена не
// ловилась).
const finallyStart = program.indexOf("\n        finally");
const mainEnd = program.indexOf("\n    /// ", finallyStart > 0 ? finallyStart : 0);
const finallyBlock = finallyStart > 0
    ? program.slice(finallyStart, mainEnd > finallyStart ? mainEnd : undefined)
    : "";

check(finallyBlock.length > 0, "Не найден блок finally в Main.");
check(/CloseSplash\(/.test(finallyBlock),
  "В finally заставка обязана закрываться: иначе она переживёт процесс.");

// --- 3б. Заставка видна минимум 4 секунды ---
//
// Требование автора: заставка показывается 4 секунды, и только потом
// запускается приложение. Проверяются ДВА независимых инварианта, потому что
// одной константы мало:
//
//   1. Само удержание существует и вызывается ДО создания главного окна.
//      Удержание ПОСЛЕ создания окна дало бы ровно тот дефект, от которого
//      заставка и защищает: окно уже готово, а на экране ещё висит картинка.
//   2. Слушатель канала запускается ДО удержания. Второй экземпляр ждёт канал
//      всего 1.5 с; если начать слушать после 4-секундной паузы, повторный
//      запуск молча завершился бы, и файл не открылся бы.
const splashMinimum = /private const int SplashMinimumDisplayMs = (\d+);/.exec(program);
check(splashMinimum !== null,
  "Минимальное время показа заставки обязано быть именованной константой: " +
  "иначе «4 секунды» превращаются в магическое число и теряются при правке.");
if (splashMinimum !== null) {
  check(Number(splashMinimum[1]) === 4000,
    "Заставка обязана показываться 4000 мс (требование автора), а не " + splashMinimum[1] + ".");
}

const waitCall = program.indexOf("WaitForSplashMinimum();");
check(waitCall > 0, "Удержание заставки обязано вызываться из Main.");
const mainFormCreation = program.indexOf("mainForm = new MainForm(");
check(mainFormCreation > 0, "Не найдено создание главного окна.");
check(waitCall > 0 && mainFormCreation > 0 && waitCall < mainFormCreation,
  "Удержание заставки обязано стоять ДО создания главного окна: иначе окно " +
  "готово, пока на экране ещё висит картинка.");

const listenCall = program.indexOf("singleInstance.StartListening();");
check(listenCall > 0, "Слушатель единственного экземпляра обязан запускаться.");
check(listenCall > 0 && waitCall > 0 && listenCall < waitCall,
  "Слушатель канала обязан запускаться ДО удержания заставки: второй экземпляр " +
  "ждёт канал 1.5 с, и после 4-секундной паузы запрос передать было бы уже некуда.");

// Запрос, пришедший во время удержания, обязан ДОЖДАТЬСЯ окна, а не потеряться.
// Слушатель поднят рано, окна ещё нет четыре секунды — и без этой ветки путь из
// повторного запуска просто исчез бы: пользователь дважды кликнул по файлу, а
// ничего не открылось.
const activationBody = program.slice(
  program.indexOf("singleInstance.ActivationRequested +="),
  program.indexOf("singleInstance.StartListening();"));
check(/_pendingActivation/.test(activationBody),
  "Запрос, пришедший во время заставки, обязан запоминаться: окна ещё нет, и " +
  "обработать его негде.");
check(/Interlocked\.Exchange\(ref _pendingActivation, null\)/.test(program),
  "Запомненный запрос обязан обрабатываться ПОСЛЕ готовности окна: иначе он " +
  "останется висеть и файл не откроется.");
check(/mainForm\.HandleExternalActivation\(activation\)/.test(program),
  "Запомненный запрос обязан идти ТЕМ ЖЕ путём, что и внешняя активация: иначе " +
  "«запуск без файла» и «запуск с файлом» станут неотличимы.");

// Отсчёт идёт от ПОКАЗА, а не от старта процесса: на медленной машине
// инициализация съедает часть бюджета, и добавка этих миллисекунд сократила бы
// показ до мелькания.
const waitBody = methodBody("private static void WaitForSplashMinimum()");
check(waitBody.length > 0, "Не найден метод WaitForSplashMinimum.");
check(/_splashShown = Stopwatch\.StartNew\(\);/.test(program),
  "Отсчёт показа обязан начинаться в момент показа заставки, а не в начале Main.");
check(/_splashShown\?\.ElapsedMilliseconds|_splashShown\.ElapsedMilliseconds/.test(waitBody),
  "Удержание обязано считаться от показа: иначе медленный старт сократит показ.");
// Заставки нет — ждать нечего. Проверка идёт на ПЕРВОМ условии метода, потому
// что ниже уже идут вычисления, и выход «где-то там» пропустил бы DoEvents.
check(/if \(_splashShown is null \|\| _splash is null \|\| _splash\.IsDisposed\)\s*\n\s*return;/.test(waitBody),
  "При отсутствии заставки (CI-прогон, невизуализируемый старт) удержание " +
  "обязано немедленно возвращаться: ждать несуществующий экран недопустимо.");
check(/Application\.DoEvents\(\);/.test(waitBody),
  "Удержание обязано оставаться отзывчивым: Thread.Sleep без DoEvents делает " +
  "окно «не отвечающим», и заставка не перерисовывается.");
check(/AppLogger\.Info\("Splash: удержание до минимума показа\."/.test(waitBody),
  "Факт удержания обязан попадать в журнал: иначе «удержали» и «не удержали» " +
  "снаружи неразличимы.");

// --- 4. Закрытие привязано к фазе настройки, а не к готовности окна ---
//
// Раньше заставку гасил ТОЛЬКО RevealMainWindow (ждёт готовности WebView2).
// При пустой пользовательской папке — ровно случай пользователя — до этого
// места дело не доходит: сначала открывается диалог настройки.
//
// Проверяется, что закрытие стоит В НАЧАЛЕ фазы, а не после её диалогов: если
// вызов окажется ниже, диалог откроется под заставкой. Текст сообщения НЕ
// фиксируется — он может меняться, а инвариант в порядке вызовов.
const setupHead = setupBody.slice(0, setupBody.indexOf("ShowDialog()") + 1);
check(/CloseSplash\(/.test(setupHead),
  "Закрытие заставки обязано стоять ДО диалогов фазы настройки, а не после них.");

// --- 4б. Заставка обязана быть ПРОЗРАЧНОЙ по альфа-каналу ---
//
// Симптом был: «логотип стоит на чёрном квадрате». Причина не в картинке — PNG
// действительно прозрачный (alpha = 0 и в углах, и в центре; непрозрачных
// пикселей вне логотипа нет). Причина в WinForms: форма принимает
// `BackgroundImage` с альфой, но САМО ОКНО остаётся непрозрачным, и под
// прозрачными пикселями виден `BackColor` формы. Проверка закрывает обе
// возможные регрессии сразу: возврат к непрозрачной отрисовке и появление
// прозрачности там, где её быть не должно (дырки насквозь).
const splashForm = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "SplashForm.cs"), "utf8");
const transparency = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Core", "WindowTransparency.cs"), "utf8");

// Возврат к `BackgroundImage` и есть исходный дефект: форма рисует BackColor под
// каждым прозрачным пикселем, и логотип снова оказывается на тёмном квадрате.
check(!/BackgroundImage\s*=/.test(splashForm),
  "Заставка не должна задавать BackgroundImage: форма рисует BackColor под прозрачными " +
  "пикселями, и логотип оказывается на чёрном квадрате.");
check(!/TransparencyKey\s*=/.test(splashForm),
  "TransparencyKey выбивает РОВНО один цвет: он рвёт сглаженные края и мягкую тень " +
  "логотипа и делает дыру насквозь на любом похожем цвете внутри картинки.");
check(/WindowTransparency\.Apply\(/.test(splashForm),
  "Прозрачность заставки обязана применяться через WindowTransparency: у формы нет " +
  "другого способа получить попиксельную альфу.");

// Применение обязано идти в OnHandleCreated: до создания окна ручки нет, и
// UpdateLayeredWindow некуда применять.
const handleCreated = /protected override void OnHandleCreated\(EventArgs e\)[\s\S]*?\n    \}/.exec(splashForm);
check(handleCreated !== null && /WindowTransparency\.Apply\(/.test(handleCreated[0]),
  "Прозрачность обязана применяться в OnHandleCreated: до этого у окна нет ручки.");
check(handleCreated !== null && /AppLogger\.Info\("Splash: прозрачность\./.test(handleCreated[0]),
  "Факт применения прозрачности обязан попадать в журнал: иначе «применилось» и " +
  "«не применилось» неразличимы снаружи, и заставка может молча остаться непрозрачной.");

// Поверхность обязана сохранять альфу: без SourceCopy сглаживание подмешивает
// цвет фона, и мягкие края становятся грязными.
check(/new Bitmap\(/.test(splashForm),
  "Заставке нужен СВОЙ битмап: BackgroundImage не доносит альфу до окна.");
check(/CompositingMode\.SourceCopy/.test(splashForm),
  "Поверхность заставки обязана рисоваться в режиме SourceCopy: иначе сглаживание " +
  "подмешивает фон и портит альфу по краям.");
check(/\.Clear\(Color\.Transparent\)/.test(splashForm),
  "Поверхность заставки обязана очищаться в ПРОЗРАЧНЫЙ цвет: иначе фон подмешается к альфе.");

// Требования к самому механизму прозрачности.
check(/WS_EX_LAYERED|WsExLayered/.test(transparency) && /UpdateLayeredWindow/.test(transparency),
  "Попиксельная прозрачность возможна только через WS_EX_LAYERED + UpdateLayeredWindow.");
check(/FormBorderStyle\.None/.test(transparency),
  "Прозрачность обязана требовать безрамочное окно: системная рамка рисуется ПОВЕРХ " +
  "layered-содержимого и остаётся непрозрачной.");
// Неудача обязана быть БЕЗОПАСНОЙ: окно, помеченное layered без содержимого, не
// рисует ничего — заставка просто исчезла бы, и причина была бы не видна.
check(/SetWindowLongPtr\(form\.Handle, GwlExStyle, new IntPtr\(style\)\)/.test(transparency),
  "При неудаче UpdateLayeredWindow стиль WS_EX_LAYERED обязан сниматься: иначе окно " +
  "перестанет рисоваться вовсе, и заставка пропадёт без следа.");

// --- 5. Приложение по-прежнему запускается ---
//
// CloseSplash вызывается до создания главного окна и в finally, то есть ошибка в
// нём сломала бы ЛЮБОЙ запуск. Проверяем, что режим CI test доходит до старта.
//
// ТРИ ловушки, каждая из которых ломает такую проверку на исправном коде:
//
//   1. Публикация ИДЁТ В РЕПОЗИТОРИЙ, а не в %TEMP%. В single-file сборке
//      ApplicationBase — это кэш распаковки `%TEMP%\.net\...`, и AppLogger,
//      ищущий MemoryAI вверх по дереву, находит его внутри этого кэша. Журнал
//      оказывается в кэше, а не рядом с exe, и проверка, читающая журнал
//      репозитория, видит пустоту. Публикация под `bin/` (он в .gitignore)
//      сохраняет путь «exe → ... → репозиторий → MemoryAI» рабочим.
//   2. Журнал ГЛОБАЛЬНЫЙ и накапливается между прогонами, поэтому маркер надо
//      искать в СВЕЖЕЙ части, а не в файле целиком.
//   3. Единственный экземпляр: уже запущенный AssistQuestEditor молча отклонит
//      новый запуск, и журнал не получит ни одной новой строки. Проверка
//      сначала убеждается, что защёлка свободна, и снимает процесс по
//      завершении — иначе следующий прогон снова окажется «сломанным».
const repoBin = path.join(root, "bin");
const publishDir = path.join(repoBin, "splash-smoke-" + Date.now());
const logPath = path.join(root, "MemoryAI", "LOGS", "assist_quest_editor.log");

const killStray = () => spawnSync("powershell", [
  "-NoProfile", "-NonInteractive", "-Command",
  "Get-Process AssistQuestEditor -ErrorAction SilentlyContinue | Stop-Process -Force"
], { encoding: "utf8", timeout: 20000 });

try {
  // `-p:AssistQuestRepositoryRoot=` подставляется ЯВНО, как это делает
  // compile.ps1. Без него свойство сборки пустое, и AppLogger определяет корень
  // по месту запуска: в single-file публикации это кэш распаковки
  // `%TEMP%\.net\...`, поэтому журнал уходит туда, а не в репозиторий — и
  // проверка, читающая журнал репозитория, видит пустоту.
  execFileSync("dotnet", [
    "publish", path.join(root, "src", "AssistQuestEditor.App", "AssistQuestEditor.App.csproj"),
    "-c", "Release", "-o", publishDir, "--nologo", "-v", "q",
    "-p:AssistQuestRepositoryRoot=" + root
  ], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });

  const exe = path.join(publishDir, "AssistQuestEditor.exe");
  check(fs.existsSync(exe), "Публикация не создала exe.");

  if (fs.existsSync(exe)) {
    killStray();

    const stray = spawnSync("powershell", [
      "-NoProfile", "-NonInteractive", "-Command",
      "@(Get-Process AssistQuestEditor -ErrorAction SilentlyContinue).Count"
    ], { encoding: "utf8" });
    check(Number(String(stray.stdout || "0").trim()) === 0,
      "Перед запуском уже работал AssistQuestEditor: единственный экземпляр молча " +
      "отклонит запуск, и проверка ничего не измерит.");

    // Отметка ДО запуска: маркер обязан появиться ПОСЛЕ неё, иначе проверка
    // зачтёт строку от прошлого прогона.
    const before = fs.existsSync(logPath) ? fs.readFileSync(logPath, "utf8").length : 0;

    spawnSync(exe, ["-citest"], { cwd: publishDir, encoding: "utf8", timeout: 20000 });
    killStray();

    const full = fs.existsSync(logPath) ? fs.readFileSync(logPath, "utf8") : "";
    const fresh = full.slice(before);

    check(/=== Assist Quest Editor START ===/.test(fresh),
      "Запуск не дошёл даже до START: exe не стартовал.");
    check(/режим=CI test/.test(fresh),
      "Приложение не дошло до старта: свежая часть журнала не содержит маркера режима CI test. " +
      "Получено: " + fresh.split("\n").slice(-6).join(" | "));
    check(!/Запуск отменён: приложение уже работает/.test(fresh),
      "Запуск был отклонён защёлкой единственного экземпляра: проверка измеряла не наш процесс.");

    // Прозрачность проверяется ПО ЖУРНАЛУ РЕАЛЬНОГО ЗАПУСКА, а не только по
    // исходникам. `applied=True` означает, что `UpdateLayeredWindow` принял
    // битмап и окно получило попиксельную альфу; статические проверки этого не
    // доказывают — P/Invoke может вернуть false и на исправном на вид коде.
    const splashLog = /Splash: прозрачность\. \| applied=(\w+); size=(\d+)x(\d+)/.exec(fresh);
    check(splashLog !== null,
      "В журнале нет строки о применении прозрачности заставки: " +
      "«применилось» и «не применилось» снаружи неразличимы.");
    if (splashLog !== null) {
      check(splashLog[1] === "True",
        "Прозрачность заставки НЕ применилась (applied=" + splashLog[1] + "): " +
        "логотип снова окажется на непрозрачном фоне.");

      // Пропорции картинки. Масштабирование допускается (логотип должен читаться
      // и на большом мониторе), но растяжение по одной оси — нет: мягкая тень
      // логотипа вытянулась бы и заставка выглядела бы сплюснутой.
      const width = Number(splashLog[2]), height = Number(splashLog[3]);
      const ratio = width / height, expected = 800 / 450;
      check(Math.abs(ratio - expected) < 0.02,
        `Заставка искажает пропорции картинки: окно ${width}x${height} (${ratio.toFixed(3)}), ` +
        `а исходник 800x450 (${expected.toFixed(3)}).`);
    }

    // Удержание заставки. Требование автора: заставка видна 4 секунды, и только
    // после этого создаётся главное окно. Проверка ИДЁТ ПО ЖУРНАЛУ реального
    // запуска, а не по исходнику: константа доказывает намерение, но не то, что
    // код до неё дошёл. В режиме -citest заставка тоже показывается (она часть
    // запуска), поэтому удержание обязано быть видно и здесь.
    //
    // Время считается по МЕТКАМ журнала: между началом подготовки заставки и
    // концом удержания обязано пройти не меньше 4 секунд. Проверяется ИМЕННО
    // факт показа, а не значение константы — «прочитали константу и не
    // подождали» так не пройдёт.
    const stamp = (pattern) => {
      const match = new RegExp("(\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}\\.\\d{3})[^\\n]*" + pattern)
        .exec(fresh);
      return match === null ? null : Date.parse(match[1].replace(" ", "T"));
    };
    const heldStart = stamp("Splash: подготовка\\.");
    const heldEnd = stamp("Splash: минимум показа выбран\\.");
    check(heldStart !== null,
      "В журнале нет строки о подготовке заставки: удержание непроверяемо.");
    check(heldEnd !== null,
      "В журнале нет строки о завершении удержания заставки: заставка обязана " +
      "показываться 4 секунды, и без этой строки «удержали» и «не удержали» неразличимы.");
    if (heldStart !== null && heldEnd !== null) {
      const held = heldEnd - heldStart;
      check(held >= 4000,
        "Заставка держалась " + held + " мс, а обязана — не меньше 4000 мс: " +
        "приложение стартовало раньше, чем заставку успели прочитать.");
      check(/Splash: минимум показа выбран\. \| minimum=4000;/.test(fresh),
        "В журнале удержания обязана стоять константа 4000: иначе удержание " +
        "считается по другому числу, и требование «4 секунды» не выполнено.");
    }
  }
} catch (error) {
  failures.push("Не удалось собрать или запустить приложение: " +
    String(error.stderr || error.message).split("\n").slice(-3).join(" "));
} finally {
  // Уборка обязательна даже при падении: оставленный процесс заблокирует
  // следующий прогон, и следующий отказ будет выглядеть как новый баг.
  killStray();
  try { fs.rmSync(publishDir, { recursive: true, force: true }); } catch { /* temp */ }
}

if (failures.length) {
  console.log("Заставка и модальные диалоги: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Заставка и модальные диалоги: OK заставка гасится до каждого модального диалога " +
  "(настройка, ресурсы, уведомление), метод идемпотентен, запуск не сломан.");
