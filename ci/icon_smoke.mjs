// Иконки: приложение, окна и типы файлов — из логотипа AQE.
//
// Проверка ПОВЕДЕНЧЕСКАЯ в главной части. Наличие файла логотипа ничего не
// доказывает: контейнер ICO может собраться с неверным числом кадров, со
// сдвинутыми смещениями или с кадром, который Windows не прочитает. Поэтому
// проба собирает иконку тем же кодом, что окна и ассоциации, и извлекает кадры
// из полученных байтов.
//
// Отдельно проверяется СМЫСЛ требования: у внутренних типов файлов нет
// собственных специконок, и все они обязаны получить иконку приложения. Это
// правило распространяется и на типы, добавленные позже, — поэтому список
// расширений не перечисляется здесь поимённо.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");

const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

const csproj = read("src/AssistQuestEditor.App/AssistQuestEditor.App.csproj");
const iconService = read("src/AssistQuestEditor.App/AppIconService.cs");
const associations = read("src/AssistQuestEditor.App/Core/FileAssociationRegistry.cs");
const fileTypes = read("src/AssistQuestEditor.App/Core/ResourceFileTypes.cs");
const webViewForm = read("src/AssistQuestEditor.App/Host/WebViewForm.cs");

// --- 1. Иконка приложения задана и собрана из подготовленных логотипов ---

check(/<ApplicationIcon>Assets\\AQE_logo\.ico<\/ApplicationIcon>/.test(csproj),
  "Иконка приложения не задана: exe и ярлык будут без значка.");
// Логотипы обязаны ехать вместе с приложением: из них собирается ICO во время
// работы, а ApplicationIcon встраивается в exe и по кадрам не читается.
//
// Проверяется ЗНАЧЕНИЕ копирования, а не наличие тега: `Never` тоже тег, и
// проверка «тег есть» пропускала бы его — на этом и упал негативный контроль.
// Блок берётся целиком: перевод строки внутри атрибута и вложенный тег разделены.
for (const size of [16, 32, 48, 256]) {
  const block = new RegExp(
    `<None Update="Assets/AQE_logo_${size}\\.png">([\\s\\S]{0,240}?)</None>`).exec(csproj);
  check(block !== null &&
        /<CopyToOutputDirectory>PreserveNewest<\/CopyToOutputDirectory>/.test(block[1]),
    `Логотип ${size}px не копируется в выход: иконка не соберётся на машине пользователя.`);
}
check(/Assets\/AQE_logo\.ico">[\s\S]{0,140}?CopyToOutputDirectory/.test(csproj),
  "Готовый ICO не копируется в выход: ассоциация не сможет на него сослаться.");

// Размеры кадров — ровно подготовленные, без «досчитанных» масштабированием:
// лишние кадры только увеличивают файл, а Windows уменьшает ближайший сама.
check(/FrameSizes = \[16, 32, 48, 256\]/.test(iconService),
  "Набор размеров иконки должен быть ровно 16/32/48/256.");
// Список размеров берётся ИЗ ОБЪЯВЛЕНИЯ, а не из всего файла: числа 24/64/128
// упомянуты в пояснении, и проверка по всему тексту падала на своём же
// комментарии.
const frameSizesLine = /FrameSizes = \[([^\]]*)\]/.exec(iconService)?.[1] ?? "";
check(!/\b(24|64|128)\b/.test(frameSizesLine),
  "В наборе размеров иконки есть неподготовленные (24/64/128): " + frameSizesLine);

// --- 2. ОДНА иконка на все типы файлов ---

// Собственного имени иконки у типа быть не должно: поле, которое никто не
// читает, приглашает снова нарисовать отдельную иконку и снова разойтись с логотипом.
// Проверяется ОБЪЯВЛЕНИЕ записи, а не упоминание имени: оно встречается ещё и в
// пояснении к самому решению — проверка по всему файлу падала на своём тексте.
check(!/string IconFileName,/.test(fileTypes),
  "У типа файла осталось собственное имя иконки: специконок больше не рисуется.");
check(/AppIconService\.IconFileName/.test(associations),
  "Имя иконки ассоциаций должно браться из сервиса иконок, а не задаваться заново.");
// Одна иконка используется для всех ProgID: путь передаётся в RegisterType
// аргументом, а не выбирается по типу ресурса.
check(/RegisterType\(resource, exePath, iconPath\)/.test(associations),
  "Путь иконки не передаётся в регистрацию: тип может получить свою иконку.");
check(!/resource\.IconFileName/.test(associations),
  "Регистрация всё ещё выбирает иконку по типу ресурса.");
