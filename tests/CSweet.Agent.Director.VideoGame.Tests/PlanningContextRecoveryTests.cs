using System.Text.Json;
using System.Net.Http.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PlanningContextRecoveryTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;

    private sealed class LocalPlanningFactAttribute : FactAttribute
    {
        public LocalPlanningFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL_URL")) ||
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL")))
                Skip = "Select the local planning model and its API base URL.";
        }
    }

    [LocalPlanningFact, Trait("Category", "LocalInference")]
    public async Task Local_model_routes_two_paraphrased_handoffs_without_owner_approval()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        var endpoint = Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL_URL")!.TrimEnd('/') + "/chat/completions";
        foreach (var question in new[] {
            "Gabriel can own the internal package label. Who should estimate the distinct catch feedback so we keep the approved scope?",
            "We need owners for the run instructions and catch cue. Adrian estimated 13 points; the calendar is unknown and zero was a placeholder. What should delivery and engineering do next?"
        })
        {
            var f = new Journey();
            var options = new DecisionOption[] { new("coordinate", "Assign the label to delivery and ask engineering to confirm capacity for the catch feedback.", null),
                new("reduce", "Remove the distinct catch feedback from the accepted scope.", null) };
            f.Decision = f.Decision with { Status = "Pending", SelectedOptionId = null, Rationale = null, DecidedByOrganizationUserId = null,
                Summary = question, Options = question.StartsWith("Gabriel") ? options : options.Reverse().ToArray(), RecommendedOptionId = "coordinate", TypeData = null };
            f.Decisions[f.Decision.Id] = f.Decision;
            f.RegisterDiscovery();
            f.Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, async (request, ct) => {
                var messages = request.GetProperty("messages").EnumerateArray().Select(m => new {
                    role = m.GetProperty("role").GetString(), content = m.GetProperty("text").GetString() });
                using var response = await http.PostAsJsonAsync(endpoint, new { model = Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL"),
                    messages, temperature = 0, max_tokens = 4096 }, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var choice = body.GetProperty("choices")[0];
                return JsonSerializer.SerializeToElement(new { role = "assistant", text = choice.GetProperty("message").GetProperty("content").GetString(), finishReason = choice.GetProperty("finish_reason").GetString() });
            });
            await DeliverPlanningTwiceAsync(f);
            Assert.Equal(0, f.Requests);
            Assert.Equal(1, f.Decides);
            Assert.Equal("coordinate", f.Decision.SelectedOptionId);
            Assert.Contains("catch", f.Decision.Rationale, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Planning_handoff_keeps_capacity_investigation_with_team_after_restart(bool legacyPayload)
    {
        var f = new Journey();
        var incident = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-planning-capacity.json")));
        f.Decision = f.Decision with {
            Status = "Pending", SelectedOptionId = null, DecidedByOrganizationUserId = null, Rationale = null,
            Summary = legacyPayload ? incident.GetProperty("summary").GetString()! : "Delivery can check the package label. Who can estimate the catch feedback within the approved scope?",
            Options = incident.GetProperty("options").Deserialize<DecisionOption[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
            RecommendedOptionId = "choice-1", TypeData = legacyPayload ? JsonSerializer.SerializeToElement(new {
                contract = "requirement-resolution.v1", facts = new { boardId = f.Board, authorityQuestion = incident.GetProperty("question") }
            }) : null
        };
        f.Decisions[f.Decision.Id] = f.Decision;
        f.RegisterDiscovery();
        var rosterSeen = false;
        f.Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) => {
            if (request.GetRawText().Contains("selected immediate action"))
            {
                Assert.DoesNotContain("choice-2", request.GetRawText());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { text = """{"change":null}""" }));
            }
            rosterSeen = request.GetRawText().Contains("Adrian");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { text = incident.GetProperty("reply").GetRawText() }));
        });
        var id = f.Decision.Id;
        await DeliverPlanningTwiceAsync(f);
        Assert.True(rosterSeen, "Director must read Adrian's roster entry using the project binding.");
        Assert.DoesNotContain(f.Decisions.Values, d => d.AuthorityRuleKey == "material-strategy-change");
        Assert.Equal(1, f.Decides);
        Assert.Equal("choice-1", f.Decisions[id].SelectedOptionId);
        Assert.Contains("Producer/Delivery", f.Decisions[id].Rationale);
        Assert.Contains("bounded capacity proposal", f.Decisions[id].Rationale);
    }

    private static async Task DeliverPlanningTwiceAsync(Journey f)
    {
        var id = f.Decision.Id;
        var work = new AgentWorkContext(Guid.NewGuid(), f.Project, null, null, null, null, null, Guid.NewGuid(), null, null);
        var notice = new GenericResourceEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, work, "Decision", id, 1, f.Decision.TypeKey, "requested", JsonSerializer.SerializeToElement(new {}));
        var eventId = Guid.NewGuid();
        for (int delivery = 0; delivery < 2; delivery++)
        {
            var host = new AgentTestHost(new VideoGameDirectorAgent(), f.Runtime, f.Context().Identity);
            Assert.True((await host.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(
                new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))).Succeeded);
            var result = await host.DeliverEventAsync(WorkstreamEventNames.DecisionRequestedV1, notice, eventId);
            Assert.True(result.Succeeded, result.ToString());
        }
    }

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_or_foreign_board_requires_internal_clarification(bool foreign)
    {
        var f = new Journey();
        f.State = f.State with { BoardId = null };
        f.Decision = f.Decision with { Status = "Pending", SelectedOptionId = null, Rationale = null,
            DecidedByOrganizationUserId = null, TypeData = null };
        var board = new WorkBoardSummary(f.Board, "Delivery", "", false, false, 1, []) { WorkstreamId = f.Project, TeamId = f.Team };
        var other = board with { Id = Guid.NewGuid(), WorkstreamId = foreign ? Guid.NewGuid() : f.Project };
        if (foreign)
        {
            f.Decision = f.Decision with { Evidence = [.. f.Decision.Evidence, new("board", other.Id, null, null, WorkBoardCapabilities.Read, "Current")] };
            f.Runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (r, _) =>
                Task.FromResult(new WorkBoardDetail(other, [], [])));
        }
        else f.Runtime.RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([board, other]));
        f.Decisions[f.Decision.Id] = f.Decision;
        f.RegisterDiscovery();
        await DeliverPlanningTwiceAsync(f);
        Assert.Equal("request-more-evidence", f.Decision.SelectedOptionId);
        Assert.Contains("board", f.Decision.Rationale);
        Assert.Equal(0, f.Models);
        Assert.Equal(0, f.Requests);
    }

    [Fact, Trait("Category", "AgentHarness")]
    public async Task Selected_change_to_accepted_scope_creates_one_specific_owner_question()
    {
        var f = new Journey { RequiresOwner = true };
        f.Decision = f.Decision with { Status = "Pending", SelectedOptionId = null, Rationale = null, DecidedByOrganizationUserId = null,
            Options = [new("replace", "Replace the playable game with a static image.", null), new("keep", "Keep the playable game.", null)] };
        f.Decisions[f.Decision.Id] = f.Decision;
        f.RegisterDiscovery();
        await DeliverPlanningTwiceAsync(f);
        var card = Assert.Single(f.Decisions.Values, d => d.AuthorityRuleKey == "material-strategy-change");
        Assert.Equal("Pending", card.Status);
        Assert.Contains("static image", card.Summary);
        Assert.Contains("Deliver a playable game.", card.Summary);
        Assert.Equal(0, f.Decides);
        await f.Recover();
        Assert.Equal(1, f.Requests);
    }

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_agent_card_reconciles_only_its_own_duplicated_question(bool includeFacts)
    {
        var f = new Journey();
        await f.Configure();
        f.Decision = f.Decision with { TypeData = includeFacts ? JsonSerializer.SerializeToElement(new { facts = new { boardId = f.Board } }) : null };
        f.Escalate();
        var stale = f.Decision.Id;
        await f.Recover();
        await f.Recover();
        Assert.Equal(DecisionStatuses.Superseded, f.Decisions[stale].Status);
        Assert.Null(f.Decisions[stale].DecidedByOrganizationUserId);
        Assert.Equal(1, f.Requests);
        Assert.Equal(1, f.Decides);
    }

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
        var f = new Journey { RequiresOwner = true }; await f.Configure();
        f.Decision = f.Decision with { Options = [new("replace", "Replace the playable game with a static image.", null), new("keep", "Keep the playable game.", null)] };
        f.Escalate(); var id = f.Decision.Id;
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
        public GameDirectorOperatingState State;
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
                    [new(rev, 1, null, "Planning artifact only. It does not approve builds, staffing or spend. Deliver a playable game. Use the approved internal working title on the package and run instructions. Include distinct feedback when the player catches a power-up.", "accepted", "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = Project }))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions, [], "", [], Guid.NewGuid(), r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
                .RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
                    Requests++; var old = Decisions[r.SupersedesDecisionId!.Value];
                    Decision = old with { Id = Guid.NewGuid(), Status = "Pending", SelectedOptionId = null, Rationale = null, Revision = 1,
                        AuthorityRuleKey = r.AuthorityRuleKey, RequestedByOrganizationUserId = Manager, DecidedByOrganizationUserId = null,
                        SupersedesDecisionId = old.Id, TypeData = r.TypeData, Summary = r.Summary, Options = r.Options, RecommendedOptionId = r.RecommendedOptionId };
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
                    var text = r.GetRawText();
                    if (text.Contains("selected immediate action"))
                    {
                        Assert.DoesNotContain("Keep the playable game.", text);
                        return Task.FromResult<object>(new { text = RequiresOwner
                            ? """{"change":{"acceptedRequirement":"Deliver a playable game.","proposedChange":"Deliver a static image."}}"""
                            : """{"change":null}""" });
                    }
                    Models++; Assert.Contains("Adrian", text); Assert.Contains("Fulfilled", text); Assert.Contains(Resource.Status, text);
                    DuringModel?.Invoke(); return Task.FromResult<object>(new { text = JsonSerializer.Serialize(new { optionId = RequiresOwner ? "replace" : Option,
                        rationale = RequiresOwner ? "Replace the playable game with a static image." : "Current approved hiring and resource facts permit draft decomposition and estimates; execute only within actual grants and readiness." }) });
                });
        }
        public AgentRuntimeContext Context() => Runtime.CreateContext(identity: new(Manager.ToString(), "Marcus", null, null, null, [], null, Guid.NewGuid().ToString(), "Owner"));
        public Task Recover() => Agent.ReassessPlanningContextAsync(Decision, State, Decisions.Values.ToArray(), Context(), default);
        public void RegisterDiscovery()
        {
            var index = new GameDirectorPortfolioIndex { Projects = [new("retained-project", Project, State.AcceptedVision!.ConversationId,
                Team, Board, "Prism Break", State.Phase, DateTimeOffset.UtcNow)] };
            Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (r, _) => {
                object? payload = r.StateKey == VideoGameDirectorAgent.ProjectStateKey(Project, null) ? State :
                    r.StateKey switch { "video-game-direction:portfolio" => index, "retained-project" => State, _ => null };
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
