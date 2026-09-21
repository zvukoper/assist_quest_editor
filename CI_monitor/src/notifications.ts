/**
 * Решение об уведомлении. Чистая логика БЕЗ `vscode` — проверяется в Node.
 *
 * Уведомление показывается только при СМЕНЕ состояния и только один раз на
 * конкретную проверку. Это исключает повторные всплывающие окна: опрос идёт
 * каждые 10 секунд, а результат проверки обычно не меняется часами.
 *
 * Для ЛОКАЛЬНОГО прогона действует дополнительное правило: успех не уведомляется
 * (см. `shouldNotifyLocalRun` в `localRun.ts`) — проверки прошли, приложение
 * соберётся и запустится, поэтому отдельное сообщение только отвлекало бы.
 *
 * Правила:
 *   1. нет данных о проверке — не уведомляем;
 *   2. уведомления выключены настройкой — не уведомляем;
 *   3. первое наблюдение после запуска редактора — не уведомляем
 *      (настройка `ciMonitor.notifyOnStartup` включает обратное поведение);
 *   4. состояние не изменилось с прошлого опроса — не уведомляем;
 *   5. состояние не входит в `ciMonitor.notifyOnStatuses` — не уведомляем;
 *   6. об этой проверке уже сообщали — не уведомляем;
 *   7. иначе — уведомляем, если состояние финальное (ошибка/успех).
 */

import { CiRun, CiRunStatus } from './types';
import { LocalRun } from './localRun';

/** Куда доставлять уведомление. */
export type NotifyTarget = 'off' | 'vscode' | 'windows' | 'both';

/** Настройки уведомлений. */
export interface NotificationSettings {
  target: NotifyTarget;
  /** Состояния, о которых сообщаем. */
  statuses: CiRunStatus[];
  /** Уведомлять о состоянии, которое уже было финальным при запуске редактора. */
  notifyOnStartup: boolean;
}

/** Решение об уведомлении. */
export interface NotificationDecision {
  notify: boolean;
  /** Вид уведомления: ошибка или успех. */
  kind: 'failure' | 'success' | null;
  /** Причина решения — пишется в журнал для диагностики. */
  reason: string;
}

/** Допустимые значения настройки `ciMonitor.notifications`. */
const TARGETS: NotifyTarget[] = ['off', 'vscode', 'windows', 'both'];

/** Разбор настройки `ciMonitor.notifications`. */
export function parseNotifyTarget(value: string): NotifyTarget {
  const normalized = (value ?? '').trim().toLowerCase();
  return (TARGETS as string[]).includes(normalized) ? (normalized as NotifyTarget) : 'windows';
}

/** Разбор настройки `ciMonitor.notifyOnStatuses`. */
export function parseNotifyStatuses(value: unknown): CiRunStatus[] {
  const allowed: CiRunStatus[] = ['failure', 'success'];
  if (!Array.isArray(value)) return allowed;
  const result = value
    .map((item) => String(item).trim().toLowerCase())
    .filter((item): item is CiRunStatus => (allowed as string[]).includes(item));
  // Пустой список означает «не уведомлять ни о чём» — это осознанный выбор.
  return result;
}

/** Включена ли доставка через редактор. */
export function usesVsCode(target: NotifyTarget): boolean {
  return target === 'vscode' || target === 'both';
}

/** Включена ли доставка через Windows. */
export function usesWindows(target: NotifyTarget): boolean {
  return target === 'windows' || target === 'both';
}

/** Слежение за сменой состояния проверок. */
export class NotificationTracker {
  /** runId → последнее наблюдённое состояние. */
  private readonly lastStatus = new Map<string, CiRunStatus>();
  /** Проверки, о которых уже сообщали (runId). */
  private readonly notified = new Set<string>();
  /** Было ли хоть одно наблюдение с момента запуска. */
  private observed = false;

  /** Максимальное число запоминаемых проверок — страница GitHub содержит 25 строк. */
  private readonly limit = 200;

