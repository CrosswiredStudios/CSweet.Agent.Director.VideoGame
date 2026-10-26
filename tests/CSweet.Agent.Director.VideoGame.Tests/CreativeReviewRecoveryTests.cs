using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class CreativeReviewRecoveryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Completed_old_review_cannot_wake_a_newer_Producer_draft(bool stale, bool expected)
    {
        var f = new Fixture("valid");
        var input = f.Session.Turns[0].Artifact!.Payload.Deserialize<VideoGameDirectorAgent.CreativeReviewRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var producer = Guid.NewGuid();
        var parent = f.Session with { Id = input.ParentSessionId!.Value, Target = new(producer, Guid.NewGuid(), "Gabriel", "Producer"), Turns = [
            new(Guid.NewGuid(), 1, producer, "Continue", "Ready", DateTimeOffset.UtcNow,
                new("video-game.production.pitch-review.v1", "1.0", "pitch", 1, true,
                    JsonSerializer.SerializeToElement(new { pitchDigest = "pitch", documentId = input.DocumentId,
                        revisionId = stale ? Guid.NewGuid() : input.RevisionId, revisionSha256 = input.Sha256, ready = true,
                        questions = Array.Empty<string>(), rationale = "Ready" }), "review")),
            new(Guid.NewGuid(), 2, f.Session.Initiator.OrganizationUserId, "Blocked", "Awaiting Naomi's creative review of the exact production brief.", DateTimeOffset.UtcNow, null)] };
        Assert.Equal(expected, VideoGameDirectorAgent.CanResumeCreativeParent(parent, input));
    }

    [Fact]
    public async Task Attention_recovers_the_child_before_a_later_reconciliation_failure()
    {
        var f = new Fixture("downstream-failure");
        var method = typeof(VideoGameDirectorAgent).GetMethod("HandleManagerAttentionReviewAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)method.Invoke(new VideoGameDirectorAgent(),
            [new AgentAttentionReviewContext(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test"), f.Context, CancellationToken.None])!);
        Assert.Equal("Downstream reconciliation unavailable", error.Message);
        Assert.Equal(1, f.Resumes);
    }

    [Theory]
    [InlineData("valid", 1)]
    [InlineData("other-agent", 0)]
    [InlineData("stale-pitch", 0)]
    [InlineData("real-blocker", 0)]
    [InlineData("exhausted", 0)]
    public async Task Recovery_rechecks_exact_authority_and_bounds_schema_retries(string fault, int expected)
    {
        var f = new Fixture(fault);
        await VideoGameDirectorAgent.RecoverIncompleteCreativeReviewAsync(f.Session.Id, f.Context, default);
        Assert.Equal(expected, f.Resumes);
    }

    [Theory]
    [InlineData(false, "Continue")]
    [InlineData(true, "Blocked")]
    public async Task Resumed_incomplete_review_requests_a_new_specialist_result_instead_of_claiming_bad_evidence(bool finalization, string expected)
    {
        var f = new Fixture("valid");
        var request = new AgentCoordinationTurnRequest(f.Session.Id, 5, 3, "Review", "Review exact evidence", [],
            f.Session.Initiator, f.Session.Target, finalization, f.Session.Turns) { WorkContext = f.Session.WorkContext };
        var result = await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal(expected, result.Disposition);
        Assert.DoesNotContain("must match the assigned reviewer", result.Content);
        if (!finalization) Assert.Equal(VideoGameDirectorAgent.CreativeReviewRequestType, result.Artifact!.Type);
    }

    [Fact]
    public void Accepted_review_can_retain_positive_observations_but_cannot_hide_required_changes()
    {
        Assert.True(VideoGameDirectorAgent.ValidCreativeReview(new("accepted", "The pitch is preserved", ["All approved creative pillars are preserved"], [], false)));
        Assert.False(VideoGameDirectorAgent.ValidCreativeReview(new("accepted", "Contradictory", ["Scope changed"], ["Restore scope"], false)));
    }

    [Fact]
    public void Rework_that_needs_Director_judgment_is_valid_but_is_not_acceptance()
    {
        Assert.True(VideoGameDirectorAgent.ValidCreativeReview(new("changes-required", "Resolve evidence method",
            ["A bounded observation method needs Director direction"], ["Record planned validation"], true)));
        Assert.False(VideoGameDirectorAgent.ValidCreativeReview(new("accepted", "Unresolved decision", [], [], true)));
    }

    private sealed class Fixture
    {
        public AgentCoordinationSession Session { get; }
        public AgentRuntimeContext Context { get; }
        public int Resumes;
        public Fixture(string fault)
        {
            var director = Guid.NewGuid(); var creative = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid();
            var parent = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid(); var pitch = Guid.NewGuid(); var pitchRevision = Guid.NewGuid();
            var state = new GameDirectorOperatingState { WorkstreamId = project, TeamId = team, CreativeDirectorEmployeeId = creative,
                HandoffSessionId = parent, AcceptedVision = new(1, "pitch", "Approved game", pitch, pitchRevision, fault == "stale-pitch" ? "new-hash" : "pitch-hash",
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
            var input = new VideoGameDirectorAgent.CreativeReviewRequest(project, team, pitch, pitchRevision, "pitch-hash", document, revision, "brief-hash", "Creative review", parent);
            var turns = new List<AgentCoordinationTurn> {
                new(Guid.NewGuid(), 0, director, "Continue", "Review", DateTimeOffset.UtcNow,
                    new(VideoGameDirectorAgent.CreativeReviewRequestType, "1.0", "brief-hash", 1, true,
                        JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "request-digest")) };
            for (var i = 0; i < (fault == "exhausted" ? 3 : 1); i++)
                turns.Add(new(Guid.NewGuid(), i + 1, creative, "Blocked", fault == "real-blocker" ? "Approved scope is unavailable" : VideoGameDirectorAgent.InvalidCreativeReview, DateTimeOffset.UtcNow, null));
            Session = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new(director, Guid.NewGuid(), "Marcus", "Director"), new(creative, Guid.NewGuid(), "Naomi", "Creative Director"),
                "Review", "Review exact evidence", [], "Blocked", 4, 3, null, false, "Incomplete review", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, turns)
                { WorkContext = new(Guid.NewGuid(), project, team, null, null, null, null, Guid.NewGuid(), null, null) };
            var runtime = new AgentTestRuntime()
                .RegisterCapability<ListAgentCoordinationRequest, AgentCoordinationSessions>(CommunicationCapabilities.CoordinationList,
                    (_, _) => Task.FromResult(new AgentCoordinationSessions([Session])))
                .RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead, (_, _) => Task.FromResult(Session))
                .RegisterCapability<ResumeAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume, (r, _) => {
                    Resumes++; Assert.Equal(Session.Revision, r.ExpectedRevision); return Task.FromResult(Session with { Status = "Active" }); })
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => {
                        if (fault == "downstream-failure" && r.StateKey != "director-creative-review-parents" && r.StateKey != VideoGameDirectorAgent.ProjectStateKey(project, null))
                            throw new InvalidOperationException("Downstream reconciliation unavailable");
                        return Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                            new Dictionary<string,string>(), [], "test", [], Guid.NewGuid(), r.StateKey == "director-creative-review-parents"
                                ? JsonSerializer.SerializeToElement(new { Parents = new[] { parent } }) : JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
                    });
            Context = runtime.CreateContext(identity: new((fault == "other-agent" ? Guid.NewGuid() : director).ToString("D"), "Marcus", null, "Director", null, [], null, Guid.NewGuid().ToString("D"), "CEO"));
        }
    }
}
