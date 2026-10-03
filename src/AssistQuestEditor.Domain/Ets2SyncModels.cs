using System.Security.Cryptography;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Логическая линия профиля ETS2. Имя и hex-каталог могут меняться, поэтому они
/// являются только наблюдаемыми алиасами. При наличии creation_time идентификатор
/// строится независимо от имени и остаётся тем же после переименования профиля.
/// </summary>
public sealed record Ets2ProfileLineage(
    string Id,
    string SteamAccountId,
    string Name,
    DateTimeOffset? CreatedAt,
    bool IsStable,
    string CurrentHexFolder,
    IReadOnlyList<string> KnownHexFolders)
{
    public static Ets2ProfileLineage FromProfile(string steamAccountId, Ets2ProfileData profile)
    {
        var account = string.IsNullOrWhiteSpace(steamAccountId) ? "unknown" : steamAccountId.Trim();
        var name = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? profile.Name
            : profile.ProfileName.Trim();

        var stable = profile.ProfileCreated is not null;
        var seed = stable
            ? account + "|" + profile.ProfileCreated!.Value.ToUniversalTime().ToUnixTimeSeconds()
            : account + "|" + profile.HexFolder + "|" + name;

        var id = "ets2-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];

        return new Ets2ProfileLineage(
            id,
            account,
            name,
            profile.ProfileCreated,
            stable,
            profile.HexFolder,
            new[] { profile.HexFolder });
    }

    public Ets2ProfileLineage Merge(Ets2ProfileData profile)
    {
        var aliases = KnownHexFolders
            .Concat(new[] { profile.HexFolder })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return this with
        {
            Name = string.IsNullOrWhiteSpace(profile.ProfileName) ? Name : profile.ProfileName.Trim(),
            CreatedAt = CreatedAt ?? profile.ProfileCreated,
            CurrentHexFolder = profile.HexFolder,
            KnownHexFolders = aliases
        };
    }
}

/// <summary>
/// Наблюдаемый снимок физического сохранения ETS2.
/// Hash-и относятся к байтам файлов игры и не являются сами по себе признаком
/// изменения с нарушением правил: один и тот же слот закономерно переписывается.
/// </summary>
public sealed record Ets2SaveFingerprint(
    string Slot,
    string Name,
    DateTimeOffset? ModifiedAt,
    long InGameMinutes,
    long Money,
    int Experience,
    int DependencyCount,
    string? MapPath,
    string InfoHash,
    string? GameHash)
{
    public string ContentKey => InfoHash + ":" + (GameHash ?? string.Empty);
}

/// <summary>Известный профиль, который AQE однажды наблюдал.</summary>
public sealed record Ets2SyncKnownProfile(
    string LineageId,
    string SteamAccountId,
    string Name,
    DateTimeOffset? CreatedAt,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool StableIdentity,
    List<string> KnownHexFolders);

/// <summary>Жёсткая привязка линии ETS2 к одному миру AQE.</summary>
public sealed record Ets2SyncBinding(
    string LineageId,
    string WorldId,
    DateTimeOffset BoundAt);

/// <summary>Состояние и первоначальная выборка профилей конкретного мира.</summary>
public sealed record Ets2SyncWorldState(
    string WorldId,
    DateTimeOffset InitializedAt,
    List<string> BaselineLineageIds,
    bool OnboardingShown);

/// <summary>Индекс сохранённого AQE-чекпоинта, связанного с конкретным ETS2 save.</summary>
public sealed record Ets2SyncCheckpointEntry(
    string Id,
    string WorldId,
    string LineageId,
    DateTimeOffset CreatedAt,
    string FileName,
    Ets2SaveFingerprint Ets2Save,
    string SimulationSaveName);

/// <summary>
/// Единый read-only ledger. Он живёт вне каталога ETS2 и Steam Cloud.
/// </summary>
public sealed record Ets2SyncLedger(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    List<Ets2SyncKnownProfile> Profiles,
    List<Ets2SyncWorldState> Worlds,
    List<Ets2SyncBinding> Bindings,
    List<Ets2SyncCheckpointEntry> Checkpoints)
{
    public const int CurrentSchemaVersion = 1;

    public static Ets2SyncLedger Empty =>
        new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            new List<Ets2SyncKnownProfile>(),
            new List<Ets2SyncWorldState>(),
            new List<Ets2SyncBinding>(),
            new List<Ets2SyncCheckpointEntry>());
}

/// <summary>Событие синхронизации, которое Host превращает в пользовательское сообщение.</summary>
public enum Ets2SyncNotificationKind
{
    InitialSetupRequired,
    NewCareerDetected,
    CareerBound,
    CareerRejected,
    OtherWorldCareerBlocked,
    ProfilesMissing,
    ProfilesReturned,
    CheckpointCreated,
    CheckpointLoaded,
    CheckpointNotFound,
    GameStarted,
    GameStopped
}

public sealed record Ets2SyncNotification(
    Ets2SyncNotificationKind Kind,
    string Message,
    string? LineageId = null,
    string? ProfileName = null);

/// <summary>Кандидат новой карьеры, ожидающий решения пользователя.</summary>
public sealed record Ets2SyncCareerCandidate(
    string LineageId,
    string ProfileName);

/// <summary>Чекпоинт, который можно безопасно применить к состоянию симулятора.</summary>
public sealed record Ets2SyncCheckpointReady(
    Ets2SyncCheckpointEntry Entry,
    SimulationSave Save);
