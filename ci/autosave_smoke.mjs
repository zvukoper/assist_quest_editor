// Автосохранение прохождения: запись В МИР и обратное чтение.
//
// История дефекта: автосохранение было РЕАЛИЗОВАНО, но невидимо для проверок.
// `SimulationSaveStore` писал в общий каталог `<Документы>\Assist Quest Editor\saves`,
// лежащий ВНЕ дерева миров, и никто этот каталог не создавал. Запись падала с
// `DirectoryNotFoundException`, который глушит вызывающий код (симуляция не должна
// падать из-за диска) — в логе оставалась строка ERROR, а наружу это выглядело как
// «автосохранение не работает»: останавливаешь симуляцию, перезапускаешь, и мир
// снова новый.
//
// Поэтому проверка статической быть НЕ МОЖЕТ: чтение исходников показывало
// корректный `SimulationSaveStore` и корректный `AutosaveWorld`. Нужен реальный
// запуск — запись файла с последующим чтением.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const store = read("src/AssistQuestEditor.App/SimulationSaveStore.cs");
const simulator = read("src/AssistQuestEditor.App/Host/SimulatorForm.cs");
const appPaths = read("src/AssistQuestEditor.App/Core/AppPaths.cs");

// --- 1. Запись обязана создавать каталог сама ---
//
// Это и есть корень дефекта: `Write` писал в путь, не проверяя, что каталог есть.
check(/private void Write\(string path, SimulationSave save\)/.test(store),
  "Запись сохранения снова стала однострочной: каталог не создаётся.");
