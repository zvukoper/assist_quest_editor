using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Канонический формат записи resource-файлов (.aqquest/.aqscene/.aqcampaign).
///
/// Файлы лежат в репозитории и читаются человеком в диффах и ревью, поэтому
/// сохранение из приложения не должно переписывать документ целиком другим
/// стилем. Зафиксированный вид:
///   - отступ 2 пробела (<see cref="JsonSerializerOptions.WriteIndented"/>);
///   - кириллица литералами, а не \uXXXX (UnsafeRelaxedJsonEscaping);
///   - перевод строки LF без CR (иначе каждый save менял бы все строки);
///   - завершающий перевод строки в конце файла;
///   - UTF-8 без BOM;
///   - порядок ключей: schemaVersion, format, definition
///     (задаётся <c>JsonPropertyOrder</c> на *Document-записях).
///
/// Отдельно от опций обмена с WebView2: там отступы только раздували бы
/// payload сообщения, а расслабленное экранирование не требуется.
/// </summary>
public static class ResourceJsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Чтение остаётся терпимым к регистру: файлы могли быть написаны
        // человеком или прежней версией редактора.
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Сериализует документ в каноническом виде: с завершающим LF.</summary>
    public static string Serialize<T>(T document) =>
        JsonSerializer.Serialize(document, Options) + "\n";

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}
