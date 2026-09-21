/**
 * Локальный режим: состояние прогона, которым управляет `ci/run_local.ps1`.
 *
 * Чистая логика БЕЗ `vscode` — проверяется в обычном Node, как и остальные
 * модули расширения.
 *
 * ## Зачем отдельный режим
 *
 * GitHub-режим разбирает публичную страницу Actions. Локальный прогон
 * (`ci/run_local.ps1`) нигде не публикуется, но выполняет тот же набор проверок.
 * Чтобы плашка показывала его так же, как обычную проверку, скрипт пишет файл
 * состояния, а расширение переводит его в те же типы (`CiRun`), что и GitHub.
 * Благодаря этому внешний вид плашки и правила уведомлений полностью общие.
 *
 * ## Прогресс
 *
 * GitHub показывает ход проверки только числом завершённых шагов. Локальный
 * прогон знает и общее число проверок, и текущую, поэтому процент считается как
 * `completedChecks / totalChecks`. Если общее число неизвестно, процент не
 * показывается — лучше не показывать ничего, чем выдуманное значение.
 */

import { CiRun, CiRunStatus } from './types';

/** Состояние локального прогона. */
export type LocalRunStatus = 'queued' | 'in_progress' | 'success' | 'failure' | 'cancelled';

/** Разобранное состояние локального прогона. */
export interface LocalRun {
  /** Номер проверки, который задаёт скрипт (или агент через файл). */
  runNumber: number;
  status: LocalRunStatus;
  /** Всего проверок в прогоне; 0 — неизвестно. */
  totalChecks: number;
  /** Уже завершённых проверок. */
  completedChecks: number;
  /** Имя выполняемой проверки. */
  currentCheck: string | null;
  /** Имена упавших проверок. */
  failedChecks: string[];
  startedAt: string | null;
  finishedAt: string | null;
  branch: string | null;
  commit: string | null;
  title: string | null;
  /** Когда файл состояния обновлялся в последний раз. */
  updatedAt: string | null;
  /** Путь к отчёту с ошибками (`MemoryAI/LOGS/CI_errors.md`). */
  reportPath: string | null;
}

/** Имя workflow для локальных прогонов — используется в подсказке и уведомлении. */
export const LOCAL_WORKFLOW_NAME = 'Локальный прогон';

/** Допустимые состояния (для устойчивого разбора внешнего JSON). */
const STATUSES: LocalRunStatus[] = ['queued', 'in_progress', 'success', 'failure', 'cancelled'];

/** Соответствие состояний локального прогона и общих состояний проверки. */
const STATUS_MAP: Record<LocalRunStatus, CiRunStatus> = {
  queued: 'queued',
  in_progress: 'in_progress',
  success: 'success',
  failure: 'failure',
  cancelled: 'cancelled',
};

/** Чтение целого числа из внешнего JSON без падения на мусоре. */
function toInt(value: unknown, fallback = 0): number {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string') {
    const parsed = Number.parseInt(value, 10);
    if (Number.isFinite(parsed)) return parsed;
  }
  return fallback;
}

/** Чтение строки из внешнего JSON. */
function toText(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** Чтение массива строк из внешнего JSON. */
function toTextArray(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.map((item) => toText(item)).filter((item): item is string => item !== null);
}

/**
 * Разбор файла состояния локального прогона.
 *
 * Возвращает `null`, если файл не JSON, не объект или не содержит номера
 * проверки: без номера плашка показывать нечего.
 */
export function parseLocalRunState(raw: string): LocalRun | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return null;
  }

  const source = parsed as Record<string, unknown>;
  const runNumber = toInt(source.runNumber, 0);
  if (runNumber <= 0) return null;

  const rawStatus = toText(source.status) ?? 'in_progress';
  const normalized = rawStatus.toLowerCase().replace(/-/g, '_');
  const status: LocalRunStatus = (STATUSES as string[]).includes(normalized)
    ? (normalized as LocalRunStatus)
    : 'in_progress';

  // Отрицательные и превышающие значения обрезаем: они попали бы в процент.
  const totalChecks = Math.max(0, toInt(source.totalChecks, 0));
  const completedRaw = Math.max(0, toInt(source.completedChecks, 0));
  const completedChecks = totalChecks > 0 ? Math.min(completedRaw, totalChecks) : completedRaw;

  return {
    runNumber,
    status,
    totalChecks,
    completedChecks,
    currentCheck: toText(source.currentCheck),
    failedChecks: toTextArray(source.failedChecks),
    startedAt: toText(source.startedAt),
    finishedAt: toText(source.finishedAt),
    branch: toText(source.branch),
    commit: toText(source.commit),
    title: toText(source.title),
    updatedAt: toText(source.updatedAt),
    reportPath: toText(source.reportPath),
  };
}

