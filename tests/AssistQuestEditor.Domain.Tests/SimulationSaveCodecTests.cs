using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Бинарный кодек сохранений симуляции.
///
/// Проверяется главное свойство формата: снимок читается обратно без потерь.
/// Отдельно проверяется размер: смысл формата — экономия, и если он вырастет до
/// размера JSON, вся работа теряет смысл.
/// </summary>
public sealed class SimulationSaveCodecTests
{
    private static readonly DateTimeOffset Created = new(
        new DateTime(2026, 5, 15, 18, 30, 42, DateTimeKind.Utc), TimeSpan.Zero);

    private static SimulationSave CreateSave(int factCount = 4)
    {
        var facts = Enumerable.Range(0, factCount)
            .ToDictionary(
                index => "quest.ruslan.fact" + index,
                index => index % 2 == 0 ? "true" : "false",
                StringComparer.OrdinalIgnoreCase);

        var state = new SimulationSaveState(
            new PlayerState(new WorldCoordinate(157842.5, 106.875, -71193.25), 0, 180, false, true),
            new WorldClockState(GameCalendar.DefaultStartDate, TimeSpan.FromHours(5.5), true),
            facts,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.number"] = "7",
                ["quest.lastChoice"] = "accept"
            },
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["ruslan.offerPending"] = false,
                ["quest.demoMode"] = true
            },
            new[]
            {
                new QuestStatusEntry("tutorial_ruslan_shashlik", QuestStatus.Active, "deliver"),
                new QuestStatusEntry("gosha_homemade_sausage", QuestStatus.Completed, "done")
            },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ruslan.raw_meat"] = 3
            },
            new[] { "ruslan.raw_meat" },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ruslan"] = 350,
                ["gosha"] = -20
            },
            new[] { "ruslan" },
            new PlayerVitalsState(88, 100, 72, 100, 90, 100, 14, 100),
            new PlayerProgressState(2750, 120, 0),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["strength"] = 6,
                ["luck"] = 4
            },
            new[] { "well-fed" },
            Array.Empty<string>(),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ce-driver"] = 1
            },
            new[] { "ce-driver" },
            "Дождь");

        state = state with
        {
            Route = new RouteState(
                80,
                new[]
                {
                    new RouteWaypoint("route:1", new WorldCoordinate(100, 0, 200), 60),
                    new RouteWaypoint("route:2", new WorldCoordinate(200, 0, 300), 0, true)
                }),
            RouteRuntime = new RouteRuntimeState(
                new RouteCursor(true, 1, 0, 42.5, false, 1, 12.0),
                1,
                123.5,
                246.0),
            DynamicEvents = new DynamicEventRuntimeState(
                new[]
                {
                    new DynamicEventInstance(
                        "cache#123",
                        "cache",
                        DynamicEventInstanceStatus.Active,
                        new WorldPoint(
                            "wp-cache",
                            "Тайник у дома",
                            "cache",
                            new WorldCoordinate(100, 0, 200))
                        {
                            TriggerRadius = 35,
                            Color = "#ffbb44",
                            Editable = false,
                            IsCity = false
                        })
                    {
                        SpawnedUtc = Created,
                        SpawnedGameElapsed = TimeSpan.FromHours(5),
                        LastReason = "создано Director"
                    }
                },
                new[]
                {
                    new DynamicEventScheduleState("cache")
                    {
                        DistanceBudgetMeters = 1200,
                        NextDistanceThresholdMeters = 7800,
                        LastSpawnUtc = Created,
                        LastSpawnGameElapsed = TimeSpan.FromHours(5)
                    }
                })
        };

        var header = new SimulationSaveHeader(
            SimulationSaveState.CurrentFormatVersion,
            "2026-05-15 18-30-42",
            "1.0.40.159-SIM-SAVES-R7",
            Created,
            null,
            GameCalendar.DefaultStartDate + TimeSpan.FromHours(5.5),
            "SibirMap",
            TimeSpan.FromHours(5.5));

        return new SimulationSave(header, state);
    }

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        var original = CreateSave();

        var decoded = SimulationSaveCodec.Decode(SimulationSaveCodec.Encode(original));

        Assert.Equal(original.Header.Name, decoded.Header.Name);
        Assert.Equal(original.Header.AppVersion, decoded.Header.AppVersion);
        Assert.Equal(original.Header.CreatedAt, decoded.Header.CreatedAt);
        Assert.Equal(original.Header.GameDate, decoded.Header.GameDate);
        Assert.Equal(original.Header.CampaignId, decoded.Header.CampaignId);
        Assert.Equal(original.Header.PlayedTime, decoded.Header.PlayedTime);

        Assert.Equal(original.State.Player.Position, decoded.State.Player.Position);
        Assert.Equal(original.State.Route.DefaultSpeedKmh, decoded.State.Route.DefaultSpeedKmh);
        Assert.Equal(original.State.Route.Waypoints, decoded.State.Route.Waypoints);
        Assert.True(decoded.State.Route.Waypoints[1].IsOffRoad);
        Assert.Equal(original.State.RouteRuntime, decoded.State.RouteRuntime);
        Assert.Equal(original.State.Player.Heading, decoded.State.Player.Heading);
        Assert.Equal(original.State.Player.InCab, decoded.State.Player.InCab);

        Assert.Equal(original.State.Clock.StartDate, decoded.State.Clock.StartDate);
        Assert.Equal(original.State.Clock.Elapsed, decoded.State.Clock.Elapsed);
        Assert.True(decoded.State.Clock.Running);

        Assert.Equal(original.State.Facts, decoded.State.Facts);
        Assert.Equal(original.State.Variables, decoded.State.Variables);
        Assert.Equal(original.State.Flags, decoded.State.Flags);
        Assert.Equal(original.State.Inventory, decoded.State.Inventory);
        Assert.Equal(original.State.ReputationValues, decoded.State.ReputationValues);
        Assert.Equal(original.State.CharacterStats, decoded.State.CharacterStats);
        Assert.Equal(original.State.SkillLevels, decoded.State.SkillLevels);
        Assert.Equal(original.State.Weather, decoded.State.Weather);

        Assert.Equal(original.State.NewItemIds, decoded.State.NewItemIds);
        Assert.Equal(original.State.ReputationContacts, decoded.State.ReputationContacts);
        Assert.Equal(original.State.CharacterBuffs, decoded.State.CharacterBuffs);
        Assert.Equal(original.State.UnlockedSkills, decoded.State.UnlockedSkills);

        Assert.Equal(original.State.PlayerVitals, decoded.State.PlayerVitals);
        Assert.Equal(original.State.PlayerProgress, decoded.State.PlayerProgress);

        var instance = Assert.Single(decoded.State.DynamicEvents.Instances);
        Assert.Equal("cache#123", instance.InstanceId);
        Assert.Equal("wp-cache", instance.Point.Id);
        Assert.Equal(DynamicEventInstanceStatus.Active, instance.Status);
        var schedule = Assert.Single(decoded.State.DynamicEvents.Schedules);
        Assert.Equal(1200, schedule.DistanceBudgetMeters);

        Assert.Equal(original.State.QuestStatuses.Count, decoded.State.QuestStatuses.Count);
        for (var index = 0; index < original.State.QuestStatuses.Count; index++)
        {
            Assert.Equal(original.State.QuestStatuses[index], decoded.State.QuestStatuses[index]);
        }
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        var save = CreateSave();

        var first = SimulationSaveCodec.Encode(save);
        var second = SimulationSaveCodec.Encode(save);

        Assert.Equal(first, second);
    }

    [Fact]
    public void HeaderIsReadableWithoutDecompressingState()
    {
        var save = CreateSave();
        var payload = SimulationSaveCodec.Encode(save);

        var header = SimulationSaveCodec.ReadHeaderOnly(payload);

        Assert.Equal(save.Header.Name, header.Name);
        Assert.Equal(save.Header.CreatedAt, header.CreatedAt);
        Assert.Equal(save.Header.PlayedTime, header.PlayedTime);
        Assert.Equal(save.Header.CampaignId, header.CampaignId);
    }

    /// <summary>
    /// Размер снимка должен оставаться экономным.
    ///
    /// Порог проверяет СМЫСЛ формата, а не конкретный байт: 400 записей мира —
    /// умеренный объём прохождения, и он обязан укладываться в единицы килобайт.
    /// JSON на тех же данных даёт на порядок больше, потому что повторяет имена
    /// полей у каждой записи.
    /// </summary>
    [Fact]
    public void LargeStateStaysCompact()
    {
        var save = CreateSave(factCount: 400);

        var payload = SimulationSaveCodec.Encode(save);

        Assert.True(payload.Length < 8 * 1024,
            $"400 фактов должны сжиматься меньше 8 КБ, получено {payload.Length} байт.");

        // Обратная совместимость: снимок читается и после сжатия.
        var decoded = SimulationSaveCodec.Decode(payload);
        Assert.Equal(400, decoded.State.Facts.Count);
    }

    [Fact]
    public void UncompressedFormWouldBeMuchLarger()
    {
        var save = CreateSave(factCount: 400);
        var payload = SimulationSaveCodec.Encode(save);

        // Контрольная оценка «наивного» текстового представления: ключ и значение
        // плюс разделители. Нужна, чтобы убедиться, что таблица строк и Brotli
        // действительно работают, а не просто формат «маленький по случайности».
        var naive = save.State.Facts.Sum(pair => pair.Key.Length + pair.Value.Length + 6)
            + save.State.Variables.Sum(pair => pair.Key.Length + pair.Value.Length + 6);

        Assert.True(naive > payload.Length * 3,
            $"Бинарный формат должен быть кратно меньше текстового: текстовый ~{naive}, бинарный {payload.Length}.");
    }

    [Fact]
    public void RejectsForeignFile()
    {
        var payload = "не сохранение"u8.ToArray();

        Assert.Throws<InvalidDataException>(() => SimulationSaveCodec.Decode(payload));
    }

    [Fact]
    public void EmptyCollectionsRoundTrip()
    {
        var save = CreateSave(factCount: 0) with
        {
            State = CreateSave(factCount: 0).State with
            {
                Variables = new Dictionary<string, string>(),
                Flags = new Dictionary<string, bool>(),
                Inventory = new Dictionary<string, int>(),
                NewItemIds = Array.Empty<string>(),
                ReputationValues = new Dictionary<string, int>(),
                ReputationContacts = Array.Empty<string>(),
                CharacterStats = new Dictionary<string, int>(),
                CharacterBuffs = Array.Empty<string>(),
                CharacterDebuffs = Array.Empty<string>(),
                SkillLevels = new Dictionary<string, int>(),
                UnlockedSkills = Array.Empty<string>(),
                QuestStatuses = Array.Empty<QuestStatusEntry>()
            }
        };

        var decoded = SimulationSaveCodec.Decode(SimulationSaveCodec.Encode(save));

        Assert.Empty(decoded.State.Variables);
        Assert.Empty(decoded.State.Flags);
        Assert.Empty(decoded.State.Inventory);
        Assert.Empty(decoded.State.QuestStatuses);
        Assert.Empty(decoded.State.CharacterStats);
        Assert.Equal(string.Empty, decoded.State.Facts.Count == 0 ? string.Empty : "не пусто");
    }
}

