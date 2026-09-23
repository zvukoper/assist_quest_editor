using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Загрузчик черт городов.
///
/// В отличие от дорог и перекрёстков, черты НЕ поставляются с данными: их рисует
/// автор вручную, потому что провести границу города можно только глазами — по
/// геометрии дорог и точек этого не сделать. Поэтому файл лежит в пользовательских
/// данных, и его отсутствие — нормальное состояние первого запуска, а не ошибка.
/// </summary>
public static class CityBoundaryWorldDataLoader
{
    /// <summary>Тот же файл, что пишет окно рисования черт.</summary>
    private const string RelativePath = "city_boundaries.json";

    /// <summary>
    /// Читает черты из пользовательского каталога.
    ///
    /// Пустой индекс вместо исключения на любом пути: пока автор не нарисовал ни
    /// одной черты, критерии честно сообщают, что черт нет, — редактор при этом
    /// полностью работоспособен.
    /// </summary>
    public static CityBoundaryIndex Load()
    {
        return new CityBoundaryStore().Load();
    }

    /// <summary>Путь к файлу черт — для журнала и диагностики.</summary>
    public static string FilePath => new CityBoundaryStore().FilePath;

    /// <summary>Каталог пользовательских правок мира (тот же, что у перекрёстков).</summary>
    public static string Root => JunctionReviewStore.DefaultRoot;

    /// <summary>Имя файла черт относительно <see cref="Root"/>.</summary>
    public static string FileName => RelativePath;
}
