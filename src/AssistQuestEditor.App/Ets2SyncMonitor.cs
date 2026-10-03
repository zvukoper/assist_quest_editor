using System.Diagnostics;
using System.Security.Cryptography;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Монитор сеанса ETS2 и его save-каталога.
///
/// Источник истины для Host остаётся ETS2, а AQE только наблюдает:
/// процесс -> активный профиль из game.log -> стабильные файлы info.sii/game.sii.
/// Любое изменение сначала должно стабилизироваться, затем превращается в
/// fingerprint. AQE никогда не редактирует эти файлы.
/// </summary>
public sealed class Ets2SyncMonitor : IDisposable
{
    private sealed record FileStamp(long InfoLength, DateTime InfoWriteUtc, long? GameLength, DateTime? GameWriteUtc);

    private sealed record PhysicalObservation(
        FileStamp Stamp,
        DateTimeOffset FirstSeenUtc,
        Ets2SaveFingerprint? Fingerprint);

    private sealed record CandidateProfile(
        Ets2ProfileData Data,
        Ets2ProfileLineage Lineage);

    private readonly string _worldId;
    private readonly string _worldName;
    private readonly Ets2ProfileReader _reader;
    private readonly Ets2SyncStore _store;
    private readonly Func<SimulationSaveState> _captureState;
    private readonly Func<string> _currentCampaignId;

    private readonly Dictionary<string, PhysicalObservation> _physicalSaves =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Ets2SaveFingerprint>> _lastObservedByLineage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _promptedCandidates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declinedCandidates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _otherWorldWarnings =
        new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _nextPollUtc;
    private bool _processWasRunning;
    private bool _observedAnyCareerProfiles;
    private bool _profilesMissing;
    private bool _activeProfileWarningShown;
    private bool _newProfileFallbackWarningShown;
    private string? _activeLineageId;
    private bool _disposed;

    public Ets2SyncMonitor(
        string worldId,
        string worldName,
        Ets2ProfileReader? reader,
        Ets2SyncStore? store,
        Func<SimulationSaveState> captureState,
        Func<string> currentCampaignId)
    {
        _worldId = worldId ?? throw new ArgumentNullException(nameof(worldId));
        _worldName = string.IsNullOrWhiteSpace(worldName) ? worldId : worldName;
        _reader = reader ?? new Ets2ProfileReader();
        _store = store ?? new Ets2SyncStore();
        _captureState = captureState ?? throw new ArgumentNullException(nameof(captureState));
        _currentCampaignId = currentCampaignId ?? throw new ArgumentNullException(nameof(currentCampaignId));
    }

    public event Action<Ets2SyncNotification>? Notification;
    public event Action<Ets2SyncCareerCandidate>? NewCareerDetected;
    public event Action<Ets2SyncCheckpointReady>? CheckpointReady;

    public bool GameRunning => IsGameRunning();

    public void Poll()
    {
        if (_disposed)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < _nextPollUtc)
            return;

        _nextPollUtc = now.AddMilliseconds(900);

