using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PitchEscalationTests
{
    [Fact]
    public async Task DecisionEventBeforePauseCommitIsRecoveredByNormalDiscovery()
    {
        var f = new Fixture(); var agent = new VideoGameDirectorAgent();
        await agent.ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        f.Decision = f.Decision! with { Status = DecisionStatuses.Decided, SelectedOptionId = "provide-direction", Rationale = "Use supplied assets.", Revision = 2 };
        f.SetSessionStatus("Active");
        var resource = new GenericResourceEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, f.Request.WorkContext!,
            "Decision", f.Decision.Id, 2, f.Decision.TypeKey, "decided", JsonSerializer.SerializeToElement(new { }));
        var notification = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), WorkstreamEventNames.DecisionDecidedV1,
            JsonSerializer.SerializeToElement(resource), DateTimeOffset.UtcNow);
        await agent.HandleEventAsync(notification, f.Context, default);
        Assert.Equal(0, f.Resumes);
        f.SetSessionStatus("Blocked");
        await agent.ReconcilePitchDecisionAsync(f.State, f.Context, default);
        await agent.HandleEventAsync(notification, f.Context, default);
        Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task ManagerCreatesOneDecisionParksProducerAndRelaysAuthoritativeAnswerBeforeResumingRefinement()
    {
        var f = new Fixture();
        var agent = new VideoGameDirectorAgent();
        var first = await agent.ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        Assert.Equal("Blocked", first.Disposition);
        Assert.Contains("Gabriel, pause", first.Content);
        Assert.Equal(1, f.Requests);
        Assert.Null(f.Decision!.DueAt);
        var repeated = await agent.ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        Assert.Equal("Blocked", repeated.Disposition);
        Assert.Equal(1, f.Requests);
        Assert.True(await agent.ReconcilePitchDecisionAsync(f.State, f.Context, default));
        Assert.Equal(0, f.Resumes);
        f.Decision = f.Decision with { Status = DecisionStatuses.Decided, SelectedOptionId = "provide-direction",
            Rationale = "Use supplied audio only. No additional spending is authorized.", Revision = 2 };
        await agent.ReconcilePitchDecisionAsync(f.State, f.Context, default);
        await agent.ReconcilePitchDecisionAsync(f.State, f.Context, default);
        Assert.Equal(1, f.Resumes); // Duplicate discovery reads Active and leaves it alone.
        var relay = await agent.ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        Assert.Equal("Continue", relay.Disposition);
        Assert.Contains(f.Decision.Rationale, relay.Content);
        var reply = relay.Artifact!.Payload.Deserialize<PitchReply>(PitchProtocol.Json)!;
        Assert.Equal(f.Decision.Id, reply.OwnerDecisionId);
        Assert.False(reply.Accepted);
        Assert.Equal(f.Review.RevisionId, reply.RevisionId);
        // The fixture deliberately provides no artifact mutation or message-send tools.
    }

    [Fact]
    public async Task OwnerPauseAndUnrelatedSessionNeverResumeWork()
    {
        var f = new Fixture(); var agent = new VideoGameDirectorAgent();
        await agent.ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        f.Decision = f.Decision! with { Status = DecisionStatuses.Decided, SelectedOptionId = "pause-affected-work", Rationale = "Wait for licensing." };
        Assert.True(await agent.ReconcilePitchDecisionAsync(f.State, f.Context, default));
        Assert.Equal(0, f.Resumes);
        Assert.False(await agent.ReconcilePitchDecisionAsync(f.State with { HandoffSessionId = Guid.NewGuid() }, f.Context, default));
        Assert.Equal(0, f.Resumes);
    }

    [Fact]
    public async Task ARealExternalConstraintIsRetainedWhileMissingDatesNeedNoEscalation()
    {
        var f = new Fixture();
        f.CachedAnswer = new(false, "No deadline is required. Plan feasibility first within existing authority.", "");
        var result = await new VideoGameDirectorAgent().ReviewProducerPitchAsync(f.Request, f.State, f.Review, f.Context, default);
        Assert.Equal("Continue", result.Disposition);
        Assert.Equal(0, f.Requests);
    }

    private sealed class Fixture
    {
        public GameDirectorOperatingState State { get; }
        public AgentCoordinationTurnRequest Request { get; }
        public PitchReview Review { get; }
        public AgentRuntimeContext Context { get; }
        public DecisionRecord? Decision;
        public DirectorReview CachedAnswer = new(false, "Audio licensing exceeds my authority.", "", true, "May we license the requested audio, or use supplied audio only?");
        public int Requests, Resumes;
        private AgentCoordinationSession Session;
        public void SetSessionStatus(string status) => Session = Session with { Status = status };
        public Fixture()
        {
            var director = Guid.NewGuid(); var producer = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid();
            var document = Guid.NewGuid(); var revision = Guid.NewGuid(); var session = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            State = new() { WorkstreamId = project, TeamId = team, ProducerEmployeeId = producer, HandoffSessionId = session,
                AcceptedVision = new(1, "pitch", "Game", Guid.NewGuid(), Guid.NewGuid(), "sha", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now) };
            Review = new("pitch", document, revision, new string('a', 64), false, ["Audio license authority?"], "Authority affects audio scope.");
            Request = new(session, 2, 2, "Brief", "Plan", [], new(director, Guid.NewGuid(), "Naomi", "Director"),
                new(producer, Guid.NewGuid(), "Gabriel", "Producer"), false, []) { WorkContext = new(Guid.NewGuid(), project, team, null, null, null, null, Guid.NewGuid(), null, null) };
            Session = new(session, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Request.Self, Request.Counterpart,
                "Brief", "Plan", [], "Blocked", 3, 3, null, false, "Production brief paused for manager decision", now, now, []) { WorkContext = Request.WorkContext };
            var markdown = string.Join("\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }.Select(x => "## " + x + "\nDetails."));
            var runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(
                    new ArtifactDocument(document, "Brief", PitchProtocol.DocumentType, "Draft", revision, null, null, null, null,
                        [new(revision, 1, null, markdown, Review.RevisionSha256, "Draft", now, null, null)]) { WorkstreamId = project, TeamId = team }))
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,
                    (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>(Decision is null ? [] : [Decision]))
                .RegisterCapability<DecisionRequest, DecisionRecord>(DecisionCapabilityNames.RequestV1, (r, _) =>
                {
                    Requests++; Assert.Equal("material-strategy-change", r.AuthorityRuleKey);
                    Decision = new(Guid.NewGuid(), project, r.TypeKey, r.Summary, r.AuthorityRuleKey, r.Options,
                        r.RecommendedOptionId, null, DecisionStatuses.Pending, null, r.Evidence, null, null, r.DueAt, 1, now, now, r.TypeData);
                    return Task.FromResult(Decision);
                })
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                        new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(),
                        r.StateKey.StartsWith("pitch-director:", StringComparison.Ordinal)
                            ? JsonSerializer.SerializeToElement(CachedAnswer, PitchProtocol.Json)
                            : JsonSerializer.SerializeToElement(State, PitchProtocol.Json), 1, now, now))))
                .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(Session))
                .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (r, _) =>
                {
                    Resumes++; Assert.Equal(Session.Revision, r.ExpectedRevision);
                    Session = Session with { Status = "Active", Revision = Session.Revision + 1 };
                    return Task.FromResult(Session);
                });
            Context = runtime.CreateContext(identity: new AgentIdentity(director.ToString("D"), "Naomi", null, "Director", null, [], null, Guid.NewGuid().ToString("D"), "Owner"));
        }
    }
}
