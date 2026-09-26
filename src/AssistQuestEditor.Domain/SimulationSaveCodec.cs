using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Бинарный кодек сохранений симуляции.
///
/// Формат: несжатый компактный блок (varint, дельта-числа, таблица строк) плюс
/// сжатие Brotli. Задача — сделать снимок маленьким даже при больших изменениях
/// мира, поэтому экономия идёт по трём направлениям:
///   1. ДЕЛЬТА от исходного состояния: статические точки мира и неизменённые
///      записи в файл не попадают вообще (см. <see cref="SimulationSaveState"/>).
///   2. ТАБЛИЦА СТРОК: повторяющиеся строки (ключи фактов, Id квестов, Id
///      предметов) записываются один раз, дальше идут индексы varint. В песочнице
///      ключи вида «quest.ruslan.introductionSeen» повторяются постоянно, и именно
///      на них экономия максимальна.
///   3. VARINT и zigzag: числа пишутся минимальным числом байт вместо фиксированных
///      8 байт. Координаты и деньги — обычные небольшие числа, поэтому выигрыш
///      почти четырёхкратный на каждом.
///
/// JSON сознательно не используется: он требует текстовых имён полей для каждой
/// записи, что на тысячах записей даёт размер в разы больше при том же смысле.
/// </summary>
public static class SimulationSaveCodec
{
    private const uint Magic = 0x41515153; // "AQSS" — Assist Quest Simulation Save
    private static readonly byte[] InnerMagic = "AQSS1"u8.ToArray();

    /// <summary>Размер словаря Brotli: максимальное качество на уровне 11.</summary>
    private const int BrotliQuality = 11;

    public static byte[] Encode(SimulationSave save)
    {
        ArgumentNullException.ThrowIfNull(save);

        using var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        WriteInner(writer, save);
        writer.Flush();

        // Сжатие внешним слоем: заголовок файла остаётся читаемым, а тело можно
        // распаковать потоком, не загружая весь снимок в память повторно.
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            buffer.Position = 0;
            buffer.CopyTo(brotli);
        }

