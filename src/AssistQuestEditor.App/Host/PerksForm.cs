using System.Text.Json;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно «Перки, баффы, скиллы».
///
/// Отдельное окно, а не панель сайдбара: четыре раздела с описанием каждого
/// пункта занимают много места, и держать их рядом с картой удобнее на втором
/// мониторе.
///
/// Форма НЕ считает содержимое сама. Перечень пунктов и описания знает каталог
/// домена, признак «действует сейчас» — канал условий; форма только собирает
/// присланное в один пакет и отдаёт странице. Иначе список задуманного
/// разъехался бы с движком при первом же добавленном эффекте.
/// </summary>
public sealed class PerksForm : WebViewForm
{
    /// <summary>
    /// Последний собранный пакет. Хранится по той же причине, что и снимок в
    /// мониторе: окно открывают ПОЗЖЕ последнего обновления мира, и без
    /// сохранённого пакета оно показывалось бы пустым до следующего события.
    /// </summary>
    private string? _lastPayloadJson;

    /// <summary>Пункт, который надо подсветить (ссылка из монитора или журнала).</summary>
    private string? _pendingHighlight;

    public PerksForm()
        : base(
            "Перки, баффы, скиллы",
            "perks.html",
            new Size(1000, 720),
            "perks")
    {
        MinimumSize = new Size(560, 380);
        MaximizeBox = true;
        MinimizeBox = false;

        // Начальная позиция ставится только при отсутствии сохранённой геометрии:
        // иначе базовый конструктор восстановил бы место и размер, а следующий за
        // ним PlaceOnScreen их перезаписал.
        if (!WindowGeometryStore.HasSaved("perks"))
            PlaceOnScreen();
    }

    /// <summary>
    /// Ставит окно на главный экран со сдвигом от центра: окно перков открывают
    /// ПОВЕРХ монитора показателей, ровно в том же центре они закрывали бы друг
    /// друга, и связь «кликнул в мониторе — увидел пункт» терялась бы.
    /// </summary>
    private void PlaceOnScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        Bounds = new Rectangle(
            area.Left + Math.Max(0, (area.Width - Width) / 2 - 60),
            area.Top + Math.Max(0, (area.Height - Height) / 2 - 40),
            Width,
            Height);
    }

    protected override void OnBrowserReady()
    {
        if (_lastPayloadJson is not null)
            PostJson(_lastPayloadJson);

        SendPendingHighlight();

        AppLogger.Info("PerksForm: окно перков готово.",
            $"hasPayload={_lastPayloadJson is not null}; size={Width}x{Height}");
    }

    /// <summary>Обновляет окно новым пакетом пунктов.</summary>
    public void SetPayloadJson(string payloadJson)
    {
        _lastPayloadJson = payloadJson;

        PostJson(payloadJson);
    }

    /// <summary>
    /// Просит подсветить пункт.
    ///
    /// Запоминается до готовности страницы: ссылка из монитора может прийти
    /// раньше, чем окно перков загрузилось, и отправленное сообщение ушло бы в
    /// пустоту — клик остался бы без ответа.
    /// </summary>
    public void Highlight(string perkId)
    {
        _pendingHighlight = string.IsNullOrWhiteSpace(perkId) ? null : perkId;
        SendPendingHighlight();
    }

    private void SendPendingHighlight()
    {
        if (string.IsNullOrEmpty(_pendingHighlight))
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "highlight",
            perkId = _pendingHighlight
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
                case "perks_ready":
                    if (_lastPayloadJson is not null)
                        PostJson(_lastPayloadJson);
                    SendPendingHighlight();
                    break;

                // Правки уходят в Симулятор: окно перков не владеет состоянием
                // мира, оно лишь просит его изменить.
                case "set_perk":
                case "set_effect":
                case "set_skill_level":
                    StateChangeRequested?.Invoke(this, json);
                    break;

                case "close_perks":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("PerksForm: ошибка разбора web message.", ex);
        }
    }

    /// <summary>
    /// Разворачивает сообщение, присланное строкой.
    ///
    /// Страница шлёт <c>postMessage(payload)</c> объектом, но WebView2 отдаёт
    /// JSON-ЗНАЧЕНИЕ: у строки это строка. Копия (<see cref="JsonElement.Clone"/>)
    /// нужна, потому что документ вложенной строки освобождается сразу после
    /// выхода из метода.
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

    /// <summary>Запрос на изменение мира (перк, эффект, очки умения).</summary>
    public event EventHandler<string>? StateChangeRequested;

    /// <summary>Просьба закрыть окно (Escape внутри окна).</summary>
    public event EventHandler? CloseRequested;
}
