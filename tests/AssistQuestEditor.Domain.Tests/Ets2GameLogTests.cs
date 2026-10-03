using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Чтение <c>game.log.txt</c>, который ETS2 держит ОТКРЫТЫМ.
///
/// Найдено на реальном запуске: игра держит журнал на запись с разделяемым
/// доступом <c>FileShare.ReadWrite</c> (без <c>Delete</c>). Обычные
/// <c>File.ReadAllLines</c> открывают файл с <c>FileShare.Read</c> и получают
/// <c>IOException</c>. Монитор ловил исключение, <c>activeHex</c> становился
/// <c>null</c>, и активный профиль не определялся — то есть новый профиль ETS2
/// не обнаруживался и связать карьеру с миром не предлагалось ИМЕННО тогда,
/// когда игра запущена. Проверка воспроизводит блокировку и читает каталог тем
/// же путём, каким его проходит приложение.
/// </summary>
public sealed class Ets2GameLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "aq-ets2-log-" + Guid.NewGuid().ToString("N")[..8]);

    public Ets2GameLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка: её остаток не влияет на результат проверки.
        }
    }

    /// <summary>
    /// Держит журнал так, как это делает игра: запись разрешена, удаление — нет.
    /// Возвращённый поток НАДО закрыть; он намеренно держится открытым на время
    /// проверки.
    /// </summary>
    private FileStream HoldLogLikeTheGame(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

    [Fact]
    public void ActiveProfileIsDetectedWhileGameLogIsHeldOpenForWriting()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "4151455F5431"));
        var logPath = Path.Combine(_root, "game.log.txt");
        File.WriteAllLines(
            logPath,
            new[]
            {
                "00:00:11.557 : [GET FILES] File /home/steam_profiles/4151455F5431/ffb_lut.sii does not exist.",
                "00:00:11.594 : Set profile finished: 'AQE_T1'"
            });

        using var held = HoldLogLikeTheGame(logPath);

        // Отрицательный контроль: именно этот вызов и падал в мониторе. Если ОС
        // вдруг перестанет блокировать файл, проверка ниже перестанет быть
        // доказательством — поэтому она здесь и стоит.
        Assert.Throws<IOException>(() => File.ReadAllLines(logPath));

        var catalog = new Ets2ProfileReader(_root, steamRoot: _root).ReadCatalog();

        Assert.Equal("4151455F5431", catalog.ActiveHexFolder);
        Assert.Equal("AQE_T1", catalog.ActiveProfileName);
    }

    [Fact]
    public void SharedReaderReadsGameLogWhileItIsHeldOpenForWriting()
    {
        var logPath = Path.Combine(_root, "game.log.txt");
        File.WriteAllLines(logPath, new[] { "первая", "вторая" });

        using var held = HoldLogLikeTheGame(logPath);

        var lines = Ets2GameLog.ReadLines(logPath);

        Assert.Equal(new[] { "первая", "вторая" }, lines);
    }
}
