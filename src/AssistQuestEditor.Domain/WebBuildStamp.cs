using System.Security.Cryptography;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Отпечаток сборки, к которому привязан профиль WebView2.
///
/// Зачем это нужно. WebView2 хранит дисковой кеш подресурсов в своём профиле
/// (UserDataFolder) и отдаёт файл по полному URL, не сверяя содержимое с диском.
/// Параметр <c>?v=</c> у страницы спасает только саму страницу: ссылки на
/// <c>simulator.js</c> и <c>theme.css</c> версионируются в разметке, и если токен
/// там устарел, браузер спокойно вернёт СТАРЫЙ файл из кеша. Наблюдалось ровно
/// это: правки в скриптах не доезжали до пользователя, хотя файлы на диске были
/// новые.
///
/// Ручная чистка профиля проблему не решает по двум причинам:
///   1) она выполняется только в <c>compile.ps1</c>, то есть при обычной отладке
///      профиль не чистится никогда;
///   2) удаление каталога профиля, который в этот момент держит живой процесс
///      WebView2, молча не срабатывает — Windows не даёт удалить открытые файлы.
///
/// Поэтому профиль привязывается к отпечатку сборки: каталог профиля включает
/// хеш содержимого Web-ресурсов и версию сборки. Изменился хотя бы один файл —
/// изменился отпечаток, приложение открывает ДРУГОЙ пустой профиль, и старый кеш
/// физически не может быть использован. Чистка при этом не нужна вовсе; она
/// остаётся только как уборка отработавших профилей.
/// </summary>
public static class WebBuildStamp
{
    /// <summary>Имя файла отпечатка, который кладётся рядом с EXE.</summary>
    public const string FileName = "web-build.stamp";

    /// <summary>Сколько символов хеша попадает в имя каталога профиля.</summary>
    private const int StampLength = 16;

    /// <summary>
    /// Считает отпечаток по версии сборки и хешам файлов.
    ///
    /// Хеши сортируются по имени файла: порядок обхода каталога на NTFS не
    /// определён, и без сортировки тот же набор файлов давал бы разные отпечатки —
    /// приложение открывало бы новый профиль при каждом запуске.
    /// </summary>
    public static string Compute(
        string buildVersion,
        IEnumerable<KeyValuePair<string, string>> fileHashes)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);

        var builder = new StringBuilder();
        builder.Append(buildVersion ?? string.Empty).Append('\n');

        foreach (var pair in fileHashes.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..StampLength].ToLowerInvariant();
    }

    /// <summary>
    /// Считает отпечаток по СОДЕРЖИМОМУ каталога Web.
    ///
    /// Читается именно содержимое, а не время изменения: копирование,
    /// распаковка single-file и обновление из архива сохраняют mtime исходника,
    /// поэтому файл с тем же временем может содержать другой код. Это тот же
    /// довод, по которому манифест ресурсов хранит хеши, а не даты.
    /// </summary>
    public static string ComputeForDirectory(string webDirectory, string buildVersion)
    {
        var hashes = new List<KeyValuePair<string, string>>();

        if (Directory.Exists(webDirectory))
        {
            foreach (var file in Directory
                         .EnumerateFiles(webDirectory, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path
                    .GetRelativePath(webDirectory, file)
                    .Replace('\\', '/');

                hashes.Add(new KeyValuePair<string, string>(
                    relative,
                    HashFile(file)));
            }
        }

        return Compute(buildVersion, hashes);
    }

    /// <summary>
    /// Ищет файл отпечатка рядом с EXE, а при его отсутствии — в базовом каталоге.
    ///
    /// Порядок тот же, что у ресурсов (см. <see cref="ResourceRootResolver"/>):
    /// в single-file публикации базовый каталог — это кэш распаковки в %TEMP%,
    /// а поставка лежит рядом с исполняемым файлом. Второй вариант нужен
    /// отладочному запуску и тестам, где <c>Environment.ProcessPath</c> не
    /// указывает на каталог приложения.
    /// </summary>
    public static string? FindStampFile(string? executableDirectory, string? appBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var beside = Path.Combine(executableDirectory, FileName);
            if (File.Exists(beside))
            {
                return beside;
            }
        }

        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            return Path.Combine(appBaseDirectory, FileName);
        }

        return null;
    }

    /// <summary>Хеш файла; недоступный файл даёт пустую строку, а не исключение.</summary>
    private static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            // Файл занят или исчез между перечислением и чтением. Пустой хеш
            // меняет отпечаток, то есть приводит к новому профилю — безопасная
            // сторона ошибки: лишняя холодная загрузка вместо старого кеша.
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Путь профиля для отпечатка: <c>&lt;корень профилей&gt;/&lt;отпечаток&gt;</c>.</summary>
    public static string ProfilePathFor(string profileRoot, string stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);

        return Path.Combine(profileRoot, stamp);
    }
}
