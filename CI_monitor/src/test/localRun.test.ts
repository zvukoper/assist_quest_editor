/**
 * Проверки локального режима: разбор состояния, прогресс числом проверок,
 * перевод в общий тип проверки и выбор источника данных.
 *
 * Логика не зависит от `vscode`, поэтому проверяется в обычном Node — так же,
 * как парсер страницы Actions и правила плашки.
 */

import { strict as assert } from 'node:assert';
import { test } from 'node:test';

import {
  createLocalRun,
  formatDuration,
  formatProgress,
  isFinished,
  isStale,
  parseLocalRunState,
  parseMode,
  selectSource,
  serializeLocalRun,
  shouldNotifyLocalRun,
  toCiRun,
  LocalRun,
} from '../localRun';

const NOW = new Date('2026-09-21T12:00:00Z');

/** Состояние локального прогона с разумными значениями по умолчанию. */
function run(overrides: Partial<LocalRun> = {}): LocalRun {
  return {
    runNumber: 7,
    status: 'in_progress',
    totalChecks: 10,
    completedChecks: 4,
    currentCheck: 'Quest Graph Playwright smoke',
    failedChecks: [],
    startedAt: '2026-09-21T11:59:30Z',
    finishedAt: null,
    branch: 'main',
    commit: '360a38e',
    title: 'main · 360a38e',
    updatedAt: '2026-09-21T12:00:00Z',
    reportPath: null,
    ...overrides,
  };
}

test('разбор файла состояния: основные поля', () => {
  const parsed = parseLocalRunState(JSON.stringify({
    runNumber: 12,
    status: 'in_progress',
    totalChecks: 11,
    completedChecks: 3,
    currentCheck: 'Тесты домена',
    failedChecks: [],
    startedAt: '2026-09-21T09:41:08Z',
    branch: 'main',
    commit: 'abc1234',
    title: 'main · abc1234',
    updatedAt: '2026-09-21T09:41:20Z',
  }));

  assert.ok(parsed);
  assert.equal(parsed.runNumber, 12);
  assert.equal(parsed.status, 'in_progress');
  assert.equal(parsed.totalChecks, 11);
  assert.equal(parsed.completedChecks, 3);
  assert.equal(parsed.currentCheck, 'Тесты домена');
  assert.equal(parsed.commit, 'abc1234');
});

test('разбор файла состояния: мусор не бросает исключение', () => {
  assert.equal(parseLocalRunState('not json'), null);
  assert.equal(parseLocalRunState('[]'), null);
  assert.equal(parseLocalRunState('"строка"'), null);
  assert.equal(parseLocalRunState('{}'), null, 'без runNumber состояние бессмысленно');
  assert.equal(parseLocalRunState('{"runNumber":0}'), null);
  assert.equal(parseLocalRunState('{"runNumber":-5}'), null);
});

test('разбор файла состояния: неизвестный статус и отрицательный прогресс', () => {
  const parsed = parseLocalRunState(JSON.stringify({
    runNumber: 3,
    status: 'что-то-новое',
    totalChecks: 5,
    completedChecks: -100,
  }));

  assert.ok(parsed);
  assert.equal(parsed.status, 'in_progress', 'неизвестный статус подменяется безопасным');
  assert.equal(parsed.completedChecks, 0, 'отрицательный прогресс обрезается');
});

test('разбор файла состояния: completed не превышает total', () => {
  const parsed = parseLocalRunState(JSON.stringify({
    runNumber: 3,
    status: 'success',
    totalChecks: 5,
    completedChecks: 99,
  }));

  assert.ok(parsed);
  assert.equal(parsed.completedChecks, 5);
  assert.equal(formatProgress(parsed), '5/5');
});

test('прогресс показывается числом пройденных проверок, а не процентами', () => {
  assert.equal(formatProgress(run({ totalChecks: 90, completedChecks: 0 })), '0/90');
  assert.equal(formatProgress(run({ totalChecks: 70, completedChecks: 5 })), '5/70');
  assert.equal(formatProgress(run({ completedChecks: 4 })), '4/10');
  assert.doesNotMatch(formatProgress(run({ completedChecks: 4 }))!, /%/, 'проценты не возвращаются');
});