        using var result = new MemoryStream();
        var outer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        outer.Write(Magic);
        outer.Write(InnerMagic.Length);
        outer.Write(InnerMagic);
        // Заголовок хранится несжатым: список сохранений показывает имя, дату и
        // игровое время, и для этого не нужно распаковывать состояние целиком.
        WriteHeader(outer, save.Header);
        // Длины пишутся varint, а не через writer.Write(long): перегрузка для
        // long всегда занимает 8 байт, и при чтении Int32 это сдвигало весь поток
        // на 8 байт (симптом — EndOfStreamException при первом же Decode).
        WriteVarint(outer, (ulong)buffer.Length);
        WriteVarint(outer, (ulong)compressed.Length);
        outer.Write(compressed.ToArray());
        outer.Flush();
        return result.ToArray();
    }

    public static SimulationSave Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var source = new MemoryStream(payload);
        var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Файл не является сохранением симуляции Assist Quest Editor.");

        var magicLength = reader.ReadInt32();
        var magic = reader.ReadBytes(magicLength);
        if (!magic.AsSpan().SequenceEqual(InnerMagic))
            throw new InvalidDataException("Неподдерживаемая версия формата сохранения.");

        var header = ReadHeader(reader);

        var innerLength = checked((int)ReadVarint(reader));
        var compressedLength = checked((int)ReadVarint(reader));
        var compressed = reader.ReadBytes(compressedLength);

        using var decompressed = new MemoryStream(innerLength);
        using (var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            brotli.CopyTo(decompressed);
        }

        decompressed.Position = 0;
        var inner = new BinaryReader(decompressed, Encoding.UTF8, leaveOpen: true);
        var state = ReadInner(inner, header.FormatVersion);

        return new SimulationSave(header, state);
    }

    /// <summary>
    /// Читает только заголовок, не распаковывая состояние.
    ///
    /// Так список сохранений остаётся быстрым даже при больших снимках: распаковка
    /// сотен файлов при каждом открытии панели была бы заметна.
    /// </summary>
    public static SimulationSaveHeader ReadHeaderOnly(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var source = new MemoryStream(payload);
        var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Файл не является сохранением симуляции Assist Quest Editor.");

        var magicLength = reader.ReadInt32();
        var magic = reader.ReadBytes(magicLength);
        if (!magic.AsSpan().SequenceEqual(InnerMagic))
            throw new InvalidDataException("Неподдерживаемая версия формата сохранения.");

        return ReadHeader(reader);
    }

    private static void WriteHeader(BinaryWriter writer, SimulationSaveHeader header)
    {
        writer.Write(header.FormatVersion);
        WriteString(writer, header.Name);
        WriteString(writer, header.AppVersion);
        WriteTimestamp(writer, header.CreatedAt);
        writer.Write(header.UpdatedAt.HasValue);
        if (header.UpdatedAt.HasValue) WriteTimestamp(writer, header.UpdatedAt.Value);
        WriteTimestamp(writer, header.GameDate);
        WriteString(writer, header.CampaignId);
        writer.Write(header.PlayedTime.Ticks);
    }

    private static SimulationSaveHeader ReadHeader(BinaryReader reader)
    {
        var formatVersion = reader.ReadInt32();
        if (formatVersion < 1 || formatVersion > SimulationSaveState.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Сохрание создано форматом v{formatVersion}, поддерживаются v1..v{SimulationSaveState.CurrentFormatVersion}.");
        }

        var name = ReadString(reader);
        var appVersion = ReadString(reader);
        var createdAt = ReadTimestamp(reader);
        DateTimeOffset? updatedAt = reader.ReadBoolean() ? ReadTimestamp(reader) : null;
        var gameDate = ReadTimestamp(reader);
        var campaignId = ReadString(reader);
        var playedTime = TimeSpan.FromTicks(reader.ReadInt64());

        return new SimulationSaveHeader(
            formatVersion, name, appVersion, createdAt, updatedAt, gameDate, campaignId, playedTime);
    }

    private static void WriteInner(BinaryWriter writer, SimulationSave save)
    {
        var header = save.Header;
        var state = save.State;

        // Игрок: координаты игрового мира и признаки движения.
        WriteDouble(writer, state.Player.Position.X);
        WriteDouble(writer, state.Player.Position.Y);
        WriteDouble(writer, state.Player.Position.Z);
        WriteDouble(writer, state.Player.SpeedKmh);
        WriteDouble(writer, state.Player.Heading);
        writer.Write(state.Player.Paused);
        writer.Write(state.Player.InCab);

        // Игровое время: стартовая дата + прошедшее время.
        WriteTimestamp(writer, state.Clock.StartDate);
        writer.Write(state.Clock.Elapsed.Ticks);
        writer.Write(state.Clock.Running);

        // Таблица строк общая для всего состояния: ключи фактов и переменных
        // пересекаются с Id предметов и квестов, поэтому одна таблица даёт
        // больше экономии, чем отдельная на каждый раздел.
        var strings = new StringTable();
        var facts = Sorted(state.Facts);
        var variables = Sorted(state.Variables);
        var flags = Sorted(state.Flags);
        var inventory = Sorted(state.Inventory);
        var reputation = Sorted(state.ReputationValues);
        var characterStats = Sorted(state.CharacterStats);
        var skillLevels = Sorted(state.SkillLevels);

        foreach (var pair in facts) { strings.Add(pair.Key); strings.Add(pair.Value); }
        foreach (var pair in variables) { strings.Add(pair.Key); strings.Add(pair.Value); }
        foreach (var pair in flags) strings.Add(pair.Key);
        foreach (var entry in state.QuestStatuses) { strings.Add(entry.QuestId); strings.Add(entry.Step); }
        foreach (var pair in inventory) strings.Add(pair.Key);
        foreach (var id in state.NewItemIds) strings.Add(id);
        foreach (var pair in reputation) strings.Add(pair.Key);
        foreach (var id in state.ReputationContacts) strings.Add(id);
        foreach (var pair in characterStats) strings.Add(pair.Key);
        foreach (var value in state.CharacterBuffs) strings.Add(value);
        foreach (var value in state.CharacterDebuffs) strings.Add(value);
        foreach (var pair in skillLevels) strings.Add(pair.Key);
        foreach (var id in state.UnlockedSkills) strings.Add(id);
        strings.Add(state.Weather);

        foreach (var instance in state.DynamicEvents.Instances)
        {
            strings.Add(instance.InstanceId);
            strings.Add(instance.DefinitionId);
            strings.Add(instance.Point.Id);
            strings.Add(instance.Point.Name);
            strings.Add(instance.Point.Category);
            strings.Add(instance.Point.Color);
            if (instance.LastReason is not null) strings.Add(instance.LastReason);
        }

        foreach (var schedule in state.DynamicEvents.Schedules)
            strings.Add(schedule.DefinitionId);

        foreach (var waypoint in state.Route.Waypoints)
            strings.Add(waypoint.Id);

        WriteStringTable(writer, strings);

        WritePairs(writer, strings, facts);
        WritePairs(writer, strings, variables);
        WriteFlags(writer, strings, flags);

        writer.Write(state.QuestStatuses.Count);
        foreach (var entry in state.QuestStatuses)
        {
            writer.Write(strings.Index(entry.QuestId));
            WriteVarint(writer, (ulong)entry.Status);
            writer.Write(strings.Index(entry.Step));
        }

        WriteIntPairs(writer, strings, inventory);
        writer.Write(state.NewItemIds.Count);
        foreach (var id in state.NewItemIds) writer.Write(strings.Index(id));

        WriteIntPairs(writer, strings, reputation);
        writer.Write(state.ReputationContacts.Count);
        foreach (var id in state.ReputationContacts) writer.Write(strings.Index(id));

        WriteDouble(writer, state.PlayerVitals.Health);
        WriteDouble(writer, state.PlayerVitals.MaxHealth);
        WriteDouble(writer, state.PlayerVitals.Energy);
        WriteDouble(writer, state.PlayerVitals.MaxEnergy);
        WriteDouble(writer, state.PlayerVitals.Hydration);
        WriteDouble(writer, state.PlayerVitals.MaxHydration);
        WriteDouble(writer, state.PlayerVitals.Fatigue);
        WriteDouble(writer, state.PlayerVitals.MaxFatigue);

        WriteVarint(writer, ZigZag(state.PlayerProgress.Money));
        WriteVarint(writer, ZigZag(state.PlayerProgress.Experience));
        WriteVarint(writer, ZigZag(state.PlayerProgress.Reserve));

        WriteIntPairs(writer, strings, characterStats);

        writer.Write(state.CharacterBuffs.Count);
        foreach (var value in state.CharacterBuffs) writer.Write(strings.Index(value));
        writer.Write(state.CharacterDebuffs.Count);
        foreach (var value in state.CharacterDebuffs) writer.Write(strings.Index(value));

        WriteIntPairs(writer, strings, skillLevels);
        writer.Write(state.UnlockedSkills.Count);
        foreach (var id in state.UnlockedSkills) writer.Write(strings.Index(id));

        writer.Write(strings.Index(state.Weather));

        if (header.FormatVersion >= 2)
            WriteDynamicEvents(writer, strings, state.DynamicEvents);
        if (header.FormatVersion >= 3)
            WriteRoute(writer, strings, state.Route, header.FormatVersion >= 5);
        if (header.FormatVersion >= 4)
            WriteRouteRuntime(writer, state.RouteRuntime, header.FormatVersion);
        if (header.FormatVersion >= 7)
            WriteMapView(writer, state.MapView);
        if (header.FormatVersion >= 8)
            WriteConditions(writer, state.Conditions);
    }

    private static SimulationSaveState ReadInner(BinaryReader reader, int formatVersion)
    {

        var player = new PlayerState(
            new WorldCoordinate(ReadDouble(reader), ReadDouble(reader), ReadDouble(reader)),
            ReadDouble(reader),
            ReadDouble(reader),
            reader.ReadBoolean(),
            reader.ReadBoolean());

        var clock = new WorldClockState(
            ReadTimestamp(reader),
            TimeSpan.FromTicks(reader.ReadInt64()),
            reader.ReadBoolean());

        var strings = ReadStringTable(reader);

        var facts = ReadPairs(reader, strings);
        var variables = ReadPairs(reader, strings);
        var flags = ReadFlags(reader, strings);

        var questCount = reader.ReadInt32();
        var questStatuses = new List<QuestStatusEntry>(questCount);
        for (var index = 0; index < questCount; index++)
        {
            var questId = strings[reader.ReadInt32()];
            var status = (QuestStatus)ReadVarint(reader);
            var step = strings[reader.ReadInt32()];
            questStatuses.Add(new QuestStatusEntry(questId, status, step));
        }

        var inventory = ReadIntPairs(reader, strings);
        var newItemCount = reader.ReadInt32();
        var newItemIds = new List<string>(newItemCount);
        for (var index = 0; index < newItemCount; index++) newItemIds.Add(strings[reader.ReadInt32()]);

        var reputation = ReadIntPairs(reader, strings);
        var contactCount = reader.ReadInt32();
        var contacts = new List<string>(contactCount);
        for (var index = 0; index < contactCount; index++) contacts.Add(strings[reader.ReadInt32()]);

        var vitals = new PlayerVitalsState(
            ReadDouble(reader), ReadDouble(reader),
            ReadDouble(reader), ReadDouble(reader),
            ReadDouble(reader), ReadDouble(reader),
            ReadDouble(reader), ReadDouble(reader));

        var progress = new PlayerProgressState(
            (int)UnZigZag(ReadVarint(reader)),
            (int)UnZigZag(ReadVarint(reader)),
            (int)UnZigZag(ReadVarint(reader)));

        var characterStats = ReadIntPairs(reader, strings);

        var buffCount = reader.ReadInt32();
        var buffs = new List<string>(buffCount);
        for (var index = 0; index < buffCount; index++) buffs.Add(strings[reader.ReadInt32()]);
        var debuffCount = reader.ReadInt32();
        var debuffs = new List<string>(debuffCount);
        for (var index = 0; index < debuffCount; index++) debuffs.Add(strings[reader.ReadInt32()]);

        var skillLevels = ReadIntPairs(reader, strings);
        var unlockedCount = reader.ReadInt32();
        var unlocked = new List<string>(unlockedCount);
        for (var index = 0; index < unlockedCount; index++) unlocked.Add(strings[reader.ReadInt32()]);

        var weather = strings[reader.ReadInt32()];
        var dynamicEvents = formatVersion >= 2
            ? ReadDynamicEvents(reader, strings)
            : DynamicEventRuntimeState.Empty;
        var route = formatVersion >= 3
            ? ReadRoute(reader, strings, formatVersion >= 5)
            : RouteState.Empty;
        var routeRuntime = formatVersion >= 4
            ? ReadRouteRuntime(reader, formatVersion)
            : RouteRuntimeState.Empty;
        var mapView = formatVersion >= 7
            ? ReadMapView(reader)
            : null;
        var conditions = formatVersion >= 8
            ? ReadConditions(reader)
            : PlayerConditionState.Empty;

        return new SimulationSaveState(
            player, clock, facts, variables, flags, questStatuses,
            inventory, newItemIds, reputation, contacts, vitals, progress,
            characterStats, buffs, debuffs, skillLevels, unlocked, weather)
        {
            DynamicEvents = dynamicEvents,
            Route = route,
            RouteRuntime = routeRuntime,
            MapView = mapView,
            Conditions = conditions
        };
    }

    private static void WriteMapView(
        BinaryWriter writer,
        SimulatorMapViewState? mapView)
    {
        writer.Write(mapView is not null);
        if (mapView is null)
            return;

        var normalized = mapView.Normalize();
        WriteDouble(writer, normalized.CenterX);
        WriteDouble(writer, normalized.CenterZ);
        WriteDouble(writer, normalized.MetersPerPixel);
    }

    private static SimulatorMapViewState? ReadMapView(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;

        return new SimulatorMapViewState(
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader)).Normalize();
    }


    private static void WriteConditions(
        BinaryWriter writer,
        PlayerConditionState conditions)
    {
        conditions = (conditions ?? PlayerConditionState.Empty).Normalize();
        WriteDouble(writer, conditions.CumulativeHealth);
        WriteDouble(writer, conditions.CumulativeEnergy);
        WriteDouble(writer, conditions.CumulativeHydration);
        WriteDouble(writer, conditions.Stress);
        WriteDouble(writer, conditions.CumulativeStress);
        WriteDouble(writer, conditions.CumulativeFatigue);
        WriteDouble(writer, conditions.CriticalFatigueGameSeconds);
        WriteDouble(writer, conditions.CriticalStressGameSeconds);

        writer.Write(conditions.Effects.Count);
        foreach (var effect in conditions.Effects)
        {
            WriteString(writer, effect.Id);
            WriteString(writer, effect.Name);
            WriteDouble(writer, effect.RemainingRealSeconds);
            writer.Write(effect.IsDebuff);
            WriteDouble(writer, effect.ExperienceMultiplier);
            WriteDouble(writer, effect.StressAccumulationSlowdownPercent);
        }
    }

    private static PlayerConditionState ReadConditions(BinaryReader reader)
    {
        var state = new PlayerConditionState(
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            ReadDouble(reader),
            Array.Empty<ActivePlayerEffectState>());

        var effectCount = reader.ReadInt32();
        var effects = new List<ActivePlayerEffectState>(effectCount);
        for (var index = 0; index < effectCount; index++)
        {
            effects.Add(new ActivePlayerEffectState(
                ReadString(reader),
                ReadString(reader),
                ReadDouble(reader),
                reader.ReadBoolean(),
                ReadDouble(reader),
                ReadDouble(reader)));
        }

        return state with { Effects = effects }.Normalize();
    }

    private static void WriteRouteRuntime(
        BinaryWriter writer,
        RouteRuntimeState runtime,
        int formatVersion)
    {
        var cursor = runtime.Cursor;
        writer.Write(cursor.Initialized);
        writer.Write(cursor.LegIndex);
        writer.Write(cursor.SegmentIndex);
        WriteDouble(writer, cursor.SegmentProgressMeters);
        writer.Write(cursor.ResumeAfterStop);
        writer.Write(cursor.StoppedAtWaypointIndex.HasValue);
        if (cursor.StoppedAtWaypointIndex.HasValue)
            writer.Write(cursor.StoppedAtWaypointIndex.Value);
        WriteDouble(writer, cursor.LastHeadingDegrees);

        if (formatVersion >= 5)
        {
            writer.Write(runtime.CurrentTargetWaypointIndex.HasValue);
            if (runtime.CurrentTargetWaypointIndex.HasValue)
                writer.Write(runtime.CurrentTargetWaypointIndex.Value);
            WriteDouble(writer, runtime.TravelRealSeconds);
            WriteDouble(writer, runtime.TravelGameSeconds);
            if (formatVersion >= 6)
                writer.Write(runtime.Enabled);
        }
    }

    private static RouteRuntimeState ReadRouteRuntime(
        BinaryReader reader,
        int formatVersion)
    {
        var cursor = new RouteCursor(
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            ReadDouble(reader),
            reader.ReadBoolean(),
            reader.ReadBoolean() ? reader.ReadInt32() : null,
            ReadDouble(reader));

        if (formatVersion < 5)
            return new RouteRuntimeState(cursor);

        // Явный целевой тип обязателен: у `var` нет цели для условного выражения,
        // и общий тип между int и null не выводится (CS0173). В вызове выше это
        // работает только потому, что параметр конструктора уже имеет тип int?.
        int? target = reader.ReadBoolean() ? reader.ReadInt32() : null;

        return new RouteRuntimeState(
            cursor,
            target,
            ReadDouble(reader),
            ReadDouble(reader),
            formatVersion >= 6 && reader.ReadBoolean());
    }

    private static void WriteRoute(
        BinaryWriter writer,
        StringTable strings,
        RouteState route,
        bool includeOffRoad)
    {
        route = (route ?? RouteState.Empty).Normalize();
        WriteDouble(writer, route.DefaultSpeedKmh);
        writer.Write(route.Waypoints.Count);

        foreach (var waypoint in route.Waypoints)
        {
            writer.Write(strings.Index(waypoint.Id));
            WriteDouble(writer, waypoint.Position.X);
            WriteDouble(writer, waypoint.Position.Y);
            WriteDouble(writer, waypoint.Position.Z);
            WriteDouble(writer, waypoint.SpeedKmh);
            if (includeOffRoad)
                writer.Write(waypoint.IsOffRoad);
        }
    }

    private static RouteState ReadRoute(
        BinaryReader reader,
        StringTable strings,
        bool hasOffRoad)
    {
        var defaultSpeed = ReadDouble(reader);
        var count = reader.ReadInt32();
        if (count < 0 || count > 10000)
            throw new InvalidDataException("Некорректное число путевых точек в сохранении.");

        var waypoints = new List<RouteWaypoint>(count);
        for (var index = 0; index < count; index++)
        {
            var id = strings[reader.ReadInt32()];
            var position = new WorldCoordinate(
                ReadDouble(reader),
                ReadDouble(reader),
                ReadDouble(reader));
            var speed = ReadDouble(reader);
            var isOffRoad = hasOffRoad && reader.ReadBoolean();
            waypoints.Add(new RouteWaypoint(id, position, speed, isOffRoad));
        }

        return new RouteState(defaultSpeed, waypoints).Normalize();
    }

    private static void WriteDynamicEvents(
        BinaryWriter writer,
        StringTable strings,
        DynamicEventRuntimeState state)
    {
        writer.Write(state.Instances.Count);
        foreach (var instance in state.Instances)
        {
            writer.Write(strings.Index(instance.InstanceId));
            writer.Write(strings.Index(instance.DefinitionId));
            writer.Write((int)instance.Status);

            var point = instance.Point;
            writer.Write(strings.Index(point.Id));
            writer.Write(strings.Index(point.Name));
            writer.Write(strings.Index(point.Category));
            writer.Write(strings.Index(point.Color));
            writer.Write(point.Editable);
            writer.Write(point.IsCity);
            WriteDouble(writer, point.Position.X);
            WriteDouble(writer, point.Position.Y);
            WriteDouble(writer, point.Position.Z);
            WriteDouble(writer, point.TriggerRadius);

            WriteTimestamp(writer, instance.SpawnedUtc);
            writer.Write(instance.SpawnedGameElapsed.Ticks);

            writer.Write(instance.DiscoveredUtc.HasValue);
            if (instance.DiscoveredUtc.HasValue)
                WriteTimestamp(writer, instance.DiscoveredUtc.Value);

            writer.Write(instance.CompletedUtc.HasValue);
            if (instance.CompletedUtc.HasValue)
                WriteTimestamp(writer, instance.CompletedUtc.Value);

            writer.Write(instance.LastReason is not null);
            if (instance.LastReason is not null)
                writer.Write(strings.Index(instance.LastReason));
        }

        writer.Write(state.Schedules.Count);
        foreach (var schedule in state.Schedules)
        {
            writer.Write(strings.Index(schedule.DefinitionId));
            WriteDouble(writer, schedule.DistanceBudgetMeters);

            writer.Write(schedule.NextDistanceThresholdMeters.HasValue);
            if (schedule.NextDistanceThresholdMeters.HasValue)
                WriteDouble(writer, schedule.NextDistanceThresholdMeters.Value);

            writer.Write(schedule.NextGameElapsed.HasValue);
            if (schedule.NextGameElapsed.HasValue)
                writer.Write(schedule.NextGameElapsed.Value.Ticks);

            writer.Write(schedule.NextRealUtc.HasValue);
            if (schedule.NextRealUtc.HasValue)
                WriteTimestamp(writer, schedule.NextRealUtc.Value);

            writer.Write(schedule.LastSpawnUtc.HasValue);
            if (schedule.LastSpawnUtc.HasValue)
                WriteTimestamp(writer, schedule.LastSpawnUtc.Value);

            writer.Write(schedule.LastSpawnGameElapsed.HasValue);
            if (schedule.LastSpawnGameElapsed.HasValue)
                writer.Write(schedule.LastSpawnGameElapsed.Value.Ticks);

            writer.Write(schedule.TriggerPending);
        }
    }

    private static DynamicEventRuntimeState ReadDynamicEvents(
        BinaryReader reader,
        StringTable strings)
    {
        var instanceCount = reader.ReadInt32();
        var instances = new List<DynamicEventInstance>(instanceCount);

        for (var index = 0; index < instanceCount; index++)
        {
            var instanceId = strings[reader.ReadInt32()];
            var definitionId = strings[reader.ReadInt32()];
            var status = (DynamicEventInstanceStatus)reader.ReadInt32();

            // Порядок чтения обязан совпадать с записью в WriteDynamicEvents:
            // Id, Name, Category, Color, Editable, IsCity, X, Y, Z, TriggerRadius.
            // Цвет читается ДО координат, поэтому он идёт в локальную переменную:
            // в объектном инициализаторе он выполнялся бы уже после конструктора,
            // то есть после ReadDouble, и порядок полей в потоке разъехался бы.
            var pointId = strings[reader.ReadInt32()];
            var pointName = strings[reader.ReadInt32()];
            var pointCategory = strings[reader.ReadInt32()];
            var pointColor = strings[reader.ReadInt32()];
            var pointEditable = reader.ReadBoolean();
            var pointIsCity = reader.ReadBoolean();
            var pointX = ReadDouble(reader);
            var pointY = ReadDouble(reader);
            var pointZ = ReadDouble(reader);
            var pointTriggerRadius = ReadDouble(reader);

            var point = new WorldPoint(
                pointId,
                pointName,
                pointCategory,
                new WorldCoordinate(pointX, pointY, pointZ),
                pointTriggerRadius)
            {
                Color = pointColor,
                Editable = pointEditable,
                IsCity = pointIsCity
            };
            var spawnedUtc = ReadTimestamp(reader);
            var spawnedGameElapsed = TimeSpan.FromTicks(reader.ReadInt64());

            DateTimeOffset? discoveredUtc = reader.ReadBoolean()
                ? ReadTimestamp(reader)
                : null;

            DateTimeOffset? completedUtc = reader.ReadBoolean()
                ? ReadTimestamp(reader)
                : null;

            var lastReason = reader.ReadBoolean()
                ? strings[reader.ReadInt32()]
                : null;

            instances.Add(new DynamicEventInstance(
                instanceId,
                definitionId,
                status,
                point)
            {
                SpawnedUtc = spawnedUtc,
                SpawnedGameElapsed = spawnedGameElapsed,
                DiscoveredUtc = discoveredUtc,
                CompletedUtc = completedUtc,
                LastReason = lastReason
            });
        }

        var scheduleCount = reader.ReadInt32();
        var schedules = new List<DynamicEventScheduleState>(scheduleCount);

        for (var index = 0; index < scheduleCount; index++)
        {
            var schedule = new DynamicEventScheduleState(strings[reader.ReadInt32()])
            {
                DistanceBudgetMeters = ReadDouble(reader),
                NextDistanceThresholdMeters = reader.ReadBoolean()
                    ? ReadDouble(reader)
                    : null,
                NextGameElapsed = reader.ReadBoolean()
                    ? TimeSpan.FromTicks(reader.ReadInt64())
                    : null,
                NextRealUtc = reader.ReadBoolean()
                    ? ReadTimestamp(reader)
                    : null,
                LastSpawnUtc = reader.ReadBoolean()
                    ? ReadTimestamp(reader)
                    : null,
                LastSpawnGameElapsed = reader.ReadBoolean()
                    ? TimeSpan.FromTicks(reader.ReadInt64())
                    : null,
                TriggerPending = reader.ReadBoolean()
            };

            schedules.Add(schedule);
        }

        return new DynamicEventRuntimeState(instances, schedules);
    }

    // --- Таблица строк -----------------------------------------------------

    /// <summary>
    /// Таблица уникальных строк с назначением стабильных индексов.
    ///
    /// Порядок — по первому появлению. Сортировка не нужна: Brotli сжимает и
    /// так, а стабильность важна лишь внутри одного снимка.
    /// </summary>
    private sealed class StringTable
    {
        private readonly List<string> _values = new();
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public int Add(string? value)
        {
            var text = value ?? string.Empty;
            if (_index.TryGetValue(text, out var existing)) return existing;

            var index = _values.Count;
            _values.Add(text);
            _index[text] = index;
            return index;
        }

        public int Count => _values.Count;
        public string this[int index] => _values[index];

        /// <summary>
        /// Индекс строки. НЕ добавляет новую строку: таблица уже записана в файл,
        /// и добавление после этого сделало бы индексы нечитаемыми. Неизвестная
        /// строка — ошибка кодека, а не повод молча испортить снимок.
        /// </summary>
        public int Index(string? value)
        {
            var text = value ?? string.Empty;
            if (_index.TryGetValue(text, out var index)) return index;

            throw new InvalidOperationException(
                "Строка «" + text + "» не была добавлена в таблицу до записи снимка.");
        }
    }

    private static void WriteStringTable(BinaryWriter writer, StringTable table)
    {
        WriteVarint(writer, (ulong)table.Count);
        for (var index = 0; index < table.Count; index++) WriteString(writer, table[index]);
    }

    private static StringTable ReadStringTable(BinaryReader reader)
    {
        var count = checked((int)ReadVarint(reader));
        var table = new StringTable();
        for (var index = 0; index < count; index++) table.Add(ReadString(reader));
        return table;
    }

    // --- Разделы -----------------------------------------------------------

    private static void WritePairs(
        BinaryWriter writer, StringTable table, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        WriteVarint(writer, (ulong)pairs.Count);
        foreach (var pair in pairs)
        {
            writer.Write(table.Index(pair.Key));
            writer.Write(table.Index(pair.Value));
        }
    }

    private static IReadOnlyDictionary<string, string> ReadPairs(BinaryReader reader, StringTable table)
    {
        var count = checked((int)ReadVarint(reader));
        var result = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var key = table[reader.ReadInt32()];
            var value = table[reader.ReadInt32()];
            result[key] = value;
        }
        return result;
    }

    private static void WriteFlags(
        BinaryWriter writer, StringTable table, IReadOnlyList<KeyValuePair<string, bool>> flags)
    {
        WriteVarint(writer, (ulong)flags.Count);
        foreach (var pair in flags)
        {
            writer.Write(table.Index(pair.Key));
            writer.Write(pair.Value);
        }
    }

    private static IReadOnlyDictionary<string, bool> ReadFlags(BinaryReader reader, StringTable table)
    {
        var count = checked((int)ReadVarint(reader));
        var result = new Dictionary<string, bool>(count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var key = table[reader.ReadInt32()];
            result[key] = reader.ReadBoolean();
        }
        return result;
    }

    private static void WriteIntPairs(
        BinaryWriter writer, StringTable table, IReadOnlyList<KeyValuePair<string, int>> pairs)
    {
        WriteVarint(writer, (ulong)pairs.Count);
        foreach (var pair in pairs)
        {
            writer.Write(table.Index(pair.Key));
            WriteVarint(writer, ZigZag(pair.Value));
        }
    }

    private static IReadOnlyDictionary<string, int> ReadIntPairs(BinaryReader reader, StringTable table)
    {
        var count = checked((int)ReadVarint(reader));
        var result = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var key = table[reader.ReadInt32()];
            result[key] = (int)UnZigZag(ReadVarint(reader));
        }
        return result;
    }

    // --- Примитивы ---------------------------------------------------------

    /// <summary>
    /// Сортирует записи по ключу перед записью.
    ///
    /// Это не требование формата, а приём сжатия: одинаковые ключи соседних
    /// записей дают повторяющиеся последовательности индексов, которые Brotli
    /// сжимает почти в ноль. Плюс одинаковый снимок всегда даёт байт-в-байт
    /// одинаковый файл, что удобно для тестов и сравнения сохранений.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, T>> Sorted<T>(
        IReadOnlyDictionary<string, T> source) =>
        source.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();

    private static ulong ZigZag(int value) =>
        (ulong)((value << 1) ^ (value >> 31));

    private static long UnZigZag(ulong value) =>
        (long)(value >> 1) ^ -(long)(value & 1);

    private static void WriteVarint(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    private static ulong ReadVarint(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var current = reader.ReadByte();
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Повреждённое сохранение: varint длиннее 64 бит.");
        }
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteVarint(writer, (ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader) =>
        Encoding.UTF8.GetString(reader.ReadBytes(checked((int)ReadVarint(reader))));

    private static void WriteDouble(BinaryWriter writer, double value) =>
        writer.Write(BitConverter.DoubleToInt64Bits(value));

    private static double ReadDouble(BinaryReader reader) =>
        BitConverter.Int64BitsToDouble(reader.ReadInt64());

    private static void WriteTimestamp(BinaryWriter writer, DateTimeOffset value)
    {
        // Секунды Unix: миллисекунды не нужны (время идёт с точностью до секунд
        // в интерфейсе), а экономия — четыре байта на каждую отметку.
        WriteVarint(writer, (ulong)value.ToUnixTimeSeconds());
    }

    private static DateTimeOffset ReadTimestamp(BinaryReader reader) =>
        DateTimeOffset.FromUnixTimeSeconds((long)ReadVarint(reader));
}
