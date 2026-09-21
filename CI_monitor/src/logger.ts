/**
 * Диагностический журнал расширения.
 *
 * Пишет в OutputChannel «CI Monitor»; по умолчанию подробность — `info`.
 * Настройка `ciMonitor.logLevel` (через настройки VS Code) управляет детализацией.
 */

import * as vscode from 'vscode';

export type LogLevel = 'silent' | 'error' | 'info' | 'debug';

const ORDER: Record<LogLevel, number> = { silent: 0, error: 1, info: 2, debug: 3 };

export class CiLogger {
  private readonly channel: vscode.OutputChannel;
  private level: LogLevel;

  constructor(private readonly readLevel: () => string) {
    this.channel = vscode.window.createOutputChannel('CI Monitor');
    this.level = this.normalize(readLevel());
  }

  private normalize(value: string): LogLevel {
    const normalized = (value ?? '').toLowerCase();
    return normalized in ORDER ? (normalized as LogLevel) : 'info';
  }

  /** Перечитать уровень после изменения настроек. */
  syncLevel(): void {
    this.level = this.normalize(this.readLevel());
  }

  private write(level: LogLevel, message: string): void {
    if (ORDER[level] > ORDER[this.level]) return;
    const stamp = new Date().toISOString().slice(11, 19);
    this.channel.appendLine(`[${stamp}] ${level.toUpperCase()} ${message}`);
  }

  error(message: string): void {
    this.write('error', message);
  }

  info(message: string): void {
    this.write('info', message);
  }

  debug(message: string): void {
    this.write('debug', message);
  }

  /** Показ журнала пользователю. */
  show(): void {
    this.channel.show(true);
  }

  dispose(): void {
    this.channel.dispose();
  }
}