test('пройденных больше общего не показывается', () => {
  const parsed = parseLocalRunState(JSON.stringify({
    runNumber: 3,
    status: 'in_progress',
    totalChecks: 11,
    completedChecks: 40,
  }));

  assert.ok(parsed);
  assert.equal(parsed.completedChecks, 11, 'разбор уже обрезает значение по общему числу');
  assert.equal(formatProgress(parsed), '11/11');
});

test('прогресс не выдумывается, когда общее число проверок неизвестно', () => {
  const unknown = run({ totalChecks: 0, completedChecks: 0 });
  assert.equal(formatProgress(unknown), null, 'лучше пусто, чем выдуманное значение');

  const local = createLocalRun(1);
  assert.equal(formatProgress(local), null);
});

test('в очереди прогресс показывает ноль пройденных', () => {
  assert.equal(formatProgress(run({ status: 'queued', totalChecks: 11, completedChecks: 0 })), '0/11');
});

test('перевод в общий тип проверки: префикс runId отделяет локальные прогоны', () => {
  const ci = toCiRun(run({ completedChecks: 4 }), NOW);

  assert.equal(ci.runNumber, 7);
  assert.equal(ci.runId, 'local:7');
  assert.equal(ci.status, 'in_progress');
  assert.equal(ci.workflowName, 'Локальный прогон');
  assert.equal(ci.branch, 'main');
  assert.equal(ci.commitSha, '360a38e');
  assert.equal(ci.runUrl, '', 'у локального прогона нет URL запуска на GitHub');
  assert.equal(ci.duration, '30s');
});

test('перевод в общий тип проверки: состояния переносятся без потерь', () => {
  assert.equal(toCiRun(run({ status: 'queued' }), NOW).status, 'queued');
  assert.equal(toCiRun(run({ status: 'in_progress' }), NOW).status, 'in_progress');
  assert.equal(toCiRun(run({ status: 'success' }), NOW).status, 'success');
  assert.equal(toCiRun(run({ status: 'failure' }), NOW).status, 'failure');
  assert.equal(toCiRun(run({ status: 'cancelled' }), NOW).status, 'cancelled');
});

test('длительность считается по завершению, а для идущего прогона — до сейчас', () => {
  assert.equal(formatDuration('2026-09-21T11:59:00Z', '2026-09-21T12:01:12Z'), '2m 12s');
  assert.equal(formatDuration('2026-09-21T11:59:58Z', '2026-09-21T12:00:00Z'), '2s');
  assert.equal(formatDuration('2026-09-21T11:59:30Z', null, NOW), '30s');
  assert.equal(formatDuration(null, null), null);
  assert.equal(formatDuration('не дата', null), null);
});

test('завершённость прогона', () => {
  assert.equal(isFinished(run({ status: 'success' })), true);
  assert.equal(isFinished(run({ status: 'failure' })), true);
  assert.equal(isFinished(run({ status: 'cancelled' })), true);
  assert.equal(isFinished(run({ status: 'in_progress' })), false);
  assert.equal(isFinished(run({ status: 'queued' })), false);
});

test('уведомление о локальном прогоне: успех не уведомляется', () => {
  // Проверки прошли — приложение соберётся и запустится, отдельное
  // уведомление не нужно и только отвлекало бы.
  assert.equal(shouldNotifyLocalRun(run({ status: 'success' })), false);

  assert.equal(shouldNotifyLocalRun(run({ status: 'failure' })), true);
  assert.equal(shouldNotifyLocalRun(run({ status: 'cancelled' })), true);

  assert.equal(shouldNotifyLocalRun(run({ status: 'in_progress' })), false);
  assert.equal(shouldNotifyLocalRun(run({ status: 'queued' })), false,
    'о ещё не завершённом прогоне уведомлять нечего');
});

