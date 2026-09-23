using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Черты городов из пользовательского файла, перечитываемые при обращении.
///
/// Файл читается заново только тогда, когда он изменился (имя, время и размер),
/// и результат запоминается. Причина не в производительности самого разбора — в
/// том, что <see cref="Current"/> вызывается на КАЖДОЙ проверке локации и на
/// каждом шаге симуляции, а разбор файла там был бы напрасной работой.
///
/// Такой кэш обязан обновляться по метке файла, а не по времени в процессе:
/// черты автор правит и снаружи приложения, а сравнение по времени «когда мы
/// в последний раз читали» пропускало бы такие правки ровно до следующего
/// изменения файла.
/// </summary>
public sealed class CityBoundaryFileSource : ICityBoundarySource
{
    private readonly string _path;
    private readonly Func<CityBoundaryIndex> _load;

    private DateTime _stamp;
    private long _length = -1;
    private CityBoundaryIndex _cached = CityBoundaryIndex.Empty;

    public CityBoundaryFileSource(string? path = null, Func<CityBoundaryIndex>? load = null)
    {
        _path = path ?? new CityBoundaryStore().FilePath;
        _load = load ?? (() => new CityBoundaryStore().Load());
    }

    public string FilePath => _path;

    public CityBoundaryIndex Current
    {
        get
        {
            // Файла нет — это нормальное состояние первого запуска, а не ошибка.
            // Пустой индекс в этом случае возвращается как есть.
            if (!File.Exists(_path))
            {
                _stamp = default;
                _length = -1;
                _cached = CityBoundaryIndex.Empty;
                return _cached;
            }

            try
            {
                var info = new FileInfo(_path);
                var stamp = info.LastWriteTimeUtc;
                var length = info.Length;

                if (length == _length && stamp == _stamp)
                    return _cached;

                _cached = _load();
                _length = length;
                _stamp = stamp;

                AppLogger.Info("CityBoundaryFileSource: черты перечитаны.",
                    $"count={_cached.Count}; path={_path}");
            }
            catch (Exception ex)
            {
                // Ошибка чтения НЕ должна ронять проверку локации: критерий тогда
                // сообщит, что черт нет, — и это лучше, чем падение редактора из-за
                // недописанного файла. Прошлый удачный результат сохраняется, пока
                // файл снова не станет читаемым.
                AppLogger.Error("CityBoundaryFileSource: не удалось перечитать черты.", ex, $"path={_path}");
            }

            return _cached;
        }
    }
}
