using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Read/write store только для собственной области AQE.
/// Ни один метод не пишет в каталог ETS2 или Steam userdata.
/// </summary>
public sealed class Ets2SyncStore
{
    private const string LedgerFileName = "ledger.json";
    private const string CheckpointsFolderName = "checkpoints";

    private readonly string _root;
    private readonly string _ledgerPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private Ets2SyncLedger? _ledger;

    public Ets2SyncStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? AppPaths.Ets2SyncRoot);
        _ledgerPath = Path.Combine(_root, LedgerFileName);
    }

    public Ets2SyncLedger Ledger => _ledger ??= LoadLedger();

    public Ets2SyncLedger LoadLedger()
    {
        if (!File.Exists(_ledgerPath))
            return Ets2SyncLedger.Empty;

        try
        {
            var ledger = JsonSerializer.Deserialize<Ets2SyncLedger>(
                File.ReadAllText(_ledgerPath),
                _jsonOptions);

            if (ledger is null || ledger.SchemaVersion > Ets2SyncLedger.CurrentSchemaVersion)
                return Ets2SyncLedger.Empty;

            _ledger = ledger;
            return ledger;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ets2SyncStore: реестр синхронизации не прочитан.", ex, "path=" + _ledgerPath);
            return Ets2SyncLedger.Empty;
        }
    }

    private void SaveLedger()
    {
        Directory.CreateDirectory(_root);

        var temp = _ledgerPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Ledger, _jsonOptions));
        File.Move(temp, _ledgerPath, true);
    }

    public Ets2SyncWorldState EnsureWorld(
        string worldId,
        IEnumerable<Ets2ProfileLineage> baselineProfiles)
    {
        var world = Ledger.Worlds.FirstOrDefault(item =>
            item.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase));

        if (world is not null)
            return world;

        var baseline = baselineProfiles
            .Select(profile => profile.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        world = new Ets2SyncWorldState(
            worldId,
            DateTimeOffset.UtcNow,
            baseline,
            OnboardingShown: false);

        Ledger.Worlds.Add(world);
        SaveLedger();
        return world;
    }

    public void MarkOnboardingShown(string worldId)
    {
        var index = Ledger.Worlds.FindIndex(item =>
            item.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        var world = Ledger.Worlds[index];
        if (world.OnboardingShown)
            return;

        Ledger.Worlds[index] = world with { OnboardingShown = true };
        SaveLedger();
    }

    public bool IsBaselineProfile(string worldId, string lineageId) =>
        Ledger.Worlds.Any(world =>
            world.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase) &&
            world.BaselineLineageIds.Any(id =>
                id.Equals(lineageId, StringComparison.OrdinalIgnoreCase)));

    public void UpsertKnownProfile(Ets2ProfileLineage lineage, DateTimeOffset seenAt)
    {
        var index = Ledger.Profiles.FindIndex(profile =>
            profile.LineageId.Equals(lineage.Id, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Ledger.Profiles.Add(new Ets2SyncKnownProfile(
                lineage.Id,
                lineage.SteamAccountId,
                lineage.Name,
                lineage.CreatedAt,
                seenAt,
                seenAt,
                lineage.IsStable,
                lineage.KnownHexFolders.ToList()));
            SaveLedger();
            return;
        }

        var current = Ledger.Profiles[index];
        var aliases = current.KnownHexFolders
            .Concat(lineage.KnownHexFolders)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var name = string.IsNullOrWhiteSpace(lineage.Name) ? current.Name : lineage.Name;
        var createdAt = current.CreatedAt ?? lineage.CreatedAt;
        var stableIdentity = current.StableIdentity || lineage.IsStable;
        var lastSeen = seenAt - current.LastSeenUtc >= TimeSpan.FromSeconds(10)
            ? seenAt
            : current.LastSeenUtc;

        var changed =
            !string.Equals(current.Name, name, StringComparison.Ordinal) ||
            current.CreatedAt != createdAt ||
            current.StableIdentity != stableIdentity ||
            current.LastSeenUtc != lastSeen ||
            !current.KnownHexFolders.SequenceEqual(aliases, StringComparer.OrdinalIgnoreCase);

        if (!changed)
            return;

        Ledger.Profiles[index] = current with
        {
            Name = name,
            CreatedAt = createdAt,
            LastSeenUtc = lastSeen,
            StableIdentity = stableIdentity,
            KnownHexFolders = aliases
        };

        SaveLedger();
    }

    public Ets2SyncKnownProfile? FindKnownProfile(string lineageId) =>
        Ledger.Profiles.FirstOrDefault(profile =>
            profile.LineageId.Equals(lineageId, StringComparison.OrdinalIgnoreCase));

    public Ets2SyncBinding? FindBindingForLineage(string lineageId) =>
        Ledger.Bindings.FirstOrDefault(binding =>
            binding.LineageId.Equals(lineageId, StringComparison.OrdinalIgnoreCase));

    public Ets2SyncBinding? FindBinding(string worldId, string lineageId) =>
        Ledger.Bindings.FirstOrDefault(binding =>
            binding.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase) &&
            binding.LineageId.Equals(lineageId, StringComparison.OrdinalIgnoreCase));

    public Ets2SyncBinding? FindBindingForWorld(string worldId) =>
        Ledger.Bindings.FirstOrDefault(binding =>
            binding.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase));

    public bool TryBind(
        string worldId,
        Ets2ProfileLineage lineage,
        out string? conflictWorldId,
        out string? replacedLineageId)
    {
        conflictWorldId = null;
        replacedLineageId = null;

        var existing = FindBindingForLineage(lineage.Id);
        if (existing is not null)
        {
            if (!existing.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase))
            {
                conflictWorldId = existing.WorldId;
                return false;
            }

            return true;
        }

        // У мира может быть ровно ОДНА активная карьера ETS2. Если пользователь
        // подключает новую карьеру, старая привязка заменяется, а Host сбрасывает
        // прохождение мира перед созданием нового чекпоинта.
        var worldBinding = FindBindingForWorld(worldId);
        if (worldBinding is not null)
        {
            Ledger.Bindings.Remove(worldBinding);
            replacedLineageId = worldBinding.LineageId;
        }

        Ledger.Bindings.Add(new Ets2SyncBinding(
            lineage.Id,
            worldId,
            DateTimeOffset.UtcNow));
        SaveLedger();
        return true;
    }

    public IReadOnlyList<Ets2SyncCheckpointEntry> CheckpointsFor(
        string worldId,
        string lineageId)
        => Ledger.Checkpoints
            .Where(entry =>
                entry.WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase) &&
                entry.LineageId.Equals(lineageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CreatedAt)
            .ToArray();

    public Ets2SyncCheckpointEntry? FindExactCheckpoint(
        string worldId,
        string lineageId,
        Ets2SaveFingerprint save)
        => CheckpointsFor(worldId, lineageId)
            .FirstOrDefault(entry =>
                string.Equals(entry.Ets2Save.ContentKey, save.ContentKey, StringComparison.Ordinal) &&
                string.Equals(entry.Ets2Save.Slot, save.Slot, StringComparison.OrdinalIgnoreCase));

    public Ets2SyncCheckpointEntry AddCheckpoint(
        string worldId,
        string lineageId,
        Ets2SaveFingerprint save,
        SimulationSave simulationSave)
    {
        var id = Guid.NewGuid().ToString("N");
        var fileName = id + ".aqcheckpoint";
        var directory = Path.Combine(_root, CheckpointsFolderName, SafeFolder(worldId));
        var path = Path.Combine(directory, fileName);

        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, SimulationSaveCodec.Encode(simulationSave));
        File.Move(temp, path, true);

        var entry = new Ets2SyncCheckpointEntry(
            id,
            worldId,
            lineageId,
            DateTimeOffset.UtcNow,
            fileName,
            save,
            simulationSave.Header.Name);

        Ledger.Checkpoints.Add(entry);
        SaveLedger();
        return entry;
    }

    public SimulationSave ReadCheckpoint(Ets2SyncCheckpointEntry entry)
    {
        var path = Path.Combine(
            _root,
            CheckpointsFolderName,
            SafeFolder(entry.WorldId),
            entry.FileName);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Файл чекпоинта AQE не найден.",
                path);

        return SimulationSaveCodec.Decode(File.ReadAllBytes(path));
    }

    private static string SafeFolder(string value)
    {
        var chars = value.Where(character =>
            char.IsLetterOrDigit(character) ||
            character is '-' or '_').ToArray();

        var result = new string(chars).Trim();
        return result.Length == 0 ? "world" : result;
    }
}
