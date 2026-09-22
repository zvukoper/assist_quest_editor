/**
 * Точка входа расширения CI Monitor.
 *
 * Расширение поддерживает два режима, которые переключаются настройкой
 * `ciMonitor.mode` или командой:
 *
 *   - **GitHub** — опрашивает и разбирает публичную страницу
 *     https://github.com/<владелец>/<репозиторий>/actions. Токены и API не
 *     используются, поэтому лимит обращений не расходуется.
 *
 *   - **Локальный** — показывает прогон `ci/run_local.ps1` из файла состояния
 *     `.ci-state/local-run.json`. Номер проверки и статусы задаёт скрипт
 *     (или команды расширения), поэтому состоянием можно управлять вручную.
 *     У локального режима есть прогресс выполнения: скрипт пишет, сколько
 *     проверок уже пройдено и сколько всего.
 *
 * В режиме `auto` локальный прогон показывается, пока он идёт, и ещё некоторое
 * время после завершения, после чего плашка сама возвращается к GitHub.
 *
 * ## Одно уведомление на прогон
 *
 * Требование: при локальном прогоне уведомляет расширение, а штатное
 * уведомление скрипта не вызывается. Скрипт не может узнать о запущенном
 * редакторе, поэтому расширение обновляет файл-признак
 * `.ci-state/monitor.heartbeat`, а `pull.ps1` проверяет его свежесть.
 * Если редактор закрыт, признак устаревает и скрипт уведомляет сам.
 * Файлы лежат вне `MemoryAI/LOGS`: та папка очищается после успешной сборки.
 */

import * as vscode from 'vscode';
import { buildBadge, BadgePresentation } from './badge';
import { CiLogger } from './logger';
import {
  createLocalRun,
  isFinished,
  parseMode,
  selectSource,
  serializeLocalRun,
  shouldNotifyLocalRun,
  toCiRun,
  LocalRun,
  MonitorMode,
} from './localRun';
import { LOCAL_STATE_RELATIVE_PATH, LocalRunWatcher, LocalWatchUpdate } from './localWatcher';
import {
  buildLocalNotification,
  parseNotifyStatuses,
  parseNotifyTarget,
} from './notifications';
import { CiPoller, PendingNotification, PollOutcome } from './poller';
import { CiStatusBar } from './statusBar';
import { CiRun, MonitorTarget } from './types';
import { cleanupToastScript, showWindowsToast } from './windowsNotifier';

/** Каталог, от которого ищется git-репозиторий (первый корень рабочей области). */
function workspaceDirectory(): string {
  const folders = vscode.workspace.workspaceFolders;
  if (folders && folders.length > 0) return folders[0].uri.fsPath;
  return process.cwd();
}

