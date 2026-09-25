using System.Globalization;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Сохраняемое состояние игрового мира.
///
/// Хранится как ДЕЛЬТА от исходного состояния песочницы: сохраняются только
/// изменённые записи, а не весь каталог мира. Это принципиально для размера
/// сохранения. Карта мира содержит тысячи статических точек, они не меняются
/// от прохождения и восстанавливаются из ресурсов приложения, поэтому в снимок
/// не попадают.
///
/// <see cref="WorldPoints"/> нужен только для точек, СОЗДАННЫХ во время игры
/// (если такие появятся): статические точки не перечисляются в снимке.
/// </summary>
public sealed record SimulationSaveState(
    PlayerState Player,
    WorldClockState Clock,
    IReadOnlyDictionary<string, string> Facts,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, bool> Flags,
    IReadOnlyList<QuestStatusEntry> QuestStatuses,
    IReadOnlyDictionary<string, int> Inventory,
    IReadOnlyList<string> NewItemIds,
    IReadOnlyDictionary<string, int> ReputationValues,
    IReadOnlyList<string> ReputationContacts,
    PlayerVitalsState PlayerVitals,
    PlayerProgressState PlayerProgress,
    IReadOnlyDictionary<string, int> CharacterStats,
    IReadOnlyList<string> CharacterBuffs,
    IReadOnlyList<string> CharacterDebuffs,
    IReadOnlyDictionary<string, int> SkillLevels,
    IReadOnlyList<string> UnlockedSkills,
    string Weather)
{
    /// <summary>
    /// Состояние World Runtime Director. Поле добавлено как расширение формата:
    /// старые сохранения v1 после чтения получают Empty, без искусственного
    /// восстановления случайных событий.
    /// </summary>
    public DynamicEventRuntimeState DynamicEvents { get; init; } = DynamicEventRuntimeState.Empty;

    /// <summary>Текущая версия формата.</summary>
    public const int CurrentFormatVersion = 5;

    /// <summary>Пользовательский маршрут Симулятора.</summary>
    public RouteState Route { get; init; } = RouteState.Empty;

    /// <summary>Runtime-прогресс движения по маршруту.</summary>
    public RouteRuntimeState RouteRuntime { get; init; } = RouteRuntimeState.Empty;
}

/// <summary>Восстанавливаемая runtime-позиция движения по маршруту.</summary>
public sealed record RouteRuntimeState(
    RouteCursor Cursor,
    int? CurrentTargetWaypointIndex = null,
    double TravelRealSeconds = 0d,
    double TravelGameSeconds = 0d)
{
    public static RouteRuntimeState Empty => new(RouteCursor.Initial);
}

/// <summary>
/// Заголовок сохранения: то, что показывается в списке.
///
/// Отдельный тип от <see cref="SimulationSaveState"/>: список сохранений
/// читается часто (и показывает размер с диска), а само состояние грузится
/// только по требованию. Заголовок лежит первым блоком файла, поэтому список
/// не требует распаковки всего снимка.
/// </summary>
public sealed record SimulationSaveHeader(
    int FormatVersion,
    string Name,
    string AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset GameDate,
    string CampaignId,
    TimeSpan PlayedTime);

/// <summary>
/// Снимок состояния плюс его заголовок.
/// </summary>
public sealed record SimulationSave(
    SimulationSaveHeader Header,
    SimulationSaveState State)
{
    public DateTimeOffset DisplayDate => Header.UpdatedAt ?? Header.CreatedAt;
}

/// <summary>
/// Имя сохранения по умолчанию: дата и время создания до секунды.
///
/// При перезаписи имя НЕ меняется (требование пользователя), поэтому генератор
/// применяется только к новым снимкам.
/// </summary>
public static class SimulationSaveNaming
{
    public static string DefaultName(DateTimeOffset createdAt) =>
        createdAt.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>Размер в человекочитаемом виде: «12,4 КБ».</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " Б";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " КБ";
        return (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " МБ";
    }

    /// <summary>
    /// Превращает отображаемое имя в допустимое имя файла.
    ///
    /// Живёт в Domain, а не в хранилище: это чистое правило именования, и его
    /// нужно покрывать тестами, не поднимая файловую систему. Двоеточие особенно
    /// важно — имя по умолчанию содержит время «18:30:42», недопустимое в имени
    /// файла Windows.
    /// </summary>
    public static string ToFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name?.Length ?? 0);
        foreach (var character in name ?? string.Empty)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '-' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "save" : result;
    }

    /// <summary>
    /// Форматирует игровую продолжительность как «2 ч 15 мин».
    ///
    /// Минуты в снимке показываются с точностью до целых: секунды в списке
    /// сохранений бесполезны и только удлиняют строку.
    /// </summary>
    public static string FormatGameTime(TimeSpan played)
    {
        if (played < TimeSpan.Zero) played = TimeSpan.Zero;
        var totalHours = (int)played.TotalHours;
        return totalHours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours} ч {played.Minutes:00} мин")
            : string.Create(CultureInfo.InvariantCulture, $"{played.Minutes} мин");
    }
}