/**
 * Процент завершения прогона.
 * `null` — процент неизвестен (нет общего числа проверок либо прогон не начат).
 */
export function computeProgress(run: LocalRun): number | null {
  if (run.totalChecks <= 0) return null;
  if (run.status === 'queued') return 0;

  const ratio = run.completedChecks / run.totalChecks;
  return Math.max(0, Math.min(100, Math.round(ratio * 100)));
}

/** Процент как строка для плашки: «42%». `null` — показывать нечего. */
export function formatProgress(run: LocalRun): string | null {
  const percent = computeProgress(run);
  return percent === null ? null : `${percent}%`;
}

/** Длительность прогона текстом: «1m 12s». */
export function formatDuration(
  startedAt: string | null,
  finishedAt: string | null,
  now: Date = new Date()
): string | null {
  if (!startedAt) return null;

  const started = Date.parse(startedAt);
  if (Number.isNaN(started)) return null;

  const finished = finishedAt ? Date.parse(finishedAt) : now.getTime();
  if (Number.isNaN(finished)) return null;

  const totalSeconds = Math.max(0, Math.round((finished - started) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;

  return minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
}

/**
 * Перевод локального прогона в общий тип проверки.
 *
 * Это ключевая точка переиспользования: после перевода плашка, её цвета,
 * значки и правила уведомлений работают без отдельной ветки кода.
 */
export function toCiRun(run: LocalRun, now: Date = new Date()): CiRun {
  return {
    runNumber: run.runNumber,
    // Префикс отделяет локальные прогоны от GitHub в слежении за уведомлениями.
    runId: `local:${run.runNumber}`,
    runUrl: '',
    workflowName: LOCAL_WORKFLOW_NAME,
    statusLabel: run.status,
    status: STATUS_MAP[run.status] ?? 'unknown',
    branch: run.branch,
    commitSha: run.commit,
    title: run.title,
    duration: formatDuration(run.startedAt, run.finishedAt, now),
    startedAt: run.startedAt,
  };
}

/** Прогон завершён (успех, ошибка или отмена)? */
export function isFinished(run: LocalRun): boolean {
  return run.status === 'success' || run.status === 'failure' || run.status === 'cancelled';
}

/**
 * Нужно ли уведомлять о завершении локального прогона.
 *
 * Успех намеренно НЕ уведомляется: если проверки прошли, приложение соберётся и
 * запустится, и это уже достаточный сигнал. Уведомление об успехе только
 * отвлекало бы, поэтому о локальном прогоне сообщаем лишь при проблемах.
 */
export function shouldNotifyLocalRun(run: LocalRun): boolean {
  return run.status === 'failure' || run.status === 'cancelled';
}

/**
 * Сериализация состояния локального прогона.
 *
 * Нужна для ручного управления: и агент, и команды расширения могут записать
 * состояние сами, не запуская `ci/run_local.ps1`. Формат совпадает с тем, что
 * пишет скрипт, поэтому расширению всё равно, кто именно обновил файл.
 */
export function serializeLocalRun(run: LocalRun): string {
  const payload: Record<string, unknown> = {
    runNumber: run.runNumber,
    status: run.status,
    totalChecks: run.totalChecks,
    completedChecks: run.completedChecks,
    updatedAt: run.updatedAt ?? new Date().toISOString(),
  };

  if (run.currentCheck) payload.currentCheck = run.currentCheck;
  if (run.failedChecks.length > 0) payload.failedChecks = run.failedChecks;
  if (run.startedAt) payload.startedAt = run.startedAt;
  if (run.finishedAt) payload.finishedAt = run.finishedAt;
  if (run.branch) payload.branch = run.branch;
  if (run.commit) payload.commit = run.commit;
  if (run.title) payload.title = run.title;
  if (run.reportPath) payload.reportPath = run.reportPath;

  return JSON.stringify(payload, null, 2);
}

/** Пустое состояние локального прогона с заданным номером. */
export function createLocalRun(runNumber: number, totalChecks = 0): LocalRun {
  return {
    runNumber,
    status: 'in_progress',
    totalChecks,
    completedChecks: 0,
    currentCheck: null,
    failedChecks: [],
    startedAt: new Date().toISOString(),
    finishedAt: null,
    branch: null,
    commit: null,
    title: null,
    updatedAt: new Date().toISOString(),
    reportPath: null,
  };
}

/**
 * Прогон устарел: файл давно не обновлялся, а состояние всё ещё «выполняется».
 *
 * Такое бывает, если скрипт был закрыт или упал, не дописав финальное
 * состояние. Без этой проверки плашка показывала бы «идёт проверка» вечно.
 */
export function isStale(run: LocalRun, now: Date = new Date(), staleAfterMs = 300000): boolean {
  if (isFinished(run)) return false;
  if (!run.updatedAt) return false;

  const updated = Date.parse(run.updatedAt);
  if (Number.isNaN(updated)) return false;

  return now.getTime() - updated > staleAfterMs;
}

/** Режим работы плашки. */
export type MonitorMode = 'auto' | 'github' | 'local';

/** Допустимые режимы (для устойчивого разбора настройки). */
const MODES: MonitorMode[] = ['auto', 'github', 'local'];

/** Разбор настройки `ciMonitor.mode`. */
export function parseMode(value: unknown): MonitorMode {
  const normalized = String(value ?? '').trim().toLowerCase();
  return (MODES as string[]).includes(normalized) ? (normalized as MonitorMode) : 'auto';
}

/** Входные данные для выбора режима. */
export interface ModeSelectionInput {
  /** Настройка режима. */
  mode: MonitorMode;
  /** Состояние локального прогона, если файл есть. */
  localRun: LocalRun | null;
  /** Локальный прогон выглядит зависшим. */
  localStale: boolean;
  /** Есть ли данные GitHub. */
  hasGithub: boolean;
  /** Сколько держать завершённый локальный прогон, прежде чем вернуться к GitHub, мс. */
  holdFinishedMs: number;
  now?: Date;
}

/** Какой источник показывать. */
export type EffectiveSource = 'local' | 'github';

/**
 * Выбор источника данных для плашки.
 *
 * Правила:
 *   - `github` — всегда GitHub (локальный прогон игнорируется);
 *   - `local` — всегда локальный прогон, даже если файла ещё нет;
 *   - `auto` — локальный прогон побеждает, пока он идёт, и ещё `holdFinishedMs`
 *     после завершения. Затем плашка возвращается к GitHub сама, без ручных
 *     действий. Зависший прогон источником не считается.
 *
 * Возвращается и признак того, что режим выбран автоматически: это нужно для
 * подсказки, чтобы пользователь понимал, почему видит локальный прогон.
 */
export function selectSource(input: ModeSelectionInput): {
  source: EffectiveSource;
  automatic: boolean;
} {
  if (input.mode === 'github') return { source: 'github', automatic: false };
  if (input.mode === 'local') return { source: 'local', automatic: false };

  const run = input.localRun;
  if (!run || input.localStale) return { source: 'github', automatic: false };

  if (!isFinished(run)) return { source: 'local', automatic: true };

  const finishedAt = run.finishedAt ? Date.parse(run.finishedAt) : Number.NaN;
  const now = (input.now ?? new Date()).getTime();

  if (!Number.isNaN(finishedAt) && now - finishedAt <= input.holdFinishedMs) {
    return { source: 'local', automatic: true };
  }

  return { source: 'github', automatic: false };
}

