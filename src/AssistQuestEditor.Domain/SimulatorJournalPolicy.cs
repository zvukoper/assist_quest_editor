namespace AssistQuestEditor.Domain;

/// <summary>
/// Какие события попадают в журнал событий Симулятора.
///
/// Зачем отдельный тип, а не проверка на месте. Хранилище каналов публикует
/// <c>ChannelChanged</c> на КАЖДУЮ запись значения — это транспортное сообщение
/// для Runtime и живого UI, а не событие мира. Внутренние процессы (накопление
/// усталости и стресса, движение игрока по маршруту, тик игрового времени)
/// меняют значения десятки раз в секунду, поэтому запись их в журнал
/// превращала журнал в нечитаемый поток технических строк, где терялись
/// настоящие события — остановки, смены скорости, выгорание.
///
/// Правило одно и лежит здесь, а не в форме: список каналов, изменения которых
/// не считаются событиями, должен быть явным и проверяемым тестом.
/// </summary>
public static class SimulatorJournalPolicy
{
    /// <summary>
    /// Каналы, изменения которых — внутренний процесс, а не событие мира.
    ///
    /// * <c>player-vitals</c> и <c>player-conditions</c> — усталость, стресс и
    ///   кумулятивные шкалы тикают каждые 250 мс игрового времени; журналу они
    ///   не подлежат ни при движении, ни во сне (сон и выгорание пишут свои
    ///   записи отдельными событиями).
    /// * <c>player</c> — позиция игрока обновляется на каждом шаге маршрута;
    ///   журнал ведут собственные маршрутные события.
    /// * <c>telemetry</c> — показания приборов меняются постоянно.
    /// * <c>sim-time</c> — ход игрового времени.
    /// </summary>
    private static readonly HashSet<string> SilentChannels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "player",
            "player-vitals",
            "player-conditions",
            "telemetry",
            "sim-time"
        };

    /// <summary>
    /// Считается ли событие подлежащим записи в журнал.
    ///
    /// События с собственными типами (маршрут, сон, выгорание, инвентарь,
    /// диалог, опубликованные самим Runtime) журналируются всегда: их источник
    /// и есть смысл записи. Фильтруются только транспортные <c>ChannelChanged</c>
    /// по тихим каналам.
    /// </summary>
    public static bool ShouldJournal(string eventType, IReadOnlyDictionary<string, string>? payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return false;

        if (!eventType.Equals("ChannelChanged", StringComparison.OrdinalIgnoreCase))
            return true;

        var channel = ChannelOf(payload);
        return channel is null || !SilentChannels.Contains(channel);
    }

    /// <summary>Проверка по имени события и каналу — для тестов и вызывающих.</summary>
    public static bool ShouldJournal(string eventType, string? channel) =>
        ShouldJournal(
            eventType,
            channel is null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channel"] = channel
                });

    /// <summary>Имя канала из payload события; <c>null</c>, если его там нет.</summary>
    public static string? ChannelOf(IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null)
            return null;

        return payload.TryGetValue("channel", out var channel) && !string.IsNullOrWhiteSpace(channel)
            ? channel
            : null;
    }
}
