param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\AssistQuestEditor.App\AssistQuestEditor.App.csproj'
$binDir = Join-Path $PSScriptRoot 'bin'
$objDir = Join-Path $PSScriptRoot 'obj'
$publishDir = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\publish'
$exePath = Join-Path $publishDir 'AssistQuestEditor.exe'
$webViewUserDataDir = if ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA 'AssistQuestEditor\WebView2'
} else {
    Join-Path $PSScriptRoot 'WebView2'
}

Write-Host "=== Assist Quest Editor: подготовка публикации ===" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $project)) {
    Write-Host "Проект не найден: $project" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.git'))) {
    Write-Host "Корень Git-репозитория не найден: $PSScriptRoot" -ForegroundColor Red
    exit 1
}

function Get-GitValue([string[]] $Arguments) {
    $value = (& git -C $PSScriptRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { return '' }
    return ($value | Out-String).Trim()
}

# Приложение собрано как WinExe (подсистема Windows), поэтому PowerShell НЕ ждёт
# его завершения: `& $exe ...` возвращает управление сразу, а код возврата не
# выставляется вовсе ($LASTEXITCODE остаётся прежним). Замерено: вызов вернулся
# за 3 мс, при этом процесс работал ещё ~280 мс. Последствие: всё, что читалось
# сразу после вызова, читалось из НЕДОПИСАННОГО файла — манифест ресурсов
# записал DemoWorld.aqezip нулевой длины (упаковщик только успел усечь файл), а
# проверка `--verify-resources` не выполнялась вообще. Ждать надо явно, как это
# делает MSBuild: там задача Exec синхронная, и сборка ведёт себя иначе.
#
# Каждый аргумент, кроме ключей, обязан быть В КАВЫЧКАХ: Start-Process склеивает
# список в одну командную строку без экранирования, поэтому путь с пробелом
# («C:\Мои проекты\...») доехал бы до приложения разрезанным на несколько
# аргументов. Тот же приём, что в ci\single_instance_probe.ps1.
function Invoke-ApplicationCommand([string] $Executable, [string[]] $Arguments) {
    return (Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -Wait).ExitCode
}

function Quote-Argument([string] $Value) {
    return '"' + $Value + '"'
}

$repositoryRoot = Get-GitValue @('rev-parse', '--show-toplevel')
$repositoryUrl = Get-GitValue @('remote', 'get-url', 'origin')
if (-not [string]::IsNullOrWhiteSpace($repositoryUrl)) {
    $repositoryUrl = $repositoryUrl -replace '^(https?://)([^/@]+@)', '$1'
}
$repositoryBranch = Get-GitValue @('rev-parse', '--abbrev-ref', 'HEAD')
$repositoryCommit = Get-GitValue @('rev-parse', 'HEAD')

if ([string]::IsNullOrWhiteSpace($repositoryRoot) -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    Write-Host "Не удалось определить контекст Git-сборки." -ForegroundColor Red
    exit 1
}

Write-Host "Git repository: $repositoryRoot" -ForegroundColor DarkGray
Write-Host "Git remote: $repositoryUrl" -ForegroundColor DarkGray
Write-Host "Git branch: $repositoryBranch" -ForegroundColor DarkGray
Write-Host "Git commit: $repositoryCommit" -ForegroundColor DarkGray


# Закрываем запущенный экземпляр перед публикацией.
$running = @(Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "AssistQuestEditor запущен. Завершаю текущий экземпляр..." -ForegroundColor Yellow
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 700
}

# Полностью удаляем старые build/output данные и профиль WebView2.
# Это исключает старый publish, старые Web-ресурсы и браузерный cache из повторного запуска.
foreach ($directory in @($publishDir, $binDir, $objDir, $webViewUserDataDir)) {
    if (Test-Path -LiteralPath $directory) {
        Write-Host "Удаляю старые данные: $directory" -ForegroundColor Yellow
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

if (Test-Path -LiteralPath $publishDir) {
    throw "Не удалось удалить старую папку publish: $publishDir"
}

if (Test-Path -LiteralPath $webViewUserDataDir) {
    throw "Не удалось удалить кэш WebView2: $webViewUserDataDir"
}

Write-Host "=== Восстановление пакетов ===" -ForegroundColor Cyan
& dotnet restore $project
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet restore завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "=== Публикация одного EXE ===" -ForegroundColor Cyan
& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:AssistQuestRepositoryRoot="$repositoryRoot" `
    -p:AssistQuestRepositoryUrl="$repositoryUrl" `
    -p:AssistQuestRepositoryBranch="$repositoryBranch" `
    -p:AssistQuestRepositoryCommit="$repositoryCommit" `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host "Публикация завершилась без ожидаемого EXE: $exePath" -ForegroundColor Red
    exit 1
}

# Проверяем, что исходные Web-ресурсы существуют перед формированием single-file.
$webSourceDir = Join-Path $PSScriptRoot 'src\AssistQuestEditor.App\Web'
$requiredWebFiles = @('main.html', 'main.js', 'simulator.html', 'simulator.js', 'theme.css')
$requiredAssetFiles = @('SplashScreen.png')
foreach ($file in $requiredWebFiles) {
    $sourceFile = Join-Path $webSourceDir $file
    if (-not (Test-Path -LiteralPath $sourceFile)) {
        throw "Отсутствует исходный Web-ресурс: $sourceFile"
    }
}

$assetSourceDir = Join-Path $PSScriptRoot 'src\AssistQuestEditor.App\Assets'
foreach ($file in $requiredAssetFiles) {
    $sourceFile = Join-Path $assetSourceDir $file
    if (-not (Test-Path -LiteralPath $sourceFile)) {
        throw "Отсутствует исходный Asset: $sourceFile"
    }
}

# Проверяем требование: каталог публикации содержит EXE и папку ресурсов data,
# и больше ничего. Папка данных появилась сознательно: в single-file ресурсы
# распаковываются в кэш %TEMP%, который не виден и не заменяется, поэтому рядом
# с EXE лежит проверенная копия по манифесту.
#
# Отчёты (проверки ресурсов и сборки демо-мира) — законные продукты сборки:
# приложение собрано как WinExe без консоли, поэтому его вердикт и итог сидера
# доходят до человека и до CI только файлом. Список обязан идти в ногу со всеми
# отчётами, которые пишутся в этот каталог, иначе проверка состава падает на
# исправной сборке (так пропускался demo-world-report.txt).
$allowedPublishExtra = @('data-verify-report.txt', 'demo-world-report.txt')
$publishedEntries = @(Get-ChildItem -LiteralPath $publishDir -Force)
$unexpected = @($publishedEntries | Where-Object {
    $_.FullName -ne $exePath -and
    $_.Name -ne 'data' -and
    $allowedPublishExtra -notcontains $_.Name
})
if (-not (Test-Path -LiteralPath $exePath) -or $unexpected.Count -gt 0) {
    Write-Host "Проверка состава публикации не пройдена. В каталоге публикации находятся:" -ForegroundColor Red
    $publishedEntries | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    exit 1
}

# Ресурсы рядом с EXE обязательны: без них приложение прочитает данные из кэша
# распаковки single-file, где могут остаться файлы прошлых сборок.
$publishedDataDir = Join-Path $publishDir 'data'
$publishedManifest = Join-Path $publishedDataDir 'data-manifest.json'
if (-not (Test-Path -LiteralPath $publishedManifest)) {
    Write-Host "Ресурсы не опубликованы: не найден $publishedManifest" -ForegroundColor Red
    exit 1
}

# DemoWorld.aqezip должен собираться из текущего DemoWorldSeeder, а не копироваться
# как потенциально устаревший бинарник из source-data. Поэтому после publish
# single-file EXE сам создаёт свежий canonical archive прямо в publish\data.
$demoArchive = Join-Path $publishedDataDir 'DemoWorld.aqezip'
Write-Host "=== Сборка bundled DemoWorld через DemoWorldSeeder ===" -ForegroundColor Cyan
$seederExitCode = Invoke-ApplicationCommand $exePath @(
    '--build-demo-world', '--output', (Quote-Argument $demoArchive))
if ($seederExitCode -ne 0) {
    Write-Host "DemoWorldSeeder завершился с кодом $seederExitCode." -ForegroundColor Red
    exit $seederExitCode
}
if (-not (Test-Path -LiteralPath $demoArchive) -or (Get-Item -LiteralPath $demoArchive).Length -le 0) {
    Write-Host "DemoWorldSeeder не создал ожидаемый архив: $demoArchive" -ForegroundColor Red
    exit 1
}
Write-Host "Bundled DemoWorld: $demoArchive" -ForegroundColor Green

$version = $null
try {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
} catch {
    $version = 'не определена'
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)

# Манифест рядом с EXE описывает содержимое папки data на момент publish, а
# DemoWorld.aqezip собирается ПОЗЖЕ: сборщик архива — сам single-file EXE.
# Поэтому манифест пересобирается по ФАКТИЧЕСКОМУ содержимому папки, и проверка
# целостности выполняется ещё раз. Без этого шага размер и SHA-256 архива в
# манифесте остаются от source-data, и приложение отказывается запускаться с
# сообщением «ресурсы не совпадают с манифестом сборки» (наблюдалось: 5350 в
# архиве против 2030 в манифесте).
$syncScript = Join-Path $PSScriptRoot 'ci\sync_data_resources.ps1'
if (-not (Test-Path -LiteralPath $syncScript)) {
    Write-Host "Скрипт синхронизации ресурсов не найден: $syncScript" -ForegroundColor Red
    exit 1
}

Write-Host "=== Манифест ресурсов под собранный DemoWorld ===" -ForegroundColor Cyan
# SourceDirectory = TargetDirectory намеренно: записи берутся из УЖЕ СОБРАННОЙ
# папки публикации, копировать ничего не нужно — источник истины здесь сам
# собранный архив. Скрипт такой режим поддерживает (лист каталога обязан быть
# 'data'), совпадающие файлы пропускает и переписывает манифест по факту.
& powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $syncScript `
    -SourceDirectory $publishedDataDir `
    -TargetDirectory $publishedDataDir `
    -AppVersion $version `
    -Commit $repositoryCommit
if ($LASTEXITCODE -ne 0) {
    Write-Host "Обновление манифеста ресурсов завершилось с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Вердикт читается из файла отчёта: приложение собрано как WinExe без консоли,
# поэтому stdout вызывающего процесса не читается (тот же приём, что в MSBuild).
$verifyReport = Join-Path $publishDir 'data-verify-report.txt'
if (Test-Path -LiteralPath $verifyReport) {
    Remove-Item -LiteralPath $verifyReport -Force
}

$verifyExitCode = Invoke-ApplicationCommand $exePath @('--verify-resources')

# Отчёт читается только после ЗАВЕРШЕНИЯ процесса (см. Invoke-ApplicationCommand):
# до этого его может не быть вовсе, и «отчёта нет» выглядело бы как обрыв сборки.
if (Test-Path -LiteralPath $verifyReport) {
    foreach ($line in [IO.File]::ReadAllLines($verifyReport)) {
        Write-Host "  $line" -ForegroundColor DarkGray
    }
}

if ($verifyExitCode -ne 0) {
    Write-Host "Проверка ресурсов публикации не пройдена (код $verifyExitCode). Отчёт: $verifyReport" -ForegroundColor Red
    exit $verifyExitCode
}

# Отдельная сверка манифеста с диском. Она закрывает то, с чего проверка
# целостности начаться не может: `ResourceIntegrityChecker` верит манифесту, а
# манифест мог быть собран из НЕДОПИСАННОГО файла. Так это и произошло: `& $exe`
# на WinExe не ждёт процесса, DemoWorld.aqezip записался в манифест нулевой
# длины, и отчёт проверки честно подтвердил согласованность — при том, что
# манифест противоречил диску, а приложение отказывалось стартовать.
$publishCheckScript = Join-Path $PSScriptRoot 'ci\check_publish_resources.ps1'
if (-not (Test-Path -LiteralPath $publishCheckScript)) {
    Write-Host "Скрипт проверки публикации не найден: $publishCheckScript" -ForegroundColor Red
    exit 1
}

& powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $publishCheckScript `
    -PublishDirectory $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Проверка согласованности публикации не пройдена (код $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "=== Публикация завершена ===" -ForegroundColor Green
Write-Host "EXE: $exePath"
Write-Host "Версия: $version"
Write-Host "Размер: $sizeMb MB"
Write-Host "WebView2 cache очищен: $webViewUserDataDir"
Write-Host "Web-ресурсы включены в single-file через IncludeAllContentForSelfExtract." -ForegroundColor Green
Write-Host "Ресурсы опубликованы рядом с EXE: $publishedDataDir" -ForegroundColor Green
Write-Host "Манифест ресурсов: $publishedManifest" -ForegroundColor Green
$resourceCount = @(Get-ChildItem -LiteralPath $publishedDataDir -Recurse -File).Count
Write-Host "Проверка: EXE + папка data, файлов ресурсов $resourceCount." -ForegroundColor Green

# После успешной публикации временные диагностические логи удаляются: если
# сборка прошла, они не нужны. При неудачной сборке логи сохраняются для разбора.
$logsDir = Join-Path $PSScriptRoot 'MemoryAI\LOGS'
$preservedLogs = @('README.md')
# Переменная выше — единый источник истины: тот же список используется и при
# удалении, и при проверке результата. Если развести условия, проверка начнёт
# считать сохранённый файл остатком и ложно сообщит об ошибке очистки.
if (Test-Path -LiteralPath $logsDir) {
    Get-ChildItem -LiteralPath $logsDir -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $preservedLogs -notcontains $_.Name } |
        Remove-Item -Force -ErrorAction Stop

    Get-ChildItem -LiteralPath $logsDir -Directory -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction Stop
}

$remainingLogs = @(Get-ChildItem -LiteralPath $logsDir -Force -ErrorAction SilentlyContinue |
    Where-Object { $preservedLogs -notcontains $_.Name })
if ($remainingLogs.Count -ne 0) {
    throw "Не удалось очистить MemoryAI/LOGS после успешной публикации."
}

if (-not $NoLaunch) {
    Write-Host "=== Запуск ===" -ForegroundColor Cyan
    Start-Process -FilePath $exePath
}
