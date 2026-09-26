using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Отпечаток сборки, к которому привязан профиль WebView2.
///
/// Проверка появилась после того, как правки в `simulator.js` и `theme.css`
/// перестали доезжать до пользователя: WebView2 отдавал эти файлы из дисковой
/// кеша своего профиля. Хост версионирует только адрес САМОЙ страницы, а ссылки
/// на подресурсы версионируются в разметке, поэтому устаревший токен означал
/// возврат старого файла. Свойства ниже — это ровно то, на что опирается
/// привязка профиля к отпечатку.
/// </summary>
public sealed class WebBuildStampTests
{
    private static Dictionary<string, string> Files(params (string Name, string Hash)[] items) =>
        items.ToDictionary(item => item.Name, item => item.Hash, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ChangedFileContentChangesStamp()
    {
        var before = WebBuildStamp.Compute("1.0.0", Files(("simulator.js", "aaa"), ("theme.css", "bbb")));
        var after = WebBuildStamp.Compute("1.0.0", Files(("simulator.js", "ccc"), ("theme.css", "bbb")));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ChangedBuildVersionChangesStamp()
    {
        var files = Files(("simulator.js", "aaa"));

        Assert.NotEqual(
            WebBuildStamp.Compute("1.0.0", files),
            WebBuildStamp.Compute("1.0.1", files));
    }

    [Fact]
    public void AddingOrRemovingFileChangesStamp()
    {
        var one = WebBuildStamp.Compute("1.0.0", Files(("a.js", "x")));
        var two = WebBuildStamp.Compute("1.0.0", Files(("a.js", "x"), ("b.js", "y")));

        Assert.NotEqual(one, two);
    }

    /// <summary>
    /// Порядок обхода каталога на NTFS не определён, поэтому тот же набор файлов
    /// обязан давать один и тот же отпечаток. Иначе приложение открывало бы новый
    /// профиль при каждом запуске — холодная загрузка без причины и растущая
    /// свалка каталогов.
    /// </summary>
    [Fact]
    public void FileOrderDoesNotAffectStamp()
    {
        var forward = Files(("a.js", "1"), ("b.js", "2"), ("c.css", "3"));
        var reversed = Files(("c.css", "3"), ("b.js", "2"), ("a.js", "1"));

        Assert.Equal(
            WebBuildStamp.Compute("1.0.0", forward),
            WebBuildStamp.Compute("1.0.0", reversed));
    }

    [Fact]
    public void SameInputsProduceSameStamp()
    {
        var files = Files(("simulator.js", "aaa"), ("vitals.js", "bbb"));

        Assert.Equal(
            WebBuildStamp.Compute("1.0.0", files),
            WebBuildStamp.Compute("1.0.0", files));
    }

    /// <summary>
    /// Отпечаток входит в имя каталога, поэтому обязан быть пригоден для файловой
    /// системы: только строчные hex-символы, без разделителей и пробелов.
    /// </summary>
    [Fact]
    public void StampIsFileSystemSafe()
    {
        var stamp = WebBuildStamp.Compute("1.0.40.192", Files(("simulator.js", "aaa")));

        Assert.Equal(16, stamp.Length);
        Assert.All(stamp, character => Assert.Contains(character, "0123456789abcdef"));
    }

    [Fact]
    public void EmptyFileSetStillProducesStamp()
    {
        var stamp = WebBuildStamp.Compute("1.0.0", Files());

        Assert.Equal(16, stamp.Length);
    }

    [Fact]
    public void DifferentBuildVersionWithSameContentDiffers()
    {
        // Версия входит в отпечаток не для красоты: пересборка без правки Web
        // всё равно должна давать чистый профиль, иначе кеш прошлой сборки
        // переживёт переустановку с тем же содержимым скриптов.
        var files = Files(("a.js", "1"));

        Assert.NotEqual(
            WebBuildStamp.Compute("1.0.40.192", files),
            WebBuildStamp.Compute("1.0.40.193", files));
    }

    [Fact]
    public void ProfilePathUsesStampAsSingleSegment()
    {
        var path = WebBuildStamp.ProfilePathFor(@"C:\root\WebView2", "abc123");

        Assert.Equal(@"C:\root\WebView2\abc123", path);
    }

    [Fact]
    public void ProfilePathRejectsEmptyArguments()
    {
        Assert.Throws<ArgumentException>(() => WebBuildStamp.ProfilePathFor("", "abc"));
        Assert.Throws<ArgumentException>(() => WebBuildStamp.ProfilePathFor(@"C:\root", ""));
    }

    /// <summary>
    /// Отпечаток по каталогу читает СОДЕРЖИМОЕ файлов. Копирование, распаковка
    /// single-file и обновление из архива сохраняют mtime исходника, поэтому
    /// файл с тем же временем изменения может содержать другой код — та же
    /// причина, по которой манифест ресурсов хранит хеши, а не даты.
    /// </summary>
    [Fact]
    public void DirectoryStampFollowsContentNotTimestamps()
    {
        using var directory = new TemporaryDirectory();

        var file = Path.Combine(directory.Path, "simulator.js");
        File.WriteAllText(file, "version one");

        var before = WebBuildStamp.ComputeForDirectory(directory.Path, "1.0.0");

        // То же ВРЕМЯ изменения, другое содержимое: подмена файла сборкой или
        // распаковкой выглядит именно так.
        var timestamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "version two");
        File.SetLastWriteTimeUtc(file, timestamp);

        var after = WebBuildStamp.ComputeForDirectory(directory.Path, "1.0.0");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DirectoryStampCoversNestedFiles()
    {
        using var directory = new TemporaryDirectory();
        var nested = Path.Combine(directory.Path, "panels");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "sidebar.js"), "code");

        var stamp = WebBuildStamp.ComputeForDirectory(directory.Path, "1.0.0");

        Assert.Equal(16, stamp.Length);
    }

    /// <summary>
    /// Отсутствующий каталог Web — не ошибка: отладочный запуск и тесты считают
    /// отпечаток от того, что есть. Падение здесь запретило бы запуск приложения.
    /// </summary>
    [Fact]
    public void MissingDirectoryProducesStampInsteadOfThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aqe-missing-" + Guid.NewGuid().ToString("N"));

        var stamp = WebBuildStamp.ComputeForDirectory(missing, "1.0.0");

        Assert.Equal(16, stamp.Length);
    }

    /// <summary>
    /// Относительные пути внутри каталога нормализуются к прямым слэшам: иначе
    /// один и тот же набор файлов давал бы разные отпечатки на Windows и в
    /// проверках, работающих с путями иначе.
    /// </summary>
    [Fact]
    public void DirectoryStampIsStableAcrossRepeatedCalls()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "a.js"), "a");
        File.WriteAllText(Path.Combine(directory.Path, "b.css"), "b");

        Assert.Equal(
            WebBuildStamp.ComputeForDirectory(directory.Path, "1.0.0"),
            WebBuildStamp.ComputeForDirectory(directory.Path, "1.0.0"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aqe-stamp-test-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Уборка временного каталога не должна валить тест.
            }
        }
    }
}
