using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Что лежит в архиве `.aqezip`. Читается ДО распаковки.
///
/// Манифест, а не «посмотрим по ходу распаковки»: диалог импорта обязан показать
/// имя, автора, даты и состав архива ДО того, как что-то попадёт на диск. Иначе
/// пользователь соглашается на перезапись, не зная, чем именно перезапишет.
/// </summary>
public sealed record WorldArchiveManifest(
    // Что именно упаковано. Строка, а не enum: формат архива переживёт
    // добавление новых видов содержимого, и старые версии не должны падать на
    // неизвестном значении.
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1)] string Id,
    [property: JsonPropertyOrder(2)] string Name,
    [property: JsonPropertyOrder(3)] string? FullName = null,
    [property: JsonPropertyOrder(4)] string? Description = null,
    [property: JsonPropertyOrder(5)] int Version = 1,
    // Включены ли зависимости. По этому признаку диалог импорта предупреждает,
    // что архив самодостаточен или, наоборот, потребует соседей.
    [property: JsonPropertyOrder(6)] bool IncludesDependencies = false,
    // Состав: относительные пути и размеры. Список, а не счётчик: пользователь
    // должен видеть, ЧТО внутри, а не «13 файлов».
    [property: JsonPropertyOrder(7)] IReadOnlyList<WorldArchiveEntry>? Entries = null,
    // Мир-родитель для кампании и квест-родитель для квеста. Пусто для мира.
    [property: JsonPropertyOrder(8)] string? ParentWorldId = null,
    [property: JsonPropertyOrder(9)] string? ParentCampaignId = null,
    [property: JsonPropertyOrder(10)] ResourceMetadata? Metadata = null);

/// <summary>Элемент состава архива.</summary>
public sealed record WorldArchiveEntry(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] long Bytes);

/// <summary>
/// Виды содержимого архива. Константы в домене: их использует и упаковщик, и
/// диалог импорта, и проверка формата.
/// </summary>
public static class WorldArchiveKinds
{
    public const string World = "world";
    public const string Campaign = "campaign";
    public const string Quest = "quest";
}

/// <summary>
/// Документ манифеста — та же трёхчастная форма, что у остальных ресурсов
/// (schemaVersion / format / содержимое). Единообразие не косметическое:
/// загрузчики и проверки в проекте распознают формат именно по паре
/// «schemaVersion + format», и архив не должен быть исключением.
/// </summary>
public sealed record WorldArchiveManifestDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Format,
    [property: JsonPropertyOrder(2)] WorldArchiveManifest Definition);
