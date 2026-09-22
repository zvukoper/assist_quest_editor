using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Репутационные требования игрового контента: покупка колбасы у Гоши и
/// квест-доставка. Это правила ТЗ, поэтому они проверяются как поведение,
/// а не как оформление.
/// </summary>
public sealed class ReputationRuntimeTests
{
    private const string ShopScene = "gosha_shop";
    private const string Sausage = "gosha.homemade_sausage";

    [Fact]
    public void SausageItemExistsInCatalog()
    {
        var item = ItemCatalogFactory.CreateStarter().SingleOrDefault(x => x.Id == Sausage);

        Assert.NotNull(item);
        Assert.Equal("Домашняя колбаса", item!.Name);
    }

    [Fact]
    public void ShopPurchaseOptionIsHiddenBelowThreeHundredFifty()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(new SceneCatalog(new[] { BuildShopScene() }), hub);

        SetReputation(hub, "gosha", 349);
        Assert.True(runtime.Start(ShopScene));
        ContinueDialogue(hub, runtime, expectChoice: true);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);

        // «Возможности просто не видно»: вариант покупки отсутствует в списке,
        // а не показывается неактивным.
        Assert.DoesNotContain(dialog!.Options, option => option.Id == "gosha.shop.buy");
        Assert.Contains(dialog.Options, option => option.Id == "gosha.shop.leave");
        Assert.Single(dialog.Options);
    }

    [Fact]
    public void ShopPurchaseOptionAppearsAtThreeHundredFifty()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(new SceneCatalog(new[] { BuildShopScene() }), hub);

        SetReputation(hub, "gosha", 350);
        Assert.True(runtime.Start(ShopScene));
        ContinueDialogue(hub, runtime, expectChoice: true);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);
        Assert.Equal(new[] { "gosha.shop.buy", "gosha.shop.leave" }, dialog!.Options.Select(o => o.Id));
    }

    [Fact]
    public void IndexCountsVisibleOptionsOnlyAndHiddenOptionCannotBeSelected()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(new SceneCatalog(new[] { BuildShopScene() }), hub);
        SetReputation(hub, "gosha", 100);

        Assert.True(runtime.Start(ShopScene));
        ContinueDialogue(hub, runtime, expectChoice: true);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog!;

        // Индекс 1 при скрытом варианте покупки — это «Уйти», а не покупка:
        // индекс считается по показанному списку.
        hub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = dialog.RequestId,
                ["index"] = "1"
            }));

        Assert.Equal(SceneRuntimeStatus.Completed, runtime.State.Status);
        Assert.Equal("gosha.shop.leave", runtime.State.LastChoiceId);
    }

    [Fact]
    public void ChoiceWithoutAnyVisibleOptionStaysPassable()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(new SceneCatalog(new[] { BuildShopScene() }), hub);
        SetReputation(hub, "gosha", 0);

        Assert.True(runtime.Start(ShopScene));
        ContinueDialogue(hub, runtime, expectChoice: true);

        // «Уйти» доступно всегда, поэтому сцена не может стать непроходимой.
        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);
        Assert.NotEmpty(dialog!.Options);
    }

    [Fact]
    public void DeliverySceneRefusesNothingButResolvesCanonicalChoice()
    {
        var scene = LoadScene("ruslan_delivery");
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(new SceneCatalog(new[] { scene }), hub);

        Assert.True(runtime.Start("ruslan_delivery"));
        ContinueDialogue(hub, runtime, expectChoice: true);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);
        Assert.Equal(new[] { "ruslan.delivery.accept", "ruslan.delivery.decline" }, dialog!.Options.Select(o => o.Id));
    }

    [Fact]
    public void NpcContactIsRecordedForSpeakerWithoutChangingReputation()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var runtime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);

        Assert.True(runtime.Start("ruslan_start"));

        var reputation = hub.Get<ReputationState>("reputation").Value;
        Assert.True(reputation.IsContacted("ruslan"));
        Assert.Equal(0, reputation.ValueOf("ruslan"));

        // Гоша ещё не появлялся: в списке репутации его быть не должно.
        Assert.False(reputation.IsContacted("gosha"));
    }

    private static void SetReputation(IDataChannelHub hub, string npcId, int value)
    {
        var channel = hub.Get<ReputationState>("reputation");
        channel.Set(channel.Value.WithValue(npcId, value), "Тест репутации");
    }

    private static void ContinueDialogue(IDataChannelHub hub, SceneRuntime runtime, bool expectChoice)
    {
        var dialogue = hub.Get<InterfaceState>("interfaces").Value.ActiveDialogue;
        Assert.NotNull(dialogue);
        Assert.True(dialogue!.RequestId.Length > 0);

        hub.Events.Publish(new SimulatorEvent(
            "DialogueContinue",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = dialogue.RequestId
            }));

        Assert.Equal(expectChoice ? "Choice" : "Dialogue", runtime.State.WaitingFor);
    }

    private static SceneDefinition LoadScene(string sceneId)
    {
        var path = Path.Combine(RepositoryRoot(), "data", "scenes", sceneId + ".aqscene");
        var document = ResourceJsonFormat.Deserialize<SceneDefinitionDocument>(File.ReadAllText(path));
        Assert.NotNull(document);
        return document!.Definition;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Маркер — файл, который обязан существовать в репозитории
        // (validate_memory.mjs тоже его требует), а не имя решения: .sln здесь нет.
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "MemoryAI", "INSTRUCTIONS.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// Сцена магазина, собранная кодом: тест не должен зависеть от того, что
    /// кто-то поправит JSON сцены и незаметно снимет требование репутации.
    /// </summary>
    private static SceneDefinition BuildShopScene() =>
        new(
            ShopScene,
            "Домашняя колбаса Гоши",
            "Тестовая сцена покупки.",
            new SceneGraph(
                ShopScene,
                "Домашняя колбаса Гоши",
                new[]
                {
                    new SceneNode("start", "SceneStart", "Начало", 80, 180,
                        new[] { new SocketDefinition("start.out", "Далее", SocketDirection.Output) }),
                    new SceneNode("dialogue", "Dialogue", "Гоша", 340, 180,
                        new[]
                        {
                            new SocketDefinition("dialogue.in", "Вход", SocketDirection.Input),
                            new SocketDefinition("dialogue.out", "Далее", SocketDirection.Output)
                        })
                    {
                        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["dialogueId"] = "gosha.shop.greeting"
                        }
                    },
                    new SceneNode("choice", "Choice", "Покупка", 640, 180,
                        new[]
                        {
                            new SocketDefinition("choice.in", "Вход", SocketDirection.Input),
                            new SocketDefinition("choice.buy", "Купить", SocketDirection.Output),
                            new SocketDefinition("choice.leave", "Уйти", SocketDirection.Output)
                        })
                    {
                        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["choiceId"] = "gosha.shop"
                        }
                    },
                    new SceneNode("buy", "SceneEnd", "Куплено", 960, 120,
                        new[] { new SocketDefinition("buy.in", "Вход", SocketDirection.Input) }),
                    new SceneNode("leave", "SceneEnd", "Ушли", 960, 250,
                        new[] { new SocketDefinition("leave.in", "Вход", SocketDirection.Input) })
                },
                new[]
                {
                    new SceneConnection("start", "start.out", "dialogue", "dialogue.in"),
                    new SceneConnection("dialogue", "dialogue.out", "choice", "choice.in"),
                    new SceneConnection("choice", "choice.buy", "buy", "buy.in"),
                    new SceneConnection("choice", "choice.leave", "leave", "leave.in")
                }),
            new[] { new SceneDialogue("gosha.shop.greeting", "Гоша", "Берёшь колбасу?") },
            new[]
            {
                new SceneChoice(
                    "gosha.shop",
                    "Домашняя колбаса",
                    "Гоша",
                    "Берёшь колбасу?",
                    new[]
                    {
                        new SceneChoiceOption("gosha.shop.buy", "Беру.", "choice.buy")
                        {
                            Requirement = new ReputationRequirement("gosha", 350)
                        },
                        new SceneChoiceOption("gosha.shop.leave", "Пока нет.", "choice.leave")
                    })
            });

    /// <summary>Каждая сцена-контент должна проходить доменный валидатор.</summary>
    [Theory]
    [InlineData("gosha_shop")]
    [InlineData("ruslan_delivery")]
    [InlineData("gosha_delivery")]
    [InlineData("gosha_meat")]
    [InlineData("ruslan_start")]
    [InlineData("ruslan_finish")]
    public void ContentScenesPassCanonicalValidation(string sceneId)
    {
        var diagnostics = SceneGraphValidator.Validate(LoadScene(sceneId));

        Assert.DoesNotContain(diagnostics, d => d.Severity == GraphDiagnosticSeverity.Error);
    }
}
