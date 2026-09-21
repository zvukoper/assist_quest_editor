/**
 * Разбор HTML страницы GitHub Actions (БЕЗ зависимостей и БЕЗ `vscode`).
 *
 * Страница https://github.com/<owner>/<repo>/actions отдаётся сервером вместе
 * с разметкой списка проверок, поэтому номер проверки и статус можно читать
 * прямо из HTML — API и токен не нужны.
 *
 * Наблюдаемая структура строки (устойчивые признаки):
 *
 *   <div class="Box-row js-socket-channel js-updatable-content" id="check_suite_96274266860" ...>
 *     <a href="/owner/repo/actions/runs/35559840717" ... aria-label="failed:  Run 147 of Проверки. <заголовок>">
 *       <svg class="octicon octicon-x-circle-fill color-fg-danger" aria-label="failed: ">   ← статус
 *       <span class="h4 Link--primary ...">заголовок коммита</span>
 *     </a>
 *     <span class="text-bold">Проверки</span>  #147:                                    ← workflow + номер
 *     <relative-time datetime="2026-09-21T04:07:46Z">                                    ← время
 *     <span class="color-fg-muted"><svg aria-label="Run duration" ...><span>2m 2s</span> ← длительность
 *     <a class="branch-name" title="main" href="/owner/repo/tree/refs/heads/main">main</a> ← ветка
 *
 * Иконки статуса:
 *   - `anim-rotate` (оранжевый крутящийся круг)      → in_progress
 *   - `octicon-x-circle-fill color-fg-danger`        → failure
 *   - `octicon-check-circle-fill color-fg-success`   → success
 *   - `octicon-skip` / `octicon-stop` / `octicon-alert-fill` → skipped / cancelled / warning
 */

import { CiRun, CiRunStatus, ParsedActionsPage } from './types';

/** Строка списка проверок. Делит HTML на независимые фрагменты. */
const ROW_DELIMITER = /<div class="Box-row js-socket-channel js-updatable-content"/g;

/**
 * Ссылка на запуск, подпись строки и подпись значка статуса.
 *
 * Подпись значка — ПЕРВЫЙ aria-label после открывающего тега ссылки: значок
 * статуса вставляется сразу за ним. Это надёжнее, чем общий aria-label строки,
 * который дополнительно содержит «Run N of <workflow>. <заголовок коммита>».
 */
const RUN_ANCHOR =
  /<a href="([^"]*?\/actions\/runs\/(\d+))"[^>]*?\saria-label="([^"]*)"[\s\S]{0,2000}?(?:aria-label="([^"]*)"|(?=<span class="h4))/;

/** Имя workflow и номер проверки. */
const WORKFLOW_AND_NUMBER = /<span class="text-bold"[^>]*>([^<]*)<\/span>\s*#(\d+):/;

/** Ветка запуска. */
const BRANCH_LINK = /\/tree\/refs\/heads\/([^"'#?]+)/;

/** Коммит запуска. */
const COMMIT_LINK = /\/commit\/([0-9a-f]{7,40})/;

/** Время запуска. */
const DATETIME = /datetime="([^"]+)"/;

/** Длительность запуска (первый <span> после иконки секундомера). */
const DURATION = /octicon-stopwatch[\s\S]*?<span>\s*([^<]+?)\s*<\/span>/;

/** Заголовок коммита в карточке запуска. */
const TITLE = /<span class="h4 Link--primary[^"]*"[^>]*>([\s\S]*?)<\/span>/;

/** Единицы HTML-сущностей, которые реально встречаются в заголовках коммитов. */
const ENTITIES: Record<string, string> = {
  '&amp;': '&',
  '&lt;': '<',
  '&gt;': '>',
  '&quot;': '"',
  '&#39;': "'",
  '&#x27;': "'",
  '&nbsp;': ' ',
};

/**
 * Раскодирование HTML-сущностей и нормализация пробелов.
 * Теги НЕ удаляются — для текста элемента используйте `extractText`.
 */
