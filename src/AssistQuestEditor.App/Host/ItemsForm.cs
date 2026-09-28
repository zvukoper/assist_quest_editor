using System.Text.Json;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно «Предметы»: каталог всех предметов симулятора.
///
/// Автор просил показать название, изображение 48×48, описание, пищевую ценность
/// и эффекты, а количество выставить полем со значением 0 по умолчанию и
/// микрокнопками «+»/«−» для добавления и изъятия единицы.
///
/// Форма НЕ собирает каталог сама. Описания и цвета живут в домене
/// (<c>ItemCatalogFactory</c>), пищевая ценность — в профиле потребления
/// (<c>CharacterConsumableCatalog</c>). Вторая копия этих чисел здесь означала
/// бы, что «съел 400 ккал» в окне разойдётся с тем, что начислит движок.
///
/// Изображения 48×48 НЕ генерируются: если реальной картинки нет, показывается
/// цвет с буквой (прямое указание автора). Рисовать вместо этого суррогатную
/// иконку значило бы выдать её за настоящую.
/// </summary>
public sealed class ItemsForm : WebViewForm
{
    /// <summary>Последний собранный пакет: окно открывают позже обновления мира.</summary>
    private string? _lastPayloadJson;

    /// <summary>Предмет, который надо подсветить (ссылка из журнала).</summary>
    private string? _pendingHighlight;

    public ItemsForm()
        : base(
            "Предметы",
            "items.html",
            new Size(1060, 720),
            "items")
    {
        MinimumSize = new Size(560, 380);
        MaximizeBox = true;
        MinimizeBox = false;

        if (!WindowGeometryStore.HasSaved("items"))
            PlaceOnScreen();
    }

    /// <summary>
    /// Ставит окно на главный экран со сдвигом: предметы открывают поверх
    /// перков и монитора, и в одном центре окна закрывали бы друг друга.
    /// </summary>
    private void PlaceOnScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        Bounds = new Rectangle(
            area.Left + Math.Max(0, (area.Width - Width) / 2 - 30),
            area.Top + Math.Max(0, (area.Height - Height) / 2 - 20),
            Width,
            Height);
    }

    protected override void OnBrowserReady()
    {
        if (_lastPayloadJson is not null)
            PostJson(_lastPayloadJson);

        SendPendingHighlight();

        AppLogger.Info("ItemsForm: окно предметов готово.",
            $"hasPayload={_lastPayloadJson is not null}; size={Width}x{Height}");
    }

    public void SetPayloadJson(string payloadJson)
    {
        _lastPayloadJson = payloadJson;

        PostJson(payloadJson);
    }

    /// <summary>
    /// Просит подсветить предмет.
    ///
    /// Запоминается до готовности страницы: ссылка из журнала может прийти
    /// раньше, чем окно загрузилось, и сообщение ушло бы в пустоту.
    /// </summary>
    public void Highlight(string itemId)
    {
        _pendingHighlight = string.IsNullOrWhiteSpace(itemId) ? null : itemId;
        SendPendingHighlight();
    }

    private void SendPendingHighlight()
    {
        if (string.IsNullOrEmpty(_pendingHighlight))
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "highlight",
            itemId = _pendingHighlight
        }));

        _pendingHighlight = null;
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = UnwrapMessage(document.RootElement);

            if (!root.TryGetProperty("action", out var actionNode))
                return;

            switch (actionNode.GetString())
            {
                case "items_ready":
                    if (_lastPayloadJson is not null)
                        PostJson(_lastPayloadJson);
                    SendPendingHighlight();
                    break;

                // Правки количества — это правки ИНВЕНТАРЯ, и владеет им
                // Симулятор: окно предметов лишь просит изменить содержимое.
                case "set_item_quantity":
                    StateChangeRequested?.Invoke(this, json);
                    break;

                case "close_items":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ItemsForm: ошибка разбора web message.", ex);
        }
    }

    /// <summary>
    /// Разворачивает сообщение, присланное строкой.
    ///
    /// WebView2 отдаёт у строки строку, а не объект, а копия
    /// (<see cref="JsonElement.Clone"/>) нужна из-за времени жизни документа
    /// вложенной строки — см. <see cref="IndicatorsForm.OnWebMessage"/>.
    /// </summary>
    private static JsonElement UnwrapMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.String)
            return root;

        var text = root.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return root;

        using var inner = JsonDocument.Parse(text);
        return inner.RootElement.Clone();
    }

    /// <summary>Запрос на изменение содержимого инвентаря.</summary>
    public event EventHandler<string>? StateChangeRequested;

    /// <summary>Просьба закрыть окно (Escape внутри окна).</summary>
    public event EventHandler? CloseRequested;
}
