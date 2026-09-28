namespace AssistQuestEditor.Domain;

/// <summary>
/// Вид ссылки в тексте журнала: что открыть по клику на подсвеченный фрагмент.
///
/// Координата была ЕДИНСТВЕННЫМ кликабельным фрагментом, и её вид был зашит в
/// список ссылок. Теперь кликабельны ещё и показатели, перки и предметы, поэтому
/// вид ссылки стал данными, а не отдельным списком на каждый случай.
/// </summary>
public enum JournalLinkKind
{
    Coordinate,
    Metric,
    Perk,
    Item
}

/// <summary>
/// Разметка ссылок в тексте журнала: <c>[[вид:значение[:подпись]]]</c>.
///
/// Разбор живёт здесь, а не в форме окна, потому что СБОРКУ строк делает домен
/// (<see cref="CharacterStateReport"/>): формат принадлежит тому, кто его пишет,
/// и держать половину договора в Host значило бы, что правка одной стороны не
/// видна другой. Здесь же он покрыт тестами домена — у окна нет собственной
/// сборки, и проверять формат через форму было бы нельзя.
///
/// Квадратные скобки выбраны разделителем потому, что текст журнала — русская
/// проза: «обычный» разделитель (двоеточие, слэш) встречается в ней сам по себе.
/// </summary>
public sealed record JournalLinkMarkup(
    JournalLinkKind Kind,
    string Value,
    string Label)
{
    /// <summary>
    /// Разбирает полезную нагрузку ссылки.
    ///
    /// Подпись — это ТЕКСТ, который игрок читает и по которому щёлкает, поэтому
    /// она обязана быть человеческим названием: без неё журнал печатал бы
    /// «rested» вместо «Отдохнувший» и «water.bottle» вместо «Вода».
    ///
    /// <paramref name="Value"/> у перка составное — «id:вид»: окну перков нужны
    /// обе половины, чтобы найти пункт, не перебирая разделы. Вид перка идёт
    /// вторым полем разметки, подпись — третьим, поэтому у перка их на одно
    /// больше, чем у показателя и предмета.
    ///
    /// Возвращает <c>null</c> для неизвестного вида, пустого значения и перка без
    /// вида. Во всех трёх случаях вызывающий обязан напечатать разметку КАК ЕСТЬ:
    /// молча съесть текст значило бы потерять часть сообщения, а сделать ссылкой
    /// то, что открыть нечем, — обмануть ожидание клика.
    /// </summary>
    public static JournalLinkMarkup? Parse(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        var parts = payload.Split(':', 4, StringSplitOptions.None);
        if (parts.Length < 2)
            return null;

        var kind = parts[0].Trim().ToLowerInvariant();
        var value = parts[1].Trim();

        if (value.Length == 0)
            return null;

        // Подпись необязательна: разметка без неё печатает значение. Так ссылка,
        // собранная по старой памяти, не ломает строку.
        string Label(int index) =>
            parts.Length > index && parts[index].Trim().Length > 0
                ? parts[index].Trim()
                : value;

        return kind switch
        {
            "metric" => new JournalLinkMarkup(JournalLinkKind.Metric, value, Label(2)),

            "perk" => parts.Length >= 3 && parts[2].Trim().Length > 0
                ? new JournalLinkMarkup(
                    JournalLinkKind.Perk,
                    value + ":" + parts[2].Trim(),
                    Label(3))
                : null,

            "item" => new JournalLinkMarkup(JournalLinkKind.Item, value, Label(2)),

            _ => null
        };
    }

    /// <summary>
    /// Значение перка без вида: «rested» из «rested:buff».
    ///
    /// Отдельное свойство, а не разбор на стороне окна: значение составное только
    /// у перка, и знать об этом в двух местах — значит рассинхронизировать их.
    /// </summary>
    public string PerkId =>
        Kind == JournalLinkKind.Perk && Value.Contains(':')
            ? Value[..Value.IndexOf(':')]
            : Value;

    /// <summary>Вид перка: «buff» или «debuff»; пусто для остальных ссылок.</summary>
    public string PerkKind =>
        Kind == JournalLinkKind.Perk && Value.Contains(':')
            ? Value[(Value.IndexOf(':') + 1)..]
            : string.Empty;
}
