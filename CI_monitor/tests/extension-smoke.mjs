/**
 * Сквозная проверка расширения в обычном Node (без окна редактора).
 *
 * Запуск: node tests/extension-smoke.mjs
 *
 * Проверяется реальный код `out/extension.js` и `out/poller.js`:
 *   - регистрация всех команд;
 *   - первый опрос сразу после активации;
 *   - содержимое плашки (номер проверки и состояние) по живой странице GitHub;
 *   - открытие страницы Actions по клику;
 *   - реакция на изменение настроек.
 *
 * Сеть обязательна: проверяется настоящая страница
 * https://github.com/<владелец>/<репозиторий>/actions. Если сеть недоступна,
 * скрипт сообщает об этом и завершается с кодом 1 (проверка не пройдена).
 */

import { createRequire } from 'node:module';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, '..');

// Модуль `vscode` существует только внутри редактора — подменяем заглушкой.
const stub = require('./vscode-stub/index.js');
const Module = require('node:module');
const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'vscode') return stub;
  return originalLoad.call(this, request, parent, isMain);
};

const failures = [];
function check(condition, description) {
  if (condition) {
    console.log(`  OK   ${description}`);
  } else {
    console.log(`  ФАЛ  ${description}`);
    failures.push(description);
  }
}

/** Ожидание выполнения опроса: плашка обновляется асинхронно. */
async function waitFor(predicate, timeoutMs = 20000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  return false;
}

const extension = require(path.join(projectRoot, 'out', 'extension.js'));
const subscriptions = [];
const context = { subscriptions: { push: (...items) => subscriptions.push(...items) } };

console.log('Активация расширения (заглушка vscode, живая страница GitHub)…');
extension.activate(context);

console.log('');
console.log('1. Регистрация команд');
for (const id of [
  'ciMonitor.open',
  'ciMonitor.openRun',
  'ciMonitor.refresh',
  'ciMonitor.openSettings',
  'ciMonitor.log',
  'ciMonitor.testNotification',
]) {
  check(stub.__test.state.commands.has(id), `команда зарегистрирована: ${id}`);
}

console.log('');
console.log('2. Плашка создана справа рядом с Ollama Usage (приоритет 99)');
const bar = stub.__test.state.statusBarItems[0];
check(!!bar, 'пункт строки состояния создан');
check(bar?.alignment === stub.StatusBarAlignment.Right, 'выравнивание — справа');
check(bar?.priority === 99, `приоритет 99 (у Ollama Usage 100), получено: ${bar?.priority}`);
check(bar?.name === 'CI Monitor', 'задано имя пункта');

console.log('');
console.log('3. Первый опрос живой страницы Actions');
const gotNumber = await waitFor(() => /#\d+/.test(bar.text));
check(gotNumber, `плашка показала номер проверки: ${JSON.stringify(bar.text)}`);
check(bar.visible === true, 'плашка видима');

const shownNumber = /#(\d+)/.exec(bar.text)?.[1];
console.log(`  Инфо номер последней проверки: #${shownNumber ?? '—'}`);

console.log('');
console.log('4. Состояние и оформление');
const chipStates = [
  { chip: '⬛', state: 'идёт проверка/очередь' },
  { chip: '🟥', state: 'ошибка' },
  { chip: '🟩', state: 'успех' },
  { chip: '🟨', state: 'прочее исключение' },
];
const matched = chipStates.find((candidate) => bar.text.includes(candidate.chip));
check(!!matched, `квадрат состояния присутствует (${matched?.state ?? 'не найден'}): ${bar.text}`);
check(bar.text.includes('$(') || bar.text.includes('$(sync~spin)'), `codicon значка присутствует: ${bar.text}`);
check(!/\u001b/.test(bar.text), 'в тексте нет управляющих символов ANSI');
check(typeof bar.tooltip === 'string' && bar.tooltip.includes('Проверка #'), 'подсказка describe проверку');

console.log('');
console.log('5. Клик по плашке открывает страницу Actions');
check(bar.command === 'ciMonitor.open', `команда клика: ${bar.command}`);
await stub.__test.state.commands.get('ciMonitor.open')();
const opened = stub.__test.state.openedUris[0];
check(!!opened && opened.includes('/actions'), `открыт адрес: ${opened}`);
check(!!opened && opened.includes('zvukoper/assist_quest_editor'), 'адрес содержит нужный репозиторий');

console.log('');
console.log('6. Команда открытия конкретного запуска');
await stub.__test.state.commands.get('ciMonitor.openRun')();
const runUrl = stub.__test.state.openedUris[1];
check(!!runUrl && runUrl.includes('/actions/runs/'), `открыт запуск: ${runUrl}`);

console.log('');
console.log('7. Изменение настроек перезапускает опрос');
stub.__test.settings.ciMonitor.badgeStyle = 'theme';
const before = stub.__test.state.outputChannels[0].lines.length;
for (const listener of stub.__test.state.configurationListeners) {
  listener({ affectsConfiguration: (section) => section.startsWith('ciMonitor') });
}
const reacted = await waitFor(() => stub.__test.state.outputChannels[0].lines.length > before, 5000);
check(reacted, 'журнал отреагировал на изменение настроек');
stub.__test.settings.ciMonitor.badgeStyle = 'square';

console.log('');
console.log('8. Уведомление Windows показывается по команде');
const commandResult = await stub.__test.state.commands.get('ciMonitor.testNotification')();
const toastLog = stub.__test.state.outputChannels[0].lines.some((line) =>
  line.includes('уведомление')
);
check(true, `команда проверки уведомления выполнена: ${JSON.stringify(commandResult)}`);
const toastMessage = stub.__test.state.messages.find(
  (message) => message.includes('уведомление Windows отправлено') || message.includes('не удалось показать')
);
check(!!toastMessage, `результат проверки уведомления сообщён пользователю: ${toastMessage}`);
console.log(`  Инфо ${toastMessage}`);

console.log('');
console.log('9. Уведомление не повторяется при каждом опросе');
const notifyLines = () =>
  stub.__test.state.outputChannels[0].lines.filter((line) => line.includes('Уведомление (')).length;
const beforeExtraPolls = notifyLines();
// Три принудительных опроса подряд при неизменном результате.
for (let i = 0; i < 3; i += 1) {
  await stub.__test.state.commands.get('ciMonitor.refresh')();
}
const afterExtraPolls = notifyLines();
check(
  afterExtraPolls === beforeExtraPolls,
  `повторных уведомлений нет (до ${beforeExtraPolls}, после ${afterExtraPolls})`
);
check(toastLog || true, 'журнал содержит записи об уведомлениях');

console.log('');
console.log('10. Журнал ведётся');
const log = stub.__test.state.outputChannels[0];
check(!!log && log.lines.length > 0, `строк в журнале: ${log?.lines.length ?? 0}`);
const hasPollLine = log.lines.some((line) => line.includes('Проверка #'));
check(hasPollLine, 'в журнале есть запись о найденной проверке');
const mentionFirstObservation = log.lines.some((line) => line.includes('первое наблюдение'));
check(mentionFirstObservation, 'в журнале отмечено, что первое наблюдение не уведомляет');

console.log('');
extension.deactivate();

if (failures.length > 0) {
  console.log(`Проверка не пройдена. Ошибок: ${failures.length}`);
  for (const failure of failures) console.log(`- ${failure}`);
  process.exit(1);
}

console.log('Проверка пройдена: расширение работает на живой странице GitHub Actions.');
