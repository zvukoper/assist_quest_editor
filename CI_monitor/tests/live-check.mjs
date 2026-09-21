/**
 * Живая проверка расширения: загружает реальную страницу GitHub Actions,
 * разбирает её и печатает итоговую плашку.
 *
 * Запуск (после `npm run compile`):
 *     node tests/live-check.mjs [владелец/репозиторий] [ветка]
 *
 * По умолчанию берётся репозиторий из git remote текущего каталога.
 * Проверяет именно тот путь кода, который выполняется в редакторе (out/parser,
 * out/badge, out/repoResolver), поэтому ловит изменения разметки GitHub.
 */

import { execFileSync } from 'node:child_process';
import { parseActionsPage, findLatestRun } from '../out/parser.js';
import { buildBadge } from '../out/badge.js';
import { actionsPageUrl, parseGitHubRemote } from '../out/repoResolver.js';

function git(args) {
  return execFileSync('git', args, { encoding: 'utf8', windowsHide: true }).trim();
}

function repoFromGit() {
  for (const args of [['remote', 'get-url', 'origin'], ['remote', '-v']]) {
    try {
      const output = git(args);
      const url = output.split(/\s+/).find((part) => part.includes('github.com'));
      const parsed = url ? parseGitHubRemote(url) : null;
      if (parsed) return parsed;
    } catch {
      // Пробуем следующий вариант.
    }
  }
  return null;
}

const args = process.argv.slice(2);
const repo = args[0]
  ? { owner: args[0].split('/')[0], repo: args[0].split('/')[1] }
  : repoFromGit();

if (!repo || !repo.owner || !repo.repo) {
  console.error('Не удалось определить репозиторий. Укажите его как "владелец/имя".');
  process.exit(1);
}

let branch = args[1] ?? null;
if (!branch) {
  try {
    const current = git(['rev-parse', '--abbrev-ref', 'HEAD']);
    branch = current && current !== 'HEAD' ? current : null;
  } catch {
    branch = null;
  }
}

const pageUrl = actionsPageUrl(repo, branch);
console.log(`Репозиторий: ${repo.owner}/${repo.repo}`);
console.log(`Ветка:       ${branch ?? '(без фильтра)'}`);
console.log(`Страница:    ${pageUrl}`);
console.log('');

const response = await fetch(pageUrl, {
  headers: { 'User-Agent': 'ci-monitor-live-check', Accept: 'text/html' },
});
console.log(`HTTP ${response.status}, ETag: ${response.headers.get('etag') ?? '—'}`);

if (!response.ok) {
  console.error(`Не удалось загрузить страницу: HTTP ${response.status}`);
  process.exit(1);
}

const html = await response.text();
const page = parseActionsPage(html, repo.owner, repo.repo);

console.log(`Разобрано проверок: ${page.runs.length}`);
console.log(`Ветки на странице:  ${page.branches.join(', ') || '—'}`);
console.log('');
console.log('Последние пять проверок страницы:');
for (const run of page.runs.slice(0, 5)) {
  console.log(
    `  #${String(run.runNumber).padEnd(6)} ${run.status.padEnd(12)} ` +
      `${(run.branch ?? '—').padEnd(10)} ${run.workflowName} — ${run.title ?? ''}`
  );
}

const latest = findLatestRun(page.runs, branch);
console.log('');

if (!latest) {
  console.log(`Для ветки ${branch ?? '(все ветки)'} подходящих проверок не найдено.`);
  process.exit(1);
}

const badge = buildBadge({
  run: latest,
  branch,
  repoLabel: `${repo.owner}/${repo.repo}`,
  pageUrl,
  showIdle: true,
  useAnsiColors: true,
  error: null,
});

console.log(`Итог: последняя проверка #${latest.runNumber} (${latest.status}).`);
console.log(
  `Плашка: ${JSON.stringify(badge.text.replace(/\u001b/g, '\\u001b'))}  состояние=${badge.stateName}`
);
console.log('');
console.log('Подсказка плашки:');
console.log(badge.tooltip);
