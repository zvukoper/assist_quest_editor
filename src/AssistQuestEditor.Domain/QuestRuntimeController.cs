namespace AssistQuestEditor.Domain;

public interface IQuestRuntimeController : IDisposable
{
    QuestRuntimeState State { get; }
    QuestGraph? ActiveGraph { get; }

    /// <summary>
    /// Идёт ли симуляция прямо сейчас. Пауза — это тоже <c>false</c>: с точки
    /// зрения исполнения мир не меняется. Отличие паузы от полной остановки
    /// хранится отдельно (<see cref="IsPaused"/>), потому что кнопка
    /// «продолжить» и автосохранение обязаны различать эти два состояния.
    /// </summary>
    bool SimulationRunning { get; }

    /// <summary>
    /// Симуляция остановлена пользователем, но не начата заново: мир сохраняет
    /// последнее состояние, и его можно продолжить кнопкой play.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Кратность игрового времени. Влияет ТОЛЬКО на скорость часов мира, не на
    /// частоту тиков и не на срабатывание квестов.
    /// </summary>
    double SimulationSpeed { get; }

    IReadOnlyCollection<string> EnabledQuestIds { get; }

    event EventHandler<QuestRuntimeEvent>? Published;

    void Start();

    /// <summary>
    /// Запускает конкретный квест по его id. Нужен Dispatcher для событий,
    /// которые при обнаружении материализуют обычный Quest Runtime.
    /// </summary>
    bool StartQuest(string questId);

    void Stop(string reason = "Runtime остановлен");
    void Reset();
    void Tick();
    void SetSimulationRunning(bool running);

    /// <summary>Ставит симуляцию на паузу, не сбрасывая состояние мира.</summary>
    void PauseSimulation();

    /// <summary>Продолжает приостановленную симуляцию.</summary>
    void ResumeSimulation();

    /// <summary>
    /// Задаёт кратность игрового времени. Значение вне допустимого диапазона
    /// игнорируется: скорость — пользовательский параметр, и «случайный ноль»
    /// здесь означал бы остановку часов при формально идущей симуляции.
    /// </summary>
    void SetSimulationSpeed(double speed);

    void SetQuestEnabled(string questId, bool enabled);
}
