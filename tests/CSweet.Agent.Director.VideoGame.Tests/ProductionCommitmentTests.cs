using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProductionCommitmentTests
{
    [Fact]
    public async Task ManagerRecordsAnEvidenceRequestWhenAcceptedPlanningEvidenceIsMissing()
    {
        var workstream = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var decision = new DecisionRecord(Guid.NewGuid(), workstream,
            "video-game.management-direction.v1", "Choose browser targets", "work-planning",
            [new DecisionOption("request-more-evidence", "More evidence", null)],
            "request-more-evidence", null, DecisionStatuses.Pending, null, [], null, null,
            null, 1, now, now);
        DecideDecisionRequest? submitted = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([decision]))
            .RegisterCapability<DecideDecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionDecide,
                (request, _) => { submitted = request; return Task.FromResult(decision); });
        await new VideoGameDirectorAgent().DecideProducerPlanningAsync(decision,
            new GameDirectorOperatingState { WorkstreamId = workstream }, runtime.CreateContext(), default);
        Assert.NotNull(submitted);
        Assert.Equal(decision.Id, submitted.DecisionId);
        Assert.Equal("request-more-evidence", submitted.SelectedOptionId);
    }

    [Fact]
    public void PlanningDirectionMustChooseAnAuthorizedOptionWithRationale()
    {
        DecisionOption[] options = [new("continue-current-plan", "Continue", null),
            new("request-more-evidence", "More evidence", null)];
        var direction = VideoGameDirectorAgent.ParsePlanningDirection(
            """{"optionId":"continue-current-plan","rationale":"The accepted brief already sets this scope.","requiresOwner":false}""", options);
        Assert.Equal("continue-current-plan", direction.OptionId);
        Assert.Throws<InvalidOperationException>(() => VideoGameDirectorAgent.ParsePlanningDirection(
            """{"optionId":"invented","rationale":"Go ahead.","requiresOwner":false}""", options));
        Assert.Throws<InvalidOperationException>(() => VideoGameDirectorAgent.ParsePlanningDirection(
            """{"optionId":"continue-current-plan","rationale":" ","requiresOwner":false}""", options));
    }

    [Fact]
    public void ProductionQuestionsAreHumanReadableAndDistinguishAssetsFromBuildCapacity()
    {
        var assets = VideoGameDirectorAgent.BuildAssetStrategyQuestion();
        var build = VideoGameDirectorAgent.BuildUnavailableToolchainQuestion(
            CrosswiredStudios.VideoGame.Contracts.VideoGameToolchainRecipeKeys.PhaserWeb2D);

        Assert.Contains("visual and audio assets", assets);
        Assert.DoesNotContain("{", assets);
        Assert.Contains("Phaser 2D web", build);
        Assert.Contains("Node/TypeScript build runner", build);
        Assert.Contains("not a request to change engines", build);
    }

    [Fact]
    public async Task ToolchainCapacityDoesNotEscalateBeforeTechnicalLeadershipExists()
    {
        var state = State() with
        {
            ManagerPreferences = new ManagerPreferenceProfile { EnginePreferences = ["latest Phaser"] }
        };
        var result = await new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(
            state, 1, Guid.NewGuid(), new AgentTestRuntime().CreateContext(), default);

        Assert.False(result.Ready);
        Assert.Null(result.State.ToolchainBlockerDecisionId);
    }

    [Fact]
    public async Task MissingCapacityQuestionUsesTheManagerSelectedPhaserRecipe()
    {
        DecisionRequest? decision = null;
        var state = State() with
        {
            ManagerPreferences = new ManagerPreferenceProfile { EnginePreferences = ["latest Phaser"] },
            SpecialistEmployeeIds = new Dictionary<string, Guid>
            {
                [CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.TechnicalDirector] = Guid.NewGuid()
            }
        };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(
                PlatformCapabilities.ToolchainCatalogRead, (request, _) =>
                {
                    Assert.Equal(CrosswiredStudios.VideoGame.Contracts.VideoGameToolchainRecipeKeys.PhaserWeb2D,
                        request.RecipeKey);
                    return Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]);
                })
            .RegisterCapability<DecisionRequest, DecisionRecord>(DecisionCapabilityNames.RequestV1,
                (request, _) =>
                {
                    decision = request;
                    return Task.FromException<DecisionRecord>(new InvalidOperationException("captured"));
                });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(
                state, 1, Guid.NewGuid(), runtime.CreateContext(), default));

        Assert.NotNull(decision);
        Assert.Contains("Phaser 2D web", decision.Summary);
        Assert.DoesNotContain("babylon", decision.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["restore-capacity", "pause"], decision.Options.Select(x => x.Id));
    }

    [Fact]
    public async Task PlanningQueuesIndependentStableTasksWithoutExecutingDecisionsOrRequeueingBlockers()
    {
        var state = State();
        var tasks = new Dictionary<string, PersonalTodoItem>();
        var runtime = new AgentTestRuntime().RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(
            PersonalTodoCapabilities.Add, (r, _) => {
                if (!tasks.TryGetValue(r.IdempotencyKey, out var task))
                    tasks[r.IdempotencyKey] = task = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Director",
                        r.Title, r.Description!, "Blocked", r.Priority, 1, 1, null, r.SourceConversationId, r.SourceMessageId,
                        [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = r.CorrelationId, WorkContext = r.WorkContext };
                return Task.FromResult(task);
            });
        var context = runtime.CreateContext();
        await VideoGameDirectorAgent.QueueProductionCommitmentsAsync(state, context, default);
        await VideoGameDirectorAgent.QueueProductionCommitmentsAsync(state, context, default);
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks.Values, x => x.Title.Contains("asset"));
        Assert.Contains(tasks.Values, x => x.Title.Contains("toolchain"));
        Assert.All(tasks.Values, x => { Assert.Equal("Blocked", x.Status); Assert.Equal(state.WorkstreamId, x.WorkContext!.WorkstreamId); Assert.Equal("Medium", x.Priority); });
    }

    [Fact]
    public async Task ProposedAuthorityCoversTheActualAssetDecisionAndToolchainSelection()
    {
        WorkstreamPlanProposalV2Request? plan = null;
        DecisionRequest? decision = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                    new Dictionary<string, string>(), [], "identity", [], Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(new InferredGameProject("Gridlock", "A local web arena.")),
                    1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
            .RegisterCapability<StartAgentCoordinationRequest, JsonElement>(CommunicationCapabilities.CoordinationStart, (r, _) => {
                plan = r.Artifact!.Payload.Deserialize<WorkstreamPlanProposalV2Request>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromException<JsonElement>(new InvalidOperationException("captured")); })
            .RegisterCapability<DecisionRequest, DecisionRecord>(DecisionCapabilityNames.RequestV1, (r, _) => {
                decision = r; return Task.FromException<DecisionRecord>(new InvalidOperationException("captured")); });
        var context = runtime.CreateContext(identity: new AgentIdentity(Guid.NewGuid().ToString(), "Director", null, "Video Game Director", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        var agent = new VideoGameDirectorAgent(); var state = State() with { CreativeDirectorEmployeeId = Guid.NewGuid() };
        await Assert.ThrowsAnyAsync<Exception>(() => agent.EnsureProjectFoundationAsync(state with { WorkstreamId = null }, 1,
            state.TeamId, Guid.NewGuid(), Guid.NewGuid(), context, default));
        await Assert.ThrowsAnyAsync<Exception>(() => agent.EnsureAssetStrategyAsync(state, 1, Guid.NewGuid(), context, default));
        Assert.NotNull(plan); Assert.NotNull(decision);
        Assert.Equal(8, plan.ProfileVersion);
        Assert.Contains(decision.AuthorityRuleKey, plan.AuthorityEnvelope.AgentAuthorizedActionKeys);
        Assert.Contains(VideoGameDirectorAgent.CertifiedToolchainAuthority, plan.AuthorityEnvelope.AgentAuthorizedActionKeys);
        Assert.Contains("launch", plan.AuthorityEnvelope.HumanRequiredActionKeys);
        Assert.DoesNotContain("launch", plan.AuthorityEnvelope.AgentAuthorizedActionKeys);
    }

    [Fact]
    public async Task AuthorizedResolvedDecisionIsConsumedWithoutASecondApproval()
    {
        var decision = new DecisionRecord(Guid.NewGuid(), Guid.NewGuid(), "video-game.asset-strategy.v1", "Asset strategy",
            VideoGameDirectorAgent.ProductionStrategyAuthority, [], "procedural", "procedural", "Decided", "Approved",
            [], null, null, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var context = new AgentTestRuntime().CreateContext();
        Assert.Equal(decision, await VideoGameDirectorAgent.DecideProductionChoiceAsync(decision,
            "procedural", "Already approved", "replay", context, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => VideoGameDirectorAgent.DecideProductionChoiceAsync(
            decision, "hybrid", "Different choice", "replay-other", context, default));
    }

    private static GameDirectorOperatingState State() => new() {
        WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid(), WorkingTitle = "Gridlock",
        AcceptedVision = new(1, "digest", "# Gridlock\nA local web arena.", Guid.NewGuid(), Guid.NewGuid(), "hash",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
    };
}
