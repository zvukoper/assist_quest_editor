using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Каталог перков, умений, баффов и дебаффов.
///
/// Проверяется ПЕРЕСЕЧЕНИЕ с движком, а не список сам по себе: каталог нужен
/// ровно затем, чтобы окно знало о существовании эффекта до того, как он выдан.
/// Опечатка в ключе не видна ни сборке, ни статической проверке — она проявляется
/// тем, что действующий бафф в окне не подсвечивается, и игрок решает, что окно
/// сломано.
/// </summary>
public sealed class CharacterPerksCatalogTests
{
    [Theory]
    [InlineData("burnout")]
    [InlineData("rested")]
    [InlineData("relaxation")]
    [InlineData("bull")]
    [InlineData("strong_bones")]
    [InlineData("power_surge")]
    [InlineData("sorbent")]
    [InlineData("analgesia")]
    [InlineData("unkempt")]
    [InlineData("bum")]
    [InlineData("analgesic_overuse")]
    [InlineData("drowsiness")]
    [InlineData("nicotine_rebound")]
    [InlineData("alcohol_aftereffect")]
    [InlineData("caffeine_overuse")]
    [InlineData("caffeine_excess")]
    [InlineData("caffeine_jitter")]
    [InlineData("late_caffeine")]
    [InlineData("caffeine_withdrawal")]
    public void EveryEffectTheEngineCreatesIsInTheCatalog(string effectId)
    {
        var entry = CharacterPerksCatalog.Find(effectId);

        Assert.NotNull(entry);

        // Раздел обязан быть баффом или дебаффом: движок создаёт именно
        // временные эффекты, и «перк» вместо «баффа» отправил бы игрока в другой
        // раздел окна, где нет кнопки деактивации.
        Assert.True(
            entry!.Category is CharacterPerkCategory.Buff or CharacterPerkCategory.Debuff,
            $"«{effectId}» — временный эффект движка, а не {entry.Category}.");
    }

    [Fact]
    public void DurationsMatchTheEngineConstants()
    {
        Assert.Equal(
            CharacterVitalsEngine.RestedDurationRealSeconds,
            CharacterPerksCatalog.Find("rested")!.DurationRealSeconds);

        Assert.Equal(
            CharacterVitalsEngine.RelaxationDurationRealSeconds,
            CharacterPerksCatalog.Find("relaxation")!.DurationRealSeconds);

        Assert.Equal(
            CharacterVitalsEngine.BurnoutDurationRealSeconds,
            CharacterPerksCatalog.Find("burnout")!.DurationRealSeconds);

        Assert.Equal(
            CharacterVitalsEngine.PowerSurgeDurationRealSeconds,
            CharacterPerksCatalog.Find("power_surge")!.DurationRealSeconds);
    }

    [Fact]
    public void EveryEntryExplainsHowItWorksAndWhatItAffects()
    {
        foreach (var entry in CharacterPerksCatalog.Entries)
        {
            // Автор просил описание у КАЖДОГО пункта: пункт без объяснения в окне
            // выглядит недоделанным, а пустое «Влияет» — обещанием, что оно ни на
            // что не влияет.
            Assert.False(string.IsNullOrWhiteSpace(entry.Description), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Affects), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), entry.Id);
        }
    }

    [Fact]
    public void IdsAreUnique()
    {
        var duplicates = CharacterPerksCatalog.Entries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EachCategoryHasEntries()
    {
        // Пустой раздел окна читается как поломка: четыре раздела заявлены, и
        // отсутствие любого из них выглядит как забытая вкладка.
        foreach (var category in Enum.GetValues<CharacterPerkCategory>())
        {
            Assert.NotEmpty(CharacterPerksCatalog.ByCategory(category));
        }
    }

    [Fact]
    public void SkillsAreLevelledAndOthersAreNot()
    {
        foreach (var entry in CharacterPerksCatalog.Entries)
        {
            if (entry.Category == CharacterPerkCategory.Skill)
            {
                // Умение без максимума очков нельзя ни вложить, ни показать:
                // «из 0» — не шкала.
                Assert.True(entry.IsLevelled, entry.Id);
                Assert.True(entry.MaxLevel > 0, entry.Id);
            }
            else
            {
                Assert.False(entry.IsLevelled, entry.Id);
            }
        }
    }

    [Fact]
    public void UnimplementedEntriesAreMarkedInProgress()
    {
        var entry = CharacterPerksCatalog.Find("perk.discharged");

        Assert.NotNull(entry);
        Assert.False(entry!.Implemented);

        // Пометка обязана попадать в имя: окно печатает displayName, и пометка,
        // живущая отдельным полем, до страницы не доехала бы.
        Assert.Contains(
            CharacterPerksCatalog.InProgressSuffix,
            CharacterPerksCatalog.DisplayName(entry, active: false));
    }

    [Fact]
    public void ActiveEntriesAreMarkedActive()
    {
        var entry = CharacterPerksCatalog.Find("burnout")!;

        var name = CharacterPerksCatalog.DisplayName(entry, active: true);

        Assert.Contains(CharacterPerksCatalog.ActiveSuffix, name);
        Assert.Contains(entry.Name, name);
    }

    [Fact]
    public void BothMarksAppearTogetherForUnfinishedActiveEntry()
    {
        var entry = CharacterPerksCatalog.Find("perk.discharged")!;

        var name = CharacterPerksCatalog.DisplayName(entry, active: true);

        // Пункт может быть и незаконченным, и действующим. Умолчание об одной из
        // половин скрыло бы от игрока правду: либо что пункт работает, либо что
        // он ещё не подключён.
        Assert.Contains(CharacterPerksCatalog.InProgressSuffix, name);
        Assert.Contains(CharacterPerksCatalog.ActiveSuffix, name);
    }

    [Fact]
    public void UnknownIdIsNotFound()
    {
        Assert.Null(CharacterPerksCatalog.Find("no-such-perk"));
        Assert.Null(CharacterPerksCatalog.Find(""));
    }

    [Fact]
    public void DurationLabelIsOnlyPrintedWhenThereIsADuration()
    {
        Assert.Contains("мин", CharacterPerksCatalog.Find("burnout")!.DurationLabel);
        Assert.Contains("ч", CharacterPerksCatalog.Find("bull")!.DurationLabel);

        // «Неопрятный» держится по условию, а не по таймеру: печатать ему «0 ч»
        // значило бы обещать срок, которого нет.
        Assert.Equal(string.Empty, CharacterPerksCatalog.Find("unkempt")!.DurationLabel);
    }
}
