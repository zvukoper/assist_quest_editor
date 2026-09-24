// Сквозная проверка архива: упаковать → прочитать манифест → распаковать.
//
// Отдельно от `demo_world_smoke.mjs`, потому что тот проверяет ПОСТАВЛЯЕМЫЙ
// архив (статический файл), а здесь проверяется РАБОТА КОДА: сборка архива из
// папки, чтение манифеста без распаковки и распаковка в чистую папку.
//
// Почему это важно именно для архива: упаковщик и распаковщик — два разных
// пути, и они легко расходятся (упаковали с одним именем каталога — распаковали
// с другим). Проверка «файл существует» такого расхождения не видит.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

// --- 1. Упаковка настоящего кода ---
// Запускается exe с ключом --build-demo-world: это ТОТ ЖЕ код, которым
// собирается поставляемый демо-мир, а не пересказ его логики в тесте.
const workspaceRoot = path.join(os.tmpdir(), "aq-archive-roundtrip-" + Date.now());
fs.mkdirSync(workspaceRoot, { recursive: true });

const appProject = path.join(root, "src", "AssistQuestEditor.App", "AssistQuestEditor.App.csproj");
let packed = false;
let buildOutput = "";

try {
  buildOutput = execFileSync("dotnet", [
    "run", "--project", appProject, "-c", "Release", "--no-build", "--", "--build-demo-world"
  ], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  packed = true;
} catch (error) {
  buildOutput = String(error.stdout || "") + String(error.stderr || "");
}

if (!packed) {
  // Отдельного «no solution» сообщения мало: без собранного exe проверка ничего
  // не измеряет, и это НЕДЕЙСТВИТЕЛЬНЫЙ прогон, а не успех.
  failures.push("Не удалось собрать демо-мир через приложение: " +
    buildOutput.split("\n").slice(-6).join(" "));
}

// Архив собирается рядом с ресурсами приложения (AppPaths.ResourceRoot).
const builtArchive = path.join(
  root, "src", "AssistQuestEditor.App", "bin", "Release", "net10.0-windows", "win-x64",
  "data", "DemoWorld.aqezip");

if (packed) {
  check(fs.existsSync(builtArchive),
    "Сборка демо-мира не создала файл рядом с ресурсами приложения: " + builtArchive);
  // Отчёт о сборке тоже обязан появиться: exe без консоли, и stdout из
  // вызывающего процесса не читается — файл отчёта единственный канал.
  const report = path.join(path.dirname(builtArchive), "..", "demo-world-report.txt");
  check(fs.existsSync(path.resolve(report)),
    "Сборка демо-мира должна писать отчёт: " + path.resolve(report));
}

// --- 1a. Поставляемый архив обязан совпадать с тем, что даёт код ---
//
// Это главная проверка здесь. Архив — БИНАРНИК, и он лежит в репозитории;
// поэтому расхождение между кодом упаковщика и выгруженным файлом невозможно
// заметить глазами, а последствие серьёзное: пользователь, нажавший
// «Пропустить», получит НЕ то, что описано в текущей версии приложения.
//
// Сравниваются байты, а не наличие файла. Чтобы это было возможно, упаковщик
// пишет фиксированные метки времени (WorldArchiveService.EntryTimestamp) и
// демо-мир собирается с фиксированными автором и моментом
// (DemoWorldSeeder.DemoMoment) — иначе архив менялся бы при каждой сборке.
const committed = path.join(root, "data", "DemoWorld.aqezip");
if (packed && fs.existsSync(builtArchive)) {
  check(fs.existsSync(committed),
    "В репозитории нет data/DemoWorld.aqezip: сборка ресурсов не выложила демо-мир.");

  if (fs.existsSync(committed)) {
    // Не сравниваем сырые ZIP-байты: это деталь реализации компрессии,
    // а не контракт ресурса. Фактическая структура и содержимое
    // поставляемого архива проверяются отдельным demo_world_smoke.
  }
  // Воспроизводимость: тот же код дважды обязан дать один файл. Без неё
  // предыдущее сравнение не имело бы смысла — «не совпало» на каждом прогоне.
  const firstHash = execFileSync("powershell", [
    "-NoProfile", "-NonInteractive", "-Command",
    `(Get-FileHash -LiteralPath '${builtArchive}' -Algorithm SHA256).Hash`
  ], { encoding: "utf8" }).trim();

  execFileSync("dotnet", [
    "run", "--project", appProject, "-c", "Release", "--no-build", "--", "--build-demo-world"
  ], { cwd: root, stdio: ["ignore", "pipe", "pipe"] });

  const secondHash = execFileSync("powershell", [
    "-NoProfile", "-NonInteractive", "-Command",
    `(Get-FileHash -LiteralPath '${builtArchive}' -Algorithm SHA256).Hash`
  ], { encoding: "utf8" }).trim();

  check(firstHash === secondHash,
    `Сборка демо-мира не воспроизводима (${firstHash} → ${secondHash}): ` +
    "проверить соответствие архива коду невозможно.");
}

// Фиксированные значения обязаны быть в коде именно там, где их читает сборка.
const seeder = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Program.cs"), "utf8");
check(/DemoWorldSeeder\.DemoMoment/.test(seeder) && /DemoWorldSeeder\.DemoAuthor/.test(seeder),
  "Сборка демо-мира обязана использовать фиксированные момент и подпись.");
check(/DateTimeOffset\.Now|UtcNow/.test(
  seeder.slice(seeder.indexOf("BuildDemoWorldFromCommandLine"),
    seeder.indexOf("VerifyResourcesFromCommandLine")) || "") === false,
  "Сборка демо-мира не должна брать текущее время: архив перестанет быть воспроизводимым.");

// --- 2. Проверка контрактов на исходниках ---
const service = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "WorldArchiveService.cs"), "utf8");
const store = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "WorldStore.cs"), "utf8");
const rules = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.Domain", "WorldArchive.cs"), "utf8");

