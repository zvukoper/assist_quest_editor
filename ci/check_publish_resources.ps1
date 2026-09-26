<#
.SYNOPSIS
    Проверяет, что каталог публикации описывает САМ СЕБЯ: манифест совпадает с диском.

.DESCRIPTION
    Отдельный шаг, а не строки внутри compile.ps1, нужен ради ПРОВЕРЯЕМОСТИ.
    Дефект, ради которого скрипт написан, был гонкой: приложение собрано как
    WinExe (подсистема Windows), поэтому `& $exe ...` возвращает управление
    сразу, и сборка читала DemoWorld.aqezip в тот момент, когда упаковщик только
    успел усечь файл. Манифест записывал нулевую длину, а проверка
    `--verify-resources` не выполнялась вообще. Симптом доходил до пользователя:
    при запуске показывался диалог «ресурсы не совпадают с манифестом сборки», и
    приложение отказывалось стартовать.

    Гонку нельзя поймать внутри самой сборки: упаковщик может успеть дописать
    файл до того, как синхронизация его прочтёт, и один и тот же дефектный код
    даёт то верный, то пустой манифест (проверено — так и было). Поэтому дефект
    ловится не «поведением сборки», а СВЕРКОЙ ФАКТА: манифест обязан описывать
    то, что лежит на диске, и это утверждение проверяется на готовом каталоге,
    детерминированно.

.PARAMETER PublishDirectory
    Каталог публикации (рядом с EXE лежат папка data и отчёты сборки).
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host $Message -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $PublishDirectory)) {
    Fail "Каталог публикации не найден: $PublishDirectory"
}

# Отчёты пишет приложение (WinExe без консоли), поэтому рядом с EXE законно
# лежат папка ресурсов, отчёты и отпечаток сборки. Любой лишний файл — остаток
# прошлой сборки, который легко принять за данные. Список обязан идти в ногу со
# всеми файлами, которые пишет сборка: иначе проверка падает на исправной сборке.
# Папка data исключается по имени: у каталога Extension пуст, и без этой проверки
# он сам попадал бы в «неожиданные файлы» (так проверка один раз и упала).
$allowedExtra = @(
    'data-verify-report.txt',
    'demo-world-report.txt',
    'web-build.stamp'
)
$entries = @(Get-ChildItem -LiteralPath $PublishDirectory -Force)
$exeFiles = @($entries | Where-Object { $_.Extension -eq '.exe' })
$unexpected = @($entries | Where-Object {
        $_.Name -ne 'data' -and
        $_.Extension -ne '.exe' -and
        $allowedExtra -notcontains $_.Name
    })

if ($exeFiles.Count -ne 1 -or $unexpected.Count -gt 0) {
    $entries | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    Fail 'Состав публикации неверен: ожидался ровно один EXE, папка data и отчёты сборки.'
}

$dataDir = Join-Path $PublishDirectory 'data'
$manifestPath = Join-Path $dataDir 'data-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Fail "Манифест ресурсов не найден: $manifestPath"
}

# Отчёт проверки целостности создаётся ПОСЛЕ завершения приложения (сборка
# удаляет прежний файл перед вызовом). Его наличие — свидетельство, что
# `--verify-resources` действительно выполнился, а не был пропущен из-за
# несинхронного запуска.
$verifyReport = Join-Path $PublishDirectory 'data-verify-report.txt'
if (-not (Test-Path -LiteralPath $verifyReport)) {
    Fail ("Приложение не оставило отчёт о ресурсах: $verifyReport. " +
        'Проверка целостности не выполнилась — сборка не дождалась процесса.')
}

$reportLines = [IO.File]::ReadAllLines($verifyReport)
if ($reportLines.Count -eq 0 -or $reportLines[0] -notmatch 'совпадают') {
    Fail "Проверка целостности ресурсов не подтверждена: $($reportLines[0])"
}
foreach ($line in $reportLines) { Write-Host "  $line" -ForegroundColor DarkGray }

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

# Каждая запись манифеста обязана совпасть с диском по размеру. Именно эта
# сверка ловит «манифест собран из недописанного файла»: нулевая длина либо
# расхождение с фактическим размером.
$mismatches = @()
foreach ($entry in $manifest.entries) {
    $file = Join-Path $dataDir ($entry.path.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $file)) {
        $mismatches += "нет файла: $($entry.path)"
        continue
    }

    $actual = (Get-Item -LiteralPath $file).Length
    if ($entry.length -ne $actual) {
        $mismatches += ("$($entry.path): в манифесте $($entry.length) Б, на диске $actual Б")
    }
}

if ($mismatches.Count -gt 0) {
    foreach ($item in $mismatches) { Write-Host "  $item" -ForegroundColor Red }
    Fail 'Манифест ресурсов расходится с диском. Сборка читала файлы, не дождавшись их записи.'
}

# Архив демо-мира проверяется отдельно и по имени: это единственный ресурс,
# который создаётся ПРИЛОЖЕНИЕМ уже после публикации, поэтому именно он страдал
# от несинхронного запуска. Общая сверка выше его тоже покрывает, но здесь
# важное утверждение звучит явно и не теряется среди прочих записей.
$archiveEntry = @($manifest.entries | Where-Object { $_.path -eq 'DemoWorld.aqezip' })
if ($archiveEntry.Count -ne 1) {
    Fail 'В манифесте ресурсов нет записи DemoWorld.aqezip.'
}

$archiveFile = Join-Path $dataDir 'DemoWorld.aqezip'
$archiveLength = (Get-Item -LiteralPath $archiveFile).Length
if ($archiveEntry[0].length -ne $archiveLength) {
    Fail ("Манифест описывает DemoWorld.aqezip размером $($archiveEntry[0].length) Б, " +
        "а на диске $archiveLength Б. Сборка читала архив, не дождавшись упаковщика.")
}

$resourceCount = @(Get-ChildItem -LiteralPath $dataDir -Recurse -File).Count
Write-Host ("Публикация согласована: EXE, файлов ресурсов $resourceCount, " +
    "DemoWorld.aqezip $archiveLength Б.") -ForegroundColor Green
exit 0
