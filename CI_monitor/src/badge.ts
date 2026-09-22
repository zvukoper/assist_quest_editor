/**
 * Внешний вид плашки. Чистая логика БЕЗ `vscode` — проверяется в обычном Node.
 *
 * ## Ограничение платформы (проверено)
 *
 * `StatusBarItem.backgroundColor` принимает ТОЛЬКО две ThemeColor:
 * `statusBarItem.errorBackground` и `statusBarItem.warningBackground` — прочие
 * значения VS Code отбрасывает (см. `ALLOWED_BACKGROUND_COLORS` в
 * `extHostStatusBar.ts`). Произвольный «чёрный» или «зелёный» фон штатным API
 * недостижим.
 *
 * Управляющие последовательности ANSI тоже не работают: VS Code вставляет текст
 * пункта как обычный текстовый узел (`renderLabelWithIcons` создаёт только
 * `<span>` с codicon), поэтому ESC-коды видны как мусор. Это подтверждает
 * `tests/ansi-probe.mjs`.
 *
 * Поэтому у плашки два режима (`ciMonitor.badgeStyle`):
 *
 * - `square` (по умолчанию) — цветной квадрат-подложка:
 *     ⬛ $(sync~spin) #147 — незавершённая проверка (оранжевый значок и текст);
 *     🟥 $(x) #147        — ошибка;
 *     🟩 $(check) #147    — успех.
 *   Даёт именно красный/зелёный/чёрный цвет и работает в любой теме.
 *
 * - `theme` — фон из темы для поддерживаемых состояний: красный
 *   (`statusBarItem.errorBackground`) для ошибки, жёлтый
 *   (`statusBarItem.warningBackground`) для остальных исключений.
 */

import { CiRun, CiRunStatus } from './types';

/** Стиль оформления плашки. */
export type BadgeStyle = 'square' | 'theme';

/** Тематический фон плашки. */
export type BadgeBackgroundKind = 'error' | 'warning' | undefined;

/** Готовое описание плашки. */
export interface BadgePresentation {
  /** Показывать ли элемент в строке состояния. */
  visible: boolean;
  /** Текст с codicon. */
  text: string;
  /** Цвет текста и значка (CSS-строка) или `undefined` — цвет темы. */
  color: string | undefined;
  /** Описание для скринридера. */
  accessibleText: string;
  /** Markdown-подсказка. */
  tooltip: string;
  /** Тематический фон (режим `theme`). */
  backgroundKind: BadgeBackgroundKind;
  /** Название состояния для диагностического журнала. */
  stateName: string;
}

/** Входные данные для построения плашки. */
export interface BadgeInput {
  run: CiRun | null;
  /** Целевая ветка; `null` — без фильтра. */
  branch: string | null;
  /** Репозиторий «владелец/имя» для подсказки. */
  repoLabel: string;
  /** Ссылка на страницу Actions. */
  pageUrl: string;
  /** Показывать ли плашку, когда проверок нет. */
  showIdle: boolean;
  /** Стиль оформления. */
  style: BadgeStyle;
  /** Текст последней ошибки опроса, если была. */
  error: string | null;
  /**
   * Пройдено проверок локального прогона.
   * `null` или не задано — прогресс неизвестен и не показывается; GitHub-режим
   * эти поля не заполняет, поэтому вид плашки там не меняется.
   */
  passedChecks?: number | null;
  /** Всего проверок локального прогона; 0 или не задано — число неизвестно. */
  totalChecks?: number | null;
  /**
   * Прогон выполнен локально (`ci/run_local.ps1`).
   * У такого прогона нет URL запуска и страницы Actions, зато есть отчёт об ошибках.
   */
  local?: boolean;
  /** Имена упавших проверок (для подсказки локального прогона). */
  failedChecks?: string[];
  /** Путь к отчёту `MemoryAI/LOGS/CI_errors.md`. */
  reportPath?: string | null;
  /** Имя выполняемой проверки (для подсказки локального прогона). */
  currentCheck?: string | null;
}

/** Иконка, цвет и подложка для каждого статуса. */
interface Appearance {
  /** Codicon значка статуса. */
  icon: string;
  /** Цвет текста и значка (CSS-строка). */
  color: string | undefined;
  /** Цветной квадрат-подложка для стиля `square`. */
  chip: string;
  /** Фон из темы для стиля `theme`. */
  themeBackground: BadgeBackgroundKind;
  /** Название состояния для подсказки. */
  label: string;
}

const ORANGE = '#ffa500';
const RED = '#f85149';
const GREEN = '#3fb950';
const YELLOW = '#d29922';
const GRAY = '#8b949e';

