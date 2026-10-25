using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectApprovalRecoveryTests
{
    [Fact]
    public async Task ExactRetryReusesCommandKeyAndChangedAssessmentDoesNotCollide()
    {
        var journey = new Journey();
        await journey.ReviewAsync();
        await journey.ReviewAsync();
        Assert.Equal(journey.Decisions[0].GetRawText(), journey.Decisions[1].GetRawText());
        journey.MaximumBudget = 100;
        await journey.ReviewAsync();
        Assert.NotEqual(journey.Decisions[0].GetProperty("decisionIdempotencyKey").GetString(),
            journey.Decisions[2].GetProperty("decisionIdempotencyKey").GetString());
        Assert.Equal(2, journey.Models);
    }

    [Fact]
    public async Task ResolvedReceiptWinsOverStalePendingDiscovery()
    {
        var journey = new Journey { ResolvedExactRead = true };
        await journey.ReviewAsync();
        Assert.Empty(journey.Decisions);
        Assert.Equal(0, journey.Models);
    }

    [Fact]
    public async Task ExistingProducerCanManageAnIndependentApprovedGame()
    {
        var journey = new Journey { Duplicate = true };
        await journey.ReviewAsync();
        var decision = Assert.Single(journey.Decisions);
        Assert.Equal("Approve", decision.GetProperty("decisionKind").GetString());
        Assert.Equal(1, journey.Models);
    }

    private sealed class Journey
    {
        private readonly Guid _manager = Guid.NewGuid(), _producer = Guid.NewGuid(), _proposal = Guid.NewGuid();
        private readonly Dictionary<string, AgentOperatingStateResponse> _cache = [];
        private readonly VideoGameDirectorAgent _agent = new();
        private readonly AgentRuntimeContext _context;
        public decimal? MaximumBudget { get; set; }
        public bool ResolvedExactRead { get; init; }
        public bool Duplicate { get; init; }
        public int Models { get; private set; }
        public List<JsonElement> Decisions { get; } = [];
        public Journey()
        {
            var pitch = new ApprovedPitchFixture(_producer);
            _cache[VideoGameDirectorAgent.StateKey] = pitch.State;
            var runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(pitch.Document))
                .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1", (request, _) =>
                {
                    var resolved = ResolvedExactRead && request.TryGetProperty("proposalId", out var id) && id.ValueKind == JsonValueKind.String;
                    var plan = new WorkstreamPlanProposalV2Request("Pulse Break", "Accepted game", ["Playable"], "Concept", _producer, null, [], [],
                        null, null, null, null, "Deliver", "proposal-key", Duplicate ? "video-game-manager-brief.v1" : "test", 1,
                        JsonSerializer.SerializeToElement(new { }), new(null, 14, [], ["launch"], ["work-planning"], null), [], [new("artifact", pitch.Pitch.ArtifactId, pitch.Pitch.ArtifactRevisionId, pitch.Pitch.ArtifactRevisionHash, "video-game.vision.v1", "Accepted")]);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new object[] { new {
                        proposalId = _proposal, status = resolved ? "Approved" : "Pending", requesterId = _producer, approverId = _manager,
                        binding = new { payload = plan, payloadHash = "hash", idempotencyKey = "proposal-key" },
                        spending = new { mode = MaximumBudget.HasValue ? "Limited" : "Unlimited", maximumAmount = MaximumBudget, currency = "USD" },
                        decision = resolved ? new { decision = "Approve", comment = "Recorded", actorId = _manager, actorName = "Director", projectId = Guid.NewGuid() } : null
                    } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                })
                .RegisterCapability<ReadPortfolioRequest, PortfolioResponse>(WorkstreamCapabilityNames.PortfolioReadV1, (_, _) =>
                {
                    var game = new WorkstreamDetail(Guid.NewGuid(), "Original working title", "Original accepted game", ["Playable"], "Concept", "Approved",
                        _producer, null, null, null, "video-game-production.v2", 2, null, "digest", 1);
                    return Task.FromResult(new PortfolioResponse([new(game, null, [], [])]));
                })
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(_cache.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => Task.FromResult(_cache[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) =>
                {
                    Models++;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new {
                        text = JsonSerializer.Serialize(new { decisionKind = "Approve", rationale = $"Assessment {Models}" }), role = "assistant"
                    }));
                })
                .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.decide.v1", (r, _) =>
                {
                    Decisions.Add(r.Clone()); // Simulate an interrupted response while pending state is retained.
                    return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
                });
            _context = runtime.CreateContext(identity: new(_manager.ToString(), "Naomi", null, "Director", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        }
        public async Task ReviewAsync()
        {
            await _agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
                JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    ["llmModel"] = JsonSerializer.SerializeToElement("test") }))), _context, default);
            await _agent.ReviewAssignedProjectsAsync(_context, default);
        }
    }
}
