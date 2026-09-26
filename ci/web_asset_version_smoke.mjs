import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

// Проверка версий web-ресурсов.
//
// Зачем. Страница Симулятора ссылается на скрипты и стили с параметром `?v=`.
// `WebView2` кеширует под-ресурсы по полному URL, а хост подставляет версию
// только в адрес САМОЙ страницы (`WebViewForm`), не в ссылки на неё. Поэтому
// правка `simulator.js` без смены `?v=` в `simulator.html` не доезжала до
// пользователя: WebView2 отдавал старый файл из своего кеша на диске.
//
// Ошибка эта невидима в CI (проверки читают файлы напрямую) и не воспроизводима
// в опубликованной сборке (`compile.ps1` чистит профиль WebView2), поэтому её
// нужно ловить статически — здесь.
//
// Правило: `?v=` обязан содержать первые 8 символов SHA-256 самого файла.
// Тогда любое изменение ресурса требует смены токена, и забыть её нельзя:
// проверка падает на конкретном файле, показывая нужное значение.
//
// Токен не пишут руками: `node ci/web_asset_version_smoke.mjs --write`
// проставляет его во всех страницах. Проверка в CI только читает.

const root = process.cwd();
const webRoot = path.join(root, "src", "AssistQuestEditor.App", "Web");
const write = process.argv.includes("--write");

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

/** Первые 8 символов SHA-256 файла. */
function assetToken(filePath) {
  const hash = crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
  return hash.slice(0, 8);
}

/** Ссылки на под-ресурсы: src="..." и href="..." внутри web-страниц. */
function subResources(html) {
  const found = [];
  const pattern = /\b(?:src|href)\s*=\s*"([^"]+)"/g;
  let match;
  while ((match = pattern.exec(html)) !== null) {
    const url = match[1];
    // Внешние адреса и якоря версионируются не нами.
    if (/^(?:[a-z]+:|\/\/|#|data:|mailto:)/i.test(url)) continue;
    const file = url.split("?")[0].split("#")[0];
    if (!/\.(?:js|css)$/i.test(file)) continue;
    found.push({ url, file, query: url.includes("?") ? url.split("?")[1] : "" });
  }
  return found;
}

const pages = fs.readdirSync(webRoot).filter(name => name.endsWith(".html")).sort();
check(pages.length > 0, "в Web нет ни одной html-страницы");

let checked = 0;
let updated = 0;

/**
 * Проставляет актуальные токены в ссылки страницы.
 *
 * Замена идёт по полному `file?query`: один и тот же ресурс встречается в
 * странице несколько раз, и токен обязан совпасть во всех ссылках.
 */
function rewritePage(page, html) {
  const replacements = [];

  for (const resource of subResources(html)) {
    const filePath = path.join(webRoot, resource.file);
    if (!fs.existsSync(filePath)) {
      failures.push(`${page}: ресурс «${resource.file}» не найден`);
      continue;
    }

    const expected = assetToken(filePath);
    const parameter = new URLSearchParams(resource.query).get("v");
    if (parameter === expected) continue;

    replacements.push({
      from: resource.url,
      to: `${resource.file}?v=${expected}`
    });
  }

  if (!replacements.length) return html;

  let result = html;
  for (const replacement of replacements) {
    result = result.split(replacement.from).join(replacement.to);
    updated += 1;
  }

  fs.writeFileSync(path.join(webRoot, page), result, "utf8");
  return result;
}

for (const page of pages) {
  let html = fs.readFileSync(path.join(webRoot, page), "utf8");
  if (write) html = rewritePage(page, html);

  for (const resource of subResources(html)) {
    const filePath = path.join(webRoot, resource.file);

    if (!fs.existsSync(filePath)) {
      // Ссылка на ресурс, которого нет, — отдельная поломка, и её видно только
      // здесь: страница молча останется без скрипта.
      failures.push(`${page}: ресурс «${resource.file}» не найден`);
      continue;
    }

    const parameter = new URLSearchParams(resource.query).get("v");
    if (!parameter) {
      failures.push(
        `${page}: у «${resource.file}» нет параметра ?v= — ` +
        `WebView2 отдаст закешированную версию. Ожидается: ` +
        `${resource.file}?v=${assetToken(filePath)}`);
      continue;
    }

    const expected = assetToken(filePath);
    if (parameter !== expected) {
      failures.push(
        `${page}: устаревший ?v= у «${resource.file}»: ${parameter} → ${expected} ` +
        `(версия не менялась после правки файла). ` +
        `Исправляется командой: node ci/web_asset_version_smoke.mjs --write`);
      continue;
    }

    checked += 1;
  }
}

if (write && updated > 0) {
  console.log(`Web asset version smoke: обновлено ссылок ${updated}`);
}

check(checked > 0, "не проверено ни одной версии ресурса — сломан разбор страниц");

if (failures.length) {
  throw new Error("Web asset version smoke: " + failures.join("; "));
}

console.log(`Web asset version smoke: OK (${checked} ресурсов)`);