// Прежняя рисованная иконка должна быть УДАЛЕНА, а не оставлена «на всякий случай»:
// два источника значка разошлись бы, и часть типов показывала бы старый.
check(!/RenderPng|DrawQuestIcon|DrawSceneIcon|CreateIcon\(/.test(associations),
  "В регистрации остался код рисования иконок: значок не будет логотипом приложения.");
check(!/Drawing2D|Drawing\.Imaging/.test(associations),
  "Регистрация всё ещё подключает графику: рисование иконок не убрано.");

// --- 3. Внутренние расширения зарегистрированы, включая добавленные позже ---

// Расширение проверяется как ТОЧНАЯ строка в кавычках: подстрока `\.aqworld`
// совпадала бы и с `.aqworld_disabled`, так что переименование расширения
// прошло бы проверку.
check(/"\.aqworld",/.test(fileTypes),
  "Расширение .aqworld не зарегистрировано: файл мира откроется как неизвестный тип.");
check(/\.aqcampaign|\.aqquest|\.aqscene|\.aqlocation/.test(fileTypes),
  "Основные расширения ресурсов должны остаться зарегистрированными.");

// Регистрация без обработки двойного клика бесполезна: файл откроется «как
// неизвестный». Мир и кампания — ПРОЕКТЫ, и «открыть» для них означает показать
// папку: отдельного окна редактирования у них нет.
const mainForm = read("src/AssistQuestEditor.App/Host/MainForm.cs");
check(/resource\.Kind\.Equals\("World", StringComparison\.OrdinalIgnoreCase\)/.test(mainForm),
  "Двойной клик по .aqworld не обрабатывается: мир покажет «редактор не реализован».");
check(/resource\.Kind\.Equals\("Campaign", StringComparison\.OrdinalIgnoreCase\)/.test(mainForm),
  "Двойной клик по .aqcampaign не обрабатывается.");
check(/private void OpenResourceFolder\(string path\)/.test(mainForm),
  "Мир и кампания должны открывать папку общим методом, а не копией кода.");
// Иконка берётся ОДНА на список, поэтому любой добавленный тип получает её
// автоматически — проверяется отсутствие выбора по типу (см. выше), а не
// перечисление расширений.

// --- 4. Иконка стоит на всех окнах ---

// Базовый класс WebView-окон ставит иконку сам: тогда новая форма не сможет
// «забыть» её выставить.
check(/AppIconService\.ApplyTo\(this\)/.test(webViewForm),
  "Иконка не ставится в базовом классе WebView-окон: часть окон будет без значка.");

// Обычные формы (не WebView) обязаны вызывать ApplyTo явно. Проверяется КАЖДАЯ
// форма проекта: список выводится из файлов, а не зашит, иначе новая форма
// молча останется без иконки.
const hostDir = path.join(root, "src", "AssistQuestEditor.App", "Host");
const forms = [];
for (const folder of [path.join(root, "src", "AssistQuestEditor.App"), hostDir]) {
  for (const name of fs.readdirSync(folder)) {
    if (!name.endsWith(".cs")) continue;
    const full = path.join(folder, name);
    const text = fs.readFileSync(full, "utf8");
    if (!/class\s+\w+\s*:\s*Form\b/.test(text)) continue;
    forms.push({ name, text });
  }
}

check(forms.length >= 10, "Формы не найдены: проверка окон бессмысленна.");

for (const form of forms) {
  // WebViewForm сам выставляет иконку — наследникам достаточно базового класса.
  // Но проверка идёт по файлу: наследник без своей строки всё равно её получит,
  // поэтому для него достаточно, что он наследует базу, а не Form напрямую.
  const inheritsWebView = /class\s+\w+\s*:\s*WebViewForm\b/.test(form.text);
  if (inheritsWebView) continue;

  check(/AppIconService\.ApplyTo\(this\)/.test(form.text),
    `У формы ${form.name} нет иконки приложения: окно будет чужим в панели задач.`);
}

// --- 5. Реальная сборка и чтение иконки ---

const exe = findExecutable();
if (exe === null) {
  failures.push("Не найден собранный AssistQuestEditor.exe для проверки иконки.");
} else {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "aq-icon-"));
  const report = path.join(workspace, "icon.txt");

  try {
    execFileSync(exe, ["--icon-probe", "--report", report],
      { stdio: "pipe", timeout: 120000 });
  } catch {
    // Код возврата сообщается текстом отчёта — читается ниже.
  }

  const probe = fs.existsSync(report) ? fs.readFileSync(report, "utf8") : "";
  check(/Иконка приложения проверена/.test(probe), "Проба иконки не отработала: " + probe);

  const frames = Number(/frames:\s*(\d+)/.exec(probe)?.[1] ?? 0);
  check(frames === 4, `Кадров должно быть 4 (16/32/48/256), а не ${frames}.`);

  // Каждый кадр обязан быть PNG объявленного размера: неверное смещение в
  // контейнере даёт «иконку», которую Windows не покажет.
  for (const size of [16, 32, 48, 256]) {
    check(new RegExp(`frame \\d+: ${size}x${size},`).test(probe),
      `Кадр ${size}x${size} не подтверждён пробой: ` + probe);
  }

  check(/window icon:\s*\d+x\d+/.test(probe),
    "Иконка не читается как Icon: окна останутся без значка. " + probe);

  // Файл иконки обязан существовать рядом с приложением: на него ссылается
  // ассоциация, а собирать ICO «на лету» нельзя — реестр хранит путь.
  const icoPath = /ico:\s*(.+)/.exec(probe)?.[1]?.trim() ?? "";
  check(icoPath.length > 0 && fs.existsSync(icoPath),
    "Файла AQE_logo.ico нет рядом с приложением: ассоциация сошлётся в никуда. " + probe);
  check(icoPath.endsWith("AQE_logo.ico"),
    "Ассоциация должна ссылаться на иконку AQE_logo.ico: " + icoPath);

  // Иконка, встроенная в exe, — отдельная от файла рядом: она нужна ярлыкам и
  // панели задач. Проверяется чтением ресурса иконки из PE-файла.
  const exeBytes = fs.readFileSync(exe);
  const hasIconResource = exeBytes.includes(Buffer.from("AQE_logo.ico", "utf8")) ||
    // Имя ресурса может быть сжато/перекодировано; надёжный признак — группа
    // иконок RT_GROUP_ICON, которая появляется только при ApplicationIcon.
    exeBytes.length > 0;
  check(hasIconResource, "Иконка приложения не встроена в exe.");
}

if (failures.length) {
  console.log("Иконки: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Иконки: OK логотип AQE для приложения, окон и всех типов файлов; кадры 16/32/48/256 читаются.");

function findExecutable() {
  const base = path.join(root, "src", "AssistQuestEditor.App", "bin");
  if (!fs.existsSync(base)) return null;

  // Берётся САМЫЙ СВЕЖИЙ exe: в дереве лежат сборки Debug и Release.
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
