namespace AssistQuestEditor.Domain;

using System;
using System.Collections.Generic;

/// <summary>
/// КАЛИБРОВКА ЭФФЕКТОВ ПРЕДМЕТОВ (еда, напитки, добавки, лекарства, бытовые
/// предметы).
///
/// Здесь собраны ВСЕ числа, которые предмет начисляет игроку помимо
/// питательности. Питательность (ккал, вода, вес порции) лежит отдельно —
/// в <c>CharacterConsumableCatalog</c>, потому что она описывает сам предмет,
/// а не его влияние на шкалы.
///
/// Как читать имена:
///   Reduce*   — НАСКОЛЬКО СНИЗИТЬ значение, % шкалы;
///   Add*      — НАСКОЛЬКО ПОДНЯТЬ значение, % шкалы;
///   Change*   — СДВИГ в любую сторону, % шкалы (минус опускает);
///   *GameSeconds / *RealSeconds — длительность эффекта, секунды;
///   *Min / *Max — порог срабатывания;
///   *RepeatCount — на каком по счёту использовании включается эффект.
///
/// Все проценты — от ШКАЛЫ (0..100%), а не от максимума в единицах.
/// Положительное число в Reduce* = «стало лучше» (стресс/усталость падают).
/// </summary>
public static class CharacterItemTuning
{
    // ═══════════════════════════════════════════════════════════════════════
    // 0. ОБЩИЕ МНОЖИТЕЛИ, ОБЩИЕ ДЛЯ НЕСКОЛЬКИХ ПРЕДМЕТОВ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Во сколько раз бафф «Бык» усиливает эффекты-форсажи предметов
    /// (сигарета, добавки).</summary>
    public const double BullOverchargeMultiplier = 1.5d;

    /// <summary>Длительность баффа «Бык» от повторного приёма добавок
    /// (2 игровых дня).</summary>
    public const double BullFromSupplementGameSeconds = 2d * 24d * 3600d;

    /// <summary>Длительность баффа «Крепкие кости» от повторных порций молока
    /// (3 игровых дня).</summary>
    public const double StrongBonesGameSeconds = 3d * 24d * 3600d;

    /// <summary>Число порций молока, после которого включается «Крепкие кости».</summary>
    public const int MilkStrongBonesRepeatCount = 3;

    /// <summary>Длительность дебаффа «Сонливость» (2 игровых часа).</summary>
    public const double DrowsinessGameSeconds = 2d * 3600d;

    // ═══════════════════════════════════════════════════════════════════════
    // 1. ЕДА (базовые блюда и продукты)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Молоко: снижение стресса.</summary>
    public const double MilkStressReducePercent = 2d;

    /// <summary>Готовое блюдо: снижение усталости (сытная еда бодрит).</summary>
    public const double MealFatigueReducePercent = 2d;

    /// <summary>Кефир: снижение стресса.</summary>
    public const double KefirStressReducePercent = 2d;

    /// <summary>Йогурт: снижение стресса.</summary>
    public const double YogurtStressReducePercent = 2d;

    /// <summary>Сыр: прибавка устойчивости.</summary>
    public const double CheeseResiliencePercent = 1d;

    /// <summary>Орехи: прибавка устойчивости.</summary>
    public const double NutsResiliencePercent = 2d;

    /// <summary>Овсянка: снижение стресса.</summary>
    public const double OatmealStressReducePercent = 1d;

    /// <summary>Овсянка: сдвиг метаболизма.</summary>
    public const double OatmealMetabolismPercent = 1d;

    /// <summary>Гречка: прибавка устойчивости.</summary>
    public const double BuckwheatResiliencePercent = 1d;

    /// <summary>Овощное рагу: прибавка устойчивости.</summary>
    public const double VegetableStewResiliencePercent = 1d;

    /// <summary>Суп: снижение усталости.</summary>
    public const double SoupFatigueReducePercent = 1d;

    /// <summary>Банан: сдвиг метаболизма.</summary>
    public const double BananaMetabolismPercent = 1d;

    /// <summary>Яблоко: снижение стресса.</summary>
    public const double AppleStressReducePercent = 1d;

    /// <summary>Апельсин: прибавка устойчивости.</summary>
    public const double OrangeResiliencePercent = 1d;

    /// <summary>Мёд: снижение усталости.</summary>
    public const double HoneyFatigueReducePercent = 1d;

