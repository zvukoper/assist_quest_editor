/**
 * Проверки логики уведомлений: решение о показе и текст сообщения.
 *
 * Ключевое требование — уведомление приходит при СМЕНЕ состояния и не
 * повторяется при каждом опросе (опрос идёт каждые 10 секунд).
 */

import { strict as assert } from 'node:assert';
import { test } from 'node:test';

import {
  NotificationSettings,
  NotificationTracker,
  buildLocalNotification,
  buildNotification,
  parseNotifyStatuses,
  parseNotifyTarget,
  truncate,
  usesVsCode,
  usesWindows,
} from '../notifications';
import { CiRun, CiRunStatus } from '../types';
import { LocalRun, shouldNotifyLocalRun } from '../localRun';

function run(status: CiRunStatus, runId = '35559840717', runNumber = 147): CiRun {
  return {
    runNumber,
    runId,
    runUrl: `https://github.com/zvukoper/assist_quest_editor/actions/runs/${runId}`,
    workflowName: 'Проверки',
    statusLabel: 'status:',
    status,
    branch: 'main',
    commitSha: 'b905b351106db6cec97eb36041d7fd1f3e99bae7',
    title: 'feat: вынести Runtime в левую панель симулятора',
    duration: '2m 2s',
    startedAt: '2026-09-21T04:07:46Z',
  };
}

function settings(overrides: Partial<NotificationSettings> = {}): NotificationSettings {
  return {
    target: 'windows',
    statuses: ['failure', 'success'],
    notifyOnStartup: false,
    ...overrides,
  };
}

test('parseNotifyTarget: допустимые значения и запасной вариант', () => {
  assert.equal(parseNotifyTarget('windows'), 'windows');
  assert.equal(parseNotifyTarget('vscode'), 'vscode');
  assert.equal(parseNotifyTarget('both'), 'both');
  assert.equal(parseNotifyTarget('off'), 'off');
  assert.equal(parseNotifyTarget('WINDOWS'), 'windows');
  // Неизвестное значение не должно молча выключать уведомления.
  assert.equal(parseNotifyTarget('telegram'), 'windows');
  assert.equal(parseNotifyTarget(''), 'windows');
});

test('parseNotifyStatuses: фильтрация и пустой список', () => {
  assert.deepEqual(parseNotifyStatuses(['failure', 'success']), ['failure', 'success']);
  assert.deepEqual(parseNotifyStatuses(['failure']), ['failure']);
  assert.deepEqual(parseNotifyStatuses([]), []);
  assert.deepEqual(parseNotifyStatuses(['FAILURE', 'мусор']), ['failure']);
  // Неверный тип возвращает значение по умолчанию.
  assert.deepEqual(parseNotifyStatuses('failure'), ['failure', 'success']);
  assert.deepEqual(parseNotifyStatuses(undefined), ['failure', 'success']);
});

test('usesVsCode / usesWindows: маршрутизация доставки', () => {
  assert.equal(usesWindows('windows'), true);
  assert.equal(usesVsCode('windows'), false);
  assert.equal(usesVsCode('vscode'), true);
  assert.equal(usesWindows('vscode'), false);
  assert.equal(usesWindows('both'), true);
  assert.equal(usesVsCode('both'), true);
  assert.equal(usesWindows('off'), false);
  assert.equal(usesVsCode('off'), false);
});

test('первое наблюдение после запуска не уведомляет', () => {
  const tracker = new NotificationTracker();
  const decision = tracker.evaluate(run('failure'), settings());

  assert.equal(decision.notify, false);
  assert.match(decision.reason, /первое наблюдение/);
});

test('notifyOnStartup включает уведомление о текущем состоянии', () => {
  const tracker = new NotificationTracker();
  const decision = tracker.evaluate(run('failure'), settings({ notifyOnStartup: true }));

  assert.equal(decision.notify, true);
  assert.equal(decision.kind, 'failure');
});

