using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProductionCommitmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_retry_returns_the_legacy_request_and_exact_pitch_to_the_specialist(bool summarizing)
    {
        var director = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Marcus Vale", "Director");
        var technical = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Reviewer");
        var id = Guid.NewGuid();
        var hash = new string('b', 64);
        var state = State();
        state = state with { AcceptedVision = state.AcceptedVision! with { Digest = hash, ArtifactRevisionHash = hash },
            ToolchainFeasibilitySessionId = id, SpecialistEmployeeIds = new Dictionary<string, Guid> { ["game-technical-director"] = technical.OrganizationUserId } };
        var original = new AgentCoordinationArtifact(VideoGameDirectorAgent.ToolchainFeasibilityRequestType,
            "1.0", hash, 1, true, JsonSerializer.SerializeToElement(new { acceptedVisionDigest = hash, requiredRecipeKey = "phaser.web-2d.v1" }), "digest");
        var now = DateTimeOffset.UtcNow;
        var request = new AgentCoordinationTurnRequest(id, 3, 3, "Toolchain feasibility approval", "Assess build setup", [],
            director, technical, summarizing,
            [new(Guid.NewGuid(), 0, director.OrganizationUserId, "Continue", "Assess build setup.", now, original),
             new(Guid.NewGuid(), 1, technical.OrganizationUserId, "Blocked", "A current, typed production planning cycle is required.", now)])
            { WorkContext = new(Guid.NewGuid(), state.WorkstreamId!.Value, state.TeamId, null, null, null, null, id, null, "video-game-production.v2") };
        var configuration = JsonSerializer.SerializeToElement(new { });
        var definition = new ToolchainAdapterDefinition(Guid.NewGuid(), "node-typescript.v1", 1, "Node", "pkg", "1", hash,
            [new("alternative.recipe", ["build"], ["web"], configuration, [], [])], configuration, configuration, [], [], now);
        var adapter = new EligibleToolchainAdapter(definition, new(definition.Id, Guid.NewGuid(), Guid.NewGuid(), "node-24",
            "refreshed-image", now, now.AddDays(1), true));
        var runtime = new AgentTestRuntime().RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(
            PlatformCapabilities.ToolchainCatalogRead, (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([adapter]));
        var result = await VideoGameDirectorAgent.RouteFeasibilityReviewAsync(request, state, original, runtime.CreateContext(), default);
        if (summarizing)
        {
            Assert.Equal("Blocked", result.Disposition);
            Assert.Contains("A current, typed production planning cycle is required.", result.Content);
            return;
        }
        Assert.Equal("Continue", result.Disposition);
        Assert.Null(result.Artifact!.Payload.GetProperty("requiredRecipeKey").GetString());
        Assert.Equal("alternative.recipe", Assert.Single(result.Artifact.Payload.GetProperty("offeredRecipeKeys").EnumerateArray()).GetString());
        Assert.Equal("refreshed-image", result.Artifact.Payload.GetProperty("eligibleDefinitions")[0].GetProperty("EnvironmentImageDigest").GetString());
        var attachment = Assert.Single(result.Artifact.Payload.GetProperty("documentReferences").EnumerateArray());
        Assert.Equal(state.AcceptedVision!.ArtifactRevisionId, attachment.GetProperty("revisionId").GetGuid());
        var wrongTeam = await VideoGameDirectorAgent.RouteFeasibilityReviewAsync(request, state with { TeamId = Guid.NewGuid() }, original, runtime.CreateContext(), default);
        Assert.Equal("Blocked", wrongTeam.Disposition);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ResumedBlockedReviewGetsFreshSpecialistEvidenceBeforeReplacingTheDecision(bool feasible, bool sameRecipe)
    {
        var agent = new VideoGameDirectorAgent();
        var victor = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var state = State();
        var pitch = state.AcceptedVision! with { Markdown = "- **Non-goals:** mobile, 3D\nUse Phaser for 2D web." };
        var now = DateTimeOffset.UtcNow;
        const string currentRecipe = "phaser.web-2d.v1";
        var oldRecipe = sameRecipe ? currentRecipe : "babylon.web-3d.v1";
        var oldEvidence = new CSweet.Agent.SDK.VideoGame.Contracts.ToolchainFeasibilityEvidenceV1(pitch.Digest, oldRecipe,
            ["web"], false, ["The Babylon scaffold cannot verify the recommended Phaser version."], [pitch.ArtifactId], now);
        var blocker = new DecisionRecord(Guid.NewGuid(), state.WorkstreamId!.Value, "video-game.toolchain-selection.v1",
            $"Technical Director feasibility is blocking recipe `{oldRecipe}`: wrong scaffold", "technical-feasibility-blocker",
            [], "remediate", null, "Pending", null, [new("artifact", pitch.ArtifactId, pitch.ArtifactRevisionId,
                pitch.ArtifactRevisionHash, "video-game.vision.v1", "Accepted")], null, null, null, 1, now, now);
        state = state with { AcceptedVision = pitch, ToolchainFeasibilitySessionId = sessionId, ToolchainFeasibilityEvidence = oldEvidence,
            ToolchainBlockerDecisionId = blocker.Id, SpecialistEmployeeIds = new Dictionary<string,Guid> { ["game-technical-director"] = victor } };
        Assert.Null(VideoGameDirectorAgent.SupersededRecipeBlocker(state, blocker with { Status = "Decided" }, currentRecipe));
        Assert.Null(VideoGameDirectorAgent.SupersededRecipeBlocker(state, blocker with { Evidence = [] }, currentRecipe));
        var technicalMember = Technical(victor);
        var runtime = RosterRuntime(state, [technicalMember]);
        var empty = JsonSerializer.SerializeToElement(new {});
        var definition = new ToolchainAdapterDefinition(Guid.NewGuid(), "node-typescript.v1", 1, "Node", "pkg", "1", "hash",
            [new(currentRecipe, ["build"], ["web"], empty, [], [])], empty, empty, [], [], now);
        var provider = new EligibleToolchainAdapter(definition, new(definition.Id, Guid.NewGuid(), Guid.NewGuid(), "node",
            "image", now, now.AddDays(1), true));
        var tech = new AgentCoordinationParticipant(victor, Guid.NewGuid(), "Victor", "Technical Director");
        var director = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Marcus", "Director");
        var session = JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with {
            Id = sessionId, Revision = 4, Status = "Blocked", Target = tech,
            WorkContext = new(Guid.NewGuid(), blocker.WorkstreamId, state.TeamId, null, null, null, null, sessionId, null, null) };
        var resumeCount = 0;
        var decisions = new List<DecisionRequest>();
        runtime.RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead,
            (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([provider, provider]));
        runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([blocker]));
        runtime.RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,
            (r, _) => { Assert.Equal(sessionId, r.SessionId); return Task.FromResult(session); });
        runtime.RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume,
            (r, _) => { Assert.Equal(session.Revision, r.ExpectedRevision); resumeCount++; session = session with { Status = "Active" }; return Task.FromResult(session); });
        runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey == VideoGameDirectorAgent.ProjectStateKey(state.WorkstreamId, null)
                ? new(Guid.NewGuid(), r.StateKey, "test", 1, "Active", new Dictionary<string,string>(), [], "test", [], Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(state), 1, now, now) : null)));
        runtime.RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
            (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                new Dictionary<string,string>(), [], "test", [], Guid.NewGuid(), r.Payload, 2, now, now)));
        runtime.RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest,
            (r, _) => { decisions.Add(r); return Task.FromResult(blocker with { Id = Guid.NewGuid(), AuthorityRuleKey = r.AuthorityRuleKey,
                Status = feasible ? "Decided" : "Pending", SelectedOptionId = feasible ? r.RecommendedOptionId : null }); });
        runtime.RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (_, _) => Task.FromResult<object>(new {
            text = """{"question":"How should we address the unresolved requirement?","blockingImpact":"Only the affected work waits.","choices":[{"label":"Investigate the requirement","consequences":"Obtain missing facts","followThrough":"Ask the technical owner for exact dependency evidence"},{"label":"Revise the requirement","consequences":"Requires owner authority","followThrough":"Propose the precise change for approval"}],"recommendedChoice":0}"""
        }));
        var context = runtime.CreateContext();
        Assert.True((await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), context, default)).Succeeded);
        for (int i = 0; i < 2; i++)
            Assert.False((await agent.EnsureToolchainDecisionAsync(state, 1, Guid.NewGuid(), context, default)).Ready);
        Assert.Equal(1, resumeCount);
        var turn = new AgentCoordinationTurnRequest(sessionId, 5, 4, "Review", "Assess", [], director, tech, false,
            [new(Guid.NewGuid(), 0, director.OrganizationUserId, "Continue", "Assess.", now,
                new(VideoGameDirectorAgent.ToolchainFeasibilityRequestType, "1.0", pitch.Digest, 1, true,
                    JsonSerializer.SerializeToElement(new { requiredRecipeKey = oldRecipe }), "digest")),
             new(Guid.NewGuid(), 1, victor, "Blocked", "Wrong engine.", now,
                new("video-game.toolchain-feasibility.v1", "1.0", pitch.Digest, 1, true, JsonSerializer.SerializeToElement(oldEvidence), "digest"))])
            { WorkContext = session.WorkContext };
        var redirect = await agent.HandleCoordinationTurnAsync(turn, context, default);
        Assert.Equal("Continue", redirect.Disposition);
        Assert.Null(redirect.Artifact!.Payload.GetProperty("requiredRecipeKey").GetString());
        Assert.Equal(currentRecipe, Assert.Single(redirect.Artifact.Payload.GetProperty("offeredRecipeKeys").EnumerateArray()).GetString());
        Assert.Empty(decisions);
        var corrected = state with { ToolchainFeasibilityEvidence = oldEvidence with { RecipeKey = currentRecipe, Feasible = feasible,
            Findings = [feasible ? "Phaser is supported" : "Unresolved dependency"] } };
        corrected = corrected with { ToolchainReviewBasis = VideoGameDirectorAgent.RequirementReviewBasis(corrected,
            technicalMember.AgentInstallationId!.Value, [provider, provider], blocker) };
        var outcome = await agent.EnsureToolchainDecisionAsync(corrected, 1, Guid.NewGuid(), context, default);
        Assert.Equal(feasible, outcome.Ready);
        var fresh = Assert.Single(decisions);
        Assert.Equal(blocker.Id, fresh.SupersedesDecisionId);
        if (feasible) Assert.Equal([provider.Eligibility.ProviderInstallationId.ToString("N"), "defer-selection"], fresh.Options.Select(x => x.Id).ToArray());
        else { Assert.DoesNotContain("Unresolved dependency", fresh.Summary); Assert.Contains("Unresolved dependency", fresh.TypeData!.Value.GetRawText());
            Assert.Contains(fresh.Options, o => o.Id == "provide-direction"); }
    }

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
            "phaser.web-2d.v1");

        Assert.Contains("visual and audio assets", assets);
        Assert.DoesNotContain("{", assets);
        Assert.Contains("phaser.web-2d.v1", build);
        Assert.Contains("compatible build runner", build);
        Assert.Contains("Planning can continue", build);
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
                [CSweet.Agent.SDK.VideoGame.Contracts.VideoGameRoleKeys.TechnicalDirector] = Guid.NewGuid()
            }
        };
        var runtime = RosterRuntime(state, [Technical(state.SpecialistEmployeeIds.Values.Single())])
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(
                PlatformCapabilities.ToolchainCatalogRead, (request, _) =>
                {
                    Assert.Null(request.RecipeKey);
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

    [Fact]
    public async Task RetainedNegativeReviewResumesOnceForChangedEvidenceWithoutBlindRetryOrOverridingAnsweredDirection()
    {
        var state = State(); var expert = Guid.NewGuid(); var installation = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        state = state with { ToolchainFeasibilitySessionId = sessionId,
            SpecialistEmployeeIds = new Dictionary<string, Guid> { ["game-technical-director"] = expert },
            ToolchainFeasibilityEvidence = new(state.AcceptedVision!.Digest, "phaser.web-2d.v1", ["web"], false,
                ["Missing Windows profile inferred from supported alternatives"], [state.AcceptedVision.ArtifactId], DateTimeOffset.UtcNow) };
        var session = JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with { Id = sessionId, Status = "Blocked", Revision = 2,
            Target = new(expert, installation, "Technical specialist", "Expert"),
            WorkContext = new(Guid.NewGuid(), state.WorkstreamId!.Value, state.TeamId, null, null, null, null, sessionId, null, null) };
        var resumes = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,
                (_, _) => Task.FromResult(session))
            .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume,
                (_, _) => { resumes++; return Task.FromResult(session with { Status = "Active" }); })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var agent = new VideoGameDirectorAgent(); var context = runtime.CreateContext();
        var resumed = await agent.ReconcileRequirementReviewAsync(state, 1, Guid.NewGuid(), installation, [], null, context, default);
        Assert.True(resumed.Stop); Assert.Equal(1, resumes);
        var unchanged = await agent.ReconcileRequirementReviewAsync(resumed.State, resumed.Revision, Guid.NewGuid(), installation, [], null, context, default);
        Assert.False(unchanged.Stop); Assert.Equal(1, resumes);
        await agent.ReconcileRequirementReviewAsync(resumed.State, resumed.Revision, Guid.NewGuid(), Guid.NewGuid(), [], null, context, default);
        Assert.Equal(2, resumes);
        var answered = JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Status = "Decided", Id = Guid.NewGuid(), Revision = 2 };
        Assert.True((await agent.ReconcileRequirementReviewAsync(state, 1, Guid.NewGuid(), installation, [], answered, context, default)).Stop);
        Assert.Equal(2, resumes);
    }

    private static AgentTestRuntime RosterRuntime(GameDirectorOperatingState state, IReadOnlyList<AgentTeammate> members, bool wrongTeam = false) =>
        new AgentTestRuntime().RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
            (request, _) => { Assert.Equal(state.TeamId, request.TeamId); return Task.FromResult(new TeamRosterV2Response(
                new AgentTeamContext((wrongTeam ? Guid.NewGuid() : state.TeamId!.Value).ToString("D"), "game", "Game", 2,
                    "producer", "Producer", members, [], members.Count, false), state.WorkstreamId)); })
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]));

    [Theory]
    [InlineData(true, "Pending", true)]
    [InlineData(false, "Pending", false)]
    [InlineData(true, "Decided", false)]
    public void SameRecipeBlockerRequiresVerifiedCurrentEvidenceAndCannotOverrideManagement(bool feasible, string status, bool replaces)
    {
        var state = State(); var pitch = state.AcceptedVision!;
        var recipe = "phaser.web-2d.v1"; var id = Guid.NewGuid();
        state = state with { ToolchainBlockerDecisionId = id,
            ToolchainFeasibilityEvidence = new(pitch.Digest, recipe, ["web"], feasible, ["Retained full findings"], [pitch.ArtifactId], DateTimeOffset.UtcNow) };
        var prior = JsonSerializer.Deserialize<DecisionRecord>("{}")! with { Id = id, WorkstreamId = state.WorkstreamId!.Value,
            Status = status, AuthorityRuleKey = "technical-feasibility-blocker", TypeKey = "video-game.toolchain-selection.v1",
            TypeData = JsonSerializer.SerializeToElement(new { recipeKey = recipe }),
            Evidence = [new("artifact", pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash, "video-game.vision.v1", "Accepted")] };
        Assert.Equal(replaces ? id : (Guid?)null, VideoGameDirectorAgent.SupersededRecipeBlocker(state, prior, recipe));
        Assert.Null(VideoGameDirectorAgent.SupersededRecipeBlocker(state with { AcceptedVision = pitch with { ArtifactRevisionHash = "changed" } }, prior, recipe));
    }

    [Fact]
    public void PortfolioMergeRetainsIndependentProjectsBeyondOneDiscoveryPage()
    {
        var entries = Enumerable.Range(0, 125).Select(i => new GameDirectorPortfolioEntry("project:" + i, Guid.NewGuid(), Guid.NewGuid(),
            null, null, "Project " + i, GameDirectorPhase.Discovery, DateTimeOffset.UtcNow)).ToArray();
        var updated = entries[0] with { WorkingTitle = "Updated title" };
        var merged = VideoGameDirectorAgent.MergePortfolioEntry(entries, updated);
        Assert.Equal(entries.Length, merged.Count);
        Assert.All(entries.Skip(1), entry => Assert.Contains(entry, merged));
    }

    private static GameDirectorOperatingState State() => new() {
        WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid(), WorkingTitle = "Gridlock",
        AcceptedVision = new(1, "digest", "# Gridlock\nA local web arena.", Guid.NewGuid(), Guid.NewGuid(), "hash",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
    };
}
