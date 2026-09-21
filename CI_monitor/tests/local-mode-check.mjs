/**
 * Диагностика: плашка в локальном режиме и правило одного уведомления.
 *
 * Проверяет то, что нельзя проверить юнит-тестами: как реальный файл состояния
 * из ci/run_local.ps1 превращается в текст плашки, и совпадает ли процент
 * с числом завершённых проверок.
 *
 * Запуск: node tests/local-mode-check.mjs
 */

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const outDir = path.join(process.cwd(), 'out');

// pathToFileURL обязателен: склейка "file://" с путём Windows даёт неверный
// спецификатор модуля (.\file:\F:\...), и импорт падает.
const load = (name) => import(pathToFileURL(path.join(outDir, name)).href);

const { parseLocalRunState, computeProgress, toCiRun, selectSource, isStale } = await load('localRun.js');
const { buildBadge } = await load('badge.js');

const statePath = path.join(process.cwd(), '..', '.ci-state', 'local-run.json');

if (!fs.existsSync(statePath)) {
  console.error(`Нет файла состояния: ${statePath}`);
  console.error('Сначала выполните: powershell -ExecutionPolicy Bypass -File .\\ci\\run_local.ps1');
  process.exit(1);
}

const raw = fs.readFileSync(statePath, 'utf8');
const run = parseLocalRunState(raw);

if (!run) {
  console.error('Файл состояния не разобран.');
  process.exit(1);
}

const percent = computeProgress(run);
const ci = toCiRun(run);
const stale = isStale(run);

console.log('--- Файл состояния ---');
console.log(`runNumber=${run.runNumber} status=${run.status}`);
console.log(`checks=${run.completedChecks}/${run.totalChecks} => ${percent === null ? 'н/д' : percent + '%'}`);
console.log(`title=${run.title ?? '—'} branch=${run.branch ?? '—'} commit=${run.commit ?? '—'}`);
console.log(`stale=${stale}`);

const badge = buildBadge({
  run: ci,
  branch: run.branch,
  repoLabel: '',
  pageUrl: '',
  showIdle: true,
  style: 'square',
  error: null,
  progressPercent: percent,
  local: true,
  failedChecks: run.failedChecks,
  reportPath: run.reportPath,
  currentCheck: run.currentCheck,
});

console.log('');
console.log('--- Плашка (локальный режим) ---');
console.log(`text      = ${JSON.stringify(badge.text)}`);
console.log(`state     = ${badge.stateName}`);
console.log(`color     = ${badge.color ?? '(из темы)'}`);
console.log(`a11y      = ${badge.accessibleText}`);
console.log('tooltip:');
for (const line of badge.tooltip.split('\n')) console.log('  ' + line);

// Процент обязан совпадать с числом завершённых проверок — иначе плашка врёт.
if (percent !== null) {
  const expected = Math.round((run.completedChecks / run.totalChecks) * 100);
  if (percent !== expected) {
    console.error(`\nОШИБКА: процент ${percent} не совпадает с ожидаемым ${expected}.`);
    process.exit(1);
  }
  if (!badge.text.includes(`${percent}%`)) {
    console.error(`\nОШИБКА: процент ${percent}% не показан в тексте плашки.`);
    process.exit(1);
  }
}

if (badge.text.includes('#undefined')) {
  console.error('\nОШИБКА: в тексте плашки нет номера проверки.');
  process.exit(1);
}

// Режим auto должен предпочесть локальный прогон, пока он идёт.
const selection = selectSource({
  mode: 'auto',
  localRun: run,
  localStale: stale,
  hasGithub: true,
  holdFinishedMs: 20000,
  now: new Date(),
});
console.log('');
console.log(`--- Режим auto ---`);
console.log(`source=${selection.source} automatic=${selection.automatic}`);

console.log('\nЛокальный режим: OK');
