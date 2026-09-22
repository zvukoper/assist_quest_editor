using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Формат resource-файлов (.aqquest/.aqscene) — часть контракта: файлы лежат в
/// репозитории и читаются человеком в диффах, поэтому сохранение из приложения
/// не должно переписывать документ другим стилем.
/// </summary>
public sealed class ResourceJsonFormatTests
{
    private static SceneDefinitionDocument SceneDocument() =>
        new(
            1,
            "aqscene",
            new SceneDefinition(
                "ruslan_start",
                "Разговор с Русланом",
                "Описание сцены.",
                new SceneGraph(
                    "ruslan_start",
                    "Разговор с Русланом",
                    new[]
                    {
                        new SceneNode(
                            "start",
                            "SceneStart",
                            "Начало сцены",
                            80,
                            180,
                            new[] { new SocketDefinition("start.out", "Далее", SocketDirection.Output) })
                    },
                    Array.Empty<SceneConnection>()),
                new[] { new SceneDialogue("ruslan.greeting", "Руслан", "Привет.") },
                Array.Empty<SceneChoice>()));

    [Fact]
    public void WritesCyrillicAsLiteralsNotUnicodeEscapes()
    {
        var json = ResourceJsonFormat.Serialize(SceneDocument());

        // Файлы в репозитории содержат литеральную кириллицу: \uXXXX превратил бы
        // каждый русский текст в нечитаемую последовательность и раздул дифф.
        Assert.Contains("Разговор с Русланом", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u04", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesCanonicalKeyOrder()
    {
        var json = ResourceJsonFormat.Serialize(SceneDocument());

        // Канонический порядок: schemaVersion, format, definition.
        var schemaIndex = json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
        var formatIndex = json.IndexOf("\"format\"", StringComparison.Ordinal);
        var definitionIndex = json.IndexOf("\"definition\"", StringComparison.Ordinal);

        Assert.True(schemaIndex >= 0 && formatIndex >= 0 && definitionIndex >= 0);
        Assert.True(schemaIndex < formatIndex, "schemaVersion должен быть первым.");
        Assert.True(formatIndex < definitionIndex, "format должен идти перед definition.");
    }

    [Fact]
    public void WritesIndentedLfAndTrailingNewline()
    {
        var json = ResourceJsonFormat.Serialize(SceneDocument());

        Assert.Contains("\n  \"schemaVersion\": 1,", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);

        // CR сделал бы каждый save изменением всех строк файла.
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeThenDeserializeRoundTripsWithoutByteChanges()
    {
        var document = SceneDocument();
        var first = ResourceJsonFormat.Serialize(document);

        var restored = ResourceJsonFormat.Deserialize<SceneDefinitionDocument>(first);
        Assert.NotNull(restored);

        // Идемпотентность: повторное сохранение не меняет файл.
        Assert.Equal(first, ResourceJsonFormat.Serialize(restored!));

        var second = ResourceJsonFormat.Deserialize<SceneDefinitionDocument>(first)!;
        Assert.Equal(document.Definition.Id, second.Definition.Id);
        Assert.Equal(document.Definition.Description, second.Definition.Description);
        Assert.Equal(document.Definition.Dialogues[0].Speaker, second.Definition.Dialogues[0].Speaker);
    }

    [Fact]
    public void QuestDocumentKeepsFormatAndOrder()
    {
        var document = new QuestDefinitionDocument(
            1,
            "aqquest",
            new QuestDefinition(
                "quest",
                "Квест",
                "Описание квеста.",
                new QuestGraph("quest", "Квест", Array.Empty<QuestNode>(), Array.Empty<QuestConnection>()),
                new[] { "ruslan_start" }));

        var json = ResourceJsonFormat.Serialize(document);

        Assert.Contains("\"format\": \"aqquest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sceneIds\"", json, StringComparison.Ordinal);
        Assert.Contains("Описание квеста.", json, StringComparison.Ordinal);
        Assert.True(
            json.IndexOf("\"format\"", StringComparison.Ordinal) <
            json.IndexOf("\"definition\"", StringComparison.Ordinal));
    }
}
