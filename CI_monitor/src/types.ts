/**
 * Общие типы CI Monitor.
 *
 * Модуль намеренно не зависит от `vscode`: парсер и логика плашки должны
 * проверяться обычным Node без запуска редактора.
 */

/** Нормализованное состояние проверки GitHub Actions. */
export type CiRunStatus =
  | 'in_progress'
  | 'queued'
  | 'success'
  | 'failure'
  | 'cancelled'
  | 'skipped'
  | 'warning'
  | 'neutral'
  | 'unknown';

/** Одна строка списка проверок со страницы Actions. */
export interface CiRun {
  /** Номер проверки в workflow, как его показывает GitHub: 147. */
  runNumber: number;
  /** Числовой идентификатор запуска (используется в URL). */
  runId: string;
  /** Абсолютная ссылка на запуск. */
  runUrl: string;
  /** Имя workflow, например «Проверки». */
  workflowName: string;
  /** Исходный текст статуса из aria-label: «failed», «completed successfully», «currently running». */
  statusLabel: string;
  /** Нормализованный статус. */
  status: CiRunStatus;
  /** Ветка запуска, если её удалось определить. */
  branch: string | null;
  /** Короткий SHA коммита. */
  commitSha: string | null;
  /** Заголовок коммита. */
  title: string | null;
  /** Длительность запуска текстом, например «2m 2s». */
  duration: string | null;
  /** Время запуска (ISO 8601), если оно есть в разметке. */
  startedAt: string | null;
}

/** Результат разбора страницы Actions. */
export interface ParsedActionsPage {
  runs: CiRun[];
  /** Ветки, для которых на странице есть проверки. */
  branches: string[];
}

/** Репозиторий GitHub. */
export interface RepoRef {
  owner: string;
  repo: string;
}

/** Цель опроса: репозиторий и ветка. */
export interface MonitorTarget extends RepoRef {
  /** `null` — не фильтровать по ветке. */
  branch: string | null;
  /** Полный URL страницы Actions. */
  pageUrl: string;
}
