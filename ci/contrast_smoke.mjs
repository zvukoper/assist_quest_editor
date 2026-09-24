// Читаемость текста в тёмных диалогах.
//
// Проверка ЗАМЕРЯЮЩАЯ и поведенческая. Дефект, который она стережёт, в исходниках
// невидим: цвета заданы верно (`ForeColor` светлый, `BackColor` тёмный), но у
// ВЫКЛЮЧЕННОЙ кнопки WinForms рисует текст СИСТЕМНЫМ серым, игнорируя заданный
// цвет. На светлой теме это бледный серый, на тёмном фоне диалога — тёмно-серый.
// Замерено на настоящем экране: яркость текста 65 при фоне 24, контраст 2,1:1 —
// вдвое ниже минимума 4,5:1, то есть панель «Выбрать изображение…» в окне
// сведений не читалась вовсе.
//
// Мерить приходится ОТРИСОВАННОЕ окно: `DrawToBitmap` отдаёт то, что просит код,
// а не то, что рисует система. Проба `--contrast-probe` сама показывает диалоги и
// считает контраст; здесь проверяется и наличие обходного пути, и его РАБОТА:
// статическая проверка класса без запуска прошла бы на классе, который никто не
// использует.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const button = read("src/AssistQuestEditor.App/Host/DarkFlatButton.cs");
const program = read("src/AssistQuestEditor.App/Program.cs");

// --- 1. Класс кнопки: выключенное состояние рисуется сам ---

check(/class DarkFlatButton : Button/.test(button),
  "Кнопка с читаемым выключенным состоянием должна быть отдельным классом кнопки.");
check(/protected override void OnPaint\(PaintEventArgs e\)/.test(button),
  "Без своей отрисовки выключенный текст остаётся системно-серым: перекрыть его свойством нельзя.");
// Ветка «включена — отдать базовому классу» обязательна: если рисовать обе ветки
// вручную, потеряется оформление темы у рабочей кнопки.
check(/if \(Enabled\)[\s\S]{0,80}?base\.OnPaint\(e\);[\s\S]{0,40}?return;/.test(button),
  "Включённая кнопка должна рисоваться базовым классом: иначе пропадёт оформление темы.");
