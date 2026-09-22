namespace AssistQuestEditor.Domain;

public sealed record QuestNodeResourceReference(
    string ResourceKind,
    string ResourceId,
    string ParameterKey);

/// <summary>
/// Central definition of resource references carried by Quest nodes.
/// Some target resource editors are planned, so the reference contract is
/// defined here before each corresponding UI editor exists.
/// </summary>
public static class QuestNodeReferenceCatalog
{
    public static IReadOnlyList<QuestNodeResourceReference> GetReferences(QuestNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var references = new List<QuestNodeResourceReference>();

        Add(references, node, "DialogueScene", "sceneId", "Scene");
        Add(references, node, "Interaction", "worldPointId", "WorldPoint");
        Add(references, node, "WaitForCondition", "conditionId", "Condition");
        Add(references, node, "Reward", "rewardId", "Reward");
        Add(references, node, "GiveItem", "itemId", "Item");
        Add(references, node, "RemoveItem", "itemId", "Item");

        return references;
    }

    private static void Add(
        ICollection<QuestNodeResourceReference> target,
        QuestNode node,
        string nodeType,
        string parameterKey,
        string resourceKind)
    {
        if (!node.NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase))
            return;

        if (!node.Parameters.TryGetValue(parameterKey, out var value) ||
            string.IsNullOrWhiteSpace(value))
            return;

        target.Add(new QuestNodeResourceReference(resourceKind, value.Trim(), parameterKey));
    }
}
