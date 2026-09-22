namespace AssistQuestEditor.Domain;

public interface IQuestRuntimeController : IDisposable
{
    QuestRuntimeState State { get; }
    QuestGraph? ActiveGraph { get; }
    bool SimulationRunning { get; }
    IReadOnlyCollection<string> EnabledQuestIds { get; }
    event EventHandler<QuestRuntimeEvent>? Published;

    void Start();
    void Stop(string reason = "Runtime остановлен");
    void Reset();
    void Tick();
    void SetSimulationRunning(bool running);
    void SetQuestEnabled(string questId, bool enabled);
}