check(/TextRenderer\.DrawText\(/.test(button) && /DisabledTextColor/.test(button),
  "Выключенный текст должен рисоваться СВОИМ цветом, а не системным.");
// Свойство цвета обязано быть скрыто от конструктора форм: проект собирается с
// TreatWarningsAsErrors, и WFO1000 валит сборку.
check(/\[DesignerSerializationVisibility\(DesignerSerializationVisibility\.Hidden\)\]/.test(button),
  "Свойство цвета выключенного текста обязано быть помечено атрибутом: иначе сборка падает на WFO1000.");

// Цвет читается (5,2:1 на фоне 23,24,25) и при этом заметно приглушён.
const disabledColor = /169, 178, 190|170, 178, 190/.test(button);
check(disabledColor,
  "Цвет выключенного текста должен быть светлым: он обязан давать контраст выше 4,5:1.");

// --- 2. Диалоги действительно используют этот класс ---

const dialogs = [
  ["ResourcePropertiesForm.cs", "окно сведений и правки ресурса"],
  ["ResourceExportForm.cs", "диалог экспорта"],
  ["ArchiveImportForm.cs", "диалог импорта архива"],
  ["ImportParentForm.cs", "выбор родителя при импорте"],
  ["ResourceCreateForm.cs", "создание ресурса"],
  ["WorldChooserForm.cs", "выбор мира"],
  ["SettingsForm.cs", "настройки"],
  ["FirstRunSetupForm.cs", "первичная настройка"]
];

for (const [file, label] of dialogs) {
  const source = read("src/AssistQuestEditor.App/Host/" + file);
  check(/DarkFlatButton/.test(source),
    `${label}: кнопки должны использовать класс с читаемым выключенным состоянием (${file}).`);
  // Обычных плоских кнопок остаться не должно: одна забытая — и именно она
  // окажется той, что выключается.
  check(!/FlatStyle = FlatStyle\.Flat/.test(source),
    `${label}: остались обычные плоские кнопки — у них выключенный текст системно-серый (${file}).`);
}

// --- 3. Реальный замер отрисованных диалогов ---

const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для пробы читаемости.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-contrast-"));
  const report = path.join(workspace, "report.txt");
  const capture = path.join(workspace, "capture");

  // `--capture` ОБЯЗАТЕЛЕН, и вот почему: замер вне экрана (`DrawToBitmap`) отдаёт
  // то, что ПРОСИТ код, — то есть заданные цвета — и у системно отрисованной
  // выключенной кнопки он показывает светлый текст, которого на экране нет.
  // Именно поэтому первый прогон негативного контроля «диалог на обычной кнопке»
  // проходил с дефектом. Снимок экрана берёт РЕАЛЬНУЮ отрисовку.
  //
  // Код возврата не проверяется отдельно: проба сообщает о находке ТЕКСТОМ
  // отчёта, а execFileSync на ненулевом коде бросает исключение. Без перехвата
  // проверка падала бы стектрейсом вместо внятного «кнопка не читается», и
  // негативный контроль засчитал бы мутанта непойманным.
  try {
    execFileSync(exe, ["--contrast-probe", "--capture", capture, "--report", report],
      { stdio: "pipe", timeout: 180000 });
  } catch {
    // Результат читается из отчёта ниже.
  }

  const probe = fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";

  check(/Читаемость диалогов проверена/.test(probe),
    "Проба читаемости не отработала: " + probe);
  check(/contrast: ok/.test(probe),
    "Найдены нечитаемые контролы: " + probe);
  // Покрытие обязательно: без него удаление диалога из пробы СНИЖАЛО бы охват,
  // и проверка оставалась бы зелёной — «нет нечитаемых» верно и на пустом списке.
  check(/coverage: ok/.test(probe),
    "Проба читаемости не покрывает обязательные диалоги: " + probe);

  // Число замеров должно быть достаточным: кнопки семи диалогов плюс поля ввода.
  const measured = /buttons measured: (\d+)/.exec(probe);
  check(measured !== null && Number(measured[1]) >= 18,
    "Замерено подозрительно мало кнопок: " + (measured?.[1] ?? "нет строки") + ".");

  // Отдельно проверяется, что замер действительно нашёл текст, а не рамку:
  // контраст должен быть выше минимума у КАЖДОЙ строки.
  const ratios = [...probe.matchAll(/контраст (\d+),(\d+):1/g)].map(match =>
    Number(match[1]) + Number(match[2]) / 10);
  check(ratios.length >= 18, "Замеры контраста отсутствуют: " + probe);
  check(ratios.every(value => value >= 4.5),
    "Есть контролы с контрастом ниже 4,5:1: " + ratios.filter(value => value < 4.5).join(", "));

  // Отдельно требуется, чтобы среди замеров БЫЛА выключенная кнопка. Без этого
  // возврат к пропуску выключенных контролов снова сделал бы замер слепым к
  // дефекту, ради которого он написан, а проверка осталась бы зелёной.
  check(/\(выключена\): контраст/.test(probe),
    "Выключенные кнопки не замерены: у них системная отрисовка и именно там был дефект.");
}

if (failures.length) {
  console.log("Читаемость диалогов: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Читаемость диалогов: OK выключенные кнопки читаемы, " +
  "окна сведений, экспорта, импорта, создания, мира, настроек и первичной настройки замерены.");

function findExecutable() {
  const base = path.join(root, "src", "AssistQuestEditor.App", "bin");
  if (!fs.existsSync(base)) return null;

  // Берётся САМЫЙ СВЕЖИЙ exe: в дереве лежат сборки Debug и Release, и
  // произвольная из них оказалась бы устаревшей — проба мерила бы прежний код.
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
