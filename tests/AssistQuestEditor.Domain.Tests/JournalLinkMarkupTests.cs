using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Разметка ссылок в тексте журнала.
///
/// Договор между тем, кто ПИШЕТ разметку (<see cref="CharacterStateReport"/>), и
/// тем, кто её ЧИТАЕТ (окно журнала), — это формат строки. Ошибка в нём не видна
/// ни сборке, ни статической проверке, а проявляется тем, что игрок читает в
/// журнале «rested» вместо «Отдохнувший» или щёлкает по названию, которое никуда
/// не ведёт.
/// </summary>
public sealed class JournalLinkMarkupTests
{
    [Theory]
    // Показатель: значение — ключ блока монитора, подпись — название шкалы.
    [InlineData("metric:energy:Энергия", JournalLinkKind.Metric, "energy", "Энергия")]
    [InlineData("metric:metabolism:Метаболизм", JournalLinkKind.Metric, "metabolism", "Метаболизм")]
    // Перк: значение составное («id:вид»), подпись — третье поле.
    [InlineData("perk:rested:buff:Отдохнувший", JournalLinkKind.Perk, "rested:buff", "Отдохнувший")]
    [InlineData("perk:unkempt:debuff:Неопрятный", JournalLinkKind.Perk, "unkempt:debuff", "Неопрятный")]
    // Предмет: значение — Id, подпись — название.
    [InlineData("item:water.bottle:Вода", JournalLinkKind.Item, "water.bottle", "Вода")]
    [InlineData("item:ruslan.raw_meat:Мясо", JournalLinkKind.Item, "ruslan.raw_meat", "Мясо")]
    public void ParsesLinkWithHumanLabel(
        string payload,
        JournalLinkKind kind,
        string value,
        string label)
    {
        var parsed = JournalLinkMarkup.Parse(payload);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(value, parsed.Value);
        Assert.Equal(label, parsed.Label);

        // Подпись обязана отличаться от значения: если они совпали, значит
        // подпись потерялась и журнал печатает внутренний Id.
        Assert.NotEqual(value, label);
    }

    [Fact]
    public void PerkKindIsSplitFromValue()
    {
        var parsed = JournalLinkMarkup.Parse("perk:rested:buff:Отдохнувший");

        Assert.NotNull(parsed);
        Assert.Equal("rested", parsed!.PerkId);
        Assert.Equal("buff", parsed.PerkKind);
    }

    [Fact]
    public void NonPerkLinksHaveNoPerkKind()
    {
        var metric = JournalLinkMarkup.Parse("metric:energy:Энергия");

        Assert.NotNull(metric);
        Assert.Equal(string.Empty, metric!.PerkKind);
    }

    [Theory]
    [InlineData("unknown:value")]
    [InlineData("metric::Энергия")]
    [InlineData("perk:rested")]
    [InlineData("perk:rested::Отдохнувший")]
    [InlineData("")]
    [InlineData("без разделителя")]
    public void InvalidPayloadIsNotALink(string payload)
    {
        // Неизвестный вид и перк без вида — не ссылки: сделать ссылкой то, что
        // открыть нечем, значит обмануть ожидание клика. Вызывающий напечатает
        // разметку как есть, поэтому сообщение не потеряется.
        Assert.Null(JournalLinkMarkup.Parse(payload));
    }

    [Fact]
    public void PayloadWithoutLabelStillParses()
    {
        // Запасной путь: разметка без подписи печатает значение. Так ссылка,
        // собранная по старой памяти, не ломает строку.
        var parsed = JournalLinkMarkup.Parse("metric:energy");

        Assert.NotNull(parsed);
        Assert.Equal("energy", parsed!.Label);
    }

    [Fact]
    public void EveryLinkTheReportEmitsParses()
    {
        // Сквозная проверка договора: что домен ПИШЕТ, то окно и РАЗБИРАЕТ.
        // Проверять сборку и разбор по отдельности значило бы не заметить
        // расхождение между ними — например, лишнее двоеточие в подписи.
        var before = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(20d),
            Metabolism = PlayerConditionScale.FromPercent(0d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(44d),
            Effects = [new ActivePlayerEffectState("unkempt", "Неопрятный", 3600d, true)]
        };

        var lines = CharacterStateReport.Describe(
            before,
            conditions,
            PlayerVitalsState.Default,
            PlayerConditionState.Empty);

        var links = ExtractLinks(lines).ToArray();

        Assert.NotEmpty(links);

        foreach (var link in links)
        {
            var parsed = JournalLinkMarkup.Parse(link);

            Assert.NotNull(parsed);
            // Подпись обязана быть человеческой: Id внутри разметки виден только
            // окну, а игрок читает подпись — и «energy» вместо «Энергия» это не
            // косметика, а признак потерянной подписи.
            Assert.DoesNotContain(
                parsed!.Label,
                new[] { "energy", "metabolism", "stress", "rested", "unkempt" });
        }

        // Оба вида разметки, которые пишет отчёт, обязаны встретиться: иначе
        // проверка прошла бы на строках, где ссылок нет вовсе.
        Assert.Contains(links, link => link.StartsWith("metric:", StringComparison.Ordinal));
        Assert.Contains(links, link => link.StartsWith("perk:", StringComparison.Ordinal));
    }

    /// <summary>Вытаскивает из строк отчёта всё, что записано как <c>[[...]]</c>.</summary>
    private static IEnumerable<string> ExtractLinks(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var cursor = 0;

            while (true)
            {
                var open = line.IndexOf("[[", cursor, StringComparison.Ordinal);
                if (open < 0)
                    break;

                var close = line.IndexOf("]]", open + 2, StringComparison.Ordinal);
                if (close < 0)
                    break;

                yield return line[(open + 2)..close];
                cursor = close + 2;
            }
        }
    }
}
