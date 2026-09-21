/**
 * Опрос страницы GitHub Actions по таймеру.
 *
 * Один опрос = одна загрузка HTML. При ответе 304 Not Modified повторный разбор
 * не выполняется — используется предыдущий результат. Параллельные запросы
 * исключены флагом `inFlight`.
 */

import * as vscode from 'vscode';
import { ActionsClient } from './actionsClient';
import { CiLogger } from './logger';
import { NotificationDecision, NotificationSettings, NotificationTracker, buildNotification, parseNotifyStatuses, parseNotifyTarget } from './notifications';
import { ParsedActionsPage, RepoRef, MonitorTarget } from './types';
import { findLatestRun } from './parser';
import { actionsPageUrl, parseRepoSetting, resolveBranchFromGit, resolveRepoFromGit } from './repoResolver';
import { CiRun } from './types';
import { showWindowsToast } from './windowsNotifier';

/** Что получилось после опроса. */
export interface PollOutcome {
  run: CiRun | null;
  error: string | null;
  target: MonitorTarget | null;
  notModified: boolean;
}

/** Уведомление, которое должен показать вызывающий код (средствами редактора). */
export interface PendingNotification {
  title: string;
  body: string;
  /** Куда доставлять через возможности редактора. */
  viaVsCode: boolean;
  viaWindows: boolean;
}

/** Настройки одного опроса. */
interface PollSettings {
  enabled: boolean;
  branch: string | null;
  intervalSeconds: number;
  timeoutMs: number;
  repoOverride: RepoRef | null;
}

export class CiPoller {
  private readonly client = new ActionsClient();
  private readonly tracker = new NotificationTracker();
  private timer: NodeJS.Timeout | null = null;
  private inFlight = false;
  private disposed = false;
  private cache: ParsedActionsPage | null = null;
  private cacheKey = '';
  /** Последняя успешная проверка — чтобы не писать в журнал одно и то же. */
  private lastLoggedKey = '';

  constructor(
    private readonly logger: CiLogger,
    private readonly workspaceDir: string,
    private readonly onOutcome: (outcome: PollOutcome, run: CiRun | null) => void,
    private readonly onNotify: (notification: PendingNotification) => void = () => {}
  ) {}

  private config(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration('ciMonitor');
  }

  private settings(): PollSettings {
    const config = this.config();
    const branchSetting = (config.get<string>('branch', '') ?? '').trim();
    return {
      enabled: config.get<boolean>('enabled', true),
      branch: branchSetting || null,
      // Опрос страницы: минимум 10 секунд, чтобы не упираться в лимиты GitHub.
      intervalSeconds: Math.max(10, config.get<number>('refreshInterval', 10)),
      timeoutMs: Math.max(3000, config.get<number>('requestTimeout', 15000)),
      repoOverride: parseRepoSetting(config.get<string>('repository', '') ?? ''),
    };
  }

  /** Настройки уведомлений. */
  private notificationSettings(): NotificationSettings {
    const config = this.config();
    return {
      target: parseNotifyTarget(config.get<string>('notifications', 'windows')),
      statuses: parseNotifyStatuses(config.get<unknown>('notifyOnStatuses', ['failure', 'success'])),
      notifyOnStartup: config.get<boolean>('notifyOnStartup', false),
    };
  }

  /**
   * Оценка уведомления и его постановка в очередь.
   * Доставку выполняет вызывающий код: Windows — через WinRT, редактор — через
   * `showInformationMessage`, чтобы кнопка «открыть запуск» была доступна.
   */
  private async maybeNotify(run: CiRun, target: MonitorTarget): Promise<void> {
    const settings = this.notificationSettings();
    const decision: NotificationDecision = this.tracker.evaluate(run, settings);

    if (!decision.notify || !decision.kind) {
      this.logger.debug(`Уведомление не нужно: ${decision.reason}.`);
      return;
    }

    const text = buildNotification(run, decision.kind, `${target.owner}/${target.repo}`);
    this.logger.info(`Уведомление (${decision.reason}): ${text.title} — ${text.body}`);

    this.onNotify({
      title: text.title,
      body: text.body,
      viaVsCode: settings.target === 'vscode' || settings.target === 'both',
      viaWindows: settings.target === 'windows' || settings.target === 'both',
    });
  }

  /** Определение цели опроса: репозиторий + ветка + URL страницы. */
  private async resolveTarget(settings: PollSettings): Promise<MonitorTarget | null> {
    const repo = settings.repoOverride ?? (await resolveRepoFromGit(this.workspaceDir));
    if (!repo) {
      this.logger.error(
        `Не удалось определить репозиторий GitHub. Задайте ciMonitor.repository в виде "владелец/имя" (каталог: ${this.workspaceDir}).`
      );
      return null;
    }

    const branch = settings.branch ?? (await resolveBranchFromGit(this.workspaceDir));
    return { ...repo, branch, pageUrl: actionsPageUrl(repo, branch) };
  }