test('смена состояния failure → success даёт уведомление об успехе', () => {
  const tracker = new NotificationTracker();
  const same = run('failure');

  // Первое наблюдение (проверка идёт).
  tracker.evaluate({ ...same, status: 'in_progress' }, settings());
  const decision = tracker.evaluate({ ...same, status: 'success' }, settings());

  assert.equal(decision.notify, true);
  assert.equal(decision.kind, 'success');
  assert.match(decision.reason, /in_progress → success/);
});

test('смена состояния in_progress → failure даёт уведомление об ошибке', () => {
  const tracker = new NotificationTracker();
  const same = run('in_progress', '1000', 12);

  tracker.evaluate(same, settings());
  const decision = tracker.evaluate({ ...same, status: 'failure' }, settings());

  assert.equal(decision.notify, true);
  assert.equal(decision.kind, 'failure');
});

test('повторный опрос с тем же состоянием не уведомляет', () => {
  const tracker = new NotificationTracker();
  const failed = run('failure', '2000', 20);

  tracker.evaluate({ ...failed, status: 'in_progress' }, settings());
  assert.equal(tracker.evaluate(failed, settings()).notify, true);
  // Следующие опросы каждые 10 секунд не должны спамить.
  for (let i = 0; i < 20; i += 1) {
    const decision = tracker.evaluate(failed, settings());
    assert.equal(decision.notify, false, `повторное уведомление на опросе ${i}`);
  }
});

test('об одной проверке уведомляем ровно один раз даже при скачках состояния', () => {
  const tracker = new NotificationTracker();
  const base = run('in_progress', '3000', 30);

  tracker.evaluate(base, settings());
  const first = tracker.evaluate({ ...base, status: 'failure' }, settings());
  // Повторный переход (например, GitHub ещё раз прислал тот же результат).
  const second = tracker.evaluate({ ...base, status: 'in_progress' }, settings());
  const third = tracker.evaluate({ ...base, status: 'failure' }, settings());

  assert.equal(first.notify, true);
  assert.equal(second.notify, false);
  assert.equal(third.notify, false, 'о проверке уже сообщали');
  assert.match(third.reason, /уже сообщали/);
});

test('новая проверка уведомляет отдельно', () => {
  const tracker = new NotificationTracker();

  tracker.evaluate(run('in_progress', '4000', 40), settings());
  assert.equal(tracker.evaluate(run('failure', '4000', 40), settings()).notify, true);

  // Новый запуск с другим runId.
  tracker.evaluate(run('in_progress', '4001', 41), settings());
  const decision = tracker.evaluate(run('success', '4001', 41), settings());

  assert.equal(decision.notify, true);
  assert.equal(decision.kind, 'success');
});

test('неизменный статус из notifyOnStatuses не уведомляет', () => {
  const tracker = new NotificationTracker();
  const only = settings({ statuses: ['failure'] });

  tracker.evaluate(run('in_progress', '5000', 50), only);
  const decision = tracker.evaluate(run('success', '5000', 50), only);

  assert.equal(decision.notify, false);
  assert.match(decision.reason, /notifyOnStatuses/);
});

test('промежуточные состояния не дают уведомлений', () => {
  for (const status of ['in_progress', 'queued', 'cancelled', 'skipped', 'warning', 'neutral'] as CiRunStatus[]) {
    const tracker = new NotificationTracker();
    tracker.evaluate(run('queued', '6000', 60), settings());
    const decision = tracker.evaluate(run(status, '6000', 60), settings());
    assert.equal(decision.notify, false, `уведомление для промежуточного состояния ${status}`);
  }
});

test('target = off полностью отключает уведомления', () => {
  const tracker = new NotificationTracker();
  const off = settings({ target: 'off', notifyOnStartup: true });

  assert.equal(tracker.evaluate(run('failure'), off).notify, false);
});

test('отсутствие проверки не уведомляет', () => {
  const tracker = new NotificationTracker();
  const decision = tracker.evaluate(null, settings({ notifyOnStartup: true }));

  assert.equal(decision.notify, false);
  assert.match(decision.reason, /нет данных/);
});

