/**
 * Проверки разбора страницы GitHub Actions.
 *
 * Основной fixture — реальная разметка GitHub (tests/fixtures/actions-page.html),
 * поэтому тест заодно ловит изменения формата страницы.
 */

import { strict as assert } from 'node:assert';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { classifyStatus, decodeText, extractText, findLatestRun, parseActionsPage, parseRunFragment } from '../parser';

const FIXTURE = path.join(__dirname, '..', '..', 'tests', 'fixtures', 'actions-page.html');

function loadFixture(): string {
  return fs.readFileSync(FIXTURE, 'utf8');
}

/** Минимальная строка запуска для проверки отдельных состояний. */
function fragment(options: {
  number: number;
  runId: string;
  workflow?: string;
  branch?: string;
  icon: string;
}): string {
  const workflow = options.workflow ?? 'Проверки';
  const branch = options.branch ?? 'main';
  return `
<div class="Box-row js-socket-channel js-updatable-content" id="check_suite_${options.runId}">
  <div class="d-table col-12">
    <div class="d-table-cell">
      <a href="/zvukoper/assist_quest_editor/actions/runs/${options.runId}" class="d-flex" aria-label="status:  Run ${options.number} of ${workflow}. Тестовый заголовок">
        ${options.icon}
        <span class="h4 Link--primary text-bold">Тестовый заголовок</span>
      </a>
      <span class="d-block text-small color-fg-muted">
        <span class="text-bold" >${workflow}</span>
        #${options.number}:
        <span class="color-fg-muted">Commit <a href="/zvukoper/assist_quest_editor/commit/b905b351106db6cec97eb36041d7fd1f3e99bae7">b905b35</a></span>
      </span>
      <div>
        <relative-time datetime="2026-09-21T04:07:46Z"></relative-time>
        <span class="color-fg-muted"><svg aria-label="Run duration"></svg><span>1m 30s</span></span>
        <a class="branch-name" title="${branch}" href="/zvukoper/assist_quest_editor/tree/refs/heads/${branch}">${branch}</a>
      </div>
    </div>
  </div>
</div>`;
}

test('реальная страница: найдены все строки проверок', () => {
  const page = parseActionsPage(loadFixture(), 'zvukoper', 'assist_quest_editor');
  assert.ok(page.runs.length >= 4, `ожидалось минимум 4 проверки, найдено ${page.runs.length}`);
});

test('реальная страница: последняя проверка — #147 со статусом failure', () => {
  const page = parseActionsPage(loadFixture(), 'zvukoper', 'assist_quest_editor');
  const latest = findLatestRun(page.runs, 'main');

  assert.ok(latest, 'последняя проверка не найдена');
  assert.equal(latest.runNumber, 147);
  assert.equal(latest.status, 'failure');
  assert.equal(latest.workflowName, 'Проверки');
  assert.equal(latest.branch, 'main');
  assert.equal(latest.commitSha, 'b905b351106db6cec97eb36041d7fd1f3e99bae7');
  assert.equal(latest.duration, '2m 2s');
  assert.equal(latest.startedAt, '2026-09-21T04:07:46Z');
  assert.equal(latest.runUrl, 'https://github.com/zvukoper/assist_quest_editor/actions/runs/35559840717');
  assert.match(latest.title ?? '', /Runtime/);
});

test('реальная страница: успешный запуск распознан как success', () => {
  const page = parseActionsPage(loadFixture(), 'zvukoper', 'assist_quest_editor');
  const success = page.runs.find((run) => run.status === 'success');

  assert.ok(success, 'успешный запуск не найден');
  // Подпись берётся у значка статуса, а не у строки целиком, и обрезается по краям.
  assert.equal(success.statusLabel, 'completed successfully:');
  assert.doesNotMatch(success.statusLabel, /Run \d+/);
});

test('реальная страница: незавершённый запуск распознан как in_progress', () => {
  const page = parseActionsPage(loadFixture(), 'zvukoper', 'assist_quest_editor');
  const running = page.runs.find((run) => run.status === 'in_progress');

  assert.ok(running, 'запуск в процессе не найден');
  assert.equal(running.runNumber, 33431);
  assert.equal(running.workflowName, 'Component Fixtures');
  assert.match(running.statusLabel, /currently running/);
});

test('все запуски имеют непустые номер, URL и статус', () => {
  const page = parseActionsPage(loadFixture(), 'zvukoper', 'assist_quest_editor');
  for (const run of page.runs) {
    assert.ok(Number.isFinite(run.runNumber) && run.runNumber > 0, `плохой номер: ${run.runNumber}`);
    assert.match(run.runUrl, /^https:\/\/github\.com\/.+\/actions\/runs\/\d+$/);
    assert.notEqual(run.status, 'unknown', `статус не распознан для #${run.runNumber}`);
  }
});

test('classifyStatus: оранжевое вращение — in_progress', () => {
  const icon = '<svg class="anim-rotate" aria-label="currently running: "></svg>';
  assert.equal(classifyStatus(icon, 'currently running: '), 'in_progress');
});

test('classifyStatus: крестик — failure, галочка — success', () => {
  const cross = '<svg class="octicon octicon-x-circle-fill color-fg-danger"></svg>';
  const check = '<svg class="octicon octicon-check-circle-fill color-fg-success"></svg>';
  assert.equal(classifyStatus(cross, 'failed: '), 'failure');
  assert.equal(classifyStatus(check, 'completed successfully:'), 'success');
});

