namespace AssistQuestEditor.Domain;

/// <summary>
/// Размеры окна инвентаря, посчитанные от сетки.
///
/// Живут в домене, а не в окне, потому что тот же расчёт нужен странице: CSS
/// задаёт сторону ячейки и отступы, окно WinForms задаёт размер. Разойдись они —
/// и содержимое либо не влезет (появится полоса прокрутки там, где сетка
/// рассчитана на весь размер), либо останется пустая полоса.
///
/// Чистая арифметика без System.Drawing: тестируется без Windows и без WinForms.
/// </summary>
public static class InventoryLayoutRules
{
    /// <summary>Столбцов в сетке инвентаря.</summary>
    public const int Columns = 6;

    /// <summary>Строк в сетке инвентаря.</summary>
    public const int Rows = 3;

    /// <summary>Всего ячеек. Пустые рисуются намеренно: по ним видно свободное место.</summary>
    public const int Capacity = Columns * Rows;

    /// <summary>
    /// Сторона квадратной ячейки в пикселях.
    ///
    /// Прежняя ячейка была 78×78. «Вчетверо меньше» — это четверть ПЛОЩАДИ, то
    /// есть сторона вдвое меньше: 39. Уменьшение стороны вчетверо дало бы 19 px,
    /// где подпись предмета перестала бы читаться.
    /// Значение совпадает с <c>--inventory-cell</c> в theme.css и проверяется
    /// прогоном: разойтись они не должны.
    /// </summary>
    public const int CellSize = 39;

    /// <summary>Зазор между ячейками.</summary>
    public const int CellGap = 7;

    /// <summary>Отступ сетки от краёв панели.</summary>
    public const int GridPadding = 10;

    /// <summary>Высота заголовка панели инвентаря.</summary>
    public const int HeaderHeight = 34;

    /// <summary>
    /// Высота блока потребностей (здоровье, энергия, жидкость, усталость).
    ///
    /// Значение ИЗМЕРЕНО на отрисованной странице, а не подобрано: четыре строки
    /// полосок плюс отступы дают ровно 79 px. Прежняя оценка «на глаз» (104)
    /// оставляла пустую полосу над кошельком ровно в 25 px — окно выглядело
    /// так, будто содержимое не догрузилось.
    /// </summary>
    public const int VitalsHeight = 79;

    /// <summary>Высота кошелька (деньги, опыт, резерв).</summary>
    public const int WalletHeight = 30;

    /// <summary>Ширина сетки: столбцы, зазоры между ними и отступы по краям.</summary>
    public static int GridWidth =>
        Columns * CellSize + (Columns - 1) * CellGap + GridPadding * 2;

    /// <summary>Высота сетки: строки и зазоры между ними.</summary>
    public static int GridHeight =>
        Rows * CellSize + (Rows - 1) * CellGap + GridPadding * 2;

    /// <summary>
    /// Ширина окна: сетка плюс поля окна.
    ///
    /// Меньше сетки окно быть не может — ячейки уехали бы за край.
    /// </summary>
    public static int WindowWidth => GridWidth + WindowPadding * 2;

    /// <summary>
    /// Высота окна: заголовок, потребности, сетка, кошелёк и поля.
    ///
    /// Считается, а не подбирается: «подогнать под высоту инвентаря» означает,
    /// что окно не выше и не ниже содержимого. Подбор на глаз дал бы либо
    /// обрезанную сетку, либо пустую полосу снизу.
    /// </summary>
    public static int WindowHeight =>
        HeaderHeight + VitalsHeight + GridHeight + WalletHeight + WindowPadding * 2;

    /// <summary>Поле окна вокруг содержимого.</summary>
    public const int WindowPadding = 2;
}