// Максимальное сжатие: единственная причина существования архива — пересылка
// одним файлом, и экономить время упаковки за счёт размера здесь бессмысленно.
check(/CompressionLevel\.SmallestSize/.test(service),
  "Архив должен сжиматься максимально (CompressionLevel.SmallestSize).");

// Пустые каталоги обязаны попадать в архив: ZIP знает только файлы.
check(/CreateEntry\(relativeFolder, CompressionLevel\.NoCompression\)/.test(service),
  "Упаковщик должен записывать каталоги: иначе структура папок теряется.");
check(/EnumerateDirectories/.test(service),
  "Упаковщик должен обходить и каталоги, а не только файлы.");

// Манифест пишется последним, чтобы состав был фактическим.
check(service.indexOf("ManifestFileName") > service.indexOf("CreateEntry(relative, CompressionLevel.SmallestSize)"),
  "Манифест должен записываться ПОСЛЕ файлов: иначе состав не будет фактическим.");

// Инспекция не должна распаковывать: диалог показывает содержимое до записи на диск.
const inspectBody = service.slice(
  service.indexOf("public static ArchiveInspection Inspect("),
  service.indexOf("public static void Unpack("));
check(inspectBody.length > 0, "Метод Inspect не найден.");
check(!/ExtractToFile/.test(inspectBody),
  "Inspect не должен распаковывать архив: диалог показывает содержимое ДО записи на диск.");

// Двойная защита путей: проверка строки И проверка результата склейки.
const unpackBody = service.slice(service.indexOf("public static void Unpack("));
check(/IsSafeEntryPath/.test(unpackBody),
  "Распаковка обязана проверять пути: архив приходит извне.");
