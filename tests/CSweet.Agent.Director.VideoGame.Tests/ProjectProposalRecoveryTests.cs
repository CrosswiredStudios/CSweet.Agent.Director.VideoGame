using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectProposalRecoveryTests
{
    [Fact]
    public async Task Corrected_contract_resumes_once_and_retains_the_same_pitch_session_and_team()
    {
        var f = new Fixture();
        var result = await f.Continue();
        Assert.False(result.Ready); Assert.Equal("Active", f.Session.Status); Assert.Equal(1, f.Resumes);
        Assert.Equal(f.State.AcceptedVision, result.State.AcceptedVision);
        Assert.Equal(f.State.TeamId, result.State.TeamId);
        Assert.Equal(f.Session.Id, result.State.WorkstreamProposalSessionId);
        f.State = result.State;
        // Another failure under the same schema is a wait, never a new retry window.
        f.Session = f.Session with { Status = "Blocked", Revision = f.Session.Revision + 2 };
        var blocked = await f.Continue();
        Assert.Contains("remains blocked", blocked.State.ProposalWait!.Reason);
        Assert.Equal(1, f.Resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_resume_response_or_failure_before_commit_reuses_the_persisted_command(bool committed)
    {
        var f = new Fixture { FailBeforeResume = !committed, LoseResumeResponse = committed };
        var first = await f.Continue();
        Assert.Contains("waiting", first.State.ProposalWait!.Reason);
        f.State = first.State; f.FailBeforeResume = false; f.LoseResumeResponse = false;
        if (committed)
        {
            // Normal public entrypoint sees Active and clears the old wait without resuming again.
            var recovered = await new VideoGameDirectorAgent().EnsureProjectFoundationAsync(f.State, 1, f.State.TeamId,
                f.State.ProducerEmployeeId!.Value, Guid.NewGuid(), f.Context, default);
            Assert.Equal("", recovered.State.ProposalWait!.Reason);
            Assert.Equal("Active", recovered.State.ProposalWait.Status);
            Assert.Equal(1, f.Resumes);
        }
        else
        {
            await f.Continue();
            Assert.Equal(1, f.Resumes);
            Assert.Equal(2, f.ResumeKeys.Count);
            Assert.Equal(f.ResumeKeys[0], f.ResumeKeys[1]);
        }
    }

    [Theory]
    [InlineData("old-schema")]
    [InlineData("unknown-error")]
    [InlineData("cancelled")]
    [InlineData("wrong-manager")]
    [InlineData("wrong-producer")]
    [InlineData("changed-pitch")]
    [InlineData("revoked-pitch")]
    [InlineData("turn-limit")]
    [InlineData("already-submitted")]
    public async Task Unchanged_failure_or_missing_authority_retains_a_specific_wait_without_restart(string fault)
    {
        var f = new Fixture();
        if (fault == "old-schema") f.OldSchema = true;
        if (fault == "unknown-error") f.Session = f.Session with { Turns = [f.Session.Turns[0], f.Session.Turns[1] with { Content = "Invalid game scope" }] };
        if (fault == "cancelled") f.Session = f.Session with { Status = "Cancelled" };
        if (fault == "wrong-manager") f.Session = f.Session with { Initiator = f.Session.Initiator with { OrganizationUserId = Guid.NewGuid() } };
        if (fault == "wrong-producer") f.State = f.State with { ProducerEmployeeId = Guid.NewGuid() };
        if (fault == "changed-pitch") f.State = f.State with { AcceptedVision = f.State.AcceptedVision! with { Digest = "another-pitch" } };
        if (fault == "revoked-pitch") f.RevokePitch = true;
        if (fault == "turn-limit") f.Session = f.Session with { MaximumTurns = f.Session.NextTurnOrdinal };
        if (fault == "already-submitted") f.State = f.State with { WorkstreamProposalId = Guid.NewGuid() };
        var first = await f.Continue();
        f.State = first.State;
        var writes = f.Writes;
        await f.Continue();
        Assert.Equal(0, f.Resumes);
        Assert.NotNull(first.State.ProposalWait);
        Assert.Equal(writes, f.Writes); // Duplicate wakes do not rewrite an unchanged wait.
    }

    [Fact]
    public void Stale_callbacks_cannot_clear_a_newer_blocker_or_restore_a_previously_cleared_wait()
    {
        var id = Guid.NewGuid();
        var waiting = new GameDirectorOperatingState { ProposalWait = new(id, 8, "Blocked", "Needs current evidence") };
        var active = waiting with { ProposalWait = new(id, 9, "Active", "") };
        Assert.Equal(active.ProposalWait, VideoGameDirectorAgent.MergeConcurrentState(active, waiting).ProposalWait);
        Assert.Equal(waiting.ProposalWait, VideoGameDirectorAgent.MergeConcurrentState(waiting,
            waiting with { ProposalWait = new(id, 7, "Active", "") }).ProposalWait);
        Assert.Null(VideoGameDirectorAgent.MergeConcurrentState(waiting, waiting with { WorkstreamId = Guid.NewGuid() }).ProposalWait);
    }

    private sealed class Fixture
    {
        public GameDirectorOperatingState State;
        public AgentCoordinationSession Session;
        public AgentRuntimeContext Context;
        public int Resumes, Writes;
        public bool OldSchema, FailBeforeResume, LoseResumeResponse, RevokePitch;
        public List<string> ResumeKeys = [];
        private readonly Dictionary<string, AgentOperatingStateResponse> states = [];
        private readonly JsonElement schema;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Fixture()
        {
            var director = Guid.NewGuid(); var producer = Guid.NewGuid(); var team = Guid.NewGuid();
            var pitch = new AcceptedGameVision(1, "pitch-digest", "Approved game", Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            var id = Guid.NewGuid();
            State = new() { TeamId = team, ProducerEmployeeId = producer, WorkstreamProposalSessionId = id, AcceptedVision = pitch };
            var template = new WorkstreamPlanProposalV2Request("Shardfall", "Accepted scope", ["Playable"], "Concept", producer,
                team, [], [], null, null, null, null, "Delivery", "original-key", "game", 1, JsonSerializer.SerializeToElement(new { }),
                new(null, null, [], [], [], null), [], [new("artifact", pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash, "video-game.vision.v1", "Accepted")]);
            Session = new(id, Guid.NewGuid(), pitch.ConversationId, pitch.ChatTurnId, pitch.MessageId,
                new(director, Guid.NewGuid(), "Director", "Initiator"), new(producer, Guid.NewGuid(), "Producer", "Target"),
                "Create project", "Create", [], "Blocked", 4, 3, null, true, "The proposal schema rejected the request.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new(Guid.NewGuid(), 0, director, "Continue", "Submit", DateTimeOffset.UtcNow,
                    new("video-game.project-foundation.request.v1", "1.0", pitch.Digest, 1, true, JsonSerializer.SerializeToElement(template, Json), "digest")),
                 new(Guid.NewGuid(), 1, producer, "Blocked", "The platform rejected the request. JSON Schema validation failed: $.existingProjectId is not allowed.", DateTimeOffset.UtcNow)]);
            var properties = JsonSerializer.SerializeToElement(template, Json).EnumerateObject().ToDictionary(p => p.Name, _ => new { });
            schema = JsonSerializer.SerializeToElement(new { type = "object", properties, additionalProperties = false });
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    Writes++;
                    return Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                })
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => {
                    if (RevokePitch) throw new PlatformCapabilityException(PlatformCapabilities.ArtifactRead, PlatformCapabilityErrorCode.Denied, "Pitch access revoked");
                    return Task.FromResult(new ArtifactDocument(pitch.ArtifactId, "Pitch", "video-game.vision.v1", "Approved", pitch.ArtifactRevisionId, null,
                        pitch.ArtifactRevisionId, null, null, [new(pitch.ArtifactRevisionId, 1, null, pitch.Markdown, pitch.ArtifactRevisionHash, "Accepted", DateTimeOffset.UtcNow, null, null)]));
                })
                .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(Session))
                .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (r, _) => {
                    Assert.Contains(states.Keys, k => k.StartsWith("project-proposal-resume:"));
                    ResumeKeys.Add(r.IdempotencyKey);
                    if (FailBeforeResume) throw new TimeoutException("Not committed");
                    Resumes++; Session = Session with { Status = "Active", Revision = Session.Revision + 1, IsFinalization = false };
                    if (LoseResumeResponse) throw new TimeoutException("Reply lost after commit");
                    return Task.FromResult(Session);
                });
            Context = runtime.CreateContext(identity: new(director.ToString(), "Director", null, null, null, [], null, null, null));
        }
        public Task<(GameDirectorOperatingState State, long? Revision, bool Ready)> Continue() =>
            new VideoGameDirectorAgent().ContinueProjectProposalWaitAsync(State, 1, Session, Guid.NewGuid(), Context,
                (_, _) => Task.FromResult(OldSchema ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }) : schema), default);
    }
}
