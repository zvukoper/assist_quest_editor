namespace AssistQuestEditor.Domain;

/// <summary>
/// ЦЕННОСТЬ БЛЮДА по формату кафе «У Дороги».
///
/// Формат задан автором как четыре числа <c>A-B-C-D</c>:
///   A — Польза для здоровья,
///   B — Утоление голода,
///   C — Утоление жажды,
///   D — Тонизирующий эффект.
///
/// Зачем отдельный тип, а не четыре числа в каталоге. У четырёх чисел один
/// смысл и одна судьба: они приходят из ОДНОЙ строки меню, показываются вместе
/// и пересчитываются в шкалы в одном месте. Четыре несвязанных <c>double</c>
/// разошлись бы при первой правке (перепутанный порядок аргументов компилируется
/// молча), а запись с ИМЕНОВАННЫМИ полями читается как строка меню.
///
/// ГДЕ ЧИСЛА. Ценности блюд лежат в <c>CharacterConsumableCatalog.CafeValues</c>
/// рядом с ккал и миллилитрами: это описание предмета, а не его влияние на шкалы.
/// Пересчёт в шкалы живёт в <c>CharacterVitalsEngine.UseItem</c>.
/// </summary>
public readonly record struct CafeValue(
    double HealthPercent,
    double HungerPercent,
    double ThirstPercent,
    double TonicPercent,
    int PriceRubles)
{
    /// <summary>Блюдо без ценности и без цены — так отвечает каталог о неизвестном.</summary>
    public static CafeValue None => default;

    /// <summary>Есть ли у позиции хоть какая-то ценность (кроме нулей).</summary>
    public bool HasEffect =>
        HealthPercent > 0d ||
        HungerPercent > 0d ||
        ThirstPercent > 0d ||
        TonicPercent > 0d;
}