  /**
   * Оценка текущей проверки.
   * Метод вызывается на каждый опрос и не имеет побочных эффектов, кроме
   * обновления внутреннего состояния слежения.
   */
  evaluate(run: CiRun | null, settings: NotificationSettings): NotificationDecision {
    if (!run) {
      return { notify: false, kind: null, reason: 'нет данных о проверке' };
    }

    const previous = this.lastStatus.get(run.runId);
    this.remember(run.runId, run.status);

    const isFirstObservation = !this.observed;
    this.observed = true;

    if (settings.target === 'off') {
      return { notify: false, kind: null, reason: 'уведомления отключены настройкой' };
    }
    if (isFirstObservation && !settings.notifyOnStartup) {
      return {
        notify: false,
        kind: null,
        reason: `первое наблюдение после запуска (состояние ${run.status}) — уведомление пропущено`,
      };
    }
    if (previous === run.status) {
      return { notify: false, kind: null, reason: `состояние не изменилось (${run.status})` };
    }
    if (!settings.statuses.includes(run.status)) {
      return { notify: false, kind: null, reason: `состояние ${run.status} не входит в notifyOnStatuses` };
    }
    if (this.notified.has(run.runId)) {
      return { notify: false, kind: null, reason: `о проверке #${run.runNumber} уже сообщали` };
    }

    const kind = run.status === 'failure' ? 'failure' : run.status === 'success' ? 'success' : null;
    if (!kind) {
      return { notify: false, kind: null, reason: `состояние ${run.status} не является финальным` };
    }

    this.notified.add(run.runId);
    this.prune();
    return {
      notify: true,
      kind,
      reason: `${previous ?? 'новая проверка'} → ${run.status}`,
    };
  }

  /** Сброс слежения (смена репозитория или ветки). */
  reset(): void {
    this.lastStatus.clear();
    this.notified.clear();
    this.observed = false;
  }

  private remember(runId: string, status: CiRunStatus): void {
    this.lastStatus.delete(runId);
    this.lastStatus.set(runId, status);
    this.prune();
  }

  /** Ограничение памяти: убираем самые старые записи. */
  private prune(): void {
    while (this.lastStatus.size > this.limit) {
      const oldest = this.lastStatus.keys().next();
      if (oldest.done) break;
      this.lastStatus.delete(oldest.value);
    }
    while (this.notified.size > this.limit) {
      const oldest = this.notified.values().next();
      if (oldest.done) break;
      this.notified.delete(oldest.value);
    }
  }
}

/** Текст уведомления. */
export interface NotificationText {
  title: string;
  body: string;
}

/** Максимальная длина строки тела уведомления. */
const BODY_LIMIT = 140;

/** Обрезка длинного текста по границе слова. */
export function truncate(value: string, limit = BODY_LIMIT): string {
  const text = value.replace(/\s+/g, ' ').trim();
  if (text.length <= limit) return text;
  const cut = text.slice(0, limit - 1);
  const space = cut.lastIndexOf(' ');
  return `${(space > limit * 0.6 ? cut.slice(0, space) : cut).trimEnd()}…`;
}

/** Сборка текста уведомления о смене состояния проверки. */
export function buildNotification(
  run: CiRun,
  kind: 'failure' | 'success',
  repoLabel: string
): NotificationText {
  const title =
    kind === 'failure'
      ? `Проверка #${run.runNumber} не прошла`
      : `Проверка #${run.runNumber} прошла успешно`;

  const parts = [run.workflowName];
  if (run.branch) parts.push(`ветка ${run.branch}`);
  if (repoLabel) parts.push(repoLabel);
  if (run.title) parts.push(run.title);

  return { title, body: truncate(parts.filter(Boolean).join(' · ')) };
}

/**
 * Текст уведомления о локальном прогоне.
 *
 * Локальное уведомление отличается тем, что сообщает о смене состояния
 * локального прогона (успех/ошибка/отмена), а не о проверке GitHub.
 * При ошибке перечисляются упавшие проверки — это то, что нужно знать сразу.
 */
export function buildLocalNotification(
  run: LocalRun,
  workspaceLabel: string
): NotificationText {
  const failed = run.failedChecks.length;

  if (run.status === 'success') {
    return {
      title: `Проверка #${run.runNumber} прошла успешно`,
      body: truncate(
        [
          'Все локальные проверки пройдены',
          run.totalChecks > 0 ? `проверок: ${run.totalChecks}` : '',
          workspaceLabel,
        ]
          .filter(Boolean)
          .join(' · ')
      ),
    };
  }

  if (run.status === 'failure') {
    const names = run.failedChecks.map((name) => name.trim()).filter(Boolean);
    return {
      title: `Проверка #${run.runNumber} не прошла`,
      body: truncate(
        [
          names.length > 0 ? names.join(', ') : 'причина в отчёте',
          `${failed} упавших`,
          workspaceLabel,
        ]
          .filter(Boolean)
          .join(' · ')
      ),
    };
  }

  return {
    title: `Проверка #${run.runNumber} отменена`,
    body: truncate(run.currentCheck ?? 'Прогон прерван'),
  };
}
