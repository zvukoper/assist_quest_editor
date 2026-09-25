// Прогон группы smoke-проверок с ОДНИМ браузером на группу.
//
// Зачем. Проверки одного окна/темы (Simulator, Editor, Map…) платили за запуск
// Chromium каждая. Групповой прогон поднимает один сервер Playwright и передаёт
// его адрес детям переменной `AQ_BROWSER_WS_ENDPOINT`; проверки подключаются к
// нему через `ci/lib/browser.mjs`.
//
// Проверки идут ПОСЛЕДОВАТЕЛЬНО и не останавливаются на первой ошибке: цель —
// увидеть ВСЕ упавшие проверки за один прогон, как это делал набор отдельных
// шагов в run_local.ps1. Итог группы — код 1, если упала хотя бы одна.
//
// Состав групп — в `ci/suites.json` (единственный источник). Скрипты, которым
// нужен собственный процесс exe или изоляция файловой системы, в группы не
// попадают и остаются отдельными проверками.

import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { chromium } from "playwright";

const root = process.cwd();
const suitesPath = path.join(root, "ci", "suites.json");
const suites = JSON.parse(fs.readFileSync(suitesPath, "utf8"));

// Аргументы: `run_suite.mjs <группа>` — прогон группы,
//            `run_suite.mjs --scripts <группа>` — печать состава группы.
const args = process.argv.slice(2);
const listOnly = args[0] === "--scripts";
const groupName = listOnly ? args[1] : args[0];

if (listOnly) {
  const group = suites.groups[groupName];
  if (!group) {
    console.error(`run_suite: неизвестная группа «${groupName}».`);
    process.exit(1);
  }
  for (const script of group.scripts) console.log(script);
  process.exit(0);
}

if (!groupName || !suites.groups[groupName]) {
  console.error("run_suite: укажите имя группы из ci/suites.json.");
  console.error("Доступные: " + Object.keys(suites.groups).join(", "));
  process.exit(1);
}

const group = suites.groups[groupName];
console.log(`=== ${group.title} (${groupName}) ===`);
console.log(`Проверок в группе: ${group.scripts.length}`);

// Сервер Playwright поднимается один раз на группу и гасится в finally:
// иначе падение проверки оставило бы висящий процесс браузера.
//
// Дети запускаются АСИНХРОННО (`spawn`, не `spawnSync`). Это не стилистика, а
// условие работоспособности: сервер Playwright живёт в ЭТОМ процессе, а ребёнок
// подключается к нему по WebSocket. `spawnSync` блокирует event loop родителя,
// поэтому родитель не может ответить на рукопожатие ребёнка — получается
// взаимная блокировка, и `spawnSync` сдаётся по таймауту `ETIMEDOUT`, хотя
// проверка не запускалась вовсе.
let server = null;
try {
  if (group.browser) {
    server = await chromium.launchServer({ headless: true });
    process.env.AQ_BROWSER_WS_ENDPOINT = server.wsEndpoint();
    console.log("Браузер группы запущен: одна сессия Chromium на все проверки.");
  }

  const failed = [];

  for (const script of group.scripts) {
    console.log("");
    console.log(`--- ${script} ---`);

    const outcome = await runScript(script);

    if (outcome.error) {
      failed.push(script);
      console.error(`FAIL ${script}: не удалось запустить (${outcome.error.message}).`);
      continue;
    }

    if (outcome.status !== 0) {
      failed.push(script);
      console.error(`FAIL ${script}: код ${outcome.status}.`);
    }
  }

  if (failed.length > 0) {
    console.error("");
    console.error(`${group.title}: упало ${failed.length} из ${group.scripts.length}`);
    for (const script of failed) console.error("  - " + script);
    process.exitCode = 1;
  } else {
    console.log("");
    console.log(`${group.title}: все ${group.scripts.length} проверок пройдены.`);
  }
} finally {
  if (server) await server.close();
}

/**
 * Запускает одну проверку и ждёт её завершения, не блокируя event loop.
 *
 * @param {string} script путь к скрипту проверки.
 * @returns {Promise<{ status: number|null, error: Error|null }>}
 */
function runScript(script) {
  return new Promise(resolve => {
    const child = spawn(process.execPath, [script], {
      cwd: root,
      stdio: "inherit",
      env: process.env
    });

    child.on("error", error => resolve({ status: null, error }));

    child.on("close", status => resolve({ status, error: null }));
  });
}

