using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectApprovalReviewTests
{
    [Theory]
    [InlineData(true, "Blocked")]
    [InlineData(false, "Continue")]
    public async Task ProducerBlockerIsPreservedAndAnExplicitResumeCanRetry(bool finalization, string expected)
    {
        var manager = Guid.NewGuid(); var producer = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var state = new GameDirectorOperatingState { ProducerEmployeeId = producer, IntakeConversationId = conversation,
            AcceptedVision = new(1, "vision", "# Game", Guid.NewGuid(), Guid.NewGuid(), "hash", conversation,
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var key = VideoGameDirectorAgent.ProjectStateKey(null, conversation);
        var runtime = new AgentTestRuntime().RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
            PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                r.StateKey == key ? new(Guid.NewGuid(), key, "test", 1, "Active", new Dictionary<string, string>(), [], key, [],
                    Guid.NewGuid(), JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null)));
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Director", null, "Director", null, [], null, null, null));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 3, 2, "Project", "Create", [],
            new(manager, Guid.NewGuid(), "Director", "Initiator"), new(producer, Guid.NewGuid(), "Producer", "Target"), finalization,
            [new(Guid.NewGuid(), 0, manager, "Continue", "Prepare proposal", DateTimeOffset.UtcNow,
                new("video-game.project-foundation.request.v1", "1.0", "vision", 1, true,
                    JsonSerializer.SerializeToElement(new { sourceConversationId = conversation }), "digest")),
             new(Guid.NewGuid(), 1, producer, "Blocked", "The outcome was invalid after one correction attempt.", DateTimeOffset.UtcNow)]);
        var result = await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(request, context, default);
        Assert.Equal(expected, result.Disposition);
        if (finalization) Assert.Contains("outcome was invalid", result.Content);
        else Assert.Contains("Retry the project proposal", result.Content);
    }

    [Theory]
    [InlineData("Approve", true)]
    [InlineData("RequestRevision", true)]
    [InlineData("RequestRevision", false)]
    [InlineData("Escalate", true)]
    public async Task ManagerAnalyzesCurrentCommandAndSpendingThenRecordsExactBoundDecision(string verdict, bool includePitch)
    {
        var manager = Guid.NewGuid(); var producer = Guid.NewGuid(); var proposal = Guid.NewGuid();
        var pitch = new ApprovedPitchFixture(producer);
        var pending = true; var models = 0; JsonElement decision = default;
        var plan = new WorkstreamPlanProposalV2Request("Game", "Accepted prototype", ["Playable"], "Concept", producer, null, [], [],
            null, null, null, null, "Deliver", "proposal-key", "game", 1, JsonSerializer.SerializeToElement(new { }),
            new(null, 14, [], ["launch"], ["work-planning"], null), [], includePitch ? [new("artifact", pitch.Pitch.ArtifactId, pitch.Pitch.ArtifactRevisionId, pitch.Pitch.ArtifactRevisionHash, "video-game.vision.v1", "Accepted")] : []);
        var cached = new Dictionary<string, AgentOperatingStateResponse>();
        cached[VideoGameDirectorAgent.StateKey] = pitch.State;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(pitch.Document))
            .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(
                pending ? new object[] { new { proposalId = proposal, status = "Pending", requesterId = producer, approverId = manager,
                    binding = new { payload = plan, payloadHash = "exact-hash", idempotencyKey = "proposal-key" },
                    spending = new { mode = "Unlimited", maximumAmount = (decimal?)null, currency = (string?)null } } } : [])))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(cached.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(cached[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                models++;
                var prompt = r.GetProperty("messages")[0].GetProperty("text").GetString()!;
                Assert.Contains("spending policy", prompt); Assert.Contains("Escalate", prompt);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(new { decisionKind = verdict, rationale = "Specific review assessment." }), role = "assistant" })); })
            .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.decide.v1", (r, _) => {
                decision = r; pending = false; return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())); });
        var context = runtime.CreateContext(identity: new(manager.ToString(), "Naomi", null, "Video Game Director", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        var agent = new VideoGameDirectorAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), context, default);
        Assert.True(configured.Succeeded, configured.Error);
        await agent.ReviewAssignedProjectsAsync(context, default);
        await agent.ReviewAssignedProjectsAsync(context, default);
        Assert.Equal(includePitch ? 1 : 0, models);
        Assert.Equal(proposal, decision.GetProperty("proposalId").GetGuid());
        Assert.Equal("exact-hash", decision.GetProperty("payloadHash").GetString());
        Assert.Equal("proposal-key", decision.GetProperty("actionIdempotencyKey").GetString());
        Assert.Equal(verdict, decision.GetProperty("decisionKind").GetString());
    }
}
