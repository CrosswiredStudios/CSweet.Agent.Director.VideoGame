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
            state, 1, Guid.NewGuid(), RosterRuntime(state, []).CreateContext(), default);

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
        var runtime = RosterRuntime(state, [Technical(state.SpecialistEmployeeIds.Values.Single())])
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

        StartAgentCoordinationRequest? handoff = null;
        runtime.RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([]));
        runtime.RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart,
            (input, _) => { handoff = input; throw new InvalidOperationException("captured resource handoff"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(
            state, 1, Guid.NewGuid(), runtime.CreateContext(), default));
        Assert.Null(decision);
        Assert.NotNull(handoff);
        Assert.Equal("platform.resource.requirement.v1", handoff.Artifact!.Type);
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

    [Theory]
    [InlineData("new-hire", true)]
    [InlineData("replacement", true)]
    [InlineData("removed", false)]
    [InlineData("wrong-role", false)]
    [InlineData("unavailable", false)]
    [InlineData("wrong-team", false)]
    public async Task ToolchainCheckDiscoversPostPackageTechnicalHireFromCurrentRoster(string condition, bool expected)
    {
        var oldLead = Guid.NewGuid(); var newLead = Guid.NewGuid();
        var state = State() with { DetailedDesignPackageId = Guid.NewGuid(),
            ToolchainBlockerDecisionId = Guid.NewGuid(),
            SpecialistEmployeeIds = condition == "new-hire" ? new Dictionary<string, Guid>()
                : new Dictionary<string, Guid> { ["game-technical-director"] = oldLead } };
        var candidate = Technical(newLead) with {
            IsAvailable = condition != "unavailable",
            DeclaredRoleKeys = condition == "wrong-role" ? ["game-engineer"] : ["game-technical-director"] };
        var runtime = RosterRuntime(state, condition == "removed" ? [] : [candidate], condition == "wrong-team");
        var catalogs = 0;
        runtime.RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(
            PlatformCapabilities.ToolchainCatalogRead, (_, _) => { catalogs++; return Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]); });
        runtime.RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([JsonSerializer.Deserialize<ResourceRequestState>("{}")! with {
                ResourceType = ResourceCapabilities.BuildToolchain, ProfileKey = "phaser.web-2d.v1", Status = "Certifying" }]));
        var result = await new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), runtime.CreateContext(), default);
        Assert.False(result.Ready);
        Assert.Equal(expected ? 1 : 0, catalogs);
        if (expected) Assert.Equal(newLead, result.State.SpecialistEmployeeIds["game-technical-director"]);
    }

    [Theory]
    [InlineData("Ready", true)] [InlineData("Approved", true)] [InlineData("Failed", true)] [InlineData("Revoked", true)]
    public async Task Resource_wake_rechecks_current_readiness_and_discovers_only_the_exact_projects_continuation(string status, bool shouldWake)
    {
        var state = State(); var requestId = Guid.NewGuid(); var discoveries = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (r, _) => {
                Assert.Equal(requestId, r.RequestId);
                return Task.FromResult<IReadOnlyList<ResourceRequestState>>([JsonSerializer.Deserialize<ResourceRequestState>("{}")! with {
                    Id = requestId, WorkstreamId = state.WorkstreamId!.Value, Status = status, ResourceType = ResourceCapabilities.BuildToolchain }]);
            })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => {
                Assert.Equal(VideoGameDirectorAgent.ProjectStateKey(state.WorkstreamId, null), r.StateKey);
                return Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                    new Dictionary<string,string>(), [], "test", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
            })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => {
                discoveries++; return Task.FromResult(new PersonalTodoDirectory([], Guid.NewGuid()));
            });
        // A stale event can claim Ready; only the authorized current-state response decides.
        await new VideoGameDirectorAgent().ReconcileResourceReadinessAsync(JsonSerializer.SerializeToElement(new { requestId, status = "Ready" }), runtime.CreateContext(), default);
        Assert.Equal(shouldWake ? 1 : 0, discoveries);
    }

    [Fact]
    public async Task Failed_resource_handoff_is_resumed_once_with_its_retained_requirement()
    {
        var state = State(); var resumes = 0;
        var session = JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with { Id = Guid.NewGuid(), Revision = 4, Status = "Failed",
            FinalSummary = "agent-failure:v1;code=validation_failed;capability=platform.resource.request.v1" };
        var runtime = RosterRuntime(state, [Technical(Guid.NewGuid())])
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead, (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]))
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([]))
            .RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart, (r, _) => {
                Assert.Equal("platform.resource.requirement.v1", r.Artifact!.Type); return Task.FromResult(session);
            })
            .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (r, _) => {
                resumes++; Assert.Equal(session.Id, r.SessionId); Assert.Equal(4, r.ExpectedRevision);
                session = session with { Status = "Active", Revision = 5 }; return Task.FromResult(session);
            });
        var director = new VideoGameDirectorAgent();
        await director.EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), runtime.CreateContext(), default);
        await director.EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), runtime.CreateContext(), default);
        Assert.Equal(1, resumes);
    }

    [Theory]
    [InlineData("WaitingDependency")] [InlineData("NeedsAttention")] [InlineData("Failed")]
    public async Task Resource_failure_reports_the_actual_dependency_instead_of_generic_waiting(string status)
    {
        var state = State();
        var runtime = RosterRuntime(state, [Technical(Guid.NewGuid())])
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead, (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]))
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([
                JsonSerializer.Deserialize<ResourceRequestState>("{}")! with { Id = Guid.NewGuid(), Status = status, ResourceType = ResourceCapabilities.BuildToolchain,
                    ProfileKey = "phaser.web-2d.v1", Condition = "Configure catalog read access for the private provider repository." }]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), runtime.CreateContext(), default));
        Assert.Contains("catalog read access", error.Message); Assert.Contains(status, error.Message);
    }

    [Theory]
    [InlineData("Blocked", "The requirement needs correction.")]
    [InlineData("Failed", "agent-failure:v1;code=denied;capability=platform.resource.request.v1")]
    [InlineData("Failed", "agent-failure:v1;code=validation_failed;capability=platform.other.v1")]
    public async Task Unrelated_or_unauthorized_handoff_failures_do_not_receive_automatic_retries(string status, string summary)
    {
        var state = State();
        var session = JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with {
            Id = Guid.NewGuid(), Revision = 4, Status = status, FinalSummary = summary };
        var runtime = RosterRuntime(state, [Technical(Guid.NewGuid())])
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead, (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]))
            .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([]))
            .RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart, (_, _) => Task.FromResult(session));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new VideoGameDirectorAgent().EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), runtime.CreateContext(), default));
        Assert.Contains(summary, error.Message);
    }

    private static AgentTeammate Technical(Guid employee) => new(employee.ToString("D"), "Technical Director", "Agent",
        null, null, "Teammate", "Active") { AgentInstallationId = Guid.NewGuid(), RuntimeEligibility = "Eligible",
        DeclaredRoleKeys = ["game-technical-director"], EffectiveCapabilities = ["work.execution.run"] };

    private static AgentTestRuntime RosterRuntime(GameDirectorOperatingState state, IReadOnlyList<AgentTeammate> members, bool wrongTeam = false) =>
        new AgentTestRuntime().RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
            (request, _) => { Assert.Equal(state.TeamId, request.TeamId); return Task.FromResult(new TeamRosterV2Response(
                new AgentTeamContext((wrongTeam ? Guid.NewGuid() : state.TeamId!.Value).ToString("D"), "game", "Game", 2,
                    "producer", "Producer", members, [], members.Count, false), state.WorkstreamId)); })
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]));

    private static GameDirectorOperatingState State() => new() {
        WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid(), WorkingTitle = "Gridlock",
        AcceptedVision = new(1, "digest", "# Gridlock\nA local web arena.", Guid.NewGuid(), Guid.NewGuid(), "hash",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
    };
}