        try
        {
            PollCore(now);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ets2SyncMonitor: ошибка цикла наблюдения ETS2.", ex);
        }
    }

    public bool BindCandidate(string lineageId)
    {
        var known = _store.FindKnownProfile(lineageId);
        if (known is null)
            return false;

        var currentBinding = _store.FindBindingForLineage(lineageId);
        if (currentBinding is not null &&
            !currentBinding.WorldId.Equals(_worldId, StringComparison.OrdinalIgnoreCase))
        {
            Notify(
                Ets2SyncNotificationKind.OtherWorldCareerBlocked,
                "Эта карьера уже связана с другим миром AQE. Для нового мира требуется новая карьера ETS2.",
                lineageId,
                known.Name);
            return false;
        }

        if (!_store.TryBind(
                _worldId,
                ToLineage(known),
                out _))
        {
            Notify(
                Ets2SyncNotificationKind.OtherWorldCareerBlocked,
                "Эта карьера уже связана с другим миром AQE. Для нового мира требуется новая карьера ETS2.",
                lineageId,
                known.Name);
            return false;
        }

        _declinedCandidates.Remove(lineageId);
        _activeLineageId = lineageId;

        Notify(
            Ets2SyncNotificationKind.CareerBound,
            $"Карьера «{known.Name}» привязана к миру «{_worldName}».",
            lineageId,
            known.Name);

        TryCreateInitialCheckpoint(lineageId);
        return true;
    }

    public void DeclineCandidate(string lineageId)
    {
        _declinedCandidates.Add(lineageId);
        var known = _store.FindKnownProfile(lineageId);
        Notify(
            Ets2SyncNotificationKind.CareerRejected,
            "Карьера не привязана к этому миру. Для игры в нём требуется отдельная новая карьера ETS2.",
            lineageId,
            known?.Name);
    }

    private void PollCore(DateTimeOffset now)
    {
        var catalog = _reader.ReadCatalog();
        var profiles = ReadCareerProfiles(catalog);

        EnsureWorldBaseline(profiles);

        if (profiles.Count > 0)
        {
            if (_profilesMissing)
            {
                _profilesMissing = false;
                Notify(
                    Ets2SyncNotificationKind.ProfilesReturned,
                    "Профили ETS2 снова обнаружены. Сохраняемые чекпоинты AQE будут сопоставлены с вернувшимся сохранением.");
            }

            _observedAnyCareerProfiles = true;
        }
        else if (_observedAnyCareerProfiles && !_profilesMissing)
        {
            _profilesMissing = true;
            Notify(
                Ets2SyncNotificationKind.ProfilesMissing,
                "Профили ETS2 сейчас недоступны. Чекпоинты этого мира сохранены в AQE и не удаляются.");
        }

        var processRunning = IsGameRunning();
        if (processRunning && !_processWasRunning)
        {
            _declinedCandidates.Clear();
            _promptedCandidates.Clear();
            _otherWorldWarnings.Clear();
            _activeProfileWarningShown = false;
            _newProfileFallbackWarningShown = false;
            Notify(Ets2SyncNotificationKind.GameStarted, "Euro Truck Simulator 2 запущен.");
        }
        else if (!processRunning && _processWasRunning)
        {
            Notify(
                Ets2SyncNotificationKind.GameStopped,
                "Euro Truck Simulator 2 завершён. AQE прекращает принимать новые ETS2 save до следующего запуска.");
            _activeLineageId = null;
        }

        _processWasRunning = processRunning;

        var previouslyUnknownLineages = profiles
            .Where(profile => _store.FindKnownProfile(profile.Lineage.Id) is null)
            .Select(profile => profile.Lineage.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
            UpsertKnown(profile, now);

        if (!processRunning)
            return;

        var activeHex = catalog.ActiveHexFolder;
        CandidateProfile? active = null;

        if (!string.IsNullOrWhiteSpace(activeHex))
        {
            active = profiles.FirstOrDefault(profile =>
                profile.Data.HexFolder.Equals(activeHex, StringComparison.OrdinalIgnoreCase));
        }

        active ??= profiles.FirstOrDefault(profile =>
            profile.Data.ProfileName is not null &&
            string.Equals(
                profile.Data.ProfileName,
                catalog.ActiveProfileName,
                StringComparison.OrdinalIgnoreCase));

        if (active is null && previouslyUnknownLineages.Count == 1)
        {
            // ETS2 может держать game.log.txt открытым так, что даже shared-read
            // недоступен. В таком окне мы не знаем точный activeHex, но точно знаем,
            // что эта линия профиля появилась только что и ранее не была известна
            // AQE. Для сценария создания новой карьеры это однозначный кандидат.
            active = profiles.FirstOrDefault(profile =>
                previouslyUnknownLineages.Contains(profile.Lineage.Id));

            if (active is not null && !_newProfileFallbackWarningShown)
            {
                _newProfileFallbackWarningShown = true;
                AppLogger.Warn(
                    "Ets2SyncMonitor: активный профиль не определён из game.log.txt; " +
                    "использую единственный новый профиль как кандидата новой карьеры.",
                    $"lineage={active.Lineage.Id}; profile={active.Lineage.Name}");
            }
        }

        if (active is null)
        {
            if (processRunning && profiles.Count > 0 && !_activeProfileWarningShown)
            {
                _activeProfileWarningShown = true;
                AppLogger.Warn(
                    "Ets2SyncMonitor: ETS2 запущен, но активный профиль не определён.",
                    $"profiles={profiles.Count}; activeHex={catalog.ActiveHexFolder ?? "<none>"}; " +
                    $"activeName={catalog.ActiveProfileName ?? "<none>"}; " +
                    $"newProfiles={previouslyUnknownLineages.Count}");
            }

            return;
        }

        _activeLineageId = active.Lineage.Id;

        var otherWorldBinding = _store.FindBindingForLineage(active.Lineage.Id);
        if (otherWorldBinding is not null &&
            !otherWorldBinding.WorldId.Equals(_worldId, StringComparison.OrdinalIgnoreCase))
        {
            if (_otherWorldWarnings.Add(active.Lineage.Id))
            {
                Notify(
                    Ets2SyncNotificationKind.OtherWorldCareerBlocked,
                    "Эта карьера уже связана с другим миром AQE. Для нового мира требуется новая карьера ETS2.",
                    active.Lineage.Id,
                    active.Lineage.Name);
            }

            return;
        }

        var currentBinding = _store.FindBinding(_worldId, active.Lineage.Id);
        if (currentBinding is null)
        {
            if (!_store.IsBaselineProfile(_worldId, active.Lineage.Id) &&
                !_promptedCandidates.Contains(active.Lineage.Id) &&
                !_declinedCandidates.Contains(active.Lineage.Id))
            {
                _promptedCandidates.Add(active.Lineage.Id);
                Notify(
                    Ets2SyncNotificationKind.NewCareerDetected,
                    $"Обнаружена новая карьера. Использовать её для игры в «{_worldName}»?",
                    active.Lineage.Id,
                    active.Lineage.Name);

                NewCareerDetected?.Invoke(
                    new Ets2SyncCareerCandidate(active.Lineage.Id, active.Lineage.Name));
            }

            return;
        }

        var saves = ReadStableSaves(active);

        if (saves.Count == 0)
            return;

        // После принятия новой карьеры первый autosave может появиться с задержкой:
        // binding уже создан, но стабильные info.sii/game.sii становятся доступны
        // только через несколько циклов. Первый стабильный save фиксируем как
        // начальную точку мира.
        if (_store.CheckpointsFor(_worldId, active.Lineage.Id).Count == 0)
        {
            var initial = saves
                .OrderByDescending(save => save.ModifiedAt ?? DateTimeOffset.MinValue)
                .First();

            CreateCheckpoint(active.Lineage, initial);
            SeedObserved(active.Lineage, initial);
        }

        var session = ReadGameLogSession(active.Lineage.CurrentHexFolder);

        if (!string.IsNullOrWhiteSpace(session.LoadedSaveSlot))
        {
            var loaded = saves.FirstOrDefault(save =>
                save.Slot.Equals(session.LoadedSaveSlot, StringComparison.OrdinalIgnoreCase));

            if (loaded is not null)
                TryRestoreExactCheckpoint(active.Lineage, loaded);
        }

        ObserveSaveChanges(active.Lineage, saves);
    }

    private void EnsureWorldBaseline(IReadOnlyList<CandidateProfile> profiles)
    {
        var world = _store.Ledger.Worlds.FirstOrDefault(item =>
            item.WorldId.Equals(_worldId, StringComparison.OrdinalIgnoreCase));

        if (world is null)
        {
            var lineages = profiles.Select(profile => profile.Lineage).ToArray();

            foreach (var profile in profiles)
                UpsertKnown(profile, DateTimeOffset.UtcNow);

            world = _store.EnsureWorld(_worldId, lineages);
        }

        if (!world.OnboardingShown &&
            !_store.Ledger.Bindings.Any(binding =>
                binding.WorldId.Equals(_worldId, StringComparison.OrdinalIgnoreCase)))
        {
            _store.MarkOnboardingShown(_worldId);
            Notify(
                Ets2SyncNotificationKind.InitialSetupRequired,
                $"В мире «{_worldName}» ещё нет привязанной карьеры ETS2.\r\n\r\n" +
                "Чтобы начать игру в этом мире, создайте НОВУЮ карьеру в Euro Truck Simulator 2. " +
                "Не используйте существующую карьеру: каждый мир AQE имеет собственную несовместимую линию прохождения.");
        }

    }

    private void UpsertKnown(CandidateProfile profile, DateTimeOffset seenAt)
    {
        _store.UpsertKnownProfile(profile.Lineage, seenAt);
    }

    private List<CandidateProfile> ReadCareerProfiles(
        Ets2ProfileCatalog catalog)
    {
        var result = new List<CandidateProfile>();

        foreach (var summary in catalog.Profiles)
        {
            if (!HasCareerEvidence(summary))
                continue;

            try
            {
                var data = _reader.ReadProfile(summary.Area, summary.HexFolder);
                if (!HasCareerEvidence(data))
                    continue;

                var lineage = Ets2ProfileLineage.FromProfile(
                    _reader.SteamAccountId ?? "unknown",
                    data);

                result.Add(new CandidateProfile(data, lineage));
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "Ets2SyncMonitor: профиль с карьерными признаками не прочитан.",
                    $"hex={summary.HexFolder}; error={ex.Message}");
            }
        }

        return result;
    }

    private bool HasCareerEvidence(Ets2ProfileSummary profile)
    {
        foreach (var directory in PhysicalProfileDirectories(profile.HexFolder))
        {
            if (HasCareerEvidence(directory))
                return true;
        }

        return false;
    }

    private static bool HasCareerEvidence(Ets2ProfileData data) =>
        data.ProfileCreated is not null ||
        data.Saves.Count > 0;

    private static bool HasCareerEvidence(string directory)
    {
        if (File.Exists(Path.Combine(directory, "profile.sii")))
            return true;

        var saveRoot = Path.Combine(directory, "save");
        if (!Directory.Exists(saveRoot))
            return false;

        try
        {
            return Directory.EnumerateDirectories(saveRoot)
                .Any(slot => File.Exists(Path.Combine(slot, "info.sii")));
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<string> PhysicalProfileDirectories(string hex)
    {
        var paths = new List<string>();

        if (!string.IsNullOrWhiteSpace(_reader.SteamCloudRoot))
            paths.Add(Path.Combine(_reader.SteamCloudRoot!, hex));

        if (!string.IsNullOrWhiteSpace(_reader.GameRoot))
        {
            paths.Add(Path.Combine(_reader.GameRoot!, "steam_profiles", hex));
            paths.Add(Path.Combine(_reader.GameRoot!, "profiles", hex));
        }

        return paths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private List<Ets2SaveFingerprint> ReadStableSaves(CandidateProfile profile)
    {
        var bySlot = new Dictionary<string, List<Ets2SaveFingerprint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in PhysicalProfileDirectories(profile.Data.HexFolder))
        {
            var saveRoot = Path.Combine(directory, "save");
            if (!Directory.Exists(saveRoot))
                continue;

            IEnumerable<string> slots;
            try
            {
                slots = Directory.EnumerateDirectories(saveRoot);
            }
            catch
            {
                continue;
            }

            foreach (var slotDirectory in slots)
            {
                var slot = Path.GetFileName(slotDirectory);
                if (string.IsNullOrWhiteSpace(slot))
                    continue;

                var infoPath = Path.Combine(slotDirectory, "info.sii");
                if (!File.Exists(infoPath))
                    continue;

                try
                {
                    var info = new FileInfo(infoPath);
                    FileInfo? game = null;
                    var gamePath = Path.Combine(slotDirectory, "game.sii");
                    if (File.Exists(gamePath))
                        game = new FileInfo(gamePath);

                    var stamp = new FileStamp(
                        info.Length,
                        info.LastWriteTimeUtc,
                        game?.Length,
                        game?.LastWriteTimeUtc);

                    var physicalKey = Path.GetFullPath(slotDirectory);

                    if (!_physicalSaves.TryGetValue(physicalKey, out var observation) ||
                        observation.Stamp != stamp)
                    {
                        _physicalSaves[physicalKey] = new PhysicalObservation(
                            stamp,
                            DateTimeOffset.UtcNow,
                            null);
                        continue;
                    }

                    if (observation.Fingerprint is null &&
                        DateTimeOffset.UtcNow - observation.FirstSeenUtc >= TimeSpan.FromMilliseconds(700))
                    {
                        var fingerprint = ReadFingerprint(slotDirectory, profile.Data.MapPath);
                        _physicalSaves[physicalKey] = observation with
                        {
                            Fingerprint = fingerprint
                        };
                    }

                    if (_physicalSaves[physicalKey].Fingerprint is { } stable)
                    {
                        if (!bySlot.TryGetValue(stable.Slot, out var list))
                        {
                            list = new List<Ets2SaveFingerprint>();
                            bySlot[stable.Slot] = list;
                        }

                        list.Add(stable);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "Ets2SyncMonitor: save ещё нельзя прочитать.",
                        $"path={slotDirectory}; error={ex.Message}");
                }
            }
        }

        return bySlot.Values
            .SelectMany(list => list
                .OrderByDescending(save => save.ModifiedAt ?? DateTimeOffset.MinValue)
                .Take(1))
            .ToList();
    }

    private static Ets2SaveFingerprint ReadFingerprint(
        string slotDirectory,
        string? mapPath)
    {
        var infoPath = Path.Combine(slotDirectory, "info.sii");
        var gamePath = Path.Combine(slotDirectory, "game.sii");

        var document = Ets2SiiDocument.Parse(Ets2Sii.ReadText(infoPath));
        const string block = "save_container";

        var infoHash = HashFile(infoPath);
        var gameHash = File.Exists(gamePath) ? HashFile(gamePath) : null;

        var modified = document.GetInt64(block, "file_time") > 0
            ? ParseUnixTime(document.GetInt64(block, "file_time"))
            : null;

        return new Ets2SaveFingerprint(
            Path.GetFileName(slotDirectory),
            document.GetString(block, "name") ?? string.Empty,
            modified,
            document.GetInt64(block, "time"),
            document.GetInt64(block, "info_money_account"),
            (int)document.GetInt64(block, "info_players_experience"),
            document.GetValues(block, "dependencies").Count,
            mapPath,
            infoHash,
            gameHash);
    }

    private void TryRestoreExactCheckpoint(
        Ets2ProfileLineage lineage,
        Ets2SaveFingerprint save)
    {
        var entry = _store.FindExactCheckpoint(_worldId, lineage.Id, save);
        if (entry is null)
        {
            return;
        }

        var saveStateKey = entry.Id;
        if (saveStateKey.Equals(_lastLoadedCheckpointId, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var simulationSave = _store.ReadCheckpoint(entry);
            _lastLoadedCheckpointId = entry.Id;
            SeedObserved(lineage, save);

            CheckpointReady?.Invoke(new Ets2SyncCheckpointReady(entry, simulationSave));
            Notify(
                Ets2SyncNotificationKind.CheckpointLoaded,
                $"Для карьеры «{lineage.Name}» восстановлен чекпоинт мира «{_worldName}».",
                lineage.Id,
                lineage.Name);
        }
        catch (Exception ex)
        {
            Notify(
                Ets2SyncNotificationKind.CheckpointNotFound,
                "Сопоставленный чекпоинт AQE найден в реестре, но его файл не удалось прочитать: " + ex.Message,
                lineage.Id,
                lineage.Name);
        }
    }

    private string? _lastLoadedCheckpointId;

    private void ObserveSaveChanges(
        Ets2ProfileLineage lineage,
        IReadOnlyList<Ets2SaveFingerprint> saves)
    {
        if (!_lastObservedByLineage.TryGetValue(lineage.Id, out var previous))
        {
            previous = new Dictionary<string, Ets2SaveFingerprint>(StringComparer.OrdinalIgnoreCase);
            _lastObservedByLineage[lineage.Id] = previous;

            foreach (var save in saves)
                previous[save.Slot] = save;

            return;
        }

        foreach (var save in saves)
        {
            if (!previous.TryGetValue(save.Slot, out var old) ||
                string.Equals(old.ContentKey, save.ContentKey, StringComparison.Ordinal))
                continue;

            previous[save.Slot] = save;
            CreateCheckpoint(lineage, save);
        }
    }

    private void TryCreateInitialCheckpoint(string lineageId)
    {
        var profile = ReadCurrentProfileByLineage(lineageId);
        if (profile is null)
            return;

        var saves = ReadStableSaves(profile);
        var newest = saves
            .OrderByDescending(save => save.ModifiedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (newest is null)
            return;

        CreateCheckpoint(profile.Lineage, newest);
        SeedObserved(profile.Lineage, newest);
    }

    private void CreateCheckpoint(Ets2ProfileLineage lineage, Ets2SaveFingerprint save)
    {
        try
        {
            var state = _captureState();
            var now = DateTimeOffset.UtcNow;
            var simulationSave = new SimulationSave(
                new SimulationSaveHeader(
                    SimulationSaveState.CurrentFormatVersion,
                    "ETS2 " + save.Slot + " — " + (string.IsNullOrWhiteSpace(save.Name) ? "save" : save.Name),
                    VersionInfo.InformationalVersion,
                    now,
                    now,
                    state.Clock.Now,
                    _currentCampaignId(),
                    state.Clock.Elapsed),
                state);

            var entry = _store.AddCheckpoint(
                _worldId,
                lineage.Id,
                save,
                simulationSave);

            SeedObserved(lineage, save);

            Notify(
                Ets2SyncNotificationKind.CheckpointCreated,
                $"Чекпоинт AQE создан по сохранению ETS2 «{save.Name}» ({save.Slot}).",
                lineage.Id,
                lineage.Name);

            AppLogger.Info(
                "Ets2SyncMonitor: создан чекпоинт.",
                $"world={_worldId}; lineage={lineage.Id}; slot={save.Slot}; checkpoint={entry.Id}");
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "Ets2SyncMonitor: не удалось создать чекпоинт.",
                ex,
                $"world={_worldId}; lineage={lineage.Id}; slot={save.Slot}");
        }
    }

    private void SeedObserved(Ets2ProfileLineage lineage, Ets2SaveFingerprint save)
    {
        if (!_lastObservedByLineage.TryGetValue(lineage.Id, out var observed))
        {
            observed = new Dictionary<string, Ets2SaveFingerprint>(StringComparer.OrdinalIgnoreCase);
            _lastObservedByLineage[lineage.Id] = observed;
        }

        observed[save.Slot] = save;
    }

    private CandidateProfile? ReadCurrentProfileByLineage(string lineageId)
    {
        try
        {
            var catalog = _reader.ReadCatalog();
            foreach (var summary in catalog.Profiles)
            {
                if (!HasCareerEvidence(summary))
                    continue;

                var data = _reader.ReadProfile(summary.Area, summary.HexFolder);
                var lineage = Ets2ProfileLineage.FromProfile(
                    _reader.SteamAccountId ?? "unknown",
                    data);

                if (lineage.Id.Equals(lineageId, StringComparison.OrdinalIgnoreCase))
                    return new CandidateProfile(data, lineage);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Ets2SyncMonitor: не удалось найти текущий профиль кандидата.", ex.Message);
        }

        return null;
    }

    private static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("eurotrucks2").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private Ets2SessionObservation ReadGameLogSession(
        string activeHex)
    {
        var loadedSlot = (string?)null;
        if (string.IsNullOrWhiteSpace(_reader.GameRoot))
            return new Ets2SessionObservation(activeHex, loadedSlot);

        var logPath = Path.Combine(_reader.GameRoot!, "game.log.txt");
        if (!File.Exists(logPath))
            return new Ets2SessionObservation(activeHex, loadedSlot);

        try
        {
            var lines = File.ReadAllLines(logPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("Loading save.", StringComparison.Ordinal))
                    continue;

                if (!TryExtractProfileHex(line, out var hex) ||
                    !hex.Equals(activeHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                var marker = "/save/";
                var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    continue;

                var tail = line[(start + marker.Length)..];
                var slash = tail.IndexOf('/');
                if (slash > 0)
                    loadedSlot = tail[..slash];

                if (loadedSlot is not null)
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Ets2SyncMonitor: не удалось прочитать game.log.txt.", ex.Message);
        }

        return new Ets2SessionObservation(activeHex, loadedSlot);
    }

    private static bool TryExtractProfileHex(string line, out string hex)
    {
        hex = string.Empty;

        foreach (var marker in new[] { "steam_profiles/", "profiles/" })
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                continue;

            if (marker == "profiles/" &&
                index >= 6 &&
                line.AsSpan(index - 6, 6).Equals("steam_", StringComparison.OrdinalIgnoreCase))
                continue;

            var tail = line[(index + marker.Length)..];
            var slash = tail.IndexOf('/');
            var candidate = slash > 0 ? tail[..slash] : tail.Trim();

            if (candidate.Length >= 2 && candidate.All(Uri.IsHexDigit))
            {
                hex = candidate;
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? ParseUnixTime(long seconds)
    {
        if (seconds <= 0)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private Ets2ProfileLineage ToLineage(Ets2SyncKnownProfile known) =>
        new(
            known.LineageId,
            known.SteamAccountId,
            known.Name,
            known.CreatedAt,
            known.StableIdentity,
            known.KnownHexFolders.FirstOrDefault() ?? string.Empty,
            known.KnownHexFolders);

    private void Notify(
        Ets2SyncNotificationKind kind,
        string message,
        string? lineageId = null,
        string? profileName = null)
        => Notification?.Invoke(new Ets2SyncNotification(kind, message, lineageId, profileName));

    public void Dispose()
    {
        _disposed = true;
        _physicalSaves.Clear();
        _lastObservedByLineage.Clear();
        _promptedCandidates.Clear();
        _declinedCandidates.Clear();
        _otherWorldWarnings.Clear();
        _activeProfileWarningShown = false;
        _newProfileFallbackWarningShown = false;
    }

    private sealed record Ets2SessionObservation(
        string ActiveProfileHex,
        string? LoadedSaveSlot);
}
