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
    /// Последний ПОЛНЫЙ снимок от Симулятора. Хранится, потому что окно открывают
    /// ПОЗЖЕ первого снимка: без него монитор показывался бы пустым до
    /// следующего обновления мира, а его можно ждать сколько угодно.
    ///
    /// Сюда попадает ТОЛЬКО полный снимок. live_state запоминать нельзя: в нём нет
    /// мира, и если он окажется «снимком для повтора», страница получит его первым
    /// сообщением и не сможет инициализироваться — окно останется пустым.
    /// </summary>
    private string? _lastSnapshotJson;

    /// <summary>
    /// Шкала, которую надо подсветить (клик по имени показателя в журнале).
    ///
    /// Запоминается, потому что окно монитора может быть ЕЩЁ НЕ загружено: в
    /// этот момент отправленное сообщение уйдёт в пустоту, и клик в журнале
    /// остался бы без ответа. Настоящие данные страница запрашивает сама
    /// (<c>indicators_ready</c>), поэтому выделение надо подождать до того же
    /// момента.
    /// </summary>
    private string? _pendingHighlight;

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

        SendPendingHighlight();

        AppLogger.Info("IndicatorsForm: окно монитора показателей готово.",
            $"hasSnapshot={_lastSnapshotJson is not null}; size={Width}x{Height}");
    }

    /// <summary>
    /// Просит подсветить блок указанной шкалы.
    ///
    /// Ключ — тот же, что в разметке отчёта (<c>[[metric:energy]]</c>) и в ключах
    /// шкал монитора: третьего способа назвать шкалу заводить нельзя, иначе
    /// ссылка из журнала вёлa бы к блоку с другим именем.
    /// </summary>
    public void Highlight(string key)
    {
        _pendingHighlight = string.IsNullOrWhiteSpace(key) ? null : key;
        SendPendingHighlight();
    }

    private void SendPendingHighlight()
    {
        if (string.IsNullOrEmpty(_pendingHighlight))
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "highlight",
            key = _pendingHighlight
        }));

        _pendingHighlight = null;
    }

    /// <summary>Обновляет монитор полным снимком Симулятора и запоминает его.</summary>
    public void SetSnapshotJson(string snapshotJson)
    {
        _lastSnapshotJson = snapshotJson;

        PostJson(snapshotJson);
    }

    /// <summary>
    /// Обновляет монитор живым состоянием (игрок, шкалы, условия, скорости).
    ///
    /// Отдельный метод, а не <see cref="SetSnapshotJson"/>: живое состояние
    /// приходит четыре раза в секунду и им нельзя подменять снимок для повтора —
    /// именно из-за этого окно монитора открывалось пустым.
    /// </summary>
    public void PushLiveStateJson(string json) => PostJson(json);

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
                    SendPendingHighlight();
                    break;

                // Окно закрывает Симулятор: страница форму Windows закрыть не
                // может, она лишь просит.
                case "close_indicators":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;

                // Клик по имени эффекта или предмета внутри монитора.
                //
                // Монитор обязан передать просьбу НАРУЖУ: окна перков и предметов
                // — отдельные формы Windows, и открыть их из JavaScript
                // невозможно. Молчание здесь — ровно тот дефект, который видел
                // автор: чип «Вода» и «Отдохнувший» выглядели кнопками, но клик
                // ничего не делал, потому что сообщение приходило в этот switch и
                // не находило ветки.
                case "open_perks":
                    NavigationRequested?.Invoke(this, new IndicatorNavigationRequestEventArgs(
                        "perks",
                        StringOrNull(root, "perkId"),
                        StringOrNull(root, "perkKind")));
                    break;

                case "open_items":
                    NavigationRequested?.Invoke(this, new IndicatorNavigationRequestEventArgs(
                        "items",
                        StringOrNull(root, "itemId"),
                        null));
                    break;

                // ПКМ по порции в блоке «Желудок»: убрать её из желудка.
                //
                // Просьба идёт НАРУЖУ по той же причине, что и открытие окон:
                // состояние мира принадлежит Симулятору, и страница не может
                // изменить желудок сама. Без этой ветки удаление выглядело бы
                // кнопкой, которая молча ничего не делает.
                case "remove_stomach_portion":
                    StomachActionRequested?.Invoke(this, new StomachActionEventArgs(
                        "remove",
                        StringOrNull(root, "itemId"),
                        null));
                    break;

                // Клик по предмету в контекстном меню пустого места желудка:
                // употребить предмет, как если бы его выбрали в инвентаре.
                case "use_stomach_item":
                    StomachActionRequested?.Invoke(this, new StomachActionEventArgs(
                        "use",
                        StringOrNull(root, "itemId"),
                        null));
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("IndicatorsForm: ошибка разбора web message.", ex);
        }
    }

    private static string? StringOrNull(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var node))
            return null;

        return node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
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

    /// <summary>
    /// Просьба открыть другое окно (перки или предметы) с подсветкой пункта.
    ///
    /// Событие, а не прямой вызов: окно монитора не знает про Симулятор, и
    /// наоборот. Так же устроены остальные окна проекта — страница лишь называет
    /// намерение, а решение принимает владелец окон.
    /// </summary>
    public event EventHandler<IndicatorNavigationRequestEventArgs>? NavigationRequested;

    /// <summary>
    /// Просьба изменить ЖЕЛУДОК: убрать порцию или употребить предмет.
    ///
    /// Отдельное событие от <see cref="NavigationRequested"/>: навигация лишь
    /// показывает другое окно, а здесь меняется СОСТОЯНИЕ МИРА, и путать эти два
    /// намерения значило бы дать странице власть над данными через событие,
    /// которое для этого не предназначено.
    /// </summary>
    public event EventHandler<StomachActionEventArgs>? StomachActionRequested;
}

/// <summary>
/// Намерение монитора изменить желудок.
///
/// <see cref="Kind"/> — «remove» (убрать порцию) или «use» (употребить предмет)
/// : два разных действия над одним и тем же Id, и без вида они были бы
/// неразличимы принимающей стороной.
/// </summary>
public sealed class StomachActionEventArgs : EventArgs
{
    public StomachActionEventArgs(string kind, string? itemId, string? portionId)
    {
        Kind = kind;
        ItemId = itemId;
        PortionId = portionId;
    }

    /// <summary>«remove» или «use».</summary>
    public string Kind { get; }

    /// <summary>Id предмета, к которому относится действие.</summary>
    public string? ItemId { get; }

    /// <summary>Уточнение порции, если порций с одним Id несколько.</summary>
    public string? PortionId { get; }
}

/// <summary>
/// Намерение монитора открыть другое окно.
///
/// «Куда» и «что подсветить» едут одним аргументом: два отдельных события
/// разошлись бы в момент добавления третьего окна, а вид ссылки (buff/debuff)
/// нужен, чтобы принимающая сторона могла сверить раздел с каталогом.
/// </summary>
public sealed class IndicatorNavigationRequestEventArgs : EventArgs
{
    public IndicatorNavigationRequestEventArgs(string target, string? id, string? kind)
    {
        Target = target;
        Id = id;
        Kind = kind;
    }

    /// <summary>«perks» или «items».</summary>
    public string Target { get; }

    /// <summary>Id пункта для подсветки; может быть пустым.</summary>
    public string? Id { get; }

    /// <summary>Вид пункта («buff»/«debuff») — только для ссылок в перки.</summary>
    public string? Kind { get; }
}
