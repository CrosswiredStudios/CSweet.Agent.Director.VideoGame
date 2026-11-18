using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PitchEscalationTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pitch_review_escalates_audio_licensing_only_for_the_exact_brief(bool staleHash)
    {
        var f = new Fixture(seedCachedReview: false);
        var review = staleHash ? f.Review with { RevisionSha256 = new string('b', 64) } : f.Review;
        var artifact = PitchProtocol.Artifact(PitchProtocol.ReviewType, review.PitchDigest, review);
        var turn = new AgentCoordinationTurn(Guid.NewGuid(), 1, f.Request.Counterpart.OrganizationUserId,
            "Continue", review.Rationale, DateTimeOffset.UtcNow,
            new(artifact.Type, artifact.SchemaVersion, artifact.Key, artifact.PageOrdinal, artifact.IsFinalPage, artifact.Payload, "fixture"));
        var request = f.Request with { Transcript = [turn] };
        var modelSteps = new List<string>();
        var replies = new List<RespondToAgentCoordinationRequest>();
        f.Runtime.RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
            (_, _) => Task.FromResult(new PersonalTodoDirectory([])));
        f.Runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (call, _) =>
        {
            modelSteps.Add(call.GetProperty("telemetry").GetProperty("invocationKind").GetString()!);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(f.CachedAnswer, PitchProtocol.Json) }));
        });
        f.Runtime.RegisterCapability<RespondToAgentCoordinationRequest, AgentCoordinationSession>(
            CommunicationCapabilities.CoordinationRespond, (reply, _) =>
            {
                replies.Add(reply);
                return Task.FromResult(f.Session with { FinalSummary = reply.Content });
            });
        var host = new AgentTestHost(new VideoGameDirectorAgent(), f.Runtime, f.Context.Identity,
            businessId: request.WorkContext!.OrganizationId.ToString("D"), installationId: request.Self.AgentInstallationId.ToString("D"));
        var settings = new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
            ["llmModel"] = JsonSerializer.SerializeToElement("scripted-director")
        });
        Assert.True((await host.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update, settings)).Succeeded);

        Assert.True((await host.DeliverCoordinationTurnAsync(request)).Succeeded);

        var reply = Assert.Single(replies);
        Assert.Equal(request.SessionId, reply.SessionId);
        Assert.Equal(request.ExpectedRevision, reply.ExpectedRevision);
        Assert.Equal(request.TurnOrdinal, reply.ExpectedTurnOrdinal);
        Assert.Equal($"coordination-turn:{request.SessionId:N}:{request.TurnOrdinal}", reply.IdempotencyKey);
        Assert.Equal(AgentCoordinationDispositions.Blocked, reply.Disposition);
        Assert.Null(reply.Artifact);
        if (staleHash)
        {
            Assert.Contains("exact shared planning document", reply.Content);
            Assert.Empty(modelSteps);
            Assert.Equal(0, f.Requests);
            return;
        }

        Assert.Equal("director-production-baseline-review", Assert.Single(modelSteps));
        Assert.Equal(1, f.Requests);
        var decision = Assert.IsType<DecisionRecord>(f.Decision);
        Assert.Equal(DecisionStatuses.Pending, decision.Status);
        Assert.Equal(f.CachedAnswer.OwnerQuestion, decision.Summary);
        Assert.Equal(f.State.WorkstreamId, decision.WorkstreamId);
        var binding = VideoGameDirectorAgent.PitchDecision(decision);
        Assert.Equal(new VideoGameDirectorAgent.PitchDecisionContext(request.SessionId, review.DocumentId,
            review.RevisionId, review.RevisionSha256, request.Counterpart.OrganizationUserId, review.PitchDigest), binding);
        Assert.Contains(decision.Id.ToString("D"), reply.Content);
    }

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
        public AgentTestRuntime Runtime { get; }
        public DecisionRecord? Decision;
        public DirectorReview CachedAnswer = new(false, "Audio licensing exceeds my authority.", "", true, "May we license the requested audio, or use supplied audio only?");
        public int Requests, Resumes;
        public AgentCoordinationSession Session { get; private set; }
        public void SetSessionStatus(string status) => Session = Session with { Status = status };
        public Fixture(bool seedCachedReview = true)
        {
            var director = Guid.NewGuid(); var producer = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid();
            var document = Guid.NewGuid(); var revision = Guid.NewGuid(); var session = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            State = new() { WorkstreamId = project, TeamId = team, ProducerEmployeeId = producer, CreativeDirectorEmployeeId = Guid.NewGuid(), HandoffSessionId = session,
                AcceptedVision = new(1, "pitch", "Game", Guid.NewGuid(), Guid.NewGuid(), "sha", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now) };
            Review = new("pitch", document, revision, new string('a', 64), false, ["Audio license authority?"], "Authority affects audio scope.");
            Request = new(session, 2, 2, "Brief", "Plan", [], new(director, Guid.NewGuid(), "Marcus", "Director"),
                new(producer, Guid.NewGuid(), "Gabriel", "Producer"), false, []) { WorkContext = new(Guid.NewGuid(), project, team, null, null, null, null, session, null, null) };
            Session = new(session, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Request.Self, Request.Counterpart,
                "Brief", "Plan", [], "Blocked", 3, 3, null, false, "Production brief paused for manager decision", now, now, []) { WorkContext = Request.WorkContext };
            var markdown = string.Join("\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }.Select(x => "## " + x + "\nDetails."));
            var reviewKey = $"pitch-director:{session:N}:{Request.TurnOrdinal}";
            AgentOperatingStateResponse? savedReview = null;
            AgentOperatingStateResponse? ReadState(string key)
            {
                if (key == reviewKey && !seedCachedReview) return savedReview;
                JsonElement? payload = key switch
                {
                    var k when k == reviewKey => JsonSerializer.SerializeToElement(CachedAnswer, PitchProtocol.Json),
                    var k when k == VideoGameDirectorAgent.ProjectStateKey(project, null) => JsonSerializer.SerializeToElement(State, PitchProtocol.Json),
                    var k when k == $"director-creative-review:{project:N}:{revision:N}:{State.AcceptedVision!.ArtifactRevisionId:N}" =>
                        CreativeReviewFixture.Accepted(State, document, revision, Review.RevisionSha256),
                    _ => null
                };
                return payload is { } data ? new(Guid.NewGuid(), key, "test", 1, "Active",
                    new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(), data, 1, now, now) : null;
            }
            Runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (read, _) =>
                {
                    Assert.Equal(document, read.GetProperty("artifactId").GetGuid());
                    return Task.FromResult(new ArtifactDocument(document, "Brief", PitchProtocol.DocumentType, "Draft", revision, null, null, null, null,
                        [new(revision, 1, null, markdown, Review.RevisionSha256, "Draft", now, null, null)]) { WorkstreamId = project, TeamId = team });
                })
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,
                    (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>(Decision is null ? [] : [Decision]))
                .RegisterCapability<DecisionRequest, DecisionRecord>(DecisionCapabilityNames.RequestV1, (r, _) =>
                {
                    Requests++; Assert.Equal("material-strategy-change", r.AuthorityRuleKey);
                    Decision = new(Guid.NewGuid(), r.WorkstreamId, r.TypeKey, r.Summary, r.AuthorityRuleKey, r.Options,
                        r.RecommendedOptionId, null, DecisionStatuses.Pending, null, r.Evidence, null, null, r.DueAt, 1, now, now, r.TypeData);
                    return Task.FromResult(Decision);
                })
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(ReadState(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (write, _) =>
                {
                    Assert.Equal(reviewKey, write.StateKey);
                    savedReview = new(Guid.NewGuid(), write.StateKey, write.SchemaId, write.SchemaVersion, write.Status,
                        write.SourceRevisions, write.ConditionCodes, write.DecisionFingerprint, write.OpenCommitmentCorrelations,
                        write.AttentionReviewId, write.Payload, 1, now, now);
                    return Task.FromResult(savedReview);
                })
                .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(Session))
                .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (r, _) =>
                {
                    Resumes++; Assert.Equal(Session.Revision, r.ExpectedRevision);
                    Session = Session with { Status = "Active", Revision = Session.Revision + 1 };
                    return Task.FromResult(Session);
                });
            Context = Runtime.CreateContext(identity: new AgentIdentity(director.ToString("D"), "Marcus", null, "Director", null, [], null, Guid.NewGuid().ToString("D"), "Owner"));
        }
    }
}