test('зависший прогон: идущий без обновлений', () => {
  const stale = run({ status: 'in_progress', updatedAt: '2026-09-21T11:40:00Z' });
  assert.equal(isStale(stale, NOW), true);

  const fresh = run({ status: 'in_progress', updatedAt: '2026-09-21T11:59:55Z' });
  assert.equal(isStale(fresh, NOW), false);

  const finished = run({ status: 'success', updatedAt: '2026-09-21T10:00:00Z' });
  assert.equal(isStale(finished, NOW), false, 'завершённый прогон зависнуть не может');
});

test('сериализация и обратный разбор сохраняют состояние', () => {
  const original = run({
    status: 'failure',
    failedChecks: ['Тесты домена', 'Синтаксис web JavaScript'],
    finishedAt: '2026-09-21T12:00:00Z',
    reportPath: 'MemoryAI/LOGS/CI_errors.md',
  });

  const parsed = parseLocalRunState(serializeLocalRun(original));
  assert.ok(parsed);
  assert.equal(parsed.runNumber, original.runNumber);
  assert.equal(parsed.status, 'failure');
  assert.deepEqual(parsed.failedChecks, original.failedChecks);
  assert.equal(parsed.reportPath, 'MemoryAI/LOGS/CI_errors.md');
});

test('разбор режима: неизвестное значение даёт auto', () => {
  assert.equal(parseMode('github'), 'github');
  assert.equal(parseMode('LOCAL'), 'local');
  assert.equal(parseMode('auto'), 'auto');
  assert.equal(parseMode(''), 'auto');
  assert.equal(parseMode(undefined), 'auto');
  assert.equal(parseMode('что-то'), 'auto');
});

test('выбор источника: явные режимы игнорируют данные', () => {
  const local = run();

  assert.equal(
    selectSource({ mode: 'github', localRun: local, localStale: false, hasGithub: true, holdFinishedMs: 20000 }).source,
    'github'
  );
  assert.equal(
    selectSource({ mode: 'local', localRun: null, localStale: false, hasGithub: true, holdFinishedMs: 20000 }).source,
    'local'
  );
});

test('режим auto: идущий локальный прогон побеждает GitHub', () => {
  const selection = selectSource({
    mode: 'auto',
    localRun: run({ status: 'in_progress' }),
    localStale: false,
    hasGithub: true,
    holdFinishedMs: 20000,
    now: NOW,
  });

  assert.equal(selection.source, 'local');
  assert.equal(selection.automatic, true, 'режим выбран автоматически');
});

test('режим auto: завершённый прогон держится localHoldSeconds, затем возвращается к GitHub', () => {
  const finished = run({ status: 'success', finishedAt: '2026-09-21T11:59:55Z' });

  const held = selectSource({
    mode: 'auto',
    localRun: finished,
    localStale: false,
    hasGithub: true,
    holdFinishedMs: 20000,
    now: NOW,
  });
  assert.equal(held.source, 'local', 'прошло 5 секунд из 20');

  const released = selectSource({
    mode: 'auto',
    localRun: finished,
    localStale: false,
    hasGithub: true,
    holdFinishedMs: 20000,
    now: new Date('2026-09-21T12:00:30Z'),
  });
  assert.equal(released.source, 'github', 'после удержания плашка возвращается к GitHub');
  assert.equal(released.automatic, false);
});

test('режим auto: зависший прогон источником не считается', () => {
  const selection = selectSource({
    mode: 'auto',
    localRun: run({ status: 'in_progress' }),
    localStale: true,
    hasGithub: true,
    holdFinishedMs: 20000,
    now: NOW,
  });

  assert.equal(selection.source, 'github');
});

test('режим auto без файла состояния показывает GitHub', () => {
  const selection = selectSource({
    mode: 'auto',
    localRun: null,
    localStale: false,
    hasGithub: true,
    holdFinishedMs: 20000,
    now: NOW,
  });

  assert.equal(selection.source, 'github');
});
