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

# Проверяем требование: каталог публикации должен содержать только один EXE.
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Force)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].FullName -ne $exePath) {
    Write-Host "Проверка single-file не пройдена. В каталоге публикации находятся:" -ForegroundColor Red
    $publishedFiles | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    exit 1
}

$version = $null
try {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
} catch {
    $version = 'не определена'
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)

Write-Host "=== Публикация завершена ===" -ForegroundColor Green
Write-Host "EXE: $exePath"
Write-Host "Версия: $version"
Write-Host "Размер: $sizeMb MB"
Write-Host "WebView2 cache очищен: $webViewUserDataDir"
Write-Host "Web-ресурсы включены в single-file через IncludeAllContentForSelfExtract." -ForegroundColor Green
Write-Host "Проверка: опубликован ровно один файл." -ForegroundColor Green

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