export function decodeText(raw: string): string {
  return raw
    .replace(/&(?:amp|lt|gt|quot|#39|#x27|nbsp);/g, (m) => ENTITIES[m] ?? m)
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Текст элемента: теги удаляются ДО раскодирования сущностей, иначе `&lt;CI&gt;`
 * сначала превратилось бы в `<CI>`, а затем было удалено как тег.
 */
export function extractText(raw: string): string {
  return decodeText(raw.replace(/<[^>]*>/g, ' '));
}

/** Раскодирование сегмента URL (имена веток с `/` и кириллицей). */
function decodeBranch(raw: string): string {
  try {
    return decodeURIComponent(raw);
  } catch {
    return raw;
  }
}

/**
 * Нормализация статуса проверки.
 * Приоритет — по иконке: она отражает фактическое состояние запуска,
 * тогда как текст aria-label бывает обобщённым («completed successfully»).
 */
export function classifyStatus(fragment: string, statusLabel: string): CiRunStatus {
  if (/anim-rotate/.test(fragment)) return 'in_progress';
  if (/octicon-x-circle-fill/.test(fragment)) return 'failure';
  if (/octicon-check-circle-fill/.test(fragment)) return 'success';
  if (/octicon-skip/.test(fragment)) return 'skipped';
  if (/octicon-stop\b/.test(fragment)) return 'cancelled';
  if (/octicon-alert-fill/.test(fragment)) return 'warning';

  const label = statusLabel.toLowerCase();
  if (label.includes('queued') || label.includes('waiting') || label.includes('pending')) return 'queued';
  if (label.includes('running') || label.includes('progress')) return 'in_progress';
  if (label.includes('success')) return 'success';
  if (label.includes('fail') || label.includes('error')) return 'failure';
  if (label.includes('cancel')) return 'cancelled';
  if (label.includes('skip')) return 'skipped';
  if (label.includes('neutral')) return 'neutral';
  return 'unknown';
}

/**
 * Нормализация подписи статуса.
 * Общая подпись строки имеет вид «<статус>:  Run N of <workflow>. <заголовок>»,
 * поэтому для резервного пути хвост отбрасывается.
 */
export function normalizeStatusLabel(label: string): string {
  const trimmed = (label ?? '').trim();
  const cut = trimmed.replace(/\s*Run \d+ of .*$/i, '').trim();
  return cut || trimmed;
}

/** Единый фрагмент страницы → CiRun, либо `null`, если это не строка проверки. */
export function parseRunFragment(fragment: string, owner: string, repo: string): CiRun | null {
  const anchor = RUN_ANCHOR.exec(fragment);
  if (!anchor) return null;

  const [, rawUrl, runId, runLabel, iconLabel] = anchor;
  const runNumberMatch = WORKFLOW_AND_NUMBER.exec(fragment);
  if (!runNumberMatch) return null;

  const [, workflowName, numberText] = runNumberMatch;
  const branchMatch = BRANCH_LINK.exec(fragment);
  const commitMatch = COMMIT_LINK.exec(fragment);
  const dateMatch = DATETIME.exec(fragment);
  const durationMatch = DURATION.exec(fragment);
  const titleMatch = TITLE.exec(fragment);

  // Относительные ссылки GitHub начинаются с «/», но на всякий случай
  // поддерживаем и абсолютный вид.
  const runUrl = rawUrl.startsWith('http') ? rawUrl : `https://github.com${rawUrl}`;

  // Подпись значка статуса точнее; общая подпись строки — резервный вариант.
  const statusLabel = normalizeStatusLabel(iconLabel || runLabel);

  return {
    runNumber: Number(numberText),
    runId,
    runUrl,
    workflowName: extractText(workflowName),
    statusLabel,
    status: classifyStatus(fragment, statusLabel),
    branch: branchMatch ? decodeBranch(branchMatch[1]) : null,
    commitSha: commitMatch ? commitMatch[1] : null,
    title: titleMatch ? extractText(titleMatch[1]) : null,
    duration: durationMatch ? extractText(durationMatch[1]) : null,
    startedAt: dateMatch ? dateMatch[1] : null,
  };
}

/** Разбор всей страницы Actions. Строки идут от новых к старым. */
export function parseActionsPage(html: string, owner: string, repo: string): ParsedActionsPage {
  const runs: CiRun[] = [];
  const branches: string[] = [];

  for (const fragment of splitRows(html)) {
    const run = parseRunFragment(fragment, owner, repo);
    if (!run) continue;
    runs.push(run);
    if (run.branch && !branches.includes(run.branch)) branches.push(run.branch);
  }

  return { runs, branches };
}

/** Деление HTML на фрагменты строк. */
function splitRows(html: string): string[] {
  const indices: number[] = [];
  ROW_DELIMITER.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = ROW_DELIMITER.exec(html)) !== null) indices.push(match.index);

  if (indices.length === 0) {
    // Резервный путь: разметка изменилась, но ссылки на запуски ещё есть.
    return html.split(/(?=<a href="[^"]*?\/actions\/runs\/\d+")/);
  }

  const fragments: string[] = [];
  for (let i = 0; i < indices.length; i += 1) {
    const end = i + 1 < indices.length ? indices[i + 1] : html.length;
    fragments.push(html.slice(indices[i], end));
  }
  return fragments;
}

/**
 * Выбор последней проверки.
 *
 * @param runs строки страницы в порядке от новых к старым;
 * @param branch целевая ветка; `null` — первая проверка на странице.
 */
export function findLatestRun(runs: CiRun[], branch: string | null): CiRun | null {
  if (runs.length === 0) return null;
  if (!branch) return runs[0];

  const wanted = branch.toLowerCase();
  for (const run of runs) {
    if (run.branch && run.branch.toLowerCase() === wanted) return run;
  }
  // Ветка не найдена — не подменяем результат проверкой из другой ветки.
  return null;
}