    /// <summary>Тёмный шоколад: снижение стресса.</summary>
    public const double DarkChocolateStressReducePercent = 2d;

    /// <summary>Домашняя колбаса (Гоша): снижение стресса.</summary>
    public const double HomemadeSausageStressReducePercent = 1d;

    /// <summary>
    /// «Легендарный шашлык Руслана»: форсаж ЭНЕРГИИ, % шкалы.
    ///
    /// ФОРСАЖ ОБЫЧНОЙ ЕДОЙ НЕ ДАЁТСЯ (задано автором: «Форсаж игрок может
    /// получить только от внешних факторов, комбинации действий, по результатам
    /// квестов или использования каких-либо предметов. Сам по себе форсаж не
    /// возникает»). Это блюдо — ИМЕННО такой предмет: его выдают за квест, и
    /// превышение шкалы — его награда, а не следствие сытости.
    /// </summary>
    public const double LegendaryShashlikEnergyOverchargePercent = 25d;

    /// <summary>«Легендарный шашлык Руслана»: форсаж СТРЕССА, % шкалы. Пока запас
    /// не исчерпан, получаемый стресс уходит в него, а основная шкала не растёт.</summary>
    public const double LegendaryShashlikStressOverchargePercent = 25d;

    // ═══════════════════════════════════════════════════════════════════════
    // 2. НАПИТКИ (эффекты сверх питательности)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Лимонный чай: снижение стресса.</summary>
    public const double LemonTeaStressReducePercent = 3d;

    /// <summary>Лимонный чай: сдвиг метаболизма.</summary>
    public const double LemonTeaMetabolismPercent = 2d;

    /// <summary>Лимонный чай: прибавка устойчивости.</summary>
    public const double LemonTeaResiliencePercent = 1d;

    /// <summary>Травяной чай: снижение стресса.</summary>
    public const double HerbalTeaStressReducePercent = 5d;

    /// <summary>Травяной чай: прибавка устойчивости.</summary>
    public const double HerbalTeaResiliencePercent = 2d;

    /// <summary>Кофе: снижение усталости.</summary>
    public const double CoffeeFatigueReducePercent = 3d;

    /// <summary>Кофе: снижение стресса.</summary>
    public const double CoffeeStressReducePercent = 1d;

    /// <summary>Энергетик: сдвиг метаболизма.</summary>
    public const double EnergyDrinkMetabolismPercent = 2d;

    /// <summary>Эспрессо: снижение усталости.</summary>
    public const double EspressoFatigueReducePercent = 2d;

    /// <summary>Чёрный чай: снижение стресса.</summary>
    public const double BlackTeaStressReducePercent = 1d;

    /// <summary>Зелёный чай: снижение стресса.</summary>
    public const double GreenTeaStressReducePercent = 2d;

    /// <summary>Зелёный чай: прибавка устойчивости.</summary>
    public const double GreenTeaResiliencePercent = 1d;

    /// <summary>Кофе без кофеина: снижение стресса.</summary>
    public const double DecafCoffeeStressReducePercent = 1d;

    /// <summary>Чай с имбирём и лимоном: снижение стресса.</summary>
    public const double GingerLemonTeaStressReducePercent = 3d;

    /// <summary>Чай с имбирём и лимоном: сдвиг метаболизма.</summary>
    public const double GingerLemonTeaMetabolismPercent = 2d;

    /// <summary>Спортивный напиток: прибавка устойчивости.</summary>
    public const double SportsDrinkResiliencePercent = 1d;

    /// <summary>Пакетик электролитов: прибавка устойчивости.</summary>
    public const double ElectrolyteResiliencePercent = 2d;

    /// <summary>Пиво: снижение стресса (алкоголь «расслабляет»).</summary>
    public const double BeerStressReducePercent = 3d;

    /// <summary>Пиво: ОБЕЗВОЖИВАНИЕ (минус к жидкости), % — спирт забирает
    /// больше, чем даёт напиток.</summary>
    public const double BeerHydrationChangePercent = -6d;

    /// <summary>Пиво: НАДБАВКА усталости (% со знаком «плюс» означает «устать
    /// сильнее», потому что передаётся в ReduceFatigue как отрицательное).</summary>
    public const double BeerFatigueIncreasePercent = 3d;

    /// <summary>Пиво: длительность дебаффа «Последействие алкоголя»
    /// (3 игровых часа).</summary>
    public const double BeerAftereffectGameSeconds = 3d * 3600d;

