/**
 * Проверки внешнего вида плашки (цвет фона, значок, номер проверки).
 */

import { strict as assert } from 'node:assert';
import { test } from 'node:test';

import { buildBadge, formatRelative, BadgeInput } from '../badge';
import { CiRun, CiRunStatus } from '../types';

function run(status: CiRunStatus, number = 147): CiRun {
  return {
    runNumber: number,
    runId: '35559840717',
    runUrl: `https://github.com/zvukoper/assist_quest_editor/actions/runs/35559840717`,
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

function input(overrides: Partial<BadgeInput> = {}): BadgeInput {
  return {
    run: run('failure'),
    branch: 'main',
    repoLabel: 'zvukoper/assist_quest_editor',
    pageUrl: 'https://github.com/zvukoper/assist_quest_editor/actions?query=branch%3Amain',
    showIdle: true,
    style: 'square',
    error: null,
    ...overrides,
  };
}

test('номер проверки виден в тексте плашки', () => {
  const badge = buildBadge(input());
  assert.ok(badge.text.includes('#147'), `нет номера в тексте: ${badge.text}`);
  assert.equal(badge.visible, true);
});

test('успех: зелёный квадрат, галочка и зелёный цвет', () => {
  const badge = buildBadge(input({ run: run('success') }));

  assert.ok(badge.text.includes('🟩'), `нет зелёного квадрата: ${badge.text}`);
  assert.ok(badge.text.includes('$(check)'), `нет галочки: ${badge.text}`);
  assert.equal(badge.color, '#3fb950');
  assert.equal(badge.backgroundKind, undefined);
  assert.equal(badge.stateName, 'success');
});

test('ошибка: красный квадрат, крестик и красный цвет', () => {
  const badge = buildBadge(input({ run: run('failure') }));

  assert.ok(badge.text.includes('🟥'), `нет красного квадрата: ${badge.text}`);
  assert.ok(badge.text.includes('$(x)'), `нет крестика: ${badge.text}`);
  assert.equal(badge.color, '#f85149');
  assert.equal(badge.stateName, 'failure');
});

test('идёт проверка: чёрный квадрат, вращающийся значок и оранжевый цвет', () => {
  const badge = buildBadge(input({ run: run('in_progress') }));

  assert.ok(badge.text.includes('⬛'), `нет чёрного квадрата: ${badge.text}`);
  assert.ok(badge.text.includes('$(sync~spin)'), `нет вращающегося значка: ${badge.text}`);
  assert.equal(badge.color, '#ffa500');
  assert.equal(badge.backgroundKind, undefined);
  assert.equal(badge.stateName, 'in_progress');
});

test('в очереди: чёрный квадрат и значок ожидания', () => {
  const badge = buildBadge(input({ run: run('queued') }));

  assert.ok(badge.text.includes('⬛'));
  assert.ok(badge.text.includes('$(watch)'));
  assert.equal(badge.stateName, 'queued');
});

test('отмена, пропуск и предупреждение: жёлтый квадрат', () => {
  for (const status of ['cancelled', 'skipped', 'warning', 'neutral', 'unknown'] as CiRunStatus[]) {
    const badge = buildBadge(input({ run: run(status) }));
    assert.ok(badge.text.includes('🟨'), `нет жёлтого квадрата для ${status}: ${badge.text}`);
  }
});

test('режим theme: фон берётся из темы, свой цвет не задаётся', () => {
  const failure = buildBadge(input({ run: run('failure'), style: 'theme' }));
  assert.equal(failure.backgroundKind, 'error');
  assert.equal(failure.color, undefined, 'цвет текста не должен перебивать контраст темы');
  assert.ok(!failure.text.includes('🟥'), 'в режиме theme квадрат не нужен');

  const cancelled = buildBadge(input({ run: run('cancelled'), style: 'theme' }));
  assert.equal(cancelled.backgroundKind, 'warning');

  const success = buildBadge(input({ run: run('success'), style: 'theme' }));
  assert.equal(success.backgroundKind, undefined);
  assert.equal(success.color, '#3fb950');
});

test('текст плашки не содержит управляющих символов', () => {
  for (const style of ['square', 'theme'] as const) {
    const badge = buildBadge(input({ run: run('in_progress'), style }));
    assert.doesNotMatch(badge.text, /\u001b/, `ANSI попал в текст (${style})`);
  }
});

test('подсказка содержит номер, workflow, ветку и ссылки', () => {
  const badge = buildBadge(input());
  assert.ok(badge.tooltip.includes('#147'));
  assert.ok(badge.tooltip.includes('Проверки'));
  assert.ok(badge.tooltip.includes('main'));
  assert.ok(badge.tooltip.includes('b905b35'));
  assert.ok(badge.tooltip.includes('2m 2s'));
  assert.ok(badge.tooltip.includes('actions/runs/35559840717'));
});

test('описание для скринридера содержит номер и состояние', () => {
  const badge = buildBadge(input({ run: run('success') }));
  assert.ok(badge.accessibleText.includes('#147'));
  assert.ok(badge.accessibleText.includes('успешно'));
});

test('нет проверок и показ простоя включён — плашка видна', () => {
  const badge = buildBadge(input({ run: null, showIdle: true }));
  assert.equal(badge.visible, true);
  assert.equal(badge.stateName, 'idle');
  assert.ok(badge.text.includes('CI'));
});

test('нет проверок и показ простоя выключен — плашка скрыта', () => {
  const badge = buildBadge(input({ run: null, showIdle: false }));
  assert.equal(badge.visible, false);
});

test('ошибка опроса: фон ошибки из темы и предупреждающий значок', () => {
  const badge = buildBadge(input({ run: null, error: 'Таймаут запроса (15000 мс)' }));
  assert.equal(badge.stateName, 'error');
  assert.equal(badge.backgroundKind, 'error');
  assert.ok(badge.text.includes('$(alert)'));
  assert.ok(badge.tooltip.includes('Таймаут запроса'));
});

test('ошибка опроса не скрывает последнюю известную проверку', () => {
  const badge = buildBadge(input({ run: run('failure'), error: 'GitHub вернул HTTP 429' }));
  assert.equal(badge.stateName, 'failure');
  assert.ok(badge.text.includes('#147'));
});

test('ANSI из текста ошибки удаляется в подсказке', () => {
  const badge = buildBadge(input({ run: null, error: '\u001b[31mсбой\u001b[0m' }));
  assert.ok(!badge.tooltip.includes('\u001b['), 'ANSI попал в подсказку');
  assert.ok(badge.tooltip.includes('сбой'));
});

test('formatRelative считает минуты, часы и дни', () => {
  const now = new Date('2026-09-21T12:00:00Z');
  assert.equal(formatRelative('2026-09-21T11:59:30Z', now), 'только что');
  assert.equal(formatRelative('2026-09-21T11:45:00Z', now), '15 мин назад');
  assert.equal(formatRelative('2026-09-21T08:00:00Z', now), '4 ч назад');
  assert.equal(formatRelative('2026-09-19T12:00:00Z', now), '2 дн назад');
  assert.equal(formatRelative(null, now), null);
  assert.equal(formatRelative('не-дата', now), null);
});