export function activate(context: vscode.ExtensionContext): void {
  const config = (): vscode.WorkspaceConfiguration => vscode.workspace.getConfiguration('ciMonitor');

  const logger = new CiLogger(() => config().get<string>('logLevel', 'info'));
  const statusBar = new CiStatusBar('ciMonitor.open');
  const workspaceDir = workspaceDirectory();

  // --- Состояние GitHub-режима ---
  let githubRun: CiRun | null = null;
  let lastError: string | null = null;
  let lastTarget: MonitorTarget | null = null;

  // --- Состояние локального режима ---
  let localRun: LocalRun | null = null;
  let localStale = false;
  /** Номер последнего локального прогона, о котором уже уведомляли. */
  let notifiedLocalRun = 0;

  /** Текущая цель для команды «открыть страницу». */
  let pageUrl = 'https://github.com';

  /** Режим работы плашки. */
  const mode = (): MonitorMode => parseMode(config().get<string>('mode', 'auto'));

  /** Что сейчас показывать: локальный прогон или GitHub. */
  const effectiveSource = (): { source: 'local' | 'github'; automatic: boolean } =>
    selectSource({
      mode: mode(),
      localRun,
      localStale,
      hasGithub: githubRun !== null,
      holdFinishedMs: Math.max(0, config().get<number>('localHoldSeconds', 20)) * 1000,
    });

  /**
   * Построение плашки для выбранного источника.
   *
   * Оба режима сходятся в одном типе `CiRun`, поэтому внешний вид (цвет,
   * значок, квадрат-подложка) полностью общий — отдельной ветки отрисовки нет.
   * Локальный режим дополнительно передаёт число пройденных и всех проверок.
   */
  const buildPresentation = (): BadgePresentation => {
    const styleSetting = config().get<string>('badgeStyle', 'square');
    const style = styleSetting === 'theme' ? 'theme' : 'square';
    const selection = effectiveSource();

    if (selection.source === 'local') {
      if (!localRun) {
        // Режим включён вручную, но прогон ещё не запускался.
        return buildBadge({
          run: null,
          branch: null,
          repoLabel: '',
          pageUrl,
          showIdle: true,
          style,
          error: null,
          local: true,
        });
      }

      const passed = localRun.completedChecks;
      const total = localRun.totalChecks;

      return buildBadge({
        run: toCiRun(localRun),
        branch: localRun.branch,
        repoLabel: '',
        pageUrl,
        showIdle: true,
        style,
        error: null,
        passedChecks: passed,
        totalChecks: total,
        local: true,
        failedChecks: localRun.failedChecks,
        reportPath: localRun.reportPath,
        currentCheck: localRun.currentCheck,
      });
    }

    return buildBadge({
      run: githubRun,
      branch: lastTarget?.branch ?? (config().get<string>('branch', '') || null),
      repoLabel: lastTarget ? `${lastTarget.owner}/${lastTarget.repo}` : '',
      pageUrl,
      showIdle: config().get<boolean>('showIdle', true),
      style,
      error: lastError,
    });
  };

  const render = (): void => {
    const selection = effectiveSource();
    const badge = buildPresentation();
    statusBar.update(badge);
    logger.debug(
      `Плашка (${selection.source}${selection.automatic ? ', режим выбран автоматически' : ''}): ` +
        `состояние=${badge.stateName}, текст=${JSON.stringify(badge.text)}.`
    );
  };

  /** Открыть отчёт об ошибках локального прогона. */
  const openReport = async (): Promise<void> => {
    const relative = localRun?.reportPath ?? 'MemoryAI/LOGS/CI_errors.md';
    const uri = vscode.Uri.joinPath(vscode.Uri.file(workspaceDir), ...relative.split(/[\\/]/));
    try {
      const document = await vscode.workspace.openTextDocument(uri);
      await vscode.window.showTextDocument(document, { preview: false });
    } catch {
      void vscode.window.showWarningMessage(`CI Monitor: отчёт не найден — ${relative}`);
    }
  };

  /**
   * Уведомление о завершении локального прогона.
   *
   * Сообщаем только о проблемах: при успешных проверках приложение соберётся и
   * запустится, поэтому отдельное уведомление не требуется и только отвлекало бы.
   *
   * Здесь выполняется требование «уведомляет только расширение»: штатное
   * уведомление скрипта подавлено признаком активности редактора. Показ идёт
   * один раз на прогон, потому что наблюдатель читает файл состояния часто.
   */
  const maybeNotifyLocal = async (update: LocalWatchUpdate): Promise<void> => {
    const run = update.run;
    if (!run || update.stale) return;
    if (!isFinished(run)) return;
    if (run.runNumber === notifiedLocalRun) return;

    // Номер запоминается до проверок ниже: иначе один и тот же прогон
    // проверялся бы на каждом чтении файла состояния.
    notifiedLocalRun = run.runNumber;

    // Успех не уведомляется намеренно: сборка и запуск приложения сами
    // сообщают о результате. См. shouldNotifyLocalRun.
    if (!shouldNotifyLocalRun(run)) {
      logger.debug(`Уведомление о локальном прогоне пропущено: состояние ${run.status} не требует уведомления.`);
      return;
    }

    const target = parseNotifyTarget(config().get<string>('notifications', 'windows'));
    if (target === 'off') {
      logger.debug('Уведомление о локальном прогоне пропущено: уведомления отключены настройкой.');
      return;
    }

    const statuses = parseNotifyStatuses(config().get<unknown>('notifyOnStatuses', ['failure', 'success']));
    const equivalent = run.status === 'failure' ? 'failure' : null;
    if (equivalent && !statuses.includes(equivalent)) {
      logger.debug(`Уведомление о локальном прогоне пропущено: состояние ${equivalent} вне notifyOnStatuses.`);
      return;
    }

    const folderLabel = vscode.workspace.workspaceFolders?.[0]?.name ?? '';
    const text = buildLocalNotification(run, folderLabel);
    logger.info(`Уведомление (локальный прогон #${run.runNumber}): ${text.title} — ${text.body}`);

    if (target === 'windows' || target === 'both') {
      const result = await showWindowsToast(text.title, text.body);
      if (result.delivered) {
        logger.info('Windows-уведомление доставлено.');
      } else {
        logger.error(
          `Windows-уведомление не доставлено (${result.message}) — показываю уведомление редактора.`
        );
        void vscode.window
          .showWarningMessage(`${text.title}: ${text.body}`, 'Открыть отчёт')
          .then((choice) => {
            if (choice === 'Открыть отчёт') void openReport();
          });
      }
    }

    if (target === 'vscode' || target === 'both') {
      void vscode.window
        .showInformationMessage(`${text.title} — ${text.body}`, 'Открыть отчёт')
        .then((choice) => {
          if (choice === 'Открыть отчёт') void openReport();
        });
    }
  };

  const localWatcher = new LocalRunWatcher(workspaceDir, logger, (update: LocalWatchUpdate) => {
    localRun = update.run;
    localStale = update.stale;
    render();
    void maybeNotifyLocal(update);
  });

  const notify = async (notification: PendingNotification): Promise<void> => {
    const runUrl = githubRun?.runUrl ?? pageUrl;

    if (notification.viaWindows) {
      const result = await showWindowsToast(notification.title, notification.body);
      if (result.delivered) {
        logger.info(`Windows-уведомление доставлено: ${notification.title}`);
      } else {
        logger.error(`Windows-уведомление не доставлено (${result.message}) — показываю уведомление редактора.`);
        void vscode.window
          .showWarningMessage(`${notification.title}: ${notification.body}`, 'Открыть запуск')
          .then((choice) => {
            if (choice === 'Открыть запуск') void vscode.env.openExternal(vscode.Uri.parse(runUrl));
          });
      }
    }

    if (notification.viaVsCode) {
      void vscode.window
        .showInformationMessage(`${notification.title} — ${notification.body}`, 'Открыть запуск')
        .then((choice) => {
          if (choice === 'Открыть запуск') void vscode.env.openExternal(vscode.Uri.parse(runUrl));
        });
    }
  };

  const poller = new CiPoller(
    logger,
    workspaceDir,
    (outcome: PollOutcome, run: CiRun | null) => {
      // При ошибке предыдущий результат сохраняется: пользователь видит и
      // последнюю известную проверку, и пометку о сбое опроса.
      if (run) githubRun = run;
      if (outcome.error) lastError = outcome.error;
      else if (outcome.run) lastError = null;
      if (outcome.target) {
        lastTarget = outcome.target;
        pageUrl = outcome.target.pageUrl;
      }
      render();
    },
    (notification) => void notify(notification)
  );

  /** Открыть источник: страницу Actions или отчёт локального прогона. */
  const openPage = async (): Promise<void> => {
    if (effectiveSource().source === 'local') {
      await openReport();
      return;
    }

    logger.info(`Открываю страницу Actions: ${pageUrl}`);
    await vscode.env.openExternal(vscode.Uri.parse(pageUrl));
  };

  const openRun = async (): Promise<void> => {
    if (effectiveSource().source === 'local') {
      await openReport();
      return;
    }

    const url = githubRun?.runUrl ?? pageUrl;
    await vscode.env.openExternal(vscode.Uri.parse(url));
  };

  /**
   * Переключение режима.
   * Источник истины — настройка `ciMonitor.mode`, поэтому команда меняет именно
   * её: режим видно в настройках, и он сохраняется между сессиями.
   */
  const setMode = async (value: MonitorMode): Promise<void> => {
    await config().update('mode', value, vscode.ConfigurationTarget.Workspace);
    logger.info(`Режим переключён: ${value}.`);
    render();
    void vscode.window.showInformationMessage(`CI Monitor: режим «${value}».`);
  };

  /**
   * Ручное управление локальным прогоном: задать номер проверки и состояние.
   * Результат пишется в тот же файл, что и скрипт, поэтому расширению всё равно,
   * кто источник — плашка и уведомления работают одинаково.
   */
  const setLocalRun = async (): Promise<void> => {
    const current = localRun?.runNumber ?? 1;
    const numberInput = await vscode.window.showInputBox({
      title: 'CI Monitor: номер локальной проверки',
      prompt: 'Этот номер показывается на плашке в локальном режиме.',
      value: String(current),
      validateInput: (value) => {
        const parsed = Number.parseInt(value, 10);
        return Number.isFinite(parsed) && parsed > 0 ? null : 'Нужно целое положительное число.';
      },
    });
    if (numberInput === undefined) return;

    const statusPick = await vscode.window.showQuickPick(
      [
        { label: 'queued', description: 'в очереди' },
        { label: 'in_progress', description: 'выполняется' },
        { label: 'success', description: 'успех' },
        { label: 'failure', description: 'ошибка' },
        { label: 'cancelled', description: 'отменено' },
      ],
      { title: 'CI Monitor: состояние локальной проверки' }
    );
    if (!statusPick) return;

    const runNumber = Number.parseInt(numberInput, 10);
    const next: LocalRun = localRun
      ? { ...localRun, runNumber, status: statusPick.label as LocalRun['status'] }
      : createLocalRun(runNumber);

    next.updatedAt = new Date().toISOString();
    if (next.status === 'in_progress' || next.status === 'queued') {
      next.finishedAt = null;
      next.startedAt = next.startedAt ?? new Date().toISOString();
    } else {
      next.finishedAt = new Date().toISOString();
    }

    try {
      await vscode.workspace.fs.writeFile(
        vscode.Uri.file(localWatcher.statePath),
        Buffer.from(serializeLocalRun(next), 'utf8')
      );
      // Наблюдатель перечитает файл и обновит плашку.
      localWatcher.reset();
      logger.info(`Локальный прогон задан вручную: #${runNumber}, ${next.status}.`);
    } catch (error) {
      void vscode.window.showErrorMessage(
        `CI Monitor: не удалось записать состояние локального прогона (${String(error)}).`
      );
    }
  };

  logger.info('CI Monitor activated.');
  logger.info(`Режим: ${mode()}. Файл состояния: ${localWatcher.statePath}.`);

  context.subscriptions.push(
    statusBar,
    logger,
    poller,
    localWatcher,
    { dispose: cleanupToastScript },
    vscode.commands.registerCommand('ciMonitor.open', () => void openPage()),
    vscode.commands.registerCommand('ciMonitor.openRun', () => void openRun()),
    vscode.commands.registerCommand('ciMonitor.refresh', async () => {
      localWatcher.reset();
      await poller.refresh();
      logger.show();
    }),
    vscode.commands.registerCommand('ciMonitor.openSettings', () =>
      vscode.commands.executeCommand('workbench.action.openSettings', 'ciMonitor')
    ),
    vscode.commands.registerCommand('ciMonitor.log', () => logger.show()),
    vscode.commands.registerCommand('ciMonitor.openReport', () => void openReport()),
    vscode.commands.registerCommand('ciMonitor.modeAuto', () => void setMode('auto')),
    vscode.commands.registerCommand('ciMonitor.modeGithub', () => void setMode('github')),
    vscode.commands.registerCommand('ciMonitor.modeLocal', () => void setMode('local')),
    vscode.commands.registerCommand('ciMonitor.setLocalRun', () => void setLocalRun()),
    /** Путь к файлу состояния — чтобы задавать статусы из терминала. */
    vscode.commands.registerCommand('ciMonitor.copyStatePath', async () => {
      await vscode.env.clipboard.writeText(LOCAL_STATE_RELATIVE_PATH);
      void vscode.window.showInformationMessage(`CI Monitor: путь скопирован — ${LOCAL_STATE_RELATIVE_PATH}`);
    }),
    /** Проверка уведомления: пользователь сразу видит, как выглядит плашка Windows. */
    vscode.commands.registerCommand('ciMonitor.testNotification', async () => {
      const result = await showWindowsToast(
        'CI Monitor: проверка уведомления',
        githubRun
          ? `Так будет выглядеть сообщение о проверке #${githubRun.runNumber} · ${githubRun.workflowName}`
          : 'Так будет выглядеть сообщение о смене состояния проверки.'
      );
      if (result.delivered) {
        void vscode.window.showInformationMessage('CI Monitor: уведомление Windows отправлено.');
      } else {
        void vscode.window.showWarningMessage(
          `CI Monitor: не удалось показать уведомление Windows (${result.message}). ` +
            'Уведомления могут быть отключены в параметрах Windows или режиме «Не беспокоить».'
        );
      }
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (!event.affectsConfiguration('ciMonitor')) return;
      logger.syncLevel();
      logger.info('Настройки ciMonitor изменены — перезапуск опроса.');
      // Смена режима меняет источник данных, поэтому перечитываем файл состояния.
      localWatcher.reset();
      render();
      void poller.restartBySettings();
    })
  );

  // Локальный прогон показывается независимо от GitHub: даже если репозиторий
  // определить не удалось, плашка локального режима продолжает работать.
  localWatcher.start();

  // Первый опрос сразу после запуска редактора, затем — по таймеру.
  void poller.restart();
}

export function deactivate(): void {
  // Ресурсы освобождаются через context.subscriptions, включая временный
  // скрипт Windows-уведомления и наблюдение за файлом состояния.
}
