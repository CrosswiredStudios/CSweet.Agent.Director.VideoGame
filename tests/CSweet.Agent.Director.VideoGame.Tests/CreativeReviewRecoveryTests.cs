using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

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
    [InlineData("exhausted", 1)] // Resumes once so the turn asks the owner how to continue.
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

    [Fact]
    public async Task An_exhausted_round_asks_the_owner_how_to_continue()
    {
        var f = new Fixture("exhausted");
        var result = await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.TurnRequest(), f.Context, default);
        Assert.Equal("Blocked", result.Disposition);
        Assert.StartsWith(VideoGameDirectorAgent.CreativeReviewWaitMarker, result.Content);
        var decision = Assert.Single(f.Decisions);
        Assert.Equal(VideoGameDirectorAgent.CreativeReviewDecisionType, decision.TypeKey);
        Assert.Equal(VideoGameDirectorAgent.RetryCreativeReviewOption, decision.RecommendedOptionId);
        Assert.Equal(new[] { VideoGameDirectorAgent.RetryCreativeReviewOption, VideoGameDirectorAgent.ReviseBriefOption,
            VideoGameDirectorAgent.KeepCreativeReviewPausedOption }, decision.Options.Select(x => x.Id));
        Assert.Equal(f.Session.Id, VideoGameDirectorAgent.CreativeReviewDecision(decision)!.SessionId);
    }

    [Theory]
    [InlineData(VideoGameDirectorAgent.RetryCreativeReviewOption, "Continue")]
    [InlineData(VideoGameDirectorAgent.ReviseBriefOption, "Blocked")]
    [InlineData(VideoGameDirectorAgent.KeepCreativeReviewPausedOption, "Blocked")]
    [InlineData(null, "Blocked")]
    public async Task The_owners_choice_decides_whether_Naomi_gets_a_fresh_round(string? option, string expected)
    {
        var f = new Fixture("exhausted");
        f.Decide(option);
        var result = await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.TurnRequest(), f.Context, default);
        Assert.Equal(expected, result.Disposition);
        if (expected == "Continue")
        {
            Assert.Contains("attempt 1/3", result.Content);
            Assert.Equal(VideoGameDirectorAgent.CreativeReviewRequestType, result.Artifact!.Type);
        }
        Assert.Single(f.Decisions); // No second decision while one is pending or decided for this round.
    }

    [Theory]
    [InlineData(VideoGameDirectorAgent.RetryCreativeReviewOption, 1)]
    [InlineData(VideoGameDirectorAgent.KeepCreativeReviewPausedOption, 0)]
    [InlineData(null, 0)]
    public async Task Attention_recovery_resumes_an_exhausted_review_only_after_a_retry_choice(string? option, int expected)
    {
        var f = new Fixture("exhausted");
        f.Decide(option);
        await VideoGameDirectorAgent.RecoverIncompleteCreativeReviewAsync(f.Session.Id, f.Context, default);
        Assert.Equal(expected, f.Resumes);
    }

    [Fact]
    public async Task A_retry_choice_resumes_the_review_when_it_is_decided()
    {
        var f = new Fixture("exhausted");
        f.Decide(VideoGameDirectorAgent.RetryCreativeReviewOption);
        await VideoGameDirectorAgent.ReconcileCreativeReviewDecisionAsync(f.Decisions[0], f.Context, default);
        Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public void Failures_are_counted_from_the_owners_latest_choice()
    {
        var creative = Guid.NewGuid(); var start = DateTimeOffset.UtcNow;
        AgentCoordinationTurn Failure(int minutes) => new(Guid.NewGuid(), minutes, creative, "Blocked",
            VideoGameDirectorAgent.InvalidCreativeReview, start.AddMinutes(minutes), null);
        var turns = new[] { Failure(1), Failure(2), Failure(3), Failure(10) };
        Assert.Equal(4, VideoGameDirectorAgent.CreativeReviewFailuresSince(turns, creative, null));
        Assert.Equal(1, VideoGameDirectorAgent.CreativeReviewFailuresSince(turns, creative, start.AddMinutes(5)));
    }

    private sealed class Fixture
    {
        public AgentCoordinationSession Session { get; }
        public AgentRuntimeContext Context { get; }
        public int Resumes;
        public List<DecisionRecord> Decisions { get; } = [];
        private Guid _project;

        public AgentCoordinationTurnRequest TurnRequest() => new(Session.Id, 5, Session.Turns.Count, "Review", "Review exact evidence", [],
            Session.Initiator, Session.Target, false, Session.Turns) { WorkContext = Session.WorkContext };

        // Records the owner's choice after the earlier failures, as the platform would.
        public void Decide(string? option)
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(1);
            var binding = new VideoGameDirectorAgent.CreativeReviewDecisionContext(Session.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "brief-hash", null);
            Decisions.Add(new(Guid.NewGuid(), _project, VideoGameDirectorAgent.CreativeReviewDecisionType, "Review could not complete",
                "material-strategy-change", [new(VideoGameDirectorAgent.RetryCreativeReviewOption, "Retry", null),
                    new(VideoGameDirectorAgent.KeepCreativeReviewPausedOption, "Keep", null)], VideoGameDirectorAgent.RetryCreativeReviewOption,
                option, option is null ? DecisionStatuses.Pending : DecisionStatuses.Decided, option is null ? null : "Owner choice", [], null, null, null, 2,
                now, now, JsonSerializer.SerializeToElement(new { creativeReview = binding }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }

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
            _project = project;
            var runtime = new AgentTestRuntime()
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,
                    (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>(Decisions.ToList()))
                .RegisterCapability<DecisionRequest, DecisionRecord>(DecisionCapabilityNames.RequestV1, (r, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var decision = new DecisionRecord(Guid.NewGuid(), r.WorkstreamId, r.TypeKey, r.Summary, r.AuthorityRuleKey, r.Options,
                        r.RecommendedOptionId, null, DecisionStatuses.Pending, null, r.Evidence, null, null, r.DueAt, 1, now, now, r.TypeData);
                    Decisions.Add(decision);
                    return Task.FromResult(decision);
                })
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
