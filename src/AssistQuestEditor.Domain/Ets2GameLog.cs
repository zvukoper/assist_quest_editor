namespace AssistQuestEditor.Domain;

/// <summary>
/// Чтение <c>game.log.txt</c> ETS2 во время работы игры.
///
/// Игра держит журнал ОТКРЫТЫМ на запись, а разделяемый доступ к нему ограничен
/// (<c>FileShare.ReadWrite</c> без <c>FileShare.Delete</c>). Обычные
/// <c>File.ReadAllLines</c> / <c>File.ReadLines</c> открывают файл с
/// <c>FileShare.Read</c> и на таком запуске получают <c>IOException</c>. Из-за
/// этого активный профиль (и загруженный слот) не определялись именно тогда,
/// когда ETS2 запущен, — то есть ровно в тот момент, когда AQE и должен
/// предложить связать новую карьеру с миром.
///
/// Правило живёт ЗДЕСЬ, в одном месте: и чтение профиля, и монитор сеанса ходят
/// за журналом, и две копии режима доступа разошлись бы — одну бы починили, а
/// вторая осталась бы с прежним поведением.
/// </summary>
public static class Ets2GameLog
{
    /// <summary>
    /// Читает журнал с максимально мягким режимом общего доступа.
    ///
    /// Сначала открываем файл как <c>FileShare.ReadWrite | FileShare.Delete</c>;
    /// если ОС всё равно не разрешает чтение, возвращаемся к обычному API, чтобы
    /// не потерять данные там, где мягкий режим недоступен.
    /// </summary>
    public static string[] ReadLines(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: false);

            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (!reader.EndOfStream)
                lines.Add(reader.ReadLine() ?? string.Empty);

            return lines.ToArray();
        }
        catch (IOException)
        {
            return File.ReadAllLines(path);
        }
    }
}
