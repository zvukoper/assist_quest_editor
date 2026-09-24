namespace AssistQuestEditor.Domain;

/// <summary>
/// Авторство ресурса: кто и когда его создал и кто последним изменил.
///
/// Отдельная запись, а не поля, потому что она проставляется ОДНИМ кодом на
/// любой файл (мир, кампания, квест, сцена): разложить её по определениям
/// значило бы дублировать правило «created_by берётся из настроек, created_on —
/// из текущего времени до секунды» в каждом сторе.
///
/// <see cref="CreatedOn"/> и <see cref="ModifiedOn"/> пишутся с секундами:
/// версия ресурса служит ключом кешбастинга и основанием для решения о
/// перезаписи при импорте, и точности до минуты для двух правок подряд не
/// хватает.
/// </summary>
public sealed record ResourceMetadata(
    string? CreatedBy = null,
    DateTimeOffset? CreatedOn = null,
    string? ModifiedBy = null,
    DateTimeOffset? ModifiedOn = null)
{
    /// <summary>Дата, по которой принимается решение «перезаписывать».</summary>
    public DateTimeOffset? EffectiveModifiedOn => ModifiedOn ?? CreatedOn;

    /// <summary>
    /// Метка ресурса для сравнения версий при импорте.
    ///
    /// «Изменился» — это не только другая дата: у ресурсов, скопированных
    /// файловым менеджером, даты могут совпасть до секунды, а содержимое
    /// отличаться. Поэтому сравнивается ещё и автор.
    /// </summary>
    public static bool Differs(ResourceMetadata? left, ResourceMetadata? right)
    {
        if (left is null && right is null)
            return false;
        if (left is null || right is null)
            return true;

        return left.EffectiveModifiedOn != right.EffectiveModifiedOn ||
               !string.Equals(left.ModifiedBy, right.ModifiedBy, StringComparison.Ordinal) ||
               !string.Equals(left.CreatedBy, right.CreatedBy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Правило допустимого псевдонима.
    ///
    /// Разрешены буквы, цифры, подчёркивание и пробел. Проверяется по Unicode-категориям,
    /// а не по диапазонам: кириллица и иероглифы обязаны проходить, а символы вроде
    /// «/» или «:» — нет, иначе имя нельзя будет показать в пути экспорта и в
    /// подписи к файлу.
    /// </summary>
    public static bool IsValidAuthor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var character in value)
        {
            if (character == '_' || character == ' ')
                continue;

            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);
            switch (category)
            {
                case System.Globalization.UnicodeCategory.UppercaseLetter:
                case System.Globalization.UnicodeCategory.LowercaseLetter:
                case System.Globalization.UnicodeCategory.TitlecaseLetter:
                case System.Globalization.UnicodeCategory.OtherLetter:
                case System.Globalization.UnicodeCategory.DecimalDigitNumber:
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Псевдоним по умолчанию: «User_» плюс дата и время до минут.
    ///
    /// До минут, а не до секунд: это подпись ЧЕЛОВЕКА, и в ней хватает минуты.
    /// Секунды нужны только у created_on/modified_on, которые пишутся отдельно.
    /// </summary>
    public static string DefaultAuthor(DateTimeOffset now) =>
        "User_" + now.ToString("yyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Помечает ресурс как созданный указанным автором.
    ///
    /// Создание и изменение разделены намеренно: у только что созданного ресурса
    /// modified_* заполняются теми же значениями, что created_*, — иначе список
    /// показывал бы «Modified by: —» у файла, который существует.
    /// </summary>
    public ResourceMetadata WithCreated(string author, DateTimeOffset moment) =>
        this with
        {
            CreatedBy = author,
            CreatedOn = moment,
            ModifiedBy = author,
            ModifiedOn = moment
        };

    /// <summary>Помечает ресурс как изменённый: created_* остаются прежними.</summary>
    public ResourceMetadata WithModified(string author, DateTimeOffset moment) =>
        this with
        {
            ModifiedBy = author,
            ModifiedOn = moment
        };
}
