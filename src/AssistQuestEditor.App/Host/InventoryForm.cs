using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Инвентарь отдельным окном (клавиша I).
///
/// Отдельное ОКНО, а не панель поверх карты: инвентарь со своей сеткой занимал
/// половину оверлея и делил место с панелью персонажа, из-за чего содержимое
/// сумки и характеристики спорили за одно и то же место. Окно можно унести на
/// второй монитор и держать открытым, не закрывая карту.
///
/// Размер подобран ПОД сетку 6×3: 6 ячеек в ряд плюс отступы, 3 строки плюс
/// заголовок и кошелёк. Числа приходят из
/// <see cref="InventoryLayoutRules"/>, чтобы страница и окно считали одинаково —
/// расхождение дало бы полосу прокрутки там, где её быть не должно.
/// </summary>
public sealed class InventoryForm : WebViewForm
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Последний снимок, пришедший от Симулятора.
    ///
    /// Хранится, потому что окно можно открыть ПОЗЖЕ первого снимка: без него
    /// инвентарь показывался бы пустым до следующего обновления, то есть до
    /// следующего события в мире — а его можно ждать сколько угодно.
    /// </summary>
    private string? _lastSnapshotJson;

    public InventoryForm()
        : base(
            "Игрок",
            "inventory.html",
            new Size(
                InventoryLayoutRules.WindowWidth,
                InventoryLayoutRules.WindowHeight),
            "inventory")
    {
        // Минимальный размер — по ОДНОЙ строке сетки: содержимое может требовать
        // больше строк, и тогда появляется прокрутка (см. inventory.js). Требовать
        // минимум в три строки значило бы запретить уменьшать окно, хотя прокрутка
        // это уже позволяет.
        MinimumSize = new Size(
            InventoryLayoutRules.WindowWidth,
            InventoryLayoutRules.MinimumWindowHeight);
        // Явного максимума нет: окно можно растянуть, и сетка останется
        // квадратной — растянутся поля, а не ячейки.
        MaximizeBox = true;
        MinimizeBox = false;

        // Начальная позиция ставится ТОЛЬКО когда сохранённой геометрии нет.
        //
        // Базовый конструктор уже восстановил сохранённые координаты и размер, и
        // безусловный PlaceOnSecondaryScreen их ПЕРЕЗАПИСЫВАЛ — окно каждый раз
        // возвращалось на одно и то же место «по умолчанию», и сохранение
        // геометрии не работало, хотя она исправно записывалась.
        if (!WindowGeometryStore.HasSaved("inventory"))
            PlaceOnSecondaryScreen();
    }

    /// <summary>
    /// Ставит окно на второй экран, если он есть.
    ///
    /// Инвентарь открывают РЯДОМ с картой, а не вместо неё: на одном экране он
    /// перекрывал бы карту, ради которой его и открыли.
    /// Вызывается ТОЛЬКО при первом открытии — дальше место и размер берутся из
    /// сохранённой геометрии.
    /// </summary>
    private void PlaceOnSecondaryScreen()
    {
        var screens = Screen.AllScreens;
        var target = screens.Length > 1 ? screens[1] : Screen.PrimaryScreen;
        var area = target?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        Bounds = new Rectangle(area.Right - Width - 24, area.Top + 24, Width, Height);
    }

    protected override void OnBrowserReady()
    {
        // Снимок отправляется СРАЗУ при готовности: окно, открытое после
        // последнего обновления мира, иначе показывало бы «ожидание данных».
        if (_lastSnapshotJson is not null)
            PostJson(_lastSnapshotJson);

        AppLogger.Info("InventoryForm: окно инвентаря готово.",
            $"hasSnapshot={_lastSnapshotJson is not null}; size={Width}x{Height}");
    }

    /// <summary>
    /// Обновляет содержимое окна снимком Симулятора.
    ///
    /// Принимает ГОТОВЫЙ json, а не объект снимка: окно не должно знать структуру
    /// состояния мира — иначе она была бы описана в двух местах, и правка одного
    /// поля в Симуляторе молча ломала бы инвентарь.
    /// </summary>
    public void SetSnapshotJson(string snapshotJson)
    {
        _lastSnapshotJson = snapshotJson;

        if (Browser.CoreWebView2 is not null)
            PostJson(snapshotJson);
    }

    /// <summary>Показывает всплывающее уведомление о выдаче или изъятии предмета.</summary>
    public void NotifyInventoryChange(string itemId, double delta)
    {
        if (Browser.CoreWebView2 is null)
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "inventory_notification",
            itemId,
            delta
        }, MessageJsonOptions));
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString() ?? string.Empty;

            // Окно инвентаря только СООБЩАЕТ о своих действиях: состояние мира
            // принадлежит Симулятору, и записывать его отсюда значило бы иметь
            // два владельца одного состояния.
            switch (action)
            {
                case "inventory_ready":
                    if (_lastSnapshotJson is not null)
                        PostJson(_lastSnapshotJson);
                    break;

                // Клавиша I и Escape действуют и в окне инвентаря: «открывать и
                // закрывать по I» означает, что клавиша обязана работать в обоих
                // окнах. Закрывает форма Host — страница форму Windows закрыть
                // не может.
                case "close_inventory":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case "mark_inventory_seen":
                    InventoryItemSeenRequested?.Invoke(this, new InventoryItemSeenEventArgs(
                        root.TryGetProperty("itemId", out var itemId) ? itemId.GetString() ?? string.Empty : string.Empty));
                    break;

                default:
                    AppLogger.Warn("InventoryForm: неизвестное действие.", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("InventoryForm: не удалось разобрать сообщение.", ex, json);
        }
    }

    /// <summary>Игрок увидел предмет: значок «новый» надо снять.</summary>
    public event EventHandler<InventoryItemSeenEventArgs>? InventoryItemSeenRequested;

    /// <summary>
    /// Просьба закрыть окно (клавиша I или Escape внутри окна).
    ///
    /// Наружу, а не закрытием в самой форме: окно закрывает Симулятор — он
    /// владеет ссылкой на него и после закрытия отправляет снимок, чтобы карта
    /// узнала об этом.
    /// </summary>
    public event EventHandler? CloseRequested;
}

public sealed class InventoryItemSeenEventArgs : EventArgs
{
    public InventoryItemSeenEventArgs(string itemId) => ItemId = itemId;

    public string ItemId { get; }
}
