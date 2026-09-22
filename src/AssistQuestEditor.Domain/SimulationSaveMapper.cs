namespace AssistQuestEditor.Domain;

/// <summary>
/// Переносит состояние между каналами симулятора и снимком сохранения.
///
/// Живёт в Domain, а не в окне симулятора: это чистый маппинг «канал → запись»
/// и «запись → канал», и его нужно покрывать тестами без поднятия WinForms.
/// <see cref="IDataChannelHub"/> — абстракция Domain, поэтому зависимость от UI
/// здесь не возникает.
///
/// Записываются только изменяемые каналы: World и Selection не сохраняются.
/// World — статические точки карты, они восстанавливаются из ресурсов приложения;
/// Selection — временное выделение редактора, и восстанавливать его в новом
/// запуске бессмысленно (пользователь видит точку, которую не выбирал).
/// </summary>
public static class SimulationSaveMapper
{
    /// <summary>Источник записи для журнала каналов.</summary>
    public const string LoadSource = "Загрузка сохранения";

    public static SimulationSaveState Capture(IDataChannelHub hub, string campaignId)
    {
        ArgumentNullException.ThrowIfNull(hub);

        var facts = hub.Get<FactState>("facts").Value.Values;
        var states = hub.Get<RuntimeStatesState>("states").Value;
        var inventory = hub.Get<InventoryState>("inventory").Value;
        var reputation = hub.Get<ReputationState>("reputation").Value;
        var character = hub.Get<CharacterState>("character").Value;
        var progress = hub.Get<PlayerProgressState>("player-progress").Value;
        var vitals = hub.Get<PlayerVitalsState>("player-vitals").Value;
        var clock = hub.Get<WorldClockState>("sim-time").Value;

        return new SimulationSaveState(
            hub.Get<PlayerState>("player").Value,
            clock,
            new Dictionary<string, string>(facts, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(states.Variables, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(states.Flags, StringComparer.OrdinalIgnoreCase),
            hub.Get<QuestStatusesState>("quest-statuses").Value.Quests.ToArray(),
            new Dictionary<string, int>(inventory.Items, StringComparer.OrdinalIgnoreCase),
            inventory.NewItemIds.ToArray(),
            reputation.Entries.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Value,
                StringComparer.OrdinalIgnoreCase),
            reputation.Entries
                .Where(pair => pair.Value.Contacted)
                .Select(pair => pair.Key)
                .ToArray(),
            vitals,
            progress,
            new Dictionary<string, int>(character.Stats, StringComparer.OrdinalIgnoreCase),
            character.Buffs.ToArray(),
            character.Debuffs.ToArray(),
            character.Skills.ToDictionary(
                skill => skill.Id,
                skill => skill.Level,
                StringComparer.OrdinalIgnoreCase),
            character.Skills.Where(skill => skill.Unlocked).Select(skill => skill.Id).ToArray(),
            hub.Get<EnvironmentState>("environment").Value.Weather);
    }

    /// <summary>
    /// Применяет снимок к каналам симулятора.
    ///
    /// Каждый канал записывается отдельно: так журнал каналов показывает, что
    /// именно изменила загрузка сохранения, а UI обновляется по обычным событиям
    /// каналов, без особой ветки «после загрузки».
    /// </summary>
    public static void Apply(IDataChannelHub hub, SimulationSaveState state)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(state);

        hub.Get<PlayerState>("player").Set(state.Player, LoadSource);

        hub.Get<FactState>("facts").Set(
            new FactState(new Dictionary<string, string>(state.Facts, StringComparer.OrdinalIgnoreCase)),
            LoadSource);

        hub.Get<RuntimeStatesState>("states").Set(
            new RuntimeStatesState(
                new Dictionary<string, bool>(state.Flags, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(state.Variables, StringComparer.OrdinalIgnoreCase),
                null,
                null),
            LoadSource);

        hub.Get<QuestStatusesState>("quest-statuses").Set(
            new QuestStatusesState(state.QuestStatuses.ToArray()),
            LoadSource);

        hub.Get<InventoryState>("inventory").Set(
            new InventoryState(
                new Dictionary<string, int>(state.Inventory, StringComparer.OrdinalIgnoreCase),
                state.NewItemIds.ToArray()),
            LoadSource);

        hub.Get<ReputationState>("reputation").Set(
            BuildReputation(state),
            LoadSource);

        hub.Get<PlayerVitalsState>("player-vitals").Set(state.PlayerVitals, LoadSource);
        hub.Get<PlayerProgressState>("player-progress").Set(state.PlayerProgress, LoadSource);

        hub.Get<CharacterState>("character").Set(
            BuildCharacter(hub, state),
            LoadSource);

        hub.Get<WorldClockState>("sim-time").Set(
            state.Clock with { Running = false },
            LoadSource);

        // Погода — часть Environment, поэтому остальные поля кадра сохраняются
        // как есть: снимок хранит только то, что относится к прохождению.
        var environment = hub.Get<EnvironmentState>("environment").Value;
        hub.Get<EnvironmentState>("environment").Set(
            environment with { Weather = state.Weather },
            LoadSource);
    }

    private static ReputationState BuildReputation(SimulationSaveState state)
    {
        var entries = new Dictionary<string, NpcReputationEntry>(StringComparer.OrdinalIgnoreCase);
        var contacts = new HashSet<string>(state.ReputationContacts, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in state.ReputationValues)
        {
            entries[pair.Key] = new NpcReputationEntry(
                pair.Key,
                pair.Value,
                contacts.Contains(pair.Key));
        }

        // Контакты без репутации тоже важны: факт знакомства с НПЦ влияет на
        // видимость в списке, даже если значение осталось нулевым.
        foreach (var npcId in contacts)
        {
            if (!entries.ContainsKey(npcId))
                entries[npcId] = new NpcReputationEntry(npcId, 0, Contacted: true);
        }

        return new ReputationState(entries);
    }

    /// <summary>
    /// Собирает канал персонажа.
    ///
    /// Скиллы берутся из каталога: снимок хранит только уровень и признак
    /// разблокировки, а описания и подписи уровней — это контент приложения, и
    /// дублировать его в каждом сохранении не нужно.
    /// </summary>
    private static CharacterState BuildCharacter(IDataChannelHub hub, SimulationSaveState state)
    {
        var skills = hub.Get<CharacterState>("character").Value.Skills
            .Select(skill =>
            {
                var level = state.SkillLevels.TryGetValue(skill.Id, out var saved) ? saved : skill.Level;
                var unlocked = state.UnlockedSkills.Contains(skill.Id, StringComparer.OrdinalIgnoreCase);
                return skill with { Level = level, Unlocked = unlocked };
            })
            .ToArray();

        return new CharacterState(
            new Dictionary<string, int>(state.CharacterStats, StringComparer.OrdinalIgnoreCase),
            skills,
            state.CharacterBuffs.ToArray(),
            state.CharacterDebuffs.ToArray());
    }
}
