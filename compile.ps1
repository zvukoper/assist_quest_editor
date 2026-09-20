param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\AssistQuestEditor.App\AssistQuestEditor.App.csproj'
$publishDir = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\publish'
$exePath = Join-Path $publishDir 'AssistQuestEditor.exe'

Write-Host "=== Assist Quest Editor: подготовка публикации ===" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $project)) {
    Write-Host "Проект не найден: $project" -ForegroundColor Red
    exit 1
}

# Закрываем запущенный экземпляр перед публикацией.
$running = @(Get-Process -Name 'AssistQuestEditor' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "AssistQuestEditor запущен. Завершаю текущий экземпляр..." -ForegroundColor Yellow
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

# Полностью удаляем старую публикацию, чтобы не осталось файлов от предыдущей сборки.
if (Test-Path -LiteralPath $publishDir) {
    Write-Host "Удаляю предыдущую публикацию: $publishDir"
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host "=== Очистка ===" -ForegroundColor Cyan
& dotnet clean $project -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet clean завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
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
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish завершился с кодом $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host "Публикация завершилась без ожидаемого EXE: $exePath" -ForegroundColor Red
    exit 1
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
Write-Host "Проверка: опубликован ровно один файл." -ForegroundColor Green

# После успешной публикации временные диагностические логи можно удалить.
$logsDir = Join-Path $PSScriptRoot 'MemoryAI\LOGS'
if (Test-Path -LiteralPath $logsDir) {
    Get-ChildItem -LiteralPath $logsDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'README.md' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $logsDir -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoLaunch) {
    Write-Host "=== Запуск ===" -ForegroundColor Cyan
    Start-Process -FilePath $exePath
}