  /** Сообщить о появлении новой проверки в журнале. */
  private logChange(run: CiRun, target: MonitorTarget, notModified: boolean): void {
    const key = `${run.runId}:${run.status}`;
    if (key === this.lastLoggedKey) return;
    this.lastLoggedKey = key;

    this.logger.info(
      `Проверка #${run.runNumber} (${run.workflowName}) — ${run.status}; ветка ${run.branch ?? '—'}; ` +
        `URL ${run.runUrl}${notModified ? '; данные из ETag-кэша' : ''}`
    );
  }

  /** Один опрос. Никогда не бросает исключение наружу. */
  async refresh(): Promise<void> {
    if (this.inFlight || this.disposed) return;
    if (!this.settings().enabled) {
      this.logger.debug('Опрос пропущен: ciMonitor.enabled = false.');
      return;
    }

    this.inFlight = true;
    try {
      const settings = this.settings();
      const target = await this.resolveTarget(settings);
      if (!target) {
        this.cache = null;
        this.cacheKey = '';
        this.client.reset();
        this.onOutcome(
          {
            run: null,
            error: 'Репозиторий GitHub не определён. Укажите ciMonitor.repository.',
            target: null,
            notModified: false,
          },
          null
        );
        return;
      }

      // При смене репозитория или ветки кэш предыдущей цели недействителен.
      const key = `${target.owner}/${target.repo}#${target.branch ?? ''}`;
      if (key !== this.cacheKey) {
        this.client.reset();
        this.cache = null;
        this.cacheKey = key;
        this.lastLoggedKey = '';
        // Смена цели опроса сбрасывает и слежение за состояниями: иначе первая
        // проверка новой ветки могла бы «унаследовать» статус прежней.
        this.tracker.reset();
        this.logger.info(`Цель опроса: ${key}. Страница: ${target.pageUrl}`);
      }

      const result = await this.client.poll(
        target.pageUrl,
        target.owner,
        target.repo,
        target.branch,
        settings.timeoutMs,
        this.cache
      );

      this.cache = result.page;
      if (!result.notModified) {
        this.logger.debug(
          `Разобрано проверок: ${result.page.runs.length}; ветки: ${result.page.branches.join(', ') || '—'}.`
        );
      }

      if (!result.run) {
        this.logger.info(
          `Для ветки ${target.branch ?? '(все ветки)'} на странице нет подходящих проверок — плашка не обновлена.`
        );
      } else {
        this.logChange(result.run, target, result.notModified);
        await this.maybeNotify(result.run, target);
      }

      this.onOutcome({ run: result.run, error: null, target, notModified: result.notModified }, result.run);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.logger.error(message);
      // При ошибке оставляем предыдущую проверку — плашка показывает её вместе с пометкой.
      this.onOutcome({ run: null, error: message, target: null, notModified: false }, null);
    } finally {
      this.inFlight = false;
    }
  }

  /** Перезапуск таймера по текущим настройкам. */
  schedule(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    if (this.disposed) return;

    const { intervalSeconds, enabled } = this.settings();
    if (!enabled) {
      this.logger.info('Опрос остановлен: ciMonitor.enabled = false.');
      return;
    }

    this.timer = setInterval(() => void this.refresh(), intervalSeconds * 1000);
    this.logger.info(`Интервал опроса: ${intervalSeconds} с.`);
  }

  /** Полный перезапуск: сброс кэша, немедленный опрос и новый таймер. */
  async restart(): Promise<void> {
    this.client.reset();
    this.cache = null;
    this.cacheKey = '';
    this.lastLoggedKey = '';
    this.tracker.reset();
    this.schedule();
    await this.refresh();
  }

  /**
   * Перезапуск по изменению настроек.
   * Показ уведомления зависит от `ciMonitor.notifications`, поэтому при правке
   * настроек слежение за состояниями сохраняется: иначе изменение интервала
   * опроса привело бы к повторному уведомлению об уже известном результате.
   */
  async restartBySettings(): Promise<void> {
    this.client.reset();
    this.cache = null;
    this.cacheKey = '';
    this.schedule();
    await this.refresh();
  }

  dispose(): void {
    this.disposed = true;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}

/** Повторный выбор последней проверки из кэша (используется в extension.ts). */
export function pickCached(cache: ParsedActionsPage | null, branch: string | null): CiRun | null {
  return cache ? findLatestRun(cache.runs, branch) : null;
}
