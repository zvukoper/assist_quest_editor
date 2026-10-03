using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Определение профиля, открытого В ИГРЕ, по журналу запуска.
///
/// Найдено на настоящем <c>game.log.txt</c>: разбор искал подстроку
/// <c>"(steam_profiles|profiles)/"</c> через <c>IndexOf</c>, то есть как обычный
/// текст, тогда как это шаблон регулярного выражения. Таких символов в журнале не
/// бывает, поэтому активный профиль не находился НИКОГДА — в окне профилей ни
/// один не помечался как открытый в игре, хотя игра его знала.
///
/// Проверка идёт через каталог профилей: файлы журнала и каталогов создаются во
/// временной папке, и чтение берётся ровно тем путём, каким его проходит приложение.
/// </summary>
public sealed class Ets2ActiveProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "aq-ets2-active-" + Guid.NewGuid().ToString("N")[..8]);

    public Ets2ActiveProfileTests() => Directory.CreateDirectory(_root);

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

    private void WriteLog(params string[] lines)
        => File.WriteAllLines(Path.Combine(_root, "game.log.txt"), lines);

    private Ets2ProfileCatalog ReadCatalog() => new Ets2ProfileReader(_root, steamRoot: _root).ReadCatalog();

    /// <summary>
    /// Облачный профиль: каталог берётся из пути <c>steam_profiles</c>, а имя — из
    /// строки «Set profile finished». Обе подписи обязаны найтись.
    /// </summary>
    [Fact]
    public void CloudProfileIsDetectedFromSteamProfilesPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "5465737432"));
        WriteLog(
            "00:00:11.557 : [GET FILES] File /home/steam_profiles/5465737432/ffb_lut.sii does not exist.",
            "00:00:11.594 : Set profile finished: 'Test2'",
            "00:00:11.646 : New profile selected: 'Test2'");

        var catalog = ReadCatalog();

        Assert.Equal("5465737432", catalog.ActiveHexFolder);
        Assert.Equal("Test2", catalog.ActiveProfileName);
        Assert.True(catalog.Profiles.Single().IsActive);
    }

    /// <summary>
    /// Локальный профиль читается из пути <c>profiles/</c> — и это НЕ хвост
    /// <c>steam_profiles/</c>. Обратный порядок разбора маркеров вернул бы обрубок
    /// «steam_» вместо имени папки.
    /// </summary>
    [Fact]
    public void LocalProfileIsDetectedFromProfilesPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "4D696B68616C7963685F484558"));
        WriteLog(
            "00:00:30.001 : File /home/profiles/4D696B68616C7963685F484558/profile.sii loaded.",
            "00:00:30.100 : Set profile finished: 'Mikhalych_HEX'");

        var catalog = ReadCatalog();

        Assert.Equal("4D696B68616C7963685F484558", catalog.ActiveHexFolder);
        Assert.Equal("Mikhalych_HEX", catalog.ActiveProfileName);
    }

    /// <summary>
    /// Берётся ПОСЛЕДНЯЯ запись о профиле: за один запуск игрок мог переключить
    /// карьеру, и ранние строки уже устарели.
    /// </summary>
    [Fact]
    public void LastProfileEntryWins()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "4151455F5431"));
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "5465737432"));
        WriteLog(
            "00:00:11.557 : File /home/steam_profiles/4151455F5431/ffb_lut.sii does not exist.",
            "00:00:11.594 : Set profile finished: 'AQE_T1'",
            "00:00:22.021 : File /home/steam_profiles/5465737432/ffb_lut.sii does not exist.",
            "00:00:22.112 : Set profile finished: 'Test2'",
            "00:00:22.576 : New profile selected: 'Test2'");

        var catalog = ReadCatalog();

        Assert.Equal("5465737432", catalog.ActiveHexFolder);
        Assert.Equal("Test2", catalog.ActiveProfileName);

        // Помечен РОВНО один профиль: подсветка двух означала бы, что журнал
        // читается целиком без учёта порядка.
        Assert.Single(catalog.Profiles, profile => profile.IsActive);
    }

    /// <summary>
    /// Журнала нет — профили всё равно перечисляются, просто без отметки. Это
    /// обычное состояние свежей установки, а не ошибка чтения.
    /// </summary>
    [Fact]
    public void NewProfileRemainsActiveWhenLaterLogLinesMentionOldProfile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "4151455F5431"));
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "5465737432"));
        WriteLog(
            "00:00:11.557 : File /home/steam_profiles/4151455F5431/ffb_lut.sii does not exist.",
            "00:00:11.594 : Set profile finished: 'AQE_T1'",
            "00:00:22.021 : File /home/steam_profiles/5465737432/ffb_lut.sii does not exist.",
            "00:00:22.112 : Set profile finished: 'Test2'",
            "00:00:22.576 : New profile selected: 'Test2'",
            "00:00:22.800 : [GET FILES] File /home/steam_profiles/4151455F5431/profile.sii exists.");

        var catalog = ReadCatalog();

        Assert.Equal("5465737432", catalog.ActiveHexFolder);
        Assert.Equal("Test2", catalog.ActiveProfileName);
        Assert.Single(catalog.Profiles, profile => profile.IsActive);
    }

    [Fact]
    public void MissingLogStillListsProfiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam_profiles", "5465737432"));

        var catalog = ReadCatalog();

        Assert.Null(catalog.ActiveHexFolder);
        Assert.Null(catalog.ActiveProfileName);
        Assert.False(catalog.Profiles.Single().IsActive);
    }
}
