using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AssistQuestEditor.App;

/// <summary>
/// Дозапись строк в общий файл журнала, когда в него пишут несколько процессов.
///
/// Зачем отдельный помощник: приложение допускает короткую одновременную работу
/// двух процессов. Второй экземпляр пишет, что передал путь, и завершается, а
/// первый в тот же момент пишет, что запрос получил.
/// <see cref="File.AppendAllText(string, string)"/> открывает файл с общим
/// доступом только на чтение, поэтому вторая запись падает с
/// <see cref="IOException"/>, а журнал такие сбои глушит: строка молча
/// пропадает. Проверка CI, которая ищет маркер в журнале, из-за этого падает
/// случайным образом — что и произошло.
///
/// Блокировка внутри процесса не помогает: у каждого процесса своя. Поэтому
/// запись защищена именованным мьютексом, общим для процессов. Имя выводится
/// из пути журнала, чтобы разные файлы не ждали друг друга.
/// </summary>
internal static class InterprocessLogWriter
{
    /// <summary>Блокировка внутри процесса: мьютекс сериализует процессы, а не потоки.</summary>
    private static readonly object Sync = new();

    private static readonly ConcurrentDictionary<string, string> MutexNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько раз пробовать захватить мьютекс, прежде чем отказаться.</summary>
    private const int MaxAttempts = 10;

    /// <summary>
    /// Ожидание мьютекса, мс. Реальная запись занимает доли миллисекунды, поэтому
    /// ожидание короткое: журнал не должен задерживать работу приложения.
    /// </summary>
    private const int WaitTimeoutMs = 1000;

    /// <summary>
    /// Дописывает строку в файл. Сбой записи не выбрасывается наружу: диагностика
    /// не должна останавливать приложение.
    /// </summary>
    public static void Append(string path, string text)
    {
        var name = MutexNames.GetOrAdd(Path.GetFullPath(path), CreateMutexName);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            Mutex? mutex = null;
            var owned = false;

            try
            {
                mutex = new Mutex(initiallyOwned: false, name);

                try
                {
                    owned = mutex.WaitOne(WaitTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Прежний владелец завершился, не освободив мьютекс: владение
                    // перешло к текущему процессу.
                    owned = true;
                }

                if (!owned)
                    continue;

                lock (Sync)
                    File.AppendAllText(path, text, new UTF8Encoding(false));

                return;
            }
            catch (IOException)
            {
                // За файл ещё держится читатель или писатель: повтор.
            }
            catch (UnauthorizedAccessException)
            {
                // Файл защищён от записи: повторять бессмысленно.
                return;
            }
            finally
            {
                if (owned)
                {
                    try
                    {
                        mutex!.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // Владение потеряно (аварийное завершение): освобождать нечего.
                    }
                }

                mutex?.Dispose();
            }
        }
    }

    /// <summary>
    /// Имя мьютекса выводится из пути журнала: разные журналы не должны ждать
    /// друг друга. Хеш нужен потому, что в имени мьютекса недопустимы «\» и «:».
    /// </summary>
    private static string CreateMutexName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return "Local\\AssistQuestEditor.Log." + Convert.ToHexString(hash, 0, 8);
    }
}
