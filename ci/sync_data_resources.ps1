param(
    [string]$SourceDirectory,
    [string]$TargetDirectory,
    [string]$AppVersion = '',
    [string]$Commit = '',
    [switch]$VerifyOnly
)

<#
.SYNOPSIS
    Синхронизация ресурсной папки data с каталогом публикации и проверка целостности.

.DESCRIPTION
    Решает две задачи сразу:

    1. Публикация ресурсов в каталог приложения. В single-file публикации
       data-ресурсы извлекаются в системный кэш распаковки (%TEMP%\.net\...),
       который живёт по своему правилу и может остаться от прошлой сборки.
       Поэтому ресурсы копируются в папку рядом с EXE и приложение читает
       именно её: файл, который лежит рядом с EXE, виден человеку и поддаётся
       замене.

    2. Манифест. В каталоге ресурсов оказывается data-manifest.json с размером
       и SHA-256 каждого файла и версией его схемы. Проверка целостности
       сравнивает папку с манифестом, поэтому необновлённый или чужой файл
       (например, оставшийся от прошлой сборки .aqquest со старой схемой)
       обнаруживается до запуска приложения, а не в момент открытия квеста.
       Копирование по mtime для этого недостаточно: копия сохраняет время
       исходника, поэтому устаревший файл выглядит свежим.

    Каталог получает ТОЛЬКО набор из манифеста. Файл, которого нет в новой
    сборке (удалённый ресурс, переименованный квест), удаляется: иначе в
    публикации накапливались бы данные от прошлых версий.

    Проверка после копирования обязательна и при сборке, и при запуске:
    - сборка падает, если ресурсы не сошлись с манифестом;
    - приложение сообщает о расхождении вместо молчаливой работы со старыми данными.

.PARAMETER SourceDirectory
    Каталог-источник ресурсов (data в корне репозитория).

.PARAMETER TargetDirectory
    Каталог публикации, куда копируются ресурсы (рядом с EXE).

.PARAMETER AppVersion
    Версия приложения, записывается в манифест для диагностики.

.PARAMETER Commit
    Git-коммит сборки, записывается в манифест для диагностики.

.PARAMETER VerifyOnly
    Ничего не копировать, только проверить target по уже существующему манифесту.
#>

$ErrorActionPreference = 'Stop'

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SchemaVersion([string] $Path) {
    <#
        Версия схемы у .aqquest/.aqscene. Читается как JSON, а не регуляркой:
        поле может стоять в любом порядке и с любыми пробелами. Отсутствие схемы
        у ресурса со схемой — ошибка сборки, а не «ресурс без схемы»: такой файл
        мог бы проехать в публикацию и сломать загрузку.
    #>
    $extension = [IO.Path]::GetExtension($Path)
    $schemaAware = $extension -in @('.aqcampaign', '.aqquest', '.aqscene')

    if (-not $schemaAware) { return $null }

    try {
        $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        throw "Ресурс со схемой не разбирается как JSON: $Path. $($_.Exception.Message)"
    }

    $value = $document.schemaVersion
    if ($null -eq $value) {
        throw "У ресурса нет schemaVersion, а для $extension она обязательна: $Path"
    }

    return [int]$value
}

function Write-Manifest([string] $Directory, [object[]] $Entries) {
    $manifest = [ordered]@{
        manifestVersion = 1
        appVersion      = $AppVersion
        commit          = $Commit
        generatedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
        entries         = @($Entries)
    }

    $path = Join-Path $Directory 'data-manifest.json'
    $json = $manifest | ConvertTo-Json -Depth 6
    # Set-Content -Encoding utf8 в PS 5.1 пишет BOM; для JSON это нежелательно,
    # поэтому запись идёт через .NET с явным UTF-8 без BOM.
    $content = $json + "`n"
    [IO.File]::WriteAllText($path, $content, (New-Object Text.UTF8Encoding($false)))
    return $path
}

