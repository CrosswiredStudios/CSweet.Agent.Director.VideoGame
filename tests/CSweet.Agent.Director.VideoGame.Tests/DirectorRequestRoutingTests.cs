using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class DirectorRequestRoutingTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    private sealed class Journey
    {
        public readonly Guid Organization = Guid.NewGuid(), Director = Guid.NewGuid(), Manager = Guid.NewGuid(),
            Project = Guid.NewGuid(), Team = Guid.NewGuid(), Creative = Guid.NewGuid(), Producer = Guid.NewGuid(),
            Chat = Guid.NewGuid(), Message = Guid.NewGuid();
        public readonly AgentTestRuntime Runtime = new();
        public readonly VideoGameDirectorAgent Agent = new();
        public GameDirectorOperatingState State => new() { WorkstreamId = Project, TeamId = Team,
            CreativeDirectorEmployeeId = Creative, ProducerEmployeeId = Producer,
            AcceptedVision = new(1, "pitch", "Approved Phaser game", Guid.NewGuid(), Guid.NewGuid(), "pitch-sha",
                Chat, Guid.NewGuid(), Message, DateTimeOffset.UtcNow) };
        public AgentRuntimeContext Context => Runtime.CreateContext(Organization.ToString(), Guid.NewGuid().ToString(),
            new AgentIdentity(Director.ToString(), "Marcus", null, "Video Game Director", null, [], null, Manager.ToString(), "CEO"));
        public CommunicationMessageReceivedEvent Incoming(string text, bool authorized = true) => new(Guid.NewGuid(), Chat.ToString(),
            (authorized ? Manager : Guid.NewGuid()).ToString(), text, new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = (authorized ? Manager : Guid.NewGuid()).ToString() },
            Guid.NewGuid(), 1, Message);
        public DecisionRecord Decision(string status = "Decided") => Empty<DecisionRecord>() with {
            Id = Guid.NewGuid(), WorkstreamId = Project, TypeKey = VideoGameDecisionTypeKeys.ToolchainSelection,
            Summary = "Enable the certified Node/TypeScript build runner for the approved Phaser game?", Status = status,
            SelectedOptionId = status == "Decided" ? "restore-capacity" : null };
        public async Task Configure(string owner)
        {
            Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                Assert.Contains("CURRENT request", r.GetRawText());
                Assert.Contains("Technical implementation requests go through the Producer", r.GetRawText());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant", text = JsonSerializer.Serialize(new {
                    owner, reason = "Accountable role for the requested outcome." }) })); });
            Assert.True((await Runtime.ExecuteCapabilityAsync(Agent, AgentConfigurationCapabilities.Update,
                new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))).Succeeded);
        }
        public async Task<bool> Route(string text, GameDirectorOperatingState? state = null, bool authorized = true)
        {
            var context = Context; var incoming = Incoming(text, authorized);
            await using var stream = context.CreateTurnStream(Chat.ToString(), incoming.TurnId, 1);
            return await Agent.RouteApprovedProjectRequestAsync(incoming, Chat, text, state ?? State, stream, context, default);
        }
        public void EmptyPersonalBoard() => Runtime.RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
            (_, _) => Task.FromResult(new PersonalTodoDirectory([], Guid.NewGuid())));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualWidgetReplyAndTimestampReplayUseRecordedDecisionWithoutModelOrCreativeDelegation(bool timestamp)
    {
        var j = new Journey(); var decision = j.Decision();
        var text = (timestamp ? "Oct 26, 9:29 AM\n" : "") + $"Decision: {decision.Summary} Answer: Enable the build runner";
        Assert.Equal(GameDirectorInboundDisposition.WorkflowInput, GameDirectorInteractionPolicy.Classify(text));
        Assert.Equal("Other", (await j.Agent.ClassifyDelegationIntentAsync(j.Incoming(text), j.Context, default)).Kind);
        j.EmptyPersonalBoard();
        j.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (r, _) => { Assert.Equal(j.Project, r.WorkstreamId); return Task.FromResult<IReadOnlyList<DecisionRecord>>([decision]); })
            .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<DelegationSnapshot>>([]));
        Assert.True(await j.Route(text));
        Assert.Contains(j.Runtime.Progress, p => p.GetProperty("delta").GetString()!.Contains("does not establish build capacity"));
    }

    [Theory]
    [InlineData("pending")] [InlineData("other-project")] [InlineData("superseded")]
    public async Task CopiedAnswerDoesNotApproveOrRequeueUnresolvedOrUnrelatedDecision(string scenario)
    {
        var j = new Journey(); var decision = j.Decision(scenario == "pending" ? "Pending" : "Decided");
        if (scenario == "other-project") decision = decision with { WorkstreamId = Guid.NewGuid() };
        if (scenario == "superseded") decision = decision with { SupersededByDecisionId = Guid.NewGuid() };
        j.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([decision]));
        Assert.True(await j.Route($"Decision: {decision.Summary}\nAnswer: Enable the build runner"));
        Assert.Contains(j.Runtime.Progress, p => p.GetProperty("delta").GetString()!.Contains("existing decision widget"));
    }

    [Theory]
    [InlineData("creative", "Review the core loop's player feedback.")]
    [InlineData("delivery", "Build an executable sprint with the Technical Director.")]
    public async Task SpecialistRequestUsesSdkTrackingWithExactSourceProjectAndApprovedPitch(string owner, string text)
    {
        var j = new Journey(); await j.Configure(owner); var state = j.State; DelegateWithTrackingRequest? sent = null;
        j.Runtime.RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create,
            (r, _) => { sent = r; return Task.FromResult(Empty<DelegationSnapshot>() with { Id = Guid.NewGuid() }); });
        Assert.True(await j.Route(text, state)); Assert.NotNull(sent);
        Assert.Equal(owner == "creative" ? j.Creative : j.Producer, sent.RecipientOrganizationUserId);
        Assert.Equal(text, sent.Instruction); Assert.Equal(j.Chat, sent.SourceConversationId); Assert.Equal(j.Message, sent.SourceMessageId);
        Assert.Equal(j.Project, sent.WorkContext!.WorkstreamId); Assert.Equal(j.Team, sent.WorkContext.TeamId);
        Assert.Contains(state.AcceptedVision!.ArtifactId.ToString(), sent.IntendedOutcome);
        Assert.Contains(state.AcceptedVision.ArtifactRevisionId.ToString(), sent.IntendedOutcome);
        Assert.Contains(state.AcceptedVision.ArtifactRevisionHash, sent.IntendedOutcome);
    }

    [Fact]
    public async Task BuildCapacityRequestRetainsReadinessWorkWithoutClaimingInstallationOrDelegatingToNaomi()
    {
        var j = new Journey(); await j.Configure("platform"); j.EmptyPersonalBoard(); var count = 0;
        j.Runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add,
            (r, _) => { count++; Assert.Equal(j.Project, r.WorkContext!.WorkstreamId); return Task.FromResult(Empty<PersonalTodoItem>()); });
        Assert.True(await j.Route("Enable the Node build runner.")); Assert.Equal(2, count);
        Assert.Contains(j.Runtime.Progress, p => p.GetProperty("delta").GetString()!.Contains("certification must be verified"));
    }

    [Fact]
    public async Task BusinessFollowThroughRetainsProjectContextOnTheDirectorTask()
    {
        var j = new Journey(); await j.Configure("director"); AddPersonalTodoItemRequest? added = null;
        j.Runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add,
            (r, _) => { added = r; return Task.FromResult(Empty<PersonalTodoItem>() with { Id = Guid.NewGuid() }); });
        Assert.True(await j.Route("Assess the budget impact of a console target."));
        Assert.Equal(j.Project, added!.WorkContext!.WorkstreamId); Assert.Equal(j.Team, added.WorkContext.TeamId);
    }

    [Fact]
    public async Task NonManagerCannotRouteWorkAndInvalidOwnerCannotCreateDelegation()
    {
        var j = new Journey(); Assert.False(await j.Route("Enable the build runner", authorized: false));
        await j.Configure("invented");
        await Assert.ThrowsAsync<InvalidOperationException>(() => j.Route("Build the game"));
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("organization", false)] [InlineData("project", false)]
    [InlineData("team", false)] [InlineData("actor", false)] [InlineData("human", false)]
    public void LeadReportsRequireAuthenticatedExactProjectTeamAndAssignedIdentity(string scenario, bool expected)
    {
        var j = new Journey(); var incoming = LeadReport(j, "Assess the delivery blocker.");
        if (scenario == "organization") incoming = incoming with { WorkContext = incoming.WorkContext! with { OrganizationId = Guid.NewGuid() } };
        if (scenario == "project") incoming = incoming with { WorkContext = incoming.WorkContext! with { WorkstreamId = Guid.NewGuid() } };
        if (scenario == "team") incoming = incoming with { WorkContext = incoming.WorkContext! with { TeamId = Guid.NewGuid() } };
        if (scenario == "actor") incoming = incoming with { Context = new Dictionary<string, string>(incoming.Context!) {
            [CommunicationMessageContextKeys.SenderOrganizationUserId] = Guid.NewGuid().ToString() } };
        if (scenario == "human") incoming = incoming with { Context = new Dictionary<string, string>(incoming.Context!) {
            [CommunicationMessageContextKeys.SenderEmployeeType] = "Human" } };
        Assert.Equal(expected, VideoGameDirectorAgent.IsProjectLeadReport(incoming, j.State, j.Organization.ToString()));
    }

    [Fact]
    public async Task AuthenticatedProducerBlockerCreatesDirectorFollowThroughWithoutForwardingItToTheCeo()
    {
        var j = new Journey(); await j.Configure("director"); var state = j.State with { Phase = GameDirectorPhase.Oversight };
        AddPersonalTodoItemRequest? added = null;
        j.Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add,
                (r, _) => { added = r; return Task.FromResult(Empty<PersonalTodoItem>() with { Id = Guid.NewGuid() }); });
        var incoming = LeadReport(j, "Assess the delivery blocker: staffing needs your decision. Reply `Retry staffing: what changed` after investigation.");
        await j.Agent.HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow), j.Context, default);
        Assert.NotNull(added); Assert.Equal(j.Project, added.WorkContext!.WorkstreamId);
        Assert.Equal(incoming.MessageId, added.SourceMessageId);
    }

    [Fact]
    public async Task ADisciplineLeadDoesNotReceiveTheirOwnRequestAsANewDelegation()
    {
        var j = new Journey(); await j.Configure("delivery"); var incoming = LeadReport(j, "Plan the first sprint.");
        await using var stream = j.Context.CreateTurnStream(j.Chat.ToString(), incoming.TurnId, 1);
        Assert.True(await j.Agent.RouteApprovedProjectRequestAsync(incoming, j.Chat, incoming.Message, j.State, stream, j.Context, default));
        Assert.Contains(j.Runtime.Progress, p => p.GetProperty("delta").GetString()!.Contains("You remain accountable for delivery"));
    }

    [Fact]
    public async Task UntypedCoordinationCannotManufactureAnOwnerDecision()
    {
        var j = new Journey();
        j.Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)));
        var result = await j.Agent.HandleCoordinationTurnAsync(new(Guid.NewGuid(), 1, 1, "Delivery question", "Can the build runner be enabled?", [],
            new(j.Director, Guid.NewGuid(), "Marcus", "Video Game Director"), new(j.Producer, Guid.NewGuid(), "Gabriel", "Producer"), false, []) {
                WorkContext = new(j.Organization, j.Project, j.Team, null, null, null, null, Guid.NewGuid(), null, null) }, j.Context, default);
        Assert.Equal("Blocked", result.Disposition); Assert.Contains("supported project-scoped artifact", result.Content);
    }

    private static CommunicationMessageReceivedEvent LeadReport(Journey j, string text) => new(Guid.NewGuid(), j.Chat.ToString(),
        j.Producer.ToString(), text, new Dictionary<string, string> {
            [CommunicationMessageContextKeys.SenderOrganizationUserId] = j.Producer.ToString(),
            [CommunicationMessageContextKeys.SenderEmployeeType] = "Agent" }, Guid.NewGuid(), 1, j.Message) {
                WorkContext = new(j.Organization, j.Project, j.Team, null, null, null, null, Guid.NewGuid(), null, null) };

    [Theory]
    [InlineData("match", true)] [InlineData("recipient", false)] [InlineData("coordinator", false)]
    [InlineData("project", false)] [InlineData("summary", false)] [InlineData("terminal", false)]
    [InlineData("pending", false)] [InlineData("superseded", false)] [InlineData("tracking-stopped", false)]
    public void TrackingCleanupIsBoundToOwnMisroutedCreativeAssignmentAndActualCurrentDecision(string scenario, bool expected)
    {
        var j = new Journey(); var decision = j.Decision();
        var delegation = Empty<DelegationSnapshot>() with { CoordinatorId = j.Director, RecipientId = j.Creative,
            WorkContext = new(j.Project, j.Team), Status = "Active", Instruction = $"Decision: {decision.Summary} Answer: Enable the build runner" };
        if (scenario == "recipient") delegation = delegation with { RecipientId = j.Producer };
        if (scenario == "coordinator") delegation = delegation with { CoordinatorId = Guid.NewGuid() };
        if (scenario == "project") delegation = delegation with { WorkContext = new(Guid.NewGuid()) };
        if (scenario == "summary") delegation = delegation with { Instruction = "Review the creative pillars." };
        if (scenario == "terminal") delegation = delegation with { Status = "Completed" };
        if (scenario == "tracking-stopped") delegation = delegation with { TrackingCancelled = true };
        if (scenario == "pending") decision = decision with { Status = "Pending" };
        if (scenario == "superseded") decision = decision with { SupersededByDecisionId = Guid.NewGuid() };
        Assert.Equal(expected, VideoGameDirectorAgent.MisroutedBuildDecision(delegation, decision, j.State, j.Director.ToString()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TrackingCleanupRereadsCurrentExecutionAndUsesRevisionCheckedSdkCancellation(bool alreadyCompleted)
    {
        var j = new Journey(); var decision = j.Decision(); j.EmptyPersonalBoard(); var reads = 0; var cancels = 0;
        var stale = Empty<DelegationSnapshot>() with { Id = Guid.NewGuid(), Revision = 1, CoordinatorId = j.Director,
            RecipientId = j.Creative, WorkContext = new(j.Project, j.Team), Status = "Active",
            Instruction = $"Decision: {decision.Summary} Answer: Enable the build runner" };
        var current = stale with { Revision = 5, ExecutionStatus = alreadyCompleted ? "Completed" : "Active" };
        j.Runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead,
            (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([decision]))
            .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read,
                (r, _) => { reads++; return Task.FromResult<IReadOnlyList<DelegationSnapshot>>([r.DelegationId.HasValue ? current : stale]); })
            .RegisterCapability<CancelDelegationRequest, DelegationSnapshot>(DelegationCapabilities.Cancel,
                (r, _) => { cancels++; Assert.Equal(current.Id, r.DelegationId); Assert.Equal(5, r.ExpectedRevision);
                    return Task.FromResult(current with { TrackingCancelled = true }); });
        Assert.True(await j.Route($"Decision: {decision.Summary} Answer: Enable the build runner"));
        Assert.Equal(2, reads); Assert.Equal(alreadyCompleted ? 0 : 1, cancels);
    }
}
