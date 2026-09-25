// Общий браузер для группы smoke-проверок.
//
// Зачем. Каждый smoke-файл сам открывал Chromium (`chromium.launch`), поэтому
// группа из восьми проверок платила за запуск браузера восемь раз. Прогон
// `ci/lib/run_suite.mjs` поднимает ОДИН сервер Playwright и передаёт его
// адрес детям через `AQ_BROWSER_WS_ENDPOINT`; проверка подключается к нему
// (`chromium.connect`) и НЕ закрывает его за собой.
//
// Правило пользования: браузер закрывает ТОЛЬКО тот, кто его открыл. Поэтому
// `closeBrowser` закрывает соединение лишь тогда, когда браузер был запущен
// здесь же (нет переменной окружения). Иначе первый же завершившийся smoke
// погасил бы браузер для всех остальных.
//
// Запуск в одиночку остаётся прежним: без `AQ_BROWSER_WS_ENDPOINT` проверка
// работает как раньше, сама поднимая и закрывая браузер.

import { chromium } from "playwright";

/**
 * Соединение с общим браузером группы — одно на процесс, сколько бы раз
 * проверка ни просила браузер.
 *
 * Ловушка, из-за которой это появилось: `chromium.connect()` открывает WebSocket
 * и держит event loop процессa живым. Проверка завершалась, печатала итог, но
 * процесс не мог выйти, пока открыто соединение, — и групповой прогон получал от
 * `spawnSync` таймаут `ETIMEDOUT`, хотя сама проверка прошла.
 */
let sharedConnection = null;

/** Браузер, запущенный ЭТИМ процессом (его и только его нужно закрывать). */
let ownedBrowser = null;

/**
 * Открывает браузер: подключается к общему серверу группы или запускает свой.
 *
 * @param {object} [options] параметры запуска (игнорируются при подключении).
 * @returns {Promise<{ browser: import("playwright").Browser, owned: boolean }>}
 */
export async function openBrowser(options = { headless: true }) {
  const endpoint = process.env.AQ_BROWSER_WS_ENDPOINT;

  if (endpoint) {
    if (!sharedConnection) {
      sharedConnection = await chromium.connect(endpoint);
    }
    return { browser: sharedConnection, owned: false };
  }

  if (!ownedBrowser) {
    ownedBrowser = await chromium.launch(options);
  }
  return { browser: ownedBrowser, owned: true };
}

/**
 * Освобождает браузер и — обязательно — закрывает соединение.
 *
 * Подключённый браузер как процесс НЕ закрывается: он общий для всей группы.
 * Но соединение закрывать надо: иначе процесс проверки не завершится.
 */
export async function closeBrowser(browser) {
  if (!browser) return;

  if (browser === sharedConnection) {
    await sharedConnection.close();
    sharedConnection = null;
    return;
  }

  if (browser === ownedBrowser) {
    await ownedBrowser.close();
    ownedBrowser = null;
  }
}