function Get-ManifestEntries([string] $Source) {
    # Порядок нормализуется: манифест должен быть побайтово воспроизводимым,
    # иначе каждая сборка переписывает файл без содержательных изменений.
    $files = Get-ChildItem -LiteralPath $Source -Recurse -File |
        Where-Object { $_.Name -ne 'data-manifest.json' } |
        Sort-Object { $_.FullName.Substring($Source.Length).TrimStart('\', '/').Replace('\', '/') }

    $entries = foreach ($file in $files) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\', '/').Replace('\', '/')
        [ordered]@{
            path          = $relative
            length        = $file.Length
            sha256         = Get-Sha256 $file.FullName
            schemaVersion = (Get-SchemaVersion $file.FullName)
        }
    }

    return @($entries)
}

if ([string]::IsNullOrWhiteSpace($TargetDirectory)) {
    throw 'Не задан каталог публикации (-TargetDirectory).'
}

if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $TargetDirectory)) {
        throw "Каталог ресурсов не найден: $TargetDirectory"
    }
    Write-Host "Проверка ресурсов по манифесту: $TargetDirectory" -ForegroundColor Cyan
    return
}

if ([string]::IsNullOrWhiteSpace($SourceDirectory) -or -not (Test-Path -LiteralPath $SourceDirectory)) {
    throw "Каталог-источник ресурсов не найден: $SourceDirectory"
}

# Пути нормализуются сразу: путь может прийти с "..\.." из MSBuild, и тогда
# сравнение длины с FullName найденных файлов даёт срез неверной длины.
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $TargetDirectory)) {
    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
}
$TargetDirectory = (Resolve-Path -LiteralPath $TargetDirectory).ProviderPath

Write-Host "=== Синхронизация ресурсов data ===" -ForegroundColor Cyan
Write-Host "Источник: $SourceDirectory" -ForegroundColor DarkGray
Write-Host "Публикация: $TargetDirectory" -ForegroundColor DarkGray

# Источник должен быть каталогом data именно из репозитория: иначе опечатка в
# пути привела бы к публикации случайного каталога, и это заметили бы только в игре.
$sourceLeaf = Split-Path -Leaf $SourceDirectory
if ($sourceLeaf -ne 'data') {
    throw "Источник должен указывать на каталог data, получено: $sourceLeaf"
}

$entries = Get-ManifestEntries $SourceDirectory
if ($entries.Count -eq 0) {
    throw "В источнике нет ресурсов: $SourceDirectory"
}

$expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $entries) { [void]$expected.Add($entry.path) }

$copied = 0
$skipped = 0
foreach ($entry in $entries) {
    $source = Join-Path $SourceDirectory ($entry.path.Replace('/', '\'))
    $target = Join-Path $TargetDirectory ($entry.path.Replace('/', '\'))
    $targetParent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetParent)) {
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    }

    # Сравнение по хешу, а не по времени: при одном mtime содержимое может
    # различаться, и тогда в публикации остался бы устаревший файл.
    if (Test-Path -LiteralPath $target) {
        $same = (Get-Item -LiteralPath $target).Length -eq $entry.length -and
                (Get-Sha256 $target) -eq $entry.sha256
        if ($same) {
            $skipped++
            continue
        }
    }

    Copy-Item -LiteralPath $source -Destination $target -Force
    $copied++
}

# Удаление лишнего: файл, которого нет в новой сборке, — это ресурс от прошлой
# версии (удалённый или переименованный). Оставлять его нельзя: он не попадёт в
# манифест, но останется в каталоге и будет выглядеть как рабочий ресурс.
$removed = @()
foreach ($existing in Get-ChildItem -LiteralPath $TargetDirectory -Recurse -File) {
    $relative = $existing.FullName.Substring($TargetDirectory.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relative -eq 'data-manifest.json') { continue }
    if (-not $expected.Contains($relative)) {
        Remove-Item -LiteralPath $existing.FullName -Force
        $removed += $relative
    }
}

# Пустые каталоги после удаления убираются, иначе структура копила бы мусор.
Get-ChildItem -LiteralPath $TargetDirectory -Recurse -Directory |
    Sort-Object { $_.FullName.Length } -Descending |
    Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
    Remove-Item -Force

$manifestPath = Write-Manifest $TargetDirectory $entries
Write-Host ("Ресурсы: файлов {0}; скопировано {1}; без изменений {2}; удалено {3}." -f `
    $entries.Count, $copied, $skipped, $removed.Count) -ForegroundColor Green
if ($removed.Count -gt 0) {
    foreach ($name in $removed) { Write-Host "  удалено: $name" -ForegroundColor Yellow }
}
Write-Host "Манифест: $manifestPath" -ForegroundColor Green
