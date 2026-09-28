using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Отчёт об изменении состояния после события (сон, ночлег, отдых).
///
/// Автор просил: «после событий типа ночлега, сна и отдыха в журнал событий
/// выводить отчёт о том, как и какие показатели изменились, какие получены
/// перки». Отчёт — это ПРАВИЛО сравнения двух состояний, поэтому он проверяется
/// расчётом: неверное сравнение не заметно ни сборке, ни статической проверке,
/// а игрок увидит в журнале ложь о своём состоянии.
/// </summary>
public sealed class CharacterStateReportTests
{
    [Fact]
    public void ReportsChangedScalesWithDirectionAndPercent()
    {
        var before = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(20d),
            Hydration = PlayerConditionScale.FromPercent(50d)
        };

        var after = before with
        {
            Energy = PlayerConditionScale.FromPercent(100d),
            Hydration = PlayerConditionScale.FromPercent(25d)
        };

        var lines = CharacterStateReport.Describe(
            before,
            PlayerConditionState.Empty,
            after,
            PlayerConditionState.Empty);

        var energy = lines.Single(line => line.Contains("Энергия"));
        Assert.Contains("20% → 100%", energy);
        Assert.Contains("+80%", energy);

        var hydration = lines.Single(line => line.Contains("Жидкость"));
        Assert.Contains("50% → 25%", hydration);
        Assert.Contains("−25%", hydration);

        // Неизменившиеся шкалы в отчёт не попадают: иначе настоящие изменения
        // тонули бы среди «Здоровье: 100% → 100%».
        Assert.DoesNotContain(lines, line => line.Contains("Устойчивость"));
    }

    [Fact]
    public void ReportsStressChangeAndCumulativeScaledOnlyWhenChanged()
    {
        var before = PlayerVitalsState.Default with
        {
            Fatigue = PlayerConditionScale.FromPercent(90d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(44d)
        };

        var after = CharacterVitalsEngine.CompleteHotelSleep(before, conditions);

        var lines = CharacterStateReport.Describe(
            before,
            conditions,
            after.Vitals,
            after.Conditions);

        var stress = lines.Single(line => line.Contains("Стресс"));
        Assert.Contains("44% → 0%", stress);

        // Кумулятивные шкалы, НЕ изменившиеся, не печатаются.
        Assert.DoesNotContain(lines, line => line.Contains("Истощение энергии"));
        Assert.DoesNotContain(lines, line => line.Contains("Истощение жидкости"));
    }

    [Fact]
    public void ReportsGainedAndLostEffects()
    {
        var before = PlayerConditionState.Empty with
        {
            Effects =
            [
                new ActivePlayerEffectState("unkempt", "Неопрятный", 3600d, true)
            ]
        };

        var after = before with
        {
            Effects =
            [
                new ActivePlayerEffectState("rested", "Отдохнувший", 3600d, false)
            ]
        };

        var lines = CharacterStateReport.Describe(
            PlayerVitalsState.Default,
            before,
            PlayerVitalsState.Default,
            after);

        var gained = lines.Single(line => line.Contains("Отдохнувший"));
        Assert.Contains("Получен бафф", gained);
        // Подпись — имя эффекта: журнал печатает ЕЁ, и ссылка без подписи
        // показала бы игроку «rested» вместо «Отдохнувший».
        Assert.Contains("[[perk:rested:buff:Отдохнувший]]", gained);

        // Снятый дебафф печатается тоже: для игрока он так же важен, как
        // полученный бафф, и умолчание о нём читалось бы как «ничего не было».
        var lost = lines.Single(line => line.Contains("Неопрятный"));
        Assert.Contains("Снят дебафф", lost);
        Assert.Contains("[[perk:unkempt:debuff:Неопрятный]]", lost);
    }

    [Fact]
    public void MetricLinesCarryLinkMarkupForClickableNames()
    {
        var before = PlayerVitalsState.Default with
        {
            Metabolism = PlayerConditionScale.FromPercent(0d)
        };

        var after = before with
        {
            Metabolism = PlayerConditionScale.FromPercent(60d)
        };

        var lines = CharacterStateReport.Describe(
            before,
            PlayerConditionState.Empty,
            after,
            PlayerConditionState.Empty);

        var metabolism = lines.Single(line => line.Contains("Метаболизм"));

        // Разметка ссылки нужна журналу: по ключу он открывает нужный блок
        // монитора, а ПОДПИСЬ печатает как текст — поэтому она человеческая.
        Assert.Contains("[[metric:metabolism:Метаболизм]]", metabolism);
    }

    [Fact]
    public void HygieneHasNoLinkBecauseMonitorHasNoBlockForIt()
    {
        var before = PlayerVitalsState.Default with
        {
            Hygiene = PlayerConditionScale.FromPercent(10d)
        };

        var after = before with
        {
            Hygiene = PlayerConditionScale.FromPercent(100d)
        };

        var lines = CharacterStateReport.Describe(
            before,
            PlayerConditionState.Empty,
            after,
            PlayerConditionState.Empty);

        var hygiene = lines.Single(line => line.Contains("Гигиена"));

        Assert.Contains("10% → 100%", hygiene);
        // Гигиена скрытая: блока в мониторе у неё нет, поэтому ссылка была бы
        // мёртвой — лучше её не ставить вовсе.
        Assert.DoesNotContain("[[metric:hygiene]]", hygiene);
    }

    [Fact]
    public void ReportsNoChangesExplicitlyInsteadOfEmptyList()
    {
        var lines = CharacterStateReport.Describe(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            PlayerVitalsState.Default,
            PlayerConditionState.Empty);

        Assert.Single(lines);
        Assert.Contains("не изменились", lines[0]);
    }

    [Fact]
    public void SubPercentChangeIsReportedInUnits()
    {
        var before = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(50d)
        };

        // Четыре единицы шкалы — это 0.04%: в процентах оно округлилось бы до
        // нуля, и отчёт сказал бы «50% → 50% (+0%)», то есть соврал бы о том,
        // что изменение было.
        var after = before with
        {
            Energy = before.Energy + 4d
        };

        var lines = CharacterStateReport.Describe(
            before,
            PlayerConditionState.Empty,
            after,
            PlayerConditionState.Empty);

        var energy = lines.Single(line => line.Contains("Энергия"));
        Assert.Contains("(+0%, 4 ед.)", energy);
    }
}