check(/Directory\.CreateDirectory\(Path\.GetDirectoryName\(path\)/.test(store),
  "Write не создаёт каталог: автосохранение упадёт на несуществующей папке.");

// --- 2. Стор симулятора указывает В МИР, а не в общий каталог вне миров ---
check(/WorldPaths\.SavesFolderPath\(world\.FolderPath\)/.test(simulator),
  "Симулятор пишет сохранения вне своего мира: снимок одного мира " +
  "можно будет загрузить в другой, где другие квесты и точки.");
// Прежний корень не должен использоваться при выбранном мире.
check(/world is null[\s\S]{0,120}?AppPaths\.SimulationSaveRoot/.test(simulator),
  "Общий каталог сохранений обязан остаться только запасным вариантом без мира.");
// Однострочное поле означало бы, что стор создан ДО получения мира.
check(!/readonly SimulationSaveStore _saveStore = new\(\)/.test(simulator),
  "Стор сохранений создан без мира: он не может знать, куда писать.");
// Старый корень описан как запасной, а не как рабочий: иначе следующий автор
// снова направит туда запись.
check(/Оставлен для совместимости/.test(appPaths),
  "AppPaths.SimulationSaveRoot больше не помечен как запасной: непонятно, куда писать.");

// --- 3. Реальная запись и чтение ---
const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки автосохранения.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-autosave-"));
  let counter = 0;

  const runProbe = (args) => {
    counter += 1;
    const report = path.join(workspace, `probe-${counter}.txt`);

    // Код возврата НЕ проверяется здесь: проба сообщает о неудаче текстом
    // отчёта, а `execFileSync` на ненулевом коде бросает исключение. Без
    // перехвата проверка падала бы стектрейсом вместо внятного «автосохранение
    // не выполнено» — и негативный контроль засчитал бы мутанта непойманным,
    // хотя exe честно упал с DirectoryNotFoundException.
    try {
      execFileSync(exe, [...args, "--report", report], { stdio: "pipe", timeout: 120000 });
    } catch {
      // Ожидаемо для мутантов: результат читается из отчёта ниже.
    }

    return fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  };

  const worldReport = runProbe(["--create-probe", workspace, "world", "Мир Сохранений"]);
  check(/Ресурс создан/.test(worldReport), "Мир для пробы не создан: " + worldReport);

  const worldId = /Id: (.+)/.exec(worldReport)?.[1]?.trim() ?? "";
  const worldFolder = /Папка: (.+)/.exec(worldReport)?.[1]?.trim() ?? "";
  check(worldId.length > 0 && worldFolder.length > 0,
    "Отчёт создания мира не назвал id или папку: " + worldReport);

  // Папка Saves удаляется ДО пробы. Именно так выглядел дефект: её не было, и
  // запись падала. Проба обязана проходить и в этом случае — стор создаёт её сам.
  const savesFolder = path.join(worldFolder, "Saves");
  fs.rmSync(savesFolder, { recursive: true, force: true });
  check(!fs.existsSync(savesFolder), "Папка Saves не удалилась перед пробой.");

  const autosaveReport = runProbe(["--autosave-probe", workspace, worldId]);
  const probeSucceeded = /Автосохранение проверено/.test(autosaveReport);

  check(probeSucceeded,
    "Автосохранение не выполнено: " + autosaveReport);

  // Дальнейшие проверки читают ДИСК и требуют пути из отчёта. Если проба не
  // прошла, проверять нечего — но падать нельзя: падение стектрейсом НОДЫ
  // сделало бы результат неотличимым от дефекта самой проверки, и негативный
  // контроль не смог бы понять, поймал он мутанта или просто сломался.
  if (!probeSucceeded) {
    reportFailures();
    process.exit(1);
  }

  check(/Файл существует: True/.test(autosaveReport),
    "Файл автосохранения не создан: " + autosaveReport);
  check(/Прочитано: session/.test(autosaveReport),
    "Автосохранение записано, но не читается обратно: " + autosaveReport);

  // Размер проверяется отдельно: файл нулевой длины «существует» и даже читается
  // как пустой снимок, но состоянием не является.
  const sizeMatch = /Размер: (\d+)/.exec(autosaveReport);
  check(sizeMatch !== null && Number(sizeMatch[1]) > 0,
    "Файл автосохранения пуст: " + autosaveReport);

  // --- Файл лежит В МИРЕ ---
  const sessionPath = /Файл: (.+)/.exec(autosaveReport)?.[1]?.trim() ?? "";
  const normalizedWorld = path.resolve(worldFolder);
  check(sessionPath.length > 0 &&
        path.resolve(sessionPath).startsWith(normalizedWorld + path.sep),
    "Автосохранение записано ВНЕ папки мира: " + sessionPath);
  check(sessionPath.length > 0 &&
        path.resolve(sessionPath).startsWith(path.resolve(savesFolder) + path.sep),
    "Автосохранение записано не в папку Saves мира: " + sessionPath);
  check(sessionPath.length > 0 && fs.existsSync(sessionPath),
    "Файла автосохранения нет на диске: " + sessionPath);

  if (sessionPath.length > 0 && fs.existsSync(sessionPath)) {
    check(fs.statSync(sessionPath).size > 0, "Файл автосохранения пуст на диске.");
  }

  // --- Вне дерева миров ничего не появилось ---
  const strayFolder = path.join(workspace, "saves");
  check(!fs.existsSync(strayFolder),
    "Появился общий каталог сохранений вне миров: " + strayFolder);

  // --- Повторная запись перезаписывает тот же слот, а не плодит файлы ---
  const secondReport = runProbe(["--autosave-probe", workspace, worldId]);
  check(/Автосохранение проверено/.test(secondReport),
    "Повторное автосохранение не выполнено: " + secondReport);

  if (fs.existsSync(savesFolder)) {
    const sessionFiles = fs.readdirSync(savesFolder)
      .filter(name => name.endsWith(".aqsave"));
    check(sessionFiles.length === 1,
      "Автосохранение размножило файлы вместо одного слота: " +
      sessionFiles.join(", "));
  }

  // --- Мир без папки Saves читается, а не считается повреждённым ---
  //
  // Мир мог быть создан прежней сборкой (без Saves) или папку могли удалить.
  // Симулятор обязан работать, а не отказываться открывать такой мир.
  const otherReport = runProbe(["--create-probe", workspace, "world", "Старый Мир"]);
  const otherId = /Id: (.+)/.exec(otherReport)?.[1]?.trim() ?? "";
  const otherFolder = /Папка: (.+)/.exec(otherReport)?.[1]?.trim() ?? "";

  if (otherFolder.length > 0) {
    fs.rmSync(path.join(otherFolder, "Saves"), { recursive: true, force: true });

    const oldWorldReport = runProbe(["--autosave-probe", workspace, otherId]);
    check(/Автосохранение проверено/.test(oldWorldReport),
      "Мир без папки Saves не смог автосохраниться: " + oldWorldReport);
    check(fs.existsSync(path.join(otherFolder, "Saves", "session.aqsave")),
      "Папка Saves не восстановлена при автосохранении старого мира.");

    // Снимки разных миров НЕ должны попадать в один слот: иначе прохождение
    // одного мира открывалось бы в другом.
    check(path.resolve(sessionPath) !==
          path.resolve(path.join(otherFolder, "Saves", "session.aqsave")),
      "Автосохранения двух миров пишутся в один файл: прохождение смешается.");
  }
}

if (failures.length) {
  reportFailures();
  process.exit(1);
}

console.log("Автосохранение: OK пишется в Saves своего мира, читается обратно, слот один, папка создаётся сама.");

function reportFailures() {
  console.log("Автосохранение: FAIL");
  failures.forEach(item => console.log(" - " + item));
}

function findExecutable() {
  const base = path.join(root, "src", "AssistQuestEditor.App", "bin");
  if (!fs.existsSync(base)) return null;

  // Берётся САМЫЙ СВЕЖИЙ exe: в дереве могут лежать сборки Debug и Release, и
  // произвольная из них оказалась бы устаревшей — проба проверяла бы прежний код.
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
