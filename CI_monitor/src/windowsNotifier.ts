/**
 * Windows-уведомление (всплывающее в центре уведомлений).
 *
 * Почему PowerShell + WinRT, а не готовый модуль:
 *   - модуль BurntToast может быть не установлен, и тогда доставка молча ломается;
 *   - WinRT-классы `Windows.UI.Notifications` есть в любой Windows 10/11, поэтому
 *     доставка не требует ни установки пакетов, ни прав администратора.
 *
 * Уведомление отправляется как всплывающее для ярлыка PowerShell. Текст
 * передаётся через переменные окружения (а не подстановкой в скрипт), поэтому
 * заголовки коммитов, кавычки и другие символы не могут повредить команду и
 * исключают внедрение кода.
 */

import { execFile } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

/**
 * Скрипт доставки.
 *
 * Все входные данные читаются из переменных окружения `CI_MONITOR_TITLE` и
 * `CI_MONITOR_BODY`; в самом скрипте нет ни одной подстановки.
 */
const TOAST_SCRIPT = `
$ErrorActionPreference = 'Stop'

$title = [Environment]::GetEnvironmentVariable('CI_MONITOR_TITLE')
$body  = [Environment]::GetEnvironmentVariable('CI_MONITOR_BODY')
if (-not $title) { $title = 'CI Monitor' }

# В Windows PowerShell 5.1 типы WinRT не видны сами по себе: их нужно подгрузить
# проекцией с ContentType = WindowsRuntime, иначе New-Object не найдёт тип.
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null

# Класс уведомления обязан совпадать с AppUserModelID вызывающего процесса,
# иначе Windows отклонит показ. Используем тот же ярлык, что и Windows PowerShell.
$appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe'

$xml = @"
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>$([System.Security.SecurityElement]::Escape($title))</text>
      <text>$([System.Security.SecurityElement]::Escape($body))</text>
    </binding>
  </visual>
</toast>
"@

$doc = New-Object Windows.Data.Xml.Dom.XmlDocument
$doc.LoadXml($xml)

$toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
`;

/** Кэш записанного скрипта. */
let scriptPath: string | null = null;

/** Один раз записывает скрипт во временный каталог. */
function ensureScript(): string {
  if (scriptPath && fs.existsSync(scriptPath)) return scriptPath;

  const dir = path.join(os.tmpdir(), 'ci-monitor');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'windows-toast.ps1');
  // PowerShell 5.1 читает .ps1 в кодировке системы, поэтому добавляем BOM:
  // без него русский текст в комментариях и строках превращается в мусор.
  fs.writeFileSync(file, `\uFEFF${TOAST_SCRIPT}`, 'utf8');
  scriptPath = file;
  return file;
}

/** Результат попытки доставки. */
export interface ToastResult {
  delivered: boolean;
  message: string;
}

/**
 * Показывает Windows-уведомление.
 * Никогда не бросает исключение: вызывающий код только записывает результат
 * в журнал, а при сбое дополнительно показывает уведомление средствами редактора.
 */
export async function showWindowsToast(title: string, body: string): Promise<ToastResult> {
  if (process.platform !== 'win32') {
    return { delivered: false, message: 'уведомления Windows доступны только в Windows' };
  }

  let file: string;
  try {
    file = ensureScript();
  } catch (error) {
    return {
      delivered: false,
      message: `не удалось подготовить скрипт уведомления: ${error instanceof Error ? error.message : String(error)}`,
    };
  }

  const env = { ...process.env, CI_MONITOR_TITLE: title, CI_MONITOR_BODY: body };

  return new Promise<ToastResult>((resolve) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', file],
      { env, windowsHide: true, timeout: 8000 },
      (error, stdout, stderr) => {
        if (error) {
          const detail = (stderr || stdout || error.message).trim().split('\n')[0];
          return resolve({ delivered: false, message: detail || 'не удалось показать уведомление' });
        }
        resolve({ delivered: true, message: 'уведомление показано' });
      }
    );
  });
}

/** Уборка временного скрипта (вызывается при освобождении ресурсов). */
export function cleanupToastScript(): void {
  if (!scriptPath) return;
  try {
    fs.unlinkSync(scriptPath);
  } catch {
    // Файл мог быть удалён системой — это не ошибка.
  }
  scriptPath = null;
}