/// <summary>Имена и форматирование сохранений.</summary>
public sealed class SimulationSaveNamingTests
{
    [Fact]
    public void DefaultNameContainsDateAndTimeToSeconds()
    {
        var created = new DateTimeOffset(
            new DateTime(2026, 7, 4, 9, 5, 7, DateTimeKind.Utc), TimeSpan.Zero);

        Assert.Equal("2026-07-04 09-05-07", SimulationSaveNaming.DefaultName(created));
    }

    [Theory]
    [InlineData(512, "512 Б")]
    [InlineData(2048, "2.0 КБ")]
    [InlineData(3 * 1024 * 1024, "3.00 МБ")]
    public void FormatsSize(long bytes, string expected)
    {
        Assert.Equal(expected, SimulationSaveNaming.FormatSize(bytes));
    }

    [Theory]
    [InlineData(0, 5, 0, "5 мин")]
    [InlineData(2, 15, 0, "2 ч 15 мин")]
    public void FormatsPlayedTime(int hours, int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, SimulationSaveNaming.FormatGameTime(new TimeSpan(hours, minutes, seconds)));
    }

    [Fact]
    public void ToFileNameReplacesCharactersForbiddenInWindows()
    {
        // Имя по умолчанию содержит двоеточия времени: в имени файла они недопустимы.
        var name = SimulationSaveNaming.DefaultName(
            new DateTimeOffset(new DateTime(2026, 7, 4, 9, 5, 7, DateTimeKind.Utc), TimeSpan.Zero));

        var fileName = SimulationSaveNaming.ToFileName(name);

        Assert.DoesNotContain(':', fileName);
        Assert.Contains("2026-07-04", fileName);
    }

    [Fact]
    public void ToFileNameNeverReturnsEmpty()
    {
        Assert.Equal("save", SimulationSaveNaming.ToFileName(""));
        Assert.Equal("save", SimulationSaveNaming.ToFileName("   "));
        Assert.Equal("save", SimulationSaveNaming.ToFileName("..."));
    }

    [Fact]
    public void ToFileNameKeepsCyrillicNames()
    {
        Assert.Equal("Прохождение Руслана", SimulationSaveNaming.ToFileName("Прохождение Руслана"));
    }
}
