using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PlanningContextRecoveryTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;

    [Fact]
    public async Task ExistingPrismBreakAgentDirectionIsReassessedWithActualHiresAndReadyResourcesAndReplaysOnce()
    {
        var f = new Journey(); await f.Configure(); var old = f.Decision;
        await f.Recover();
        Assert.Equal("Superseded", f.Decisions[old.Id].Status);
        Assert.Equal("Decided", f.Decision.Status); Assert.Equal("continue-current-plan", f.Decision.SelectedOptionId);
        Assert.Contains("draft", f.Decision.Rationale); Assert.Equal(1, f.Requests); Assert.Equal(1, f.Models);
        await f.Recover(); await f.Recover(); Assert.Equal(1, f.Requests); Assert.Equal(1, f.Models);
        // A genuinely changed readiness basis permits a new judgment, without fabricating working capacity.
        f.Resource = f.Resource with { Status = "CapacityUnavailable", Revision = 2 };
        f.Option = "request-more-evidence"; await f.Recover();
        Assert.Equal(2, f.Models); Assert.Equal("request-more-evidence", f.Decision.SelectedOptionId);
    }

    [Fact]
    public async Task PendingAgentEscalationCanRecoverWithoutAnsweringTheHumanQuestionOrRepeatingIt()
    {
        var f = new Journey(); await f.Configure(); f.Escalate(); var old = f.Decision;
        await f.Recover(); Assert.Equal("Superseded", f.Decisions[old.Id].Status);
        Assert.Equal("work-planning", f.Decision.AuthorityRuleKey); Assert.Equal(f.Manager, f.Decision.DecidedByOrganizationUserId);
        Assert.Equal(1, f.Requests); Assert.Equal(1, f.Decides);
    }

    [Fact]
    public async Task AStillReservedQuestionStaysPendingAndAnUnchangedContextDoesNotGenerateAnotherReview()
    {
        var f = new Journey { RequiresOwner = true }; await f.Configure(); f.Escalate(); var id = f.Decision.Id;
        await f.Recover(); await f.Recover();
        Assert.Equal(id, f.Decision.Id); Assert.Equal("Pending", f.Decision.Status);
        Assert.Equal(0, f.Requests); Assert.Equal(0, f.Decides); Assert.Equal(1, f.Models);
    }

    [Theory]
    [InlineData("human-answer")][InlineData("unknown-author")][InlineData("ceo-hold")]
    [InlineData("qa-disposition")][InlineData("foreign-manager")][InlineData("unrelated-type")]
    public async Task RecoveryCannotOverrideHumanAuthorityOrUnrelatedWork(string scenario)
    {
        var f = new Journey(); await f.Configure();
        f.Decision = scenario switch {
            "human-answer" => f.Decision with { DecidedByOrganizationUserId = Guid.NewGuid() },
            "unknown-author" => f.Decision with { DecidedByOrganizationUserId = null },
            "foreign-manager" => f.Decision with { DecidedByOrganizationUserId = Guid.NewGuid() },
            "unrelated-type" => f.Decision with { TypeKey = "financial-approval" },
            _ => f.Decision with { TypeData = JsonSerializer.SerializeToElement(new { boardId = f.Board, kind = scenario }) }
        };
        f.Decisions[f.Decision.Id] = f.Decision; await f.Recover();
        Assert.Equal(0, f.Models); Assert.Equal(0, f.Requests); Assert.Equal(0, f.Decides);
    }

    [Fact]
    public async Task ContextChangedDuringModelJudgmentCannotBeRecordedAsCurrentDirection()
    {
        var f = new Journey(); await f.Configure(); f.DuringModel = () => f.Resource = f.Resource with { Status = "Rejected", Revision = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recover()); Assert.Equal(0, f.Requests); Assert.Equal(0, f.Decides);
    }

    [Fact]
    public async Task AnAgentEscalationCannotHideAndReplaceAHumanAnsweredPredecessor()
    {
        var f = new Journey(); await f.Configure(); f.Escalate();
        var parent = f.Decisions[f.Decision.SupersedesDecisionId!.Value];
        f.Decisions[parent.Id] = parent with { DecidedByOrganizationUserId = Guid.NewGuid() };
        await f.Recover(); Assert.Equal(0, f.Models); Assert.Equal(0, f.Requests);
    }

    [Fact]
    public async Task InterruptedReplacementWriteRecoversTheSamePendingDecisionThroughNormalFollowthrough()
    {
        var f = new Journey(); await f.Configure(); f.LoseDecisionReply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Recover()); var replacement = f.Decision.Id;
        // The replacement is durably pending. Normal decision discovery resumes it; no old transcript is rerun.
        f.LoseDecisionReply = false;
        f.RegisterDiscovery();
        await f.Agent.ReconcilePlanningContextsAsync(f.Context(), default);
        await f.Agent.ReconcilePlanningContextsAsync(f.Context(), default);
        Assert.Equal(replacement, f.Decision.Id); Assert.Equal("Decided", f.Decision.Status); Assert.Equal(1, f.Requests);
    }

    private sealed class Journey
    {
        public readonly Guid Project = Guid.NewGuid(), Board = Guid.NewGuid(), Team = Guid.NewGuid(), Manager = Guid.NewGuid();
        public readonly VideoGameDirectorAgent Agent = new();
        public readonly AgentTestRuntime Runtime = new();
        public readonly Dictionary<Guid, DecisionRecord> Decisions = [];
        private readonly Dictionary<string, AgentOperatingStateResponse> states = [];
        public DecisionRecord Decision;
        public readonly GameDirectorOperatingState State;
        public ResourceRequestState Resource;
        public int Models, Requests, Decides;
        public bool RequiresOwner, LoseDecisionReply;
        public string Option = "continue-current-plan";
        public Action? DuringModel;
        public Journey()
        {
            var doc = Guid.NewGuid(); var rev = Guid.NewGuid();
            var fixture = JsonSerializer.Deserialize<VideoGameDirectorAgent.PlanningDirection>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-planning-authority-loop.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Decision = Empty<DecisionRecord>() with { Id = Guid.NewGuid(), WorkstreamId = Project, Revision = 2,
                TypeKey = "video-game.management-direction.v1", AuthorityRuleKey = "work-planning", Status = "Decided",
                RequestedByOrganizationUserId = Guid.NewGuid(), DecidedByOrganizationUserId = Manager,
                Summary = "Authorize engineering for the approaching increment", Rationale = fixture.Rationale, SelectedOptionId = fixture.OptionId,
                Options = [new("continue-current-plan", "Continue", null), new("request-more-evidence", "Evidence", null)], RecommendedOptionId = "continue-current-plan",
                Evidence = [new("artifact", doc, rev, "accepted", "video-game.production-plan.v1", "Accepted")],
                TypeData = JsonSerializer.SerializeToElement(new { boardId = Board, kind = "escalate" }) };
            Decisions[Decision.Id] = Decision;
            State = new() { WorkstreamId = Project, BoardId = Board, TeamId = Team,
                AcceptedVision = new(1, "Game", "Approved playable game", Guid.NewGuid(), Guid.NewGuid(), "vision", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
            var roster = new AgentTeamContext(Team.ToString(), "delivery", "Delivery", 4, "producer", "Producer",
                [new("engineer", "Adrian", "Agent", null, null, "Member", "Available") { DeclaredRoleKeys = ["engineer"], EffectiveCapabilities = ["implement"] }], [], 1, false);
            var staffing = Empty<ResourceChangeRequestResponse>() with { Id = Guid.NewGuid(), WorkstreamId = Project, TeamId = Team,
                Status = "Approved", DeliveryStatus = "Fulfilled", Roles = [], DecidedByOrganizationUserId = Manager };
            Resource = Empty<ResourceRequestState>() with { Id = Guid.NewGuid(), WorkstreamId = Project, Status = "Ready", Revision = 1, PlanHash = "approved-plan", ProviderInstallationId = Guid.NewGuid() };
            Runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(new WorkBoardDetail(
                new(Board, "Prism Break", "", false, false, 4, []) { WorkstreamId = Project, TeamId = Team }, [], [])))
                .RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(roster, Project)))
                .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([staffing])))
                .RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([Resource]))
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (r, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>(
                    r.DecisionId is Guid id ? [Decisions[id]] : Decisions.Values.ToArray()))
                .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(new ArtifactDocument(doc, "Brief", "video-game.production-plan.v1", "Accepted", rev, null, rev, null, null,
                    [new(rev, 1, null, "Planning artifact only. It does not approve builds, staffing or spend. Deliver a playable game.", "accepted", "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = Project }))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
                .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
                    Requests++; var old = Decisions[r.SupersedesDecisionId!.Value];
                    Decision = old with { Id = Guid.NewGuid(), Status = "Pending", SelectedOptionId = null, Rationale = null, Revision = 1,
                        AuthorityRuleKey = r.AuthorityRuleKey, RequestedByOrganizationUserId = Manager, DecidedByOrganizationUserId = null,
                        SupersedesDecisionId = old.Id, TypeData = r.TypeData };
                    Decisions[old.Id] = old with { Status = "Superseded", SupersededByDecisionId = Decision.Id, Revision = old.Revision + 1 };
                    Decisions[Decision.Id] = Decision; return Task.FromResult(Decision);
                })
                .RegisterCapability<DecideDecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionDecide, (r, _) => {
                    if (LoseDecisionReply) throw new IOException("Interrupted before the decision reply");
                    Decides++; Decision = Decision with { Status = "Decided", SelectedOptionId = r.SelectedOptionId, Rationale = r.Rationale, DecidedByOrganizationUserId = Manager, Revision = Decision.Revision + 1 };
                    Decisions[Decision.Id] = Decision; return Task.FromResult(Decision);
                })
                .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (_, _) => Task.FromResult(Empty<PersonalTodoItem>()))
                .RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) => {
                    Models++; var text = r.GetRawText(); Assert.Contains("Adrian", text); Assert.Contains("Fulfilled", text); Assert.Contains(Resource.Status, text);
                    DuringModel?.Invoke(); return Task.FromResult<object>(new { text = JsonSerializer.Serialize(new { optionId = Option,
                        rationale = "Current approved hiring and resource facts permit draft decomposition and estimates; execute only within actual grants and readiness.", requiresOwner = RequiresOwner }) });
                });
        }
        public AgentRuntimeContext Context() => Runtime.CreateContext(identity: new(Manager.ToString(), "Marcus", null, null, null, [], null, Guid.NewGuid().ToString(), "Owner"));
        public Task Recover() => Agent.ReassessPlanningContextAsync(Decision, State, Decisions.Values.ToArray(), Context(), default);
        public void RegisterDiscovery()
        {
            var index = new GameDirectorPortfolioIndex { Projects = [new("retained-project", Project, State.AcceptedVision!.ConversationId,
                Team, Board, "Prism Break", State.Phase, DateTimeOffset.UtcNow)] };
            Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => {
                object? payload = r.StateKey switch { "video-game-direction:portfolio" => index, "retained-project" => State, _ => null };
                return Task.FromResult(new AgentOperatingStateReadResponse(payload is null ? states.GetValueOrDefault(r.StateKey) :
                    new(Guid.NewGuid(), r.StateKey, "retained", 1, "Active", new Dictionary<string, string>(), [], "", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(payload), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
            });
        }
        public void Escalate()
        {
            var old = Decision;
            Decision = old with { Id = Guid.NewGuid(), AuthorityRuleKey = "material-strategy-change", Status = "Pending", SelectedOptionId = null,
                RequestedByOrganizationUserId = Manager, DecidedByOrganizationUserId = null, SupersedesDecisionId = old.Id };
            Decisions[old.Id] = old with { Status = "Superseded", SupersededByDecisionId = Decision.Id };
            Decisions[Decision.Id] = Decision;
        }
        public async Task Configure() => Assert.True((await Runtime.ExecuteCapabilityAsync(Agent, AgentConfigurationCapabilities.Update,
            new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))).Succeeded);
    }
}
