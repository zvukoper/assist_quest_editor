namespace AssistQuestEditor.Domain;

/// <summary>
/// Правила выгрузки ресурсов в архив `.aqezip`.
///
/// Отдельный от папки способ выгрузки, а не «то же плюс имя файла»: архив нужен
/// ровно там, где папка неудобна — пересылка одним файлом. Отсюда два следствия,
/// которые и задают правила: содержимое обязано быть СЖАТЫМ (иначе смысла в
/// архиве нет), а пути внутри — безопасными, потому что архив приходит от
/// другого человека.
/// </summary>
public static class WorldArchiveRules
{
    /// <summary>
    /// Метка формата в файле-манифесте. Как и у остальных ресурсов, строка
    /// задаётся один раз: разойтись с читателем она не должна.
    /// </summary>
    public const string FormatName = "aqezip";

    /// <summary>Расширение архива. Используется и в имени файла, и в ассоциациях.</summary>
    public const string Extension = ".aqezip";

    /// <summary>Версия формата архива, которую понимает это приложение.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Имя файла-манифеста внутри архива.</summary>
    public const string ManifestFileName = "archive.json";

    /// <summary>
    /// Имя поставляемого демонстрационного мира в папке данных.
    ///
    /// Константой, а не литералом в двух местах: файл кладёт сборка ресурсов, а
    /// читает приложение, и «Пропустить» ищет именно его.
    /// </summary>
    public const string DemoWorldFileName = "DemoWorld" + Extension;

    /// <summary>
    /// Безопасен ли путь элемента архива.
    ///
    /// Архив приходит ИЗВНЕ, и запись вида `../../Windows/System32/x` при
    /// распаковке ушла бы за пределы папки мира. Это не теория: распаковка без
    /// проверки — известный класс уязвимостей, и полагаться на то, что «архив
    /// сделали мы же», нельзя: архив можно отредактировать после выгрузки.
    ///
    /// Пустой каталог (`campaigns/`) допустим — это обычная запись zip, и
    /// запрещать её значило бы ломать архивы, созданные стандартными
    /// упаковщиками. Запрещены абсолютные пути, выход вверх, пустые сегменты и
    /// двоеточие диска.
    /// </summary>
    public static bool IsSafeEntryPath(string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            return false;

        var normalized = entryPath.Replace('\\', '/').Trim();

        // Каталог помечается завершающим слэшем: он не часть пути, а признак.
        if (normalized.EndsWith('/'))
            normalized = normalized[..^1];

        if (normalized.Length == 0)
            return false;

        // Абсолютный путь (в том числе «\\server\share» после нормализации).
        if (normalized.StartsWith('/'))
            return false;

        // «C:...» — диск. Проверяются два символа, а не поиск ':' вообще:
        // двоеточие встречается в именах файлов, созданных не Windows, но
        // «диск в начале» — это уже абсолютный путь.
        if (normalized.Length >= 2 && normalized[1] == ':')
            return false;

        foreach (var segment in normalized.Split('/'))
        {
            // Пустой сегмент — это «//» или завершающий слэш у файла: и то и
            // другое означает путь, который распаковщики трактуют по-разному.
            if (segment.Length == 0)
                return false;

            if (segment.Equals("..", StringComparison.Ordinal) ||
                segment.Equals(".", StringComparison.Ordinal))
                return false;

            // Символы, запрещённые в именах файлов Windows: путь с ними либо не
            // распакуется, либо распакуется в другое место.
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Сколько места занимает архив относительно исходной папки.
    ///
    /// Нужно интерфейсу: «сжатие 12%» объясняет пользователю, зачем галочка, а
    /// «архив больше папки» — честный ответ на вопрос, почему иногда так бывает
    /// (уже сжатые картинки внутри архива не сжимаются повторно).
    /// </summary>
    public static int CompressionPercent(long sourceBytes, long archiveBytes)
    {
        if (sourceBytes <= 0 || archiveBytes < 0)
            return 0;

        return (int)Math.Round(archiveBytes * 100d / sourceBytes);
    }
}

/// <summary>
/// Правила импорта архива: куда распаковывать и что делать с занятым именем.
///
/// В домене, потому что это ЕДИНСТВЕННОЕ место, где решается судьба чужих
/// данных. Ошибка здесь не «неудобна», а разрушительна: перезапись без
/// предупреждения уничтожает работу автора, а имя папки, собранное по-разному в
/// диалоге и в сторе, привело бы к тому, что диалог обешает одну папку, а
/// запись уходит в другую.
/// </summary>
public static class WorldArchiveImportRules
{
    /// <summary>
    /// Имя папки, куда распакуется мир из архива.
    ///
    /// Строится из ИМЕНИ мира, а не из id: пользователь видит папки глазами, и
    /// «Демо Мир» понятнее, чем «demoworld». Для кампании и квеста имя папки —
    /// это id, потому что их адресация в контенте идёт по id (ссылки на кампанию
    /// в квестах и на сцены в квестах).
    /// </summary>
    public static string TargetFolderName(WorldArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Kind.Equals(WorldArchiveKinds.World, StringComparison.OrdinalIgnoreCase))
        {
            var source = string.IsNullOrWhiteSpace(manifest.FullName) ? manifest.Name : manifest.FullName!;
            var folderName = ResourceNaming.ToFolderName(source);
            if (folderName.Trim().Length > 0)
                return folderName;
        }

        // Кампания и квест адресуются по id; пустой id означает повреждённый
        // манифест, и подставлять туда имя мира было бы хуже, чем отказаться.
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException("В манифесте архива не задан id ресурса.");

        return ResourceNaming.ToFolderName(manifest.Id);
    }

    /// <summary>
    /// Свободное имя папки, если желаемое занято.
    ///
    /// Вариант «переименовать как новый» обязателен: это единственный способ
    /// импортировать архив рядом с уже существующим ресурсом, не тронув его.
    /// Нумерация начинается с 2 — «(1)» рядом с исходным именем читается как
    /// «первая копия», и автор не понимает, где оригинал.
    ///
    /// <paramref name="exists"/> передаётся функцией: правило не должно знать о
    /// файловой системе, иначе его нельзя проверить без диска.
    /// </summary>
    public static string UniqueFolderName(string desired, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desired);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(desired))
            return desired;

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{desired} ({index})";
            if (!exists(candidate))
                return candidate;
        }

        // Тысяча копий одного ресурса — это уже не «переименовать как новый», а
        // признак зацикленного импорта. Явный отказ лучше бесконечного поиска.
        throw new InvalidOperationException(
            "Не удалось подобрать свободное имя для «" + desired + "»: слишком много копий.");
    }
}

