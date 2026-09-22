using System.Text.Json;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Быстрое чтение версии схемы ресурса.
///
/// Нужно отдельно от полной десериализации: проверка целостности обязана
/// работать с любым файлом, включая несовместимый, и не падать на нём. Полная
/// загрузка такого файла завершилась бы исключением, а проверка должна лишь
/// сообщить о несовместимой схеме.
/// </summary>
public static class ResourceSchemaProbe
{
    /// <summary>
    /// Расширения, у которых есть поле schemaVersion. Посторонние файлы
    /// (например, PNG портрета) схемы не имеют, и это не ошибка.
    /// </summary>
    private static readonly HashSet<string> SchemaAwareExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".aqquest", ".aqscene" };

    /// <summary>
    /// Требуется ли этому файлу версия схемы.
    ///
    /// Нужно проверке целостности: у ресурса со схемой её отсутствие — это
    /// несовместимый файл, а не «ресурс без схемы». Без этой проверки
    /// повреждённый .aqquest проходил бы сверку, потому что его хеш совпадал
    /// с манифестом, собранным из того же файла.
    /// </summary>
    public static bool IsSchemaAware(string path) =>
        SchemaAwareExtensions.Contains(Path.GetExtension(path));

    /// <summary>Версия схемы или null, если файл её не содержит.</summary>
    public static int? TryReadSchemaVersion(string path)
    {
        if (!SchemaAwareExtensions.Contains(Path.GetExtension(path)))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema))
                return null;

            return schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            // Разобрать файл нельзя — это тоже несовместимость, но о ней сообщит
            // проверка хеша/содержимого: здесь достаточно отсутствия версии.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