const APPEARANCE: Record<CiRunStatus, Appearance> = {
  in_progress: {
    icon: '$(sync~spin)',
    color: ORANGE,
    chip: '⬛',
    themeBackground: undefined,
    label: 'выполняется',
  },
  queued: {
    icon: '$(watch)',
    color: ORANGE,
    chip: '⬛',
    themeBackground: undefined,
    label: 'в очереди',
  },
  success: {
    icon: '$(check)',
    color: GREEN,
    chip: '🟩',
    themeBackground: undefined,
    label: 'успешно',
  },
  failure: {
    icon: '$(x)',
    color: RED,
    chip: '🟥',
    themeBackground: 'error',
    label: 'ошибка',
  },
  cancelled: {
    icon: '$(circle-slash)',
    color: YELLOW,
    chip: '🟨',
    themeBackground: 'warning',
    label: 'отменено',
  },
  skipped: {
    icon: '$(debug-step-over)',
    color: GRAY,
    chip: '🟨',
    themeBackground: 'warning',
    label: 'пропущено',
  },
  warning: {
    icon: '$(warning)',
    color: YELLOW,
    chip: '🟨',
    themeBackground: 'warning',
    label: 'предупреждение',
  },
  neutral: {
    icon: '$(circle-outline)',
    color: GRAY,
    chip: '🟨',
    themeBackground: 'warning',
    label: 'без результата',
  },
  unknown: {
    icon: '$(question)',
    color: GRAY,
    chip: '🟨',
    themeBackground: 'warning',
    label: 'неизвестно',
  },
};

/** Очистка ошибки от управляющих символов и переносов строк для подсказки. */
function sanitize(message: string): string {
  // eslint-disable-next-line no-control-regex
  return message.replace(/\u001b\[[0-9;]*m/g, '').replace(/[\r\n]+/g, ' ').trim();
}

/** Относительное время запуска: «5 мин назад». */
export function formatRelative(iso: string | null, now: Date): string | null {
  if (!iso) return null;
  const started = Date.parse(iso);
  if (Number.isNaN(started)) return null;

  const diffMs = now.getTime() - started;
  if (diffMs < 0) return 'только что';

  const minutes = Math.floor(diffMs / 60000);
  if (minutes < 1) return 'только что';
  if (minutes < 60) return `${minutes} мин назад`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} ч назад`;

  const days = Math.floor(hours / 24);
  return `${days} дн назад`;
}

/** Сборка строки подсказки локального прогона. */
function buildLocalTooltip(run: CiRun, appearance: Appearance, input: BadgeInput, now: Date): string {
  const lines: string[] = [];
  lines.push(`### Локальный прогон #${run.runNumber}: ${appearance.label}`);
  lines.push('');

  const progress = formatProgress(input.passedChecks, input.totalChecks);
  const parts: string[] = [];
  if (progress !== null) parts.push(`пройдено ${progress}`);
  if (run.duration) parts.push(`длительность ${run.duration}`);
  const relative = formatRelative(run.startedAt, now);
  if (relative) parts.push(relative);
  if (parts.length > 0) lines.push(parts.join(' · '));

  if (input.currentCheck) {
    lines.push('');
    lines.push(`Сейчас: ${input.currentCheck}`);
  }

  if (input.failedChecks && input.failedChecks.length > 0) {
    lines.push('');
    lines.push('**Упавшие проверки:**');
    for (const name of input.failedChecks) lines.push(`- ${name}`);
  }

  if (run.branch || run.commitSha) {
    const identity: string[] = [];
    if (run.branch) identity.push(`ветка \`${run.branch}\``);
    if (run.commitSha) identity.push(`коммит \`${run.commitSha.slice(0, 7)}\``);
    lines.push('');
    lines.push(identity.join(' · '));
  }

  if (run.title) {
    lines.push('');
    lines.push(run.title);
  }

  lines.push('');
  if (input.reportPath) {
    lines.push(`Отчёт об ошибках: \`${input.reportPath}\``);
  } else {
    lines.push('Локальный прогон выполняется \`ci/run_local.ps1\`.');
  }

  return lines.join('\n');
}

/** Неотрицательное целое число проверок; `null` — значение непригодно. */
function normalizeCount(value: number | null | undefined): number | null {
  if (value === null || value === undefined) return null;
  if (!Number.isFinite(value)) return null;
  const rounded = Math.round(value);
  return rounded >= 0 ? rounded : null;
}

/**
 * Прогресс числом проверок: «5/24».
 *
 * Проценты намеренно не показываются: по «45%» не видно, сколько проверок
 * осталось, а по «5/24» — видно. Пройденное число не превышает общее, иначе
 * плашка показала бы «99/11». Без общего числа показывать нечего — `null`.
 */
