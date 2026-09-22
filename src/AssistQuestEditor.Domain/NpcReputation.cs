namespace AssistQuestEditor.Domain;

/// <summary>
/// НПЦ, с которым игрок может выстраивать репутацию.
///
/// <see cref="Id"/> — стабильный ключ для нод и состояния (не переименовывается),
/// <see cref="Name"/> — отображаемое имя, <see cref="Avatar"/> — путь к портрету
/// относительно каталога приложения.
///
/// <see cref="SpeakerAliases"/> нужен потому, что Speaker в Dialogue/Choice —
/// это отображаемое имя («Руслан»), а репутация ведётся по Id («ruslan»).
/// Без этого сопоставления контакт нельзя было бы зафиксировать.
/// </summary>
public sealed record NpcDefinition(
    string Id,
    string Name,
    string Avatar,
    IReadOnlyList<string> SpeakerAliases)
{
    public bool MatchesSpeaker(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return false;

        var value = speaker.Trim();

        if (value.Equals(Name, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(Id, StringComparison.OrdinalIgnoreCase))
            return true;

        return SpeakerAliases.Any(alias =>
            alias.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Каталог НПЦ. Отдельный от ItemCatalog, потому что репутация ведётся по людям,
/// а не по фракциям: у каждого НПЦ своя шкала и свой портрет.
/// </summary>
public static class NpcCatalogFactory
{
    /// <summary>Общая заглушка портрета, пока нет реальных изображений НПЦ.</summary>
    public const string DefaultAvatar = "data/images/avatar_placeholder.png";

    public static IReadOnlyList<NpcDefinition> CreateStarter() =>
    [
        new NpcDefinition(
            "ruslan",
            "Руслан",
            DefaultAvatar,
            ["Руслан"]),
        new NpcDefinition(
            "gosha",
            "Гоша",
            DefaultAvatar,
            ["Гоша"])
    ];
}

/// <summary>
/// Репутация одного НПЦ вместе с признаком состоявшегося контакта.
///
/// <see cref="Contacted"/> взводится, когда игрок впервые видит реплику или
/// вариант выбора этого НПЦ. В списке репутации показываются только те НПЦ,
/// с которыми контакт состоялся, поэтому «незнакомцы» не захламляют экран.
/// </summary>
public sealed record NpcReputationEntry(
    string NpcId,
    int Value,
    bool Contacted)
{
    /// <summary>Представление для UI: диапазон, цвет и прогресс.</summary>
    public ReputationView View => ReputationScale.Describe(Value);
}

/// <summary>
/// Репутация всех НПЦ вместе с признаками состоявшихся контактов.
///
/// Единственный источник истины по репутации: канал <c>reputation</c> хранит
/// именно это значение. Словарь, а не список: ноды обращаются по Id, а порядок
/// вывода задаёт UI (сначала контактировавшие, затем по имени).
/// </summary>
public sealed record ReputationState(
    IReadOnlyDictionary<string, NpcReputationEntry> Entries)
{
    public static ReputationState Empty =>
        new(new Dictionary<string, NpcReputationEntry>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Создаёт состояние с предзаполненными НПЦ (репутация 0, без контакта).</summary>
    public static ReputationState ForNpcs(IEnumerable<NpcDefinition> npcs) =>
        new(npcs.ToDictionary(
            npc => npc.Id,
            npc => new NpcReputationEntry(npc.Id, 0, Contacted: false),
            StringComparer.OrdinalIgnoreCase));

    public int ValueOf(string npcId) =>
        Entries.TryGetValue(npcId, out var entry) ? entry.Value : 0;

    public bool IsContacted(string npcId) =>
        Entries.TryGetValue(npcId, out var entry) && entry.Contacted;

    /// <summary>
    /// Возвращает новое состояние с изменённой репутацией.
    /// Заодно взводится контакт: изменить репутацию можно только после
    /// взаимодействия с НПЦ, поэтому отдельный флаг здесь не нужен.
    /// </summary>
    public ReputationState WithChange(string npcId, int delta) =>
        WithEntry(npcId, entry => (entry?.Value ?? 0) + delta, contacted: true);

    /// <summary>Отмечает контакт с НПЦ, не меняя репутацию.</summary>
    public ReputationState WithContact(string npcId) =>
        WithEntry(npcId, entry => entry?.Value ?? 0, contacted: true);

    /// <summary>Устанавливает абсолютное значение (нужно редактору симулятора).</summary>
    public ReputationState WithValue(string npcId, int value) =>
        WithEntry(npcId, _ => value, contacted: true);

    private ReputationState WithEntry(
        string npcId,
        Func<NpcReputationEntry?, int> valueSelector,
        bool contacted)
    {
        if (string.IsNullOrWhiteSpace(npcId))
            return this;

        var current = Entries.TryGetValue(npcId, out var entry) ? entry : null;
        var values = new Dictionary<string, NpcReputationEntry>(Entries, StringComparer.OrdinalIgnoreCase)
        {
            [npcId] = new NpcReputationEntry(
                npcId,
                valueSelector(current),
                current?.Contacted == true || contacted)
        };

        return new ReputationState(values);
    }
}
