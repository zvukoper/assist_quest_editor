namespace AssistQuestEditor.App;

/// <summary>
/// Нажатие «общей» клавиши симулятора, поднятое дочерним окном.
///
/// Общие клавиши обрабатываются ДО содержимого окна, поэтому окно лишь сообщает о
/// нажатии, а что именно делать, решает Симулятор: он владеет дочерними окнами и
/// один знает состояние мира, от которого зависит смысл клавиши.
/// </summary>
public sealed class WebViewHotKeyEventArgs : EventArgs
{
    public WebViewHotKeyEventArgs(Keys key) => Key = key;

    public Keys Key { get; }
}

/// <summary>
/// Правило общих клавиш симулятора: какие клавиши общие и как о них сообщить.
///
/// Правило живёт в ОДНОМ месте, потому что клавиши обрабатывают и окна с WebView
/// (см. <see cref="WebViewForm"/>), и обычные дочерние окна
/// (<see cref="JournalForm"/>, <see cref="CampaignsForm"/>). Своя копия условия в
/// каждом окне означала бы, что новая общая клавиша появится только в тех окнах,
/// о которых вспомнили, — и «I» перестала бы работать именно там, где её ждут.
/// </summary>
public static class SimulatorHotKey
{
    /// <summary>
    /// Обрабатывает нажатие, если клавиша общая.
    ///
    /// Возвращает <c>true</c> и тогда, когда подписчика нет: клавиша уже
    /// разобрана окном и НЕ должна уходить дальше в содержимое окна. Иначе «I»
    /// открывала бы инвентарь и одновременно попадала на страницу.
    /// </summary>
    public static bool TryHandle(
        object sender,
        Keys keyData,
        EventHandler<WebViewHotKeyEventArgs>? handler)
    {
        var key = keyData & Keys.KeyCode;

        if (key != Keys.I && key != Keys.Escape)
        {
            return false;
        }

        handler?.Invoke(sender, new WebViewHotKeyEventArgs(key));
        return true;
    }
}
