namespace AssistQuestEditor.Domain;

public interface IQuestRuntimeController : IDisposable
{
    QuestRuntimeState State { get; }
    QuestGraph? ActiveGraph { get; }
    event EventHandler<QuestRuntimeEvent>? Published;

    void Start();
    void Stop(string reason = "Runtime остановлен");
    void Reset();
    void Tick();
}