test('classifyStatus: остановленный и пропущенный запуски', () => {
  assert.equal(classifyStatus('<svg class="octicon octicon-stop"></svg>', ''), 'cancelled');
  assert.equal(classifyStatus('<svg class="octicon octicon-skip"></svg>', ''), 'skipped');
});

test('classifyStatus: без иконок используется текст подписи', () => {
  assert.equal(classifyStatus('<div></div>', 'queued: '), 'queued');
  assert.equal(classifyStatus('<div></div>', 'waiting: '), 'queued');
  assert.equal(classifyStatus('<div></div>', 'neutral: '), 'neutral');
  assert.equal(classifyStatus('<div></div>', 'что-то новое'), 'unknown');
});

test('запуск без значка статуса: подпись строки «Run N of …» не даёт ложный статус', () => {
  const run = parseRunFragment(
    fragment({ number: 8, runId: '500', icon: '<svg class="octicon octicon-check-circle-fill"></svg>' }),
    'zvukoper',
    'assist_quest_editor'
  );
  // В сгенерированном фрагменте подписи у значка нет, поэтому берётся подпись
  // строки, но хвост «Run N of <workflow>. <заголовок>» отбрасывается.
  assert.ok(run);
  assert.equal(run.status, 'success');
  assert.equal(run.statusLabel, 'status:');
});

test('статус не подменяется заголовком коммита, содержащим слово success', () => {
  const run = parseRunFragment(
    fragment({ number: 9, runId: '501', icon: '<div></div>' }).replace(
      'Тестовый заголовок',
      'fix: failed checks are now success'
    ),
    'zvukoper',
    'assist_quest_editor'
  );
  assert.ok(run);
  assert.equal(run.statusLabel, 'status:', 'в подпись попал хвост строки');
  assert.equal(run.status, 'unknown');
});

test('иконка важнее подписи: running-вращение при обобщённой подписи', () => {
  const icon = '<svg class="anim-rotate"></svg>';
  assert.equal(classifyStatus(icon, 'completed successfully: '), 'in_progress');
});

test('очередь: запуск без вращения и без галочки — queued', () => {
  const run = parseRunFragment(
    fragment({ number: 12, runId: '900', icon: '<svg aria-label="queued"></svg>' }),
    'zvukoper',
    'assist_quest_editor'
  );
  assert.ok(run);
  assert.equal(run.status, 'queued');
  assert.equal(run.runNumber, 12);
});

test('фильтр по ветке: проверка другой ветки не подменяет результат', () => {
  const html =
    fragment({ number: 30, runId: '1', branch: 'dev', icon: '<svg class="octicon octicon-x-circle-fill"></svg>' }) +
    fragment({ number: 29, runId: '2', branch: 'main', icon: '<svg class="octicon octicon-check-circle-fill"></svg>' });

  const page = parseActionsPage(html, 'zvukoper', 'assist_quest_editor');
  assert.deepEqual(page.branches.sort(), ['dev', 'main']);

  const dev = findLatestRun(page.runs, 'dev');
  assert.equal(dev?.runNumber, 30);
  assert.equal(dev?.status, 'failure');

  const main = findLatestRun(page.runs, 'main');
  assert.equal(main?.runNumber, 29);
  assert.equal(main?.status, 'success');
});

test('ветки с косой чертой раскодируются из URL', () => {
  const html = fragment({
    number: 5,
    runId: '7',
    branch: 'feature%2Fnew-ui',
    icon: '<svg class="octicon octicon-check-circle-fill"></svg>',
  });
  const run = parseRunFragment(html, 'zvukoper', 'assist_quest_editor');

  assert.equal(run?.branch, 'feature/new-ui');
  assert.ok(findLatestRun([run!], 'feature/new-ui'));
});

test('несуществующая ветка: возвращается null, а не чужая проверка', () => {
  const html = fragment({
    number: 4,
    runId: '9',
    branch: 'main',
    icon: '<svg class="octicon octicon-check-circle-fill"></svg>',
  });
  const page = parseActionsPage(html, 'zvukoper', 'assist_quest_editor');

  assert.equal(findLatestRun(page.runs, 'release'), null);
});

test('без фильтра по ветке берётся самая свежая проверка страницы', () => {
  const html =
    fragment({ number: 20, runId: '1', branch: 'dev', icon: '<svg class="octicon octicon-check-circle-fill"></svg>' }) +
    fragment({ number: 19, runId: '2', branch: 'main', icon: '<svg class="octicon octicon-x-circle-fill"></svg>' });
  const page = parseActionsPage(html, 'zvukoper', 'assist_quest_editor');

  assert.equal(findLatestRun(page.runs, null)?.runNumber, 20);
});

test('пустой HTML не приводит к исключению', () => {
  const page = parseActionsPage('', 'zvukoper', 'assist_quest_editor');
  assert.deepEqual(page.runs, []);
  assert.equal(findLatestRun(page.runs, 'main'), null);
});

test('decodeText: HTML-сущности и пробелы', () => {
  assert.equal(decodeText('fix: &amp; правки'), 'fix: & правки');
  assert.equal(decodeText('&lt;CI&gt; &quot;кавычки&quot;'), '<CI> "кавычки"');
  assert.equal(decodeText('  много \n пробелов  '), 'много пробелов');
});

test('extractText: теги удаляются, сущности раскодируются после них', () => {
  assert.equal(extractText('<span class="h4">fix: &amp; правки</span>'), 'fix: & правки');
  assert.equal(extractText('&lt;CI&gt; в теге'), '<CI> в теге');
});