test('reset забывает наблюдения (смена ветки)', () => {
  const tracker = new NotificationTracker();

  tracker.evaluate(run('in_progress', '7000', 70), settings());
  assert.equal(tracker.evaluate(run('failure', '7000', 70), settings()).notify, true);

  tracker.reset();
  // После сброса это снова «первое наблюдение».
  const decision = tracker.evaluate(run('failure', '7000', 70), settings());
  assert.equal(decision.notify, false);
  assert.match(decision.reason, /первое наблюдение/);
});

test('buildNotification: заголовок и тело для ошибки и успеха', () => {
  const failure = buildNotification(run('failure'), 'failure', 'zvukoper/assist_quest_editor');
  assert.equal(failure.title, 'Проверка #147 не прошла');
  assert.ok(failure.body.includes('Проверки'));
  assert.ok(failure.body.includes('ветка main'));
  assert.ok(failure.body.includes('zvukoper/assist_quest_editor'));

  const success = buildNotification(run('success'), 'success', 'zvukoper/assist_quest_editor');
  assert.equal(success.title, 'Проверка #147 прошла успешно');
});

test('buildNotification: отсутствие ветки и репозитория не ломает текст', () => {
  const withoutBranch = buildNotification({ ...run('failure'), branch: null, title: null }, 'failure', '');
  assert.equal(withoutBranch.body, 'Проверки');
});

test('truncate: длинный текст обрезается по границе слова', () => {
  const long = 'слово '.repeat(60);
  const result = truncate(long, 60);

  assert.ok(result.length <= 60, `длина ${result.length}`);
  assert.ok(result.endsWith('…'));
  assert.ok(!result.includes('  '), 'двойные пробелы не допускаются');
});

/** Локальный прогон с заданным состоянием. */
function localRun(status: LocalRun['status']): LocalRun {
  return {
    runNumber: 12,
    status,
    totalChecks: 11,
    completedChecks: 11,
    currentCheck: null,
    failedChecks: status === 'failure' ? ['Тесты домена'] : [],
    startedAt: '2026-09-21T09:41:08Z',
    finishedAt: '2026-09-21T09:41:20Z',
    branch: 'main',
    commit: '360a38e',
    title: 'main · 360a38e',
    updatedAt: '2026-09-21T09:41:20Z',
    reportPath: null,
  };
}

test('локальный прогон: успех не уведомляется, проблемы — уведомляются', () => {
  // Проверки прошли — приложение соберётся и запустится, отдельное уведомление
  // только отвлекало бы.
  assert.equal(shouldNotifyLocalRun(localRun('success')), false);
  assert.equal(shouldNotifyLocalRun(localRun('failure')), true);
  assert.equal(shouldNotifyLocalRun(localRun('cancelled')), true);
});

test('buildLocalNotification: ошибка перечисляет упавшие проверки', () => {
  const text = buildLocalNotification(localRun('failure'), 'assist_quest_editor');

  assert.equal(text.title, 'Проверка #12 не прошла');
  assert.ok(text.body.includes('Тесты домена'), `нет имени проверки: ${text.body}`);
});

test('buildLocalNotification: текст не содержит переносов строк', () => {
  for (const status of ['success', 'failure', 'cancelled'] as const) {
    const text = buildLocalNotification(localRun(status), 'проект');
    assert.ok(!/[\r\n]/.test(text.title), `перенос в заголовке: ${text.title}`);
    assert.ok(!/[\r\n]/.test(text.body), `перенос в теле: ${text.body}`);
  }
});

test('truncate: короткий текст не изменяется', () => {
  assert.equal(truncate('короткий заголовок', 60), 'короткий заголовок');
});

test('тело уведомления умещается в лимит', () => {
  const longTitle = 'feat: очень длинное описание изменения '.repeat(10);
  const { body } = buildNotification({ ...run('failure'), title: longTitle }, 'failure', 'zvukoper/repo');

  assert.ok(body.length <= 140, `длина тела ${body.length}`);
});