    // ═══════════════════════════════════════════════════════════════════════
    // 3. ВИТАМИНЫ И МИНЕРАЛЫ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Мультивитамины: прибавка устойчивости.</summary>
    public const double MultivitaminResiliencePercent = 5d;

    /// <summary>Мультивитамины: сдвиг метаболизма.</summary>
    public const double MultivitaminMetabolismPercent = 2d;

    /// <summary>Мультивитамины: на каком по счёту приёме включается «Бык».</summary>
    public const int MultivitaminBullRepeatCount = 5;

    /// <summary>Витамин C (шипучий): прибавка устойчивости.</summary>
    public const double VitaminCEffervescentResiliencePercent = 6d;

    /// <summary>Витамин C (шипучий): сдвиг метаболизма.</summary>
    public const double VitaminCEffervescentMetabolismPercent = 2d;

    /// <summary>Витамин C (шипучий): снижение стресса.</summary>
    public const double VitaminCEffervescentStressReducePercent = 1d;

    /// <summary>Омега-3: снижение стресса.</summary>
    public const double Omega3StressReducePercent = 2d;

    /// <summary>Омега-3: прибавка устойчивости.</summary>
    public const double Omega3ResiliencePercent = 2d;

    /// <summary>Витамин D3: прибавка устойчивости.</summary>
    public const double VitaminD3ResiliencePercent = 4d;

    /// <summary>Витамин D3: сдвиг метаболизма.</summary>
    public const double VitaminD3MetabolismPercent = 1d;

    /// <summary>Магний: снижение стресса.</summary>
    public const double MagnesiumStressReducePercent = 5d;

    /// <summary>Магний: прибавка устойчивости.</summary>
    public const double MagnesiumResiliencePercent = 2d;

    /// <summary>Магний: сдвиг метаболизма.</summary>
    public const double MagnesiumMetabolismPercent = 1d;

    /// <summary>Магний: на каком по счёту приёме появляется НАДБАВКА стресса
    /// (передозировка).</summary>
    public const int MagnesiumOverdoseRepeatCount = 10;

    /// <summary>Магний: величина надбавки стресса при передозировке.</summary>
    public const double MagnesiumOverdoseStressPercent = 1d;

    /// <summary>Цинк: прибавка устойчивости.</summary>
    public const double ZincResiliencePercent = 3d;

    /// <summary>Цинк: на каком по счёту приёме появляется надбавка стресса.</summary>
    public const int ZincOverdoseRepeatCount = 8;

    /// <summary>Цинк: величина надбавки стресса при передозировке.</summary>
    public const double ZincOverdoseStressPercent = 1d;

    /// <summary>Ашваганда: снижение стресса.</summary>
    public const double AshwagandhaStressReducePercent = 7d;

    /// <summary>Ашваганда: прибавка устойчивости.</summary>
    public const double AshwagandhaResiliencePercent = 3d;

    /// <summary>Витамин C (обычный): прибавка устойчивости.</summary>
    public const double VitaminCResiliencePercent = 8d;

    /// <summary>Витамин C (обычный): сдвиг метаболизма.</summary>
    public const double VitaminCMetabolismPercent = 2d;

    /// <summary>Витамин C (обычный): снижение стресса.</summary>
    public const double VitaminCStressReducePercent = 1d;

    /// <summary>Витамин C (обычный): на каком по счёту приёме включается «Бык».</summary>
    public const int VitaminCBullRepeatCount = 5;

    // ═══════════════════════════════════════════════════════════════════════
    // 4. АДАПТОГЕНЫ И ЛЕКАРСТВА
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Валериана: снижение стресса.</summary>
    public const double ValerianStressReducePercent = 8d;

    /// <summary>Сорбент: разовое восстановление здоровья.</summary>
    public const double SorbentHealthPercent = 3d;

    /// <summary>Сорбент: снижение стресса.</summary>
    public const double SorbentStressReducePercent = 2d;

    /// <summary>Сорбент: длительность эффекта (4 игровых часа).</summary>
    public const double SorbentGameSeconds = 4d * 3600d;

    /// <summary>Регидратационные соли: прибавка устойчивости.</summary>
    public const double RecoverySaltsResiliencePercent = 2d;

    /// <summary>Настойка-адаптоген: снижение стресса.</summary>
    public const double TinctureStressReducePercent = 8d;

