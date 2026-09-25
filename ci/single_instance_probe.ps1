<#
.SYNOPSIS
    Проверка единственного экземпляра приложения (single instance).

.DESCRIPTION
    Запускает опубликованный AssistQuestEditor.exe, затем второй раз с путём к
    файлу. Ожидается:
        1) работает ровно один процесс;
        2) второй запуск завершается сам, не создавая окна;
        3) путь доходит до первого экземпляра (записи в журнале).

    Проверка требует опубликованного exe: single instance — поведение процесса,
    а не домена, поэтому Playwright/юнит-тестом его не проверить.

    Запускается как отдельная проверка в ci\run_local.ps1 при наличии publish.
#>
param(
    [string]$ExePath,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $RepositoryRoot 'bin\Release\net10.0-windows\win-x64\publish\AssistQuestEditor.exe'
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "Single instance probe: пропущено, exe не найден: $ExePath" -ForegroundColor Yellow
    exit 0
}

# Проверка запускает exe с ключом -citest, но опубликованный файл мог остаться от
# прошлой сборки и про этот ключ ничего не знать. Тогда он принимает его за путь к
# файлу, ничего не находит и запускается как обычный пользовательский старт —
# вместе с установочными сценариями. Проверка на таком файле бессмысленна и, что
# хуже, ложно падает, поэтому устаревший exe честно помечается пропущенным.
function Get-ExpectedNumericVersion {
    $versionFile = Join-Path $RepositoryRoot 'src\AssistQuestEditor.App\Core\VersionInfo.cs'
    if (-not (Test-Path -LiteralPath $versionFile)) { return $null }

    $match = [regex]::Match(
        [IO.File]::ReadAllText($versionFile),
        'NumericVersion\s*=\s*"([^"]+)"')

    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

$exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
$expectedVersion = Get-ExpectedNumericVersion

if ($expectedVersion -and $exeVersion.FileVersion -ne $expectedVersion) {
    Write-Host 'Single instance probe: пропущено, опубликованный exe устарел.' -ForegroundColor Yellow
    Write-Host "  exe: $($exeVersion.FileVersion); ожидается: $expectedVersion" -ForegroundColor Yellow
    Write-Host '  Соберите публикацию: .\compile.ps1 -NoLaunch' -ForegroundColor Yellow
    exit 0
}

# Версия совпала, но publish мог быть собран из другого коммита: поведение тоже
# не соответствует текущему коду, и ключ -citest мог появиться позже сборки.
$expectedCommit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Out-String).Trim()
$product = [string]$exeVersion.ProductVersion
$productCommit = if ($product -like '*+*') { $product.Substring($product.IndexOf('+') + 1) } else { '' }

if ($expectedCommit -and $productCommit -and $productCommit -ne $expectedCommit) {
    Write-Host 'Single instance probe: пропущено, publish собран из другого коммита.' -ForegroundColor Yellow
    Write-Host "  publish: $($productCommit.Substring(0, [Math]::Min(12, $productCommit.Length)))" -ForegroundColor Yellow
    Write-Host "  HEAD:    $($expectedCommit.Substring(0, 12))" -ForegroundColor Yellow
    exit 0
}

$logPath = Join-Path $RepositoryRoot 'MemoryAI\LOGS\assist_quest_editor.log'
$target = Join-Path $RepositoryRoot 'data\quests\tutorial_ruslan_shashlik.aqquest'

# Читаются только строки, появившиеся после старта пробы. Иначе маркер от
# прошлого прогона засчитался бы как успех текущего, и проверка молча перестала
# бы что-либо проверять.
$script:LogOffset = 0

function Get-AppLogLength {
    if (-not (Test-Path -LiteralPath $logPath)) { return 0 }
    return (Get-Item -LiteralPath $logPath).Length
}

function Get-NewAppLogLines {
    if (-not (Test-Path -LiteralPath $logPath)) { return @() }

    # Журнал в этот момент дописывается приложением, поэтому файл открывается с
    # общим доступом на чтение и запись: обычное чтение упало бы на блокировке.
    $stream = $null
    try {
        $stream = New-Object IO.FileStream(
            $logPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite)
    }
    catch {
        return @()
    }

    try {
        if ($stream.Length -le $script:LogOffset) { return @() }
        $stream.Seek($script:LogOffset, [IO.SeekOrigin]::Begin) | Out-Null
        $buffer = New-Object byte[] ($stream.Length - $script:LogOffset)
        $read = $stream.Read($buffer, 0, $buffer.Length)
        $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        return @($text -split "`r?`n" | Where-Object { $_ })
    }
    finally {
        $stream.Dispose()
    }
}

function Wait-ForLogMarker {
    param([string]$Pattern, [int]$TimeoutMs = 30000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (Get-NewAppLogLines | Select-String -Pattern $Pattern -Quiet) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Stop-ProbeApp {
    Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

Stop-ProbeApp

# Отсчёт ведётся от текущего конца журнала: интересуют только строки этого
# прогона.
$script:LogOffset = Get-AppLogLength

$failures = New-Object System.Collections.Generic.List[string]

try {
    # Первый экземпляр: CI test; установочный сценарий не должен выполняться.
    $first = Start-Process -FilePath $ExePath -ArgumentList @('-citest') -PassThru
    if (-not (Wait-ForLogMarker -Pattern 'Single instance: экземпляр первый')) {
        $failures.Add('Первый экземпляр не зафиксировал захват мьютекса.')
    }

    if (-not (Wait-ForLogMarker -Pattern 'Startup: режим=CI test')) {
        $failures.Add('Приложение не зафиксировало режим -citest.')
    }

    if (Get-NewAppLogLines | Select-String -Pattern 'CampaignInstaller:' -Quiet) {
        $failures.Add('В режиме -citest был запущен CampaignInstaller.')
    }

    # Второй экземпляр с путём к файлу: должен передать путь и завершиться.
    $second = Start-Process -FilePath $ExePath -ArgumentList @('-citest', ('"' + $target + '"')) -PassThru
    if (-not $second.WaitForExit(30000)) {
        $failures.Add('Второй экземпляр не завершился: ожидалась передача запроса.')
        try { $second.Kill() } catch { }
    }
    elseif ($second.ExitCode -ne 0) {
        $failures.Add("Второй экземпляр завершился с кодом $($second.ExitCode).")
    }

    Start-Sleep -Milliseconds 700

    $running = @(Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue)
    if ($running.Count -ne 1) {
        $failures.Add("Ожидался ровно один процесс, найдено: $($running.Count).")
    }

    if (-not (Wait-ForLogMarker -Pattern 'Single instance: получен запрос открытия')) {
        $failures.Add('Первый экземпляр не получил запрос открытия по каналу.')
    }

    # Обработка запроса ждётся С ТАЙМАУТОМ, а не читается сразу. Заставка
    # удерживается 4 секунды ДО создания главного окна, а окна до этого нет —
    # показать нечего, и запрос обрабатывается только после его появления.
    # Немедленное чтение требовало бы, чтобы окно существовало мгновенно, то
    # есть запрещало бы саму задержку запуска.
    if (-not (Wait-ForLogMarker -Pattern 'Внешний запуск: обработка запроса')) {
        $failures.Add('Первый экземпляр не обработал внешний запуск.')
    }
}
finally {
    Stop-ProbeApp
}

if ($failures.Count) {
    Write-Host 'ОШИБКА: single instance probe' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host ('- ' + $_) -ForegroundColor Red }
    exit 1
}

Write-Host 'Single instance probe: OK' -ForegroundColor Green
