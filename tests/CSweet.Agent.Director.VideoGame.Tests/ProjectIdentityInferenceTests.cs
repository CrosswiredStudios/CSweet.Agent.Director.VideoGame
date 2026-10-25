using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectIdentityInferenceTests
{
    private const string Description = "Turn every shot into a song. Break tuned bricks, build chiptune melodies, and make each run your own performance.";

    [Theory]
    [InlineData("# RESONANCE — Game Pitch\nA browser brick-breaker composes chiptunes.", "RESONANCE")]
    [InlineData("# Creative Brief\nThe game is RESONANCE: Afterlight, a musical brick-breaker.", "RESONANCE: Afterlight")]
    [InlineData("A browser brick-breaker where players compose chiptunes. No game name has been chosen.", "Resonance")]
    public async Task NewProposalUsesInferredProductIdentityBeforeCoordinationAndReusesItAfterInterruption(string markdown, string title)
    {
        var journey = await Journey.CreateAsync(JsonSerializer.Serialize(new { title, description = Description }));
        var state = journey.State with { AcceptedVision = journey.Vision with { Markdown = markdown }, WorkingTitle = "Stale document heading" };
        for (var retry = 0; retry < 2; retry++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journey.Agent.EnsureProjectFoundationAsync(
                state, 1, state.TeamId, state.ProducerEmployeeId!.Value, Guid.NewGuid(), journey.Context, default));
            Assert.Equal("Coordination response interrupted", error.Message);
        }
        Assert.Equal(1, journey.Models);
        Assert.Equal(2, journey.Handoffs.Count);
        Assert.Equal(journey.Handoffs[0].IdempotencyKey, journey.Handoffs[1].IdempotencyKey);
        Assert.All(journey.Handoffs, handoff => {
            var payload = handoff.Artifact!.Payload;
            var plan = payload.Deserialize<WorkstreamPlanProposalV2Request>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(title, plan.Name);
            Assert.Equal(Description, plan.Outcome);
            Assert.Equal(title, plan.ProfileData.GetProperty("workingTitle").GetString());
            Assert.Equal(title, payload.GetProperty("projectIdentity").GetProperty("title").GetString());
            Assert.Equal(Description, payload.GetProperty("projectIdentity").GetProperty("description").GetString());
            var evidence = Assert.Single(plan.InitialEvidence);
            Assert.Equal(state.AcceptedVision!.ArtifactRevisionId, evidence.RevisionId);
            Assert.Equal(state.AcceptedVision.ArtifactRevisionHash, evidence.Digest);
        });
        using var source = JsonDocument.Parse(journey.ModelRequests.Single().GetProperty("messages")[1].GetProperty("text").GetString()!);
        Assert.Equal(markdown, source.RootElement.GetProperty("acceptedVision").GetString());
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"title\":\"RESONANCE — Game Pitch\",\"description\":\"Musical arcade game.\"}")]
    [InlineData("{\"title\":\"Game Pitch\",\"description\":\"Musical arcade game.\"}")]
    [InlineData("{\"title\":\"\",\"description\":\"Musical arcade game.\"}")]
    [InlineData("{\"title\":\"RESONANCE\"}")]
    [InlineData("{\"title\":12,\"description\":\"Musical arcade game.\"}")]
    [InlineData("{\"title\":\"RESONANCE\\nGame Pitch\",\"description\":\"Musical arcade game.\"}")]
    public async Task InvalidIdentityGetsOneCorrectionBeforeItCanBeUsed(string invalid)
    {
        var journey = await Journey.CreateAsync(invalid, JsonSerializer.Serialize(new { title = "RESONANCE", description = Description }));
        var identity = await journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default);
        Assert.Equal("RESONANCE", identity.Title);
        Assert.Equal(Description, identity.Description);
        Assert.Equal(2, journey.Models);
        Assert.Empty(journey.Handoffs);
        Assert.Equal(identity, await journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default));
        Assert.Equal(2, journey.Models);
    }

    [Fact]
    public async Task RepeatedInvalidOutputBlocksProposalAndDuplicateWakesDoNotRestartCorrectionBudget()
    {
        var journey = await Journey.CreateAsync("{}", "{}");
        for (var retry = 0; retry < 2; retry++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journey.Agent.EnsureProjectFoundationAsync(
                journey.State, 1, journey.State.TeamId, journey.State.ProducerEmployeeId!.Value, Guid.NewGuid(), journey.Context, default));
            Assert.Contains("No project proposal was started", error.Message);
        }
        Assert.Equal(2, journey.Models);
        Assert.Empty(journey.Handoffs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedFieldsAreRejected(bool oversizedTitle)
    {
        var invalid = JsonSerializer.Serialize(new { title = oversizedTitle ? new string('A', 241) : "RESONANCE",
            description = oversizedTitle ? Description : new string('A', 1001) });
        var journey = await Journey.CreateAsync(invalid, invalid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default));
        Assert.Equal(2, journey.Models);
    }

    [Fact]
    public async Task ExactAcceptedRevisionBindsCacheAndSavedIdentitySurvivesRestartWithoutModelConfiguration()
    {
        var journey = await Journey.CreateAsync(
            JsonSerializer.Serialize(new { title = "RESONANCE", description = Description }),
            JsonSerializer.Serialize(new { title = "RESONANCE: Afterlight", description = Description }));
        var first = await journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default);
        Assert.Equal(first, await new VideoGameDirectorAgent().InferProjectIdentityAsync(journey.Vision, journey.Context, default));
        var second = await journey.Agent.InferProjectIdentityAsync(journey.Vision with { ArtifactRevisionId = Guid.NewGuid() }, journey.Context, default);
        Assert.NotEqual(first.Title, second.Title);
        Assert.Equal(2, journey.Models);
    }

    [Fact]
    public async Task ConfigurationCorrectionCanRetryFailedInference()
    {
        var journey = await Journey.CreateAsync("{}", "{}", JsonSerializer.Serialize(new { title = "RESONANCE", description = Description }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default));
        await journey.ConfigureAsync("corrected-model");
        var identity = await journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default);
        Assert.Equal("RESONANCE", identity.Title);
        Assert.Equal(3, journey.Models);
    }

    [Fact]
    public async Task AttemptKeysStayBoundedAcrossModelsAndAcceptedLegacyIdentityIsReused()
    {
        var journey = await Journey.CreateAsync(JsonSerializer.Serialize(new { title = "RESONANCE", description = Description }));
        await journey.ConfigureAsync(new string('x', 500));
        var identity = await journey.Agent.InferProjectIdentityAsync(journey.Vision, journey.Context, default);
        var vision = journey.Vision;
        var legacyKey = "game-project-identity:v1:" + VideoGameDirectorAgent.Digest(JsonSerializer.Serialize(new {
            vision.ArtifactId, vision.ArtifactRevisionId, vision.ArtifactRevisionHash, vision.Digest,
            contentDigest = VideoGameDirectorAgent.Digest(vision.Markdown)
        }));
        Assert.Contains(legacyKey, journey.States.Keys);
        Assert.Contains(journey.States.Keys, x => x.StartsWith("game-project-identity-attempt:v1:") && x.Length <= 128);
        Assert.All(journey.States.Keys, key => Assert.True(AgentOperatingStateKey.IsValid(key)));
        await journey.ConfigureAsync("a-different-model");
        Assert.Equal(identity, await journey.Agent.InferProjectIdentityAsync(vision, journey.Context, default));
        Assert.Equal(1, journey.Models);
    }

    private sealed class Journey
    {
        public VideoGameDirectorAgent Agent { get; } = new();
        public AgentRuntimeContext Context { get; private set; } = null!;
        public int Models { get; private set; }
        public List<JsonElement> ModelRequests { get; } = [];
        public List<StartAgentCoordinationRequest> Handoffs { get; } = [];
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public AcceptedGameVision Vision { get; } = new(1, "pitch-digest", "# RESONANCE — Game Pitch\nA musical brick-breaker.",
            Guid.NewGuid(), Guid.NewGuid(), "accepted-hash", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        public GameDirectorOperatingState State => new() { AcceptedVision = Vision, TeamId = _team, ProducerEmployeeId = _producer, CreativeDirectorEmployeeId = Guid.NewGuid() };
        private readonly Guid _team = Guid.NewGuid(), _producer = Guid.NewGuid(), _provider = Guid.NewGuid();

        public static async Task<Journey> CreateAsync(params string[] responses)
        {
            var journey = new Journey();
            var states = journey.States;
            var pending = new Queue<string>(responses);
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId,
                        r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                    journey.Models++; journey.ModelRequests.Add(r.Clone());
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { text = pending.Dequeue(), role = "assistant" }));
                })
                .RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart, (r, _) => {
                    // The accepted identity must be durable before any external proposal handoff.
                    Assert.Contains(states.Keys, x => x.StartsWith("game-project-identity:v1:") && !x.Contains(":attempt:"));
                    journey.Handoffs.Add(r);
                    throw new InvalidOperationException("Coordination response interrupted");
                });
            journey.Context = runtime.CreateContext(identity: new(Guid.NewGuid().ToString(), "Director", null, "Director", null, [], null, Guid.NewGuid().ToString(), "Owner"));
            await journey.ConfigureAsync("test-model");
            return journey;
        }

        public async Task ConfigureAsync(string model)
        {
            var result = await Agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
                JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(_provider), ["llmModel"] = JsonSerializer.SerializeToElement(model)
                }))), Context, default);
            Assert.True(result.Succeeded, result.Error);
        }
    }
}