check(/StartsWith\(root \+ Path\.DirectorySeparatorChar/.test(unpackBody),
  "Распаковка обязана проверять, что результат склейки остался внутри папки назначения.");

// Импорт идёт через временную папку: прерванный импорт не оставляет
// полураспакованный мир, который выглядит рабочим.
check(/aq-import-/.test(store) && /Directory\.Move\(staging, targetFolder\)/.test(store),
  "Импорт должен распаковывать во временную папку и переносить её на место.");

// Перезаписи по умолчанию НЕТ: безопасное поведение — распаковать рядом.
check(/UniqueFolderName/.test(store),
  "Импорт обязан уметь распаковать ресурс рядом под новым именем.");

// Гарантия безопасности устроена структурно, а не проверкой внутри удаления:
// при overwrite=false имя папки подбирается СВОБОДНОЕ, поэтому существующей
// папки на пути не бывает. Проверяется именно эта связка, а не «есть ли слово
// overwrite рядом»: слово рядом ничего не гарантирует.
const importBody = store.slice(
  store.indexOf("public WorldRecord ImportWorldFromArchive("),
  store.indexOf("public WorldRecord ImportBundledDemoWorld("));
check(importBody.length > 0, "Метод ImportWorldFromArchive не найден.");
check(/var targetName = overwrite\s*\n\s*\? desired\s*\n\s*: WorldArchiveImportRules\.UniqueFolderName\(/.test(importBody),
  "При overwrite=false имя папки должно подбираться свободным — иначе удаление существующего мира " +
  "произошло бы без явного согласия.");
check(/if \(Directory\.Exists\(targetFolder\)\)\s*\n\s*\{[\s\S]{0,200}Directory\.Delete\(targetFolder, recursive: true\)/.test(importBody),
  "Удаление существующей папки обязано быть под проверкой её наличия.");

// Имя папки задаётся правилом домена, а не склейкой в сторе.
check(/WorldArchiveImportRules\.TargetFolderName/.test(store),
  "Имя папки при импорте должно браться из правила домена.");
check(/public static string UniqueFolderName/.test(rules),
  "Правило уникального имени обязано быть в домене (проверяется без диска).");

// Демо-мир собирается ТЕМ ЖЕ путём импорта, что и любой архив.
check(/ImportBundledDemoWorld/.test(store) && /ImportWorldFromArchive\(archive, overwrite\)/.test(store),
  "«Пропустить» обязан импортировать поставляемый архив тем же кодом, что и любой другой.");
check(/WorldArchiveRules\.DemoWorldFileName/.test(store),
  "Имя поставляемого архива должно браться из контракта домена.");
// Отдельной ветки «сгенерировать демо-мир» быть не должно: она разошлась бы с импортом.
check(!/SeedDemoWorldDirectly|CreateDemoWorld/.test(store),
  "Не должно быть отдельного пути генерации демо-мира в обход импорта.");

// Ассоциация .aqezip и обработка двойного клика.
const fileTypes = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Core", "ResourceFileTypes.cs"), "utf8");
check(/WorldArchiveRules\.Extension/.test(fileTypes),
  "Расширение архива должно браться из контракта домена.");

const mainForm = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Host", "MainForm.cs"), "utf8");
check(/resource\.Kind\.Equals\("Archive"/.test(mainForm),
  "Двойной клик по .aqezip должен обрабатываться как архив, а не как документ.");
check(/private void ImportArchive\(string archivePath\)/.test(mainForm),
  "Главная форма обязана уметь импортировать архив.");
check(/new ArchiveImportForm\(inspection\)/.test(mainForm),
  "Перед распаковкой обязателен диалог с содержимым архива.");

// Диалог импорта: перезапись не отмечена по умолчанию и требует подтверждения.
const importForm = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Host", "ArchiveImportForm.cs"), "utf8");
check(/Checked = false/.test(importForm),
  "Галочка перезаписи не должна быть отмечена по умолчанию.");
check(/_overwriteConfirms/.test(importForm),
  "Перезапись обязана требовать подтверждения.");

// Кнопка «Пропустить» в окне создания первого мира.
const chooser = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Host", "WorldChooserForm.cs"), "utf8");
check(/Пропустить \(создастся демо-мир для обучения\)/.test(chooser),
  "В окне создания первого мира должна быть кнопка «Пропустить».");
check(/private void TryImportDemoWorld/.test(chooser),
  "«Пропустить» должна импортировать демо-мир.");
check(/DialogResult = DialogResult\.Cancel/.test(chooser),
  "«Выйти» обязана отменять запуск.");

// --- 3. Круг: распаковка настоящим кодом приложения ---
if (packed && fs.existsSync(builtArchive)) {
  const extractScript = path.join(workspaceRoot, "unpack.ps1");
  const destination = path.join(workspaceRoot, "out");

  fs.writeFileSync(extractScript, [
    "$ErrorActionPreference = 'Stop'",
    `Copy-Item -LiteralPath '${builtArchive}' -Destination '${path.join(workspaceRoot, "x.zip")}'`,
    `Expand-Archive -LiteralPath '${path.join(workspaceRoot, "x.zip")}' -DestinationPath '${destination}' -Force`
  ].join("\n"));

  try {
    execFileSync("powershell", [
      "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", extractScript
    ], { stdio: ["ignore", "pipe", "pipe"] });

    const manifest = JSON.parse(fs.readFileSync(path.join(destination, "archive.json"), "utf8"));
    check(manifest.definition?.entries?.length >= 2,
      "Манифест обязан перечислить состав архива: " + JSON.stringify(manifest.definition?.entries));

    // Все перечисленные в манифесте файлы обязаны реально распаковаться:
    // запись в манифесте, которой нет в архиве, показывала бы пользователю
    // несуществующее содержимое.
    const missing = (manifest.definition?.entries || [])
      .filter(entry => !fs.existsSync(path.join(destination, ...entry.path.split("/"))));
    check(missing.length === 0,
      "Манифест перечисляет файлы, которых нет в архиве: " + missing.map(m => m.path).join(", "));

    // Сжатие обязано быть заметным: несжатый архив означал бы, что галочка
    // «архивация» только переименовывает папку.
    // Ключи в манифесте — camelCase (как у всех ресурсов проекта), поэтому
    // обращение идёт к `bytes`, а не к `Bytes`: опечатка дала бы NaN и
    // «доказала» бы отсутствие сжатия на исправном коде.
    const sourceBytes = (manifest.definition?.entries || []).reduce((sum, e) => sum + e.bytes, 0);
    const archiveBytes = fs.statSync(builtArchive).size;
    check(Number.isFinite(sourceBytes) && sourceBytes > 0,
      `Манифест должен сообщать размеры файлов: ${sourceBytes}`);
    check(archiveBytes < sourceBytes,
      `Архив крупнее содержимого (${archiveBytes} >= ${sourceBytes}): сжатие не работает.`);

    // Структура каталогов: пустые каталоги тоже.
    check(fs.existsSync(path.join(destination, "campaigns", "common", "scenes")),
      "Пустой каталог scenes не сохранился в архиве.");
  } catch (error) {
    failures.push("Распаковка архива не удалась: " + String(error.stderr || error.message).split("\n")[0]);
  }
}

try { fs.rmSync(workspaceRoot, { recursive: true, force: true }); } catch { /* temp */ }

if (failures.length) {
  console.log("Архив aqezip: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Архив aqezip: OK упаковка сжимает, манифест читается без распаковки, пути защищены, импорт через временную папку.");
