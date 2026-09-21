/**
 * Наблюдение за файлом состояния локального прогона.
 *
 * Расширение не запускает проверки само и не общается с терминалом: `pull.ps1`
 * пишет `.ci-state/local-run.json`, а расширение только читает его.
 * Это единственный канал данных, поэтому он простой и не зависит от того,
 * откуда именно запущен скрипт.
 *
 * Файл может отсутствовать (локальный прогон не запускался) — это нормальное
 * состояние, а не ошибка.
 */

import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { CiLogger } from './logger';
import { LocalRun, isStale, parseLocalRunState } from './localRun';

/**
 * Каталог состояния CI Monitor внутри репозитория.
 *
 * Лежит ВНЕ `MemoryAI/LOGS` намеренно: папка логов очищается после успешной
 * публикации (`compile.ps1`), а здесь хранится номер последней локальной проверки.
 * Если бы номер жил в логах, после каждой успешной сборки нумерация начиналась
 * бы заново с #1.
 */
export const LOCAL_STATE_DIR = '.ci-state';

/** Относительный путь файла состояния внутри репозитория. */
export const LOCAL_STATE_RELATIVE_PATH = path.join(LOCAL_STATE_DIR, 'local-run.json');

/**
 * Относительный путь файла-признака «расширение активно».
 *
 * Нужен, чтобы не было двух уведомлений об одном прогоне. Требование: при
 * локальном прогоне уведомляет расширение, а штатное уведомление скрипта не
 * вызывается. Скрипт не может сам узнать о запущенном редакторе, поэтому
 * расширение периодически обновляет этот файл, а `pull.ps1` проверяет его
 * свежесть. Если редактор закрыт, файл устаревает и скрипт уведомляет сам —
 * иначе пользователь не узнал бы о падении проверок.
 */
export const LOCAL_HEARTBEAT_RELATIVE_PATH = path.join(LOCAL_STATE_DIR, 'monitor.heartbeat');

/** Что сообщает наблюдатель при каждом измерении. */
export interface LocalWatchUpdate {
  /** Разобранное состояние; `null` — файла нет или он не читается. */
  run: LocalRun | null;
  /** Файл состояния существует. */
  exists: boolean;
  /** Прогон не обновлялся и выглядит зависшим. */
  stale: boolean;
  /** Путь к файлу состояния (для открытия и диагностики). */
  statePath: string;
}

/**
 * Наблюдатель за файлом состояния локального прогона.
 *
 * Используется пара «наблюдение за файлом + страховочный таймер»:
 *   - `fs.watch` даёт мгновенную реакцию на запись скрипта;
 *   - таймер нужен, потому что `fs.watch` на Windows может пропускать события
 *     при переименовании/замене файла, а также для обнаружения зависшего прогона.
 */
export class LocalRunWatcher {
  private watcher: fs.FSWatcher | null = null;
  private timer: NodeJS.Timeout | null = null;
  private disposed = false;
  /** Защита от повторной обработки одного и того же содержимого файла. */
  private lastRaw: string | null = null;

  constructor(
    private readonly workspaceDir: string,
    private readonly logger: CiLogger,
    private readonly onUpdate: (update: LocalWatchUpdate) => void,
    /** Период страховочной проверки, мс. */
    private readonly pollIntervalMs = 1000
  ) {}

  /** Путь к файлу состояния. */
  get statePath(): string {
    return path.join(this.workspaceDir, LOCAL_STATE_RELATIVE_PATH);
  }

  /** Путь к файлу-признаку активности редактора. */
  get heartbeatPath(): string {
    return path.join(this.workspaceDir, LOCAL_HEARTBEAT_RELATIVE_PATH);
  }

  /**
   * Отметка активности редактора.
   *
   * Содержит время в миллисекундах: скрипту достаточно сравнить его с текущим,
   * поэтому формат намеренно максимально простой.
   */
  writeHeartbeat(): void {
    if (this.disposed) return;

    try {
      fs.mkdirSync(path.dirname(this.heartbeatPath), { recursive: true });
      fs.writeFileSync(this.heartbeatPath, String(Date.now()), 'utf8');
    } catch (error) {
      this.logger.debug(`Не удалось обновить признак активности редактора: ${String(error)}`);
    }
  }

  /** Запуск наблюдения: немедленное чтение, затем watch + таймер. */
  start(): void {
    if (this.disposed) return;

    this.writeHeartbeat();
    this.readAndReport();

    const directory = path.dirname(this.statePath);
    try {
      fs.mkdirSync(directory, { recursive: true });
      this.watcher = fs.watch(directory, (_event, filename) => {
        // События приходят и на другие файлы в каталоге логов.
        if (filename && filename !== path.basename(this.statePath)) return;
        this.readAndReport();
      });
      this.watcher.on('error', (error) => {
        // Наблюдение может не работать (сеть, права) — таймер продолжит опрос.
        this.logger.debug(`Наблюдение за файлом состояния недоступно: ${String(error)}`);
      });
    } catch (error) {
      this.logger.debug(`Не удалось начать наблюдение за файлом состояния: ${String(error)}`);
    }

    this.timer = setInterval(() => {
      // Признак активности обновляется вместе с опросом: один таймер вместо двух.
      this.writeHeartbeat();
      this.readAndReport();
    }, this.pollIntervalMs);
  }

  /** Остановка наблюдения без освобождения ресурсов. */
  stop(): void {
    if (this.watcher) {
      this.watcher.close();
      this.watcher = null;
    }
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /**
   * Чтение файла состояния и передача результата наверх.
   * Частота вызовов здесь высокая, поэтому при неизменном содержимом
   * обработчик не вызывается повторно.
   */
  private readAndReport(): void {
    if (this.disposed) return;

    const statePath = this.statePath;
    let raw: string;

    try {
      raw = fs.readFileSync(statePath, 'utf8');
    } catch {
      // Файла нет — это норма: локальный прогон ещё не запускался.
      if (this.lastRaw !== null) {
        this.lastRaw = null;
        this.onUpdate({ run: null, exists: false, stale: false, statePath });
      }
      return;
    }

    // Сравнение по содержимому, а не по mtime: скрипт перезаписывает файл
    // целиком, поэтому одинаковый текст означает отсутствие изменений.
    if (raw === this.lastRaw) return;
    this.lastRaw = raw;

    const run = parseLocalRunState(raw);
    if (!run) {
      this.logger.debug('Файл состояния локального прогона не разобран — плашка не обновлена.');
      return;
    }

    const stale = isStale(run);
    if (stale) {
      this.logger.debug(
        `Локальный прогон #${run.runNumber} не обновлялся и считается зависшим.`
      );
    }

    this.onUpdate({ run, exists: true, stale, statePath });
  }

  /** Кнопка сброса: забыть прочитанное состояние, чтобы файл перечитался. */
  reset(): void {
    this.lastRaw = null;
  }

  /** Полный перезапуск наблюдения. */
  restart(): void {
    this.stop();
    this.reset();
    this.start();
  }

  dispose(): void {
    this.disposed = true;
    this.stop();
  }
}

/** Путь к файлу состояния в рабочей области (для подсказки и команд). */
export function localStatePath(workspaceDir: string): vscode.Uri {
  return vscode.Uri.file(path.join(workspaceDir, LOCAL_STATE_RELATIVE_PATH));
}