function formatProgress(
  passed: number | null | undefined,
  total: number | null | undefined
): string | null {
  const totalCount = normalizeCount(total);
  if (totalCount === null || totalCount === 0) return null;

  const passedCount = normalizeCount(passed) ?? 0;
  return `${Math.min(passedCount, totalCount)}/${totalCount}`;
}

/** Сборка строки подсказки. */
function buildTooltip(run: CiRun, appearance: Appearance, input: BadgeInput, now: Date): string {
  if (input.local) return buildLocalTooltip(run, appearance, input, now);

  const lines: string[] = [];
  lines.push(`### Проверка #${run.runNumber}: ${appearance.label}`);
  lines.push('');
  lines.push(`**${run.workflowName}** · ветка \`${run.branch ?? input.branch ?? '—'}\``);

  const parts: string[] = [];
  if (run.commitSha) parts.push(`коммит \`${run.commitSha.slice(0, 7)}\``);
  if (run.duration) parts.push(`длительность ${run.duration}`);
  const relative = formatRelative(run.startedAt, now);
  if (relative) parts.push(relative);
  if (parts.length > 0) lines.push(parts.join(' · '));

  if (run.title) {
    lines.push('');
    lines.push(run.title);
  }

  lines.push('');
  if (input.repoLabel) lines.push(`Репозиторий: \`${input.repoLabel}\``);
  lines.push(`[Открыть страницу Actions](${input.pageUrl})`);
  lines.push(`[Открыть запуск #${run.runNumber}](${run.runUrl})`);

  return lines.join('\n');
}

/** Плашка «нет данных». */
function idlePresentation(input: BadgeInput, icon: string, title: string, body: string): BadgePresentation {
  return {
    visible: input.showIdle,
    text: `${icon} CI`,
    color: undefined,
    accessibleText: `CI Monitor: ${title}`,
    tooltip: [`### CI Monitor: ${title}`, '', body, '', `[Открыть страницу Actions](${input.pageUrl})`].join('\n'),
    backgroundKind: undefined,
    stateName: 'idle',
  };
}

/** Построение плашки по текущему состоянию опроса. */
export function buildBadge(input: BadgeInput, now: Date = new Date()): BadgePresentation {
  if (input.error && !input.run) {
    const message = sanitize(input.error);
    return {
      visible: true,
      text: '$(alert) CI',
      color: undefined,
      accessibleText: `CI Monitor: ошибка опроса. ${message}`,
      tooltip: [
        '### CI Monitor: ошибка опроса',
        '',
        message,
        '',
        `[Открыть страницу Actions](${input.pageUrl})`,
      ].join('\n'),
      backgroundKind: 'error',
      stateName: 'error',
    };
  }

  if (!input.run) {
    const branchHint = input.branch ? `Ветка: \`${input.branch}\`` : 'Фильтр по ветке не задан.';
    return idlePresentation(
      input,
      '$(circle-outline)',
      'проверок не найдено',
      [
        'На странице Actions нет запусков для выбранной ветки.',
        '',
        branchHint,
        'Проверьте настройки `ciMonitor.repository` и `ciMonitor.branch`.',
      ].join('\n')
    );
  }

  const run = input.run;
  const appearance = APPEARANCE[run.status] ?? APPEARANCE.unknown;
  const withChip = input.style === 'square';
  const progress = formatProgress(input.passedChecks, input.totalChecks);

  // Прогресс добавляется только между значком и номером и только когда он
  // известен. Так GitHub-режим (числа проверок не заданы) сохраняет прежний вид.
  const progressSuffix = progress === null ? '' : ` ${progress}`;
  const text = `${withChip ? `${appearance.chip} ` : ''}${appearance.icon}${progressSuffix} #${run.runNumber}`;

  // В режиме theme фон задаёт тема и сама подбирает контрастный цвет текста,
  // поэтому собственный цвет в этом случае только мешает читаемости.
  const backgroundKind: BadgeBackgroundKind =
    input.style === 'theme' ? appearance.themeBackground : undefined;
  const color = backgroundKind ? undefined : appearance.color;

  const localPrefix = input.local ? 'Локальный прогон' : 'Проверка';
  const accessibleText =
    `${localPrefix} #${run.runNumber} — ${appearance.label}. ${run.workflowName}.` +
    (progress === null ? '' : ` Пройдено проверок: ${progress}.`);

  return {
    visible: true,
    text,
    color,
    accessibleText,
    tooltip: buildTooltip(run, appearance, input, now),
    backgroundKind,
    stateName: run.status,
  };
}

