import fs from "node:fs";
import path from "node:path";

// Проверка привязки профиля WebView2 к отпечатку сборки.
//
// Зачем. WebView2 держит дисковый кеш подресурсов в своём профиле и отдаёт файл
// по полному URL, не сверяя содержимое с диском. Хост версионирует только адрес
// САМОЙ страницы (`WebViewForm`: `?v=` на `simulator.html`), а ссылки на
// `simulator.js` и `theme.css` версионируются в разметке. Поэтому устаревший
// токен означал, что правка скрипта не доезжала до пользователя: браузер
// возвращал старый файл из кеша. Ровно это и наблюдалось.
//
// Чистка профиля проблему не решала: каталог, который держит живой процесс
// WebView2, Windows удалить не даёт, поэтому шаг «удалить профиль» в compile.ps1
// молча не срабатывал, а при обычной отладке профиль не чистился вовсе.
//
// Защита — привязка профиля к отпечатку: каталог профиля включает хеш содержимого
// Web-ресурсов и версию сборки, поэтому изменённый скрипт открывает ДРУГОЙ пустой
// профиль, и старый кеш физически не может быть использован.
//
// Проверка статическая: она следит, что связка не распалась при будущей правке.
// Ключевое утверждение — приложение обязано считать профиль ПО ОТПЕЧАТКУ, а не по
// постоянному пути; иначе защита исчезает незаметно для всех остальных проверок.

const root = process.cwd();
const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

const read = relative => fs.readFileSync(path.join(root, relative), "utf8");

// 1. Правило отпечатка живёт в Domain (покрыто тестами), а не в форме.
const stampSource = read("src/AssistQuestEditor.Domain/WebBuildStamp.cs");
check(
  /public static string Compute\(/.test(stampSource),
  "WebBuildStamp.Compute не найден: приложение потеряло общее правило отпечатка"
);
check(
  /public static string ComputeForDirectory\(/.test(stampSource),
  "WebBuildStamp.ComputeForDirectory не найден: отпечаток по каталогу не считается"
);
check(
  /public static string\? FindStampFile\(/.test(stampSource),
  "WebBuildStamp.FindStampFile не найден: файл отпечатка рядом с EXE не ищется"
);

// 2. Профиль обязан выбираться по отпечатку.
const profileSource = read("src/AssistQuestEditor.App/Core/WebViewProfile.cs");
check(
  /CurrentProfilePath/.test(profileSource),
  "WebViewProfile.CurrentProfilePath отсутствует: путь профиля больше не зависит от отпечатка"
);
check(
  /WebBuildStamp\.ComputeForDirectory\(/.test(profileSource),
  "WebViewProfile не считает отпечаток по содержимому Web: отладочный запуск снова использует общий кеш"
);

// 3. Форма обязана брать путь профиля из WebViewProfile. Постоянный путь означал бы,
//    что вся защита отпечатка не подключена — это и есть исходный дефект.
const formSource = read("src/AssistQuestEditor.App/Host/WebViewForm.cs");
check(
  /UserDataFolder = GetWebViewUserDataFolder\(\)/.test(formSource),
  "WebViewForm больше не задаёт UserDataFolder: профиль WebView2 не управляется приложением"
);
check(
  /return WebViewProfile\.CurrentProfilePath;/.test(formSource),
  "WebViewForm использует постоянный путь профиля вместо отпечатка: кеш подресурсов переживёт пересборку"
);
check(
  !/return AppPaths\.WebViewUserDataRoot;/.test(formSource),
  "WebViewForm вернулся к постоянному профилю (AppPaths.WebViewUserDataRoot): старая защита от кеша не работает"
);

// 4. Профиль готовится ДО создания окон, иначе первое окно откроет старый путь.
const programSource = read("src/AssistQuestEditor.App/Program.cs");
check(
  /WebViewProfile\.Prepare\(\);/.test(programSource),
  "Program не вызывает WebViewProfile.Prepare: профиль не создаётся под текущий отпечаток"
);
check(
  /args\.Any\(arg => string\.Equals\(arg, "--write-web-stamp"/.test(programSource),
  "Program не поддерживает --write-web-stamp: сборка не может записать отпечаток"
);
check(
  /WriteWebStampFromCommandLine/.test(programSource),
  "Program не содержит WriteWebStampFromCommandLine: режим записи отпечатка отсутствует"
);

// 5. Сборка обязана вызывать этот режим и признавать файл отпечатка законным.
const compileSource = read("compile.ps1");
check(
  /--write-web-stamp/.test(compileSource),
  "compile.ps1 не запрашивает отпечаток у приложения: профиль остался без привязки к сборке"
);
check(
  /'web-build\.stamp'/.test(compileSource),
  "compile.ps1 не ожидает файл web-build.stamp в составе публикации"
);

// 6. Проверка состава публикации обязана пропускать файл отпечатка, иначе сборка
//    падает на исправном каталоге.
const publishCheck = read("ci/check_publish_resources.ps1");
check(
  /'web-build\.stamp'/.test(publishCheck),
  "check_publish_resources.ps1 не знает про web-build.stamp: проверка состава упадёт на исправной сборке"
);

// 7. Обе стороны не должны считать отпечаток по-своему. Своя формула в PowerShell
//    разошлась бы с приложением, и профиль открывался бы по чужому пути.
check(
  !/Get-FileHash -LiteralPath[^\n]*-Algorithm SHA256/.test(compileSource),
  "compile.ps1 считает отпечаток сам: нужна одна реализация (WebBuildStamp) в приложении"
);

if (failures.length > 0) {
  for (const failure of failures) console.error("  FAIL: " + failure);
  console.error(`WebView2 profile stamp smoke: ПРОВАЛЕНО (${failures.length})`);
  process.exit(1);
}

console.log("WebView2 profile stamp smoke: OK (профиль привязан к отпечатку сборки)");
