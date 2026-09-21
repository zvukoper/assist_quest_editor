<#
.SYNOPSIS
    Windows-уведомление (всплывающее в центре уведомлений) для скриптов репозитория.

.DESCRIPTION
    Доставка выполняется через WinRT-классы Windows.UI.Notifications. Такой способ
    не требует ни установки модулей (например BurntToast), ни прав администратора,
    и работает на любой Windows 10/11.

    Уведомление показывает отдельный процесс powershell.exe. Причина: проекция
    WinRT-типов доступна в Windows PowerShell 5.1, но не гарантирована в
    PowerShell 7. Вызов дочернего powershell.exe даёт одинаковое поведение
    независимо от того, каким хостом запущен вызывающий скрипт.

    Текст передаётся через переменные окружения, а внутренний скрипт — через
    -EncodedCommand (UTF-16LE base64). Поэтому заголовки коммитов, кавычки,
    переводы строк и другие символы не могут повредить команду.

    Функция никогда не бросает исключение: уведомление является вспомогательным
    действием и не должно ломать сборку или проверки.

.EXAMPLE
    Show-AssistQuestToast -Title 'Проверки не пройдены' -Body 'Сборка отменена.'

.EXAMPLE
    Show-AssistQuestToast -Title 'Готово' -Body 'Публикация успешна.' -TitleId 'AQE'
#>
function Show-AssistQuestToast {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,

        [Parameter(Mandatory = $false)]
        [string]$Body = '',

        # Идентификатор класса уведомления. Позволяет обновлять/замещать
        # предыдущее уведомление того же типа, а не плодить новые.
        [Parameter(Mandatory = $false)]
        [string]$TitleId = 'AssistQuestEditor'
    )

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        Write-Host 'Уведомление Windows пропущено: платформа не Windows.' -ForegroundColor DarkGray
        return $false
    }

    # Внутренний скрипт. Никаких подстановок: значения читаются из окружения.
    $inner = @'
$ErrorActionPreference = 'Stop'

$title = $env:AQE_TOAST_TITLE
$body = $env:AQE_TOAST_BODY
$titleId = $env:AQE_TOAST_TITLE_ID
if (-not $title) { $title = 'Assist Quest Editor' }

# В Windows PowerShell 5.1 типы WinRT не видны сами по себе: их нужно подгрузить
# проекцией с ContentType = WindowsRuntime, иначе New-Object не найдёт тип.
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null

# Класс уведомления обязан совпадать с AppUserModelID вызывающего процесса,
# иначе Windows отклонит показ. Используем ярлык Windows PowerShell.
$appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'

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
'@

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

    # Значения передаём дочернему процессу через окружение, чтобы не собирать
    # командную строку из пользовательских данных.
    $previousTitle = $env:AQE_TOAST_TITLE
    $previousBody = $env:AQE_TOAST_BODY
    $previousTitleId = $env:AQE_TOAST_TITLE_ID

    try {
        $env:AQE_TOAST_TITLE = $Title
        $env:AQE_TOAST_BODY = $Body
        $env:AQE_TOAST_TITLE_ID = $TitleId

        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>$null

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Уведомление Windows не доставлено (код $LASTEXITCODE)." -ForegroundColor DarkGray
            return $false
        }

        return $true
    } catch {
        Write-Host "Уведомление Windows не доставлено: $($_.Exception.Message)" -ForegroundColor DarkGray
        return $false
    } finally {
        $env:AQE_TOAST_TITLE = $previousTitle
        $env:AQE_TOAST_BODY = $previousBody
        $env:AQE_TOAST_TITLE_ID = $previousTitleId
    }
}

<#
.SYNOPSIS
    Проверяет, что расширение CI Monitor запущено и сейчас обновляет признак активности.

.DESCRIPTION
    Требование: при локальном прогоне уведомляет расширение, а штатное
    уведомление скрипта не вызывается. Скрипт не может напрямую узнать о
    запущенном редакторе, поэтому расширение раз в секунду пишет в
    `.ci-state/monitor.heartbeat` текущее время в миллисекундах.

    Файл лежит вне `MemoryAI/LOGS`: та папка очищается после успешной сборки,
    а признак активности должен переживать сборку, иначе уведомление о
    следующем прогоне показал бы скрипт.

    Функция читает этот файл и считает признак свежим, если он обновлялся не
    позднее `MaxAgeSeconds` назад. Свежий признак означает «уведомит расширение»,
    устаревший или отсутствующий — «уведомляет скрипт».

    Вынесено сюда, а не в вызывающий скрипт, чтобы `pull.ps1` и
    `ci/run_local.ps1` не дублировали одну и ту же проверку.

.EXAMPLE
    if (Test-AssistQuestEditorMonitorActive) { 'уведомит расширение' }
#>
function Test-AssistQuestEditorMonitorActive {
    [CmdletBinding()]
    param(
        # Максимальный возраст признака активности в секундах.
        [Parameter(Mandatory = $false)]
        [int]$MaxAgeSeconds = 15,

        # Корень репозитория. По умолчанию — каталог выше этого скрипта.
        [Parameter(Mandatory = $false)]
        [string]$RepositoryRoot = ''
    )

    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Split-Path -Parent $PSScriptRoot
    }

    $heartbeatPath = Join-Path $RepositoryRoot '.ci-state\monitor.heartbeat'
    if (-not (Test-Path -LiteralPath $heartbeatPath)) {
        return $false
    }

    try {
        $raw = ([System.IO.File]::ReadAllText($heartbeatPath)).Trim()
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return $false
        }

        $stamp = [int64]0
        if (-not [int64]::TryParse($raw, [ref]$stamp)) {
            return $false
        }

        $age = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - $stamp
        return $age -ge 0 -and $age -le ($MaxAgeSeconds * 1000)
    } catch {
        return $false
    }
}
