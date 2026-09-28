using System.Text.Json;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно «Монитор показателей».
///
/// Отдельное ОКНО, а не панель сайдбара: список из семи шкал с полным разбором
/// формулы по каждой занимает много места, а сайдбар делит высоту с остальными
/// разделами. Окно можно унести на второй монитор и держать открытым рядом с
/// картой, наблюдая, как меняются значения и скорости.
///
/// Обновляется ТЕМ ЖЕ снимком, что и карта, и окно игрока: отдельная сборка
/// состояния означала бы второе описание мира, и правка одного поля в Симуляторе
/// молча ломала бы монитор. Никаких собственных данных форма не считает —
/// формулы разбирает страница, числа приходят из домена.
/// </summary>
public sealed class IndicatorsForm : WebViewForm
{
    /// <summary>
    /// Последний снимок от Симулятора. Хранится, потому что окно открывают
    /// ПОЗЖЕ первого снимка: без него монитор показывался бы пустым до
    /// следующего обновления мира, а его можно ждать сколько угодно.
    /// </summary>
    private string? _lastSnapshotJson;

    public IndicatorsForm()
        : base(
            "Монитор показателей",
            "indicators.html",
            new Size(940, 720),
            "indicators")
    {
        MinimumSize = new Size(520, 360);
        MaximizeBox = true;
        MinimizeBox = false;

        // Начальная позиция ставится только при отсутствии сохранённой геометрии:
        // иначе базовый конструктор восстановил бы место и размер, а следующий за
        // ним PlaceOnPrimaryScreen их перезаписал (ровно этот дефект был в окне
        // инвентаря).
        if (!WindowGeometryStore.HasSaved("indicators"))
            PlaceOnScreen();
    }

    /// <summary>
    /// Ставит окно на ГЛАВНЫЙ экран: монитор читают вместе с картой и следят за
    /// значениями, поэтому он не должен открываться «неизвестно где».
    /// </summary>
    private void PlaceOnScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        Bounds = new Rectangle(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2,
            Width,
            Height);
    }

    protected override void OnBrowserReady()
    {
        if (_lastSnapshotJson is not null)
            PostJson(_lastSnapshotJson);

        AppLogger.Info("IndicatorsForm: окно монитора показателей готово.",
            $"hasSnapshot={_lastSnapshotJson is not null}; size={Width}x{Height}");
    }

    /// <summary>Обновляет монитор снимком Симулятора.</summary>
    public void SetSnapshotJson(string snapshotJson)
    {
        _lastSnapshotJson = snapshotJson;

        if (Browser.CoreWebView2 is not null)
            PostJson(snapshotJson);
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
                case "indicators_ready":
                    if (_lastSnapshotJson is not null)
                        PostJson(_lastSnapshotJson);
                    break;

                // Окно закрывает Симулятор: страница форму Windows закрыть не
                // может, она лишь просит.
                case "close_indicators":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("IndicatorsForm: ошибка разбора web message.", ex);
        }
    }

    /// <summary>
    /// Разворачивает сообщение, присланное строкой.
    ///
    /// Страница шлёт <c>postMessage(payload)</c> объектом, но WebView2 при этом
    /// отдаёт JSON-ЗНАЧЕНИЕ: у строки это строка, а не объект. Копия
    /// (<see cref="JsonElement.Clone"/>) нужна, потому что документ вложенной
    /// строки освобождается сразу после выхода из метода.
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

    /// <summary>Просьба закрыть окно (Escape внутри окна).</summary>
    public event EventHandler? CloseRequested;
}