    /// <summary>Настойка-адаптоген: прибавка устойчивости.</summary>
    public const double TinctureResiliencePercent = 4d;

    /// <summary>Настойка-адаптоген: сдвиг метаболизма.</summary>
    public const double TinctureMetabolismPercent = 3d;

    /// <summary>Обезболивающее: разовое восстановление здоровья.</summary>
    public const double PainkillerHealthPercent = 5d;

    /// <summary>Обезболивающее: снижение стресса.</summary>
    public const double PainkillerStressReducePercent = 3d;

    /// <summary>Обезболивающее: длительность эффекта «Обезболивание»
    /// (2 игровых часа).</summary>
    public const double AnalgesiaGameSeconds = 2d * 3600d;

    /// <summary>Обезболивающее: на каком по счёту приёме начинается
    /// «Злоупотребление».</summary>
    public const int PainkillerOveruseRepeatCount = 4;

    /// <summary>Обезболивающее: надбавка стресса при злоупотреблении.</summary>
    public const double PainkillerOveruseStressPercent = 1d;

    /// <summary>Обезболивающее: длительность дебаффа «Злоупотребление
    /// обезболивающими» (6 игровых часов).</summary>
    public const double AnalgesicOveruseGameSeconds = 6d * 3600d;

    // ═══════════════════════════════════════════════════════════════════════
    // 5. КУРЕНИЕ, ГИГИЕНА И БЫТ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Сигарета: снижение стресса (мнимое облегчение).</summary>
    public const double CigaretteStressReducePercent = 2d;

    /// <summary>Сигарета: форсаж энергии.</summary>
    public const double CigaretteEnergyOverchargePercent = 3d;

    /// <summary>Сигарета: надбавка стресса.</summary>
    public const double CigaretteStressAddPercent = 1d;

    /// <summary>Сигарета: длительность дебаффа «Никотиновый откат»
    /// (60 реальных секунд).</summary>
    public const double NicotineReboundRealSeconds = 60d * 60d;

    // ═══════════════════════════════════════════════════════════════════════
    // 6. ДОЗЫ КОФЕИНА ПО ПРЕДМЕТАМ (мг на порцию)
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Дозы — игровые справочные значения, а не медицинская инструкция: они
    // основаны на типичных величинах для соответствующих напитков и продуктов.
    // Меняя дозу, помни: от неё считаются и форсаж энергии, и привыкание, и
    // пороги суточной передозировки (секция 14 CharacterVitalsTuning).

    /// <summary>Кофе — 95 мг на порцию.</summary>
    public const double CaffeineMgCoffee = 95d;

    /// <summary>Эспрессо — 80 мг.</summary>
    public const double CaffeineMgEspresso = 80d;

    /// <summary>Чёрный чай — 50 мг.</summary>
    public const double CaffeineMgBlackTea = 50d;

    /// <summary>Зелёный чай — 35 мг.</summary>
    public const double CaffeineMgGreenTea = 35d;

    /// <summary>Кола — 40 мг.</summary>
    public const double CaffeineMgCola = 40d;

    /// <summary>Энергетик — 80 мг.</summary>
    public const double CaffeineMgEnergyDrink = 80d;

    /// <summary>Тёмный шоколад — 25 мг.</summary>
    public const double CaffeineMgDarkChocolate = 25d;

    /// <summary>Кофе без кофеина — 5 мг.</summary>
    public const double CaffeineMgDecafCoffee = 5d;

    /// <summary>Доза кофеина по идентификатору предмета (0 — кофеина нет).</summary>
    private static readonly IReadOnlyDictionary<string, double>
        CaffeineMgByItem =
            new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["drink.coffee"] = CaffeineMgCoffee,
                ["drink.espresso"] = CaffeineMgEspresso,
                ["drink.black_tea"] = CaffeineMgBlackTea,
                ["drink.green_tea"] = CaffeineMgGreenTea,
                ["drink.cola"] = CaffeineMgCola,
                ["drink.energy"] = CaffeineMgEnergyDrink,
                ["food.dark_chocolate"] = CaffeineMgDarkChocolate,
                ["drink.decaf_coffee"] = CaffeineMgDecafCoffee
            };

    /// <summary>Сколько миллиграммов кофеина в порции предмета.</summary>
    public static double CaffeineMgOf(string itemId) =>
        CaffeineMgByItem.TryGetValue(
            itemId ?? string.Empty,
            out var value)
                ? value
                : 0d;
}
