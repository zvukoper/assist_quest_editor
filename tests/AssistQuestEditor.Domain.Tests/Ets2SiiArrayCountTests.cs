using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Разбор текстового SII на предмет ПОДМЕНЫ СЧЁТЧИКА ЭЛЕМЕНТОМ.
///
/// Найдено на настоящих файлах игры: <c>active_mods: 1</c> стоит перед
/// <c>active_mods[0]</c>, и оба писались в один список значений. Счётчик
/// становился первым «модом», а в окне профилей появлялся мод с именем «1» рядом
/// с настоящим. Игра при этом показывала «Моды (Активно: 1)» — число совпадало, и
/// расхождение выглядело не как ошибка разбора, а как ошибка справочника.
/// </summary>
public sealed class Ets2SiiArrayCountTests
{
    /// <summary>Счётчик перед элементами — вид, в котором его пишет игра.</summary>
    [Fact]
    public void CountBeforeElementsIsNotAnElement()
    {
        const string text = """
            SiiNunit
            {
            user_profile : _nameless.1 {
             face: 121
             active_mods: 1
             active_mods[0]: "sibirmap13_sv_for160|SibirMap 2.13.0"
             customization: 24712
            }

            }
            """;

        var document = Ets2SiiDocument.Parse(text);
        var mods = document.GetValues("user_profile", "active_mods");

        Assert.Single(mods);
        Assert.Equal("sibirmap13_sv_for160|SibirMap 2.13.0", mods[0]);
        Assert.Equal(1L, document.GetArrayCount("user_profile", "active_mods"));

        // Скалярные поля рядом обязаны остаться на месте: разведение счётчиков
        // массивов не должно ломать обычные поля.
        Assert.Equal("121", document.GetString("user_profile", "face"));
        Assert.Equal("24712", document.GetString("user_profile", "customization"));
    }

    /// <summary>
    /// Счётчик ПОСЛЕ элементов — тоже счётчик: порядок строк в SII не гарантирован,
    /// и разбор не имеет права зависеть от него.
    /// </summary>
    [Fact]
    public void CountAfterElementsIsNotAnElement()
    {
        const string text = """
            SiiNunit
            {
            save_container : _nameless.2 {
             dependencies[0]: "dlc|dlc_iberia|Iberia"
             dependencies[1]: "mod|promods|ProMods"
             dependencies: 2
             name: "Первый рейс"
            }

            }
            """;

        var document = Ets2SiiDocument.Parse(text);
        var dependencies = document.GetValues("save_container", "dependencies");

        Assert.Equal(2, dependencies.Count);
        Assert.Contains("dlc|dlc_iberia|Iberia", dependencies);
        Assert.Contains("mod|promods|ProMods", dependencies);
        Assert.DoesNotContain("2", dependencies);
        Assert.Equal(2L, document.GetArrayCount("save_container", "dependencies"));
        Assert.Equal("Первый рейс", document.GetString("save_container", "name"));
    }

    /// <summary>
    /// У массива БЕЗ счётчика значения обязаны читаться как есть: у
    /// <c>dependencies</c> игры счётчика не бывает, и его отсутствие — не повод
    /// отбросить первый элемент.
    /// </summary>
    [Fact]
    public void ArrayWithoutCountKeepsEveryElement()
    {
        const string text = """
            SiiNunit
            {
            user_profile : _nameless.3 {
             user_data[0]: ""
             user_data[1]: "0.002777"
             user_data[2]: 2
            }

            }
            """;

        var document = Ets2SiiDocument.Parse(text);
        var values = document.GetValues("user_profile", "user_data");

        Assert.Equal(3, values.Count);
        Assert.Equal("", values[0]);
        Assert.Equal("0.002777", values[1]);
        Assert.Equal("2", values[2]);
        Assert.Null(document.GetArrayCount("user_profile", "user_data"));
    }

    /// <summary>
    /// Скалярное поле, ЗНАЧЕНИЕ которого совпадает с числом, остаётся скаляром даже
    /// если рядом есть массив. Иначе <c>face: 121</c> уехало бы в счётчики, и лицо
    /// персонажа в окне профилей пропало бы.
    /// </summary>
    [Fact]
    public void ScalarFieldIsNotMistakenForCount()
    {
        const string text = """
            SiiNunit
            {
            user_profile : _nameless.4 {
             active_mods: 3
             active_mods[0]: "a|Мод А"
             active_mods[1]: "b|Мод Б"
             active_mods[2]: "c|Мод В"
             face: 1
            }

            }
            """;

        var document = Ets2SiiDocument.Parse(text);

        Assert.Equal(3, document.GetValues("user_profile", "active_mods").Count);
        Assert.Equal("1", document.GetString("user_profile", "face"));
        Assert.Null(document.GetArrayCount("user_profile", "face"));
        Assert.Equal(3L, document.GetArrayCount("user_profile", "active_mods"));
    }

    /// <summary>
    /// Повтор имени блока в одном файле объединяет поля и НЕ дублирует имя блока:
    /// иначе на каждый слот сохранения приходилась бы лишняя запись в перечне
    /// блоков.
    /// </summary>
    [Fact]
    public void RepeatedBlockMergesFieldsWithoutDuplicateName()
    {
        const string text = """
            SiiNunit
            {
            save_container : _nameless.5 {
             name: "Первый"
            }

            save_container : _nameless.6 {
             name: "Второй"
            }

            }
            """;

        var document = Ets2SiiDocument.Parse(text);

        Assert.Equal(1, document.BlockCount);
        Assert.Equal(new[] { "save_container" }, document.BlockNames);
    }
}
