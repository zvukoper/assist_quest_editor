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
