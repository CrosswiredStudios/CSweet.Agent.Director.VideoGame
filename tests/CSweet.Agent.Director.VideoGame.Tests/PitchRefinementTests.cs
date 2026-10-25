using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PitchRefinementTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, true, false)]
    public async Task BothProducerConfidenceAndCreativeAcceptanceAreRequired(bool producerReady, bool directorAccepts, bool expectedAcceptance, bool cached)
    {
        var producer = Guid.NewGuid(); var director = Guid.NewGuid(); var workstream = Guid.NewGuid(); var team = Guid.NewGuid();
        var session = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid(); var decisions = 0;
        var markdown = string.Join("\n", new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }.Select(x => $"## {x}\nDetail."));
        var state = new GameDirectorOperatingState { WorkstreamId = workstream, TeamId = team, ProducerEmployeeId = producer, CreativeDirectorEmployeeId = Guid.NewGuid(),
            AcceptedVision = new(1, "pitch", "Accepted arcade pitch", Guid.NewGuid(), Guid.NewGuid(), "pitch-sha", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var modelCalls = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,
                (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(
                new ArtifactDocument(document, "Planning", PitchProtocol.DocumentType, "InReview", revision, revision, null, null, null,
                    [new(revision, 1, null, markdown, "draft-hash", "Submitted", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)]) { WorkstreamId = workstream, TeamId = team }))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(cached || request.StateKey.StartsWith("director-creative-review:") ? new(Guid.NewGuid(), request.StateKey, "test", 1, "Active",
                    new Dictionary<string, string>(), [], "cached", [], Guid.NewGuid(), request.StateKey.StartsWith("director-creative-review:") ? CreativeReviewFixture.Accepted(state, document, revision, "draft-hash") : JsonSerializer.SerializeToElement(new DirectorReview(directorAccepts, "Answers and scope assessment", markdown)), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), request.StateKey, "test", 1, "Active",
                    new Dictionary<string, string>(), [], "review", [], Guid.NewGuid(), request.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) =>
            {
                modelCalls++; Assert.Equal(8192, request.GetProperty("maxOutputTokens").GetInt32());
                var prompt = request.GetProperty("messages")[0].GetProperty("text").GetString()!;
                Assert.Contains("never reproduce it", prompt);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(
                    new DirectorReview(directorAccepts, "Scope and criteria are sufficient.", ""), PitchProtocol.Json), role = "assistant" }));
            })
            .RegisterCapability<DecideArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactDecide,
                (request, _) => { decisions++; Assert.Equal(revision, request.RevisionId); return Task.FromResult(new ArtifactDocument(document, "Planning", PitchProtocol.DocumentType, "Approved", revision, null, revision, null, null, [])); });
        var request = new AgentCoordinationTurnRequest(session, 2, 2, "Pitch", "Refine", [],
            new(director, Guid.NewGuid(), "Director", "Director"), new(producer, Guid.NewGuid(), "Producer", "Producer"), false, [])
            { WorkContext = new(Guid.NewGuid(), workstream, team, Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
        var agent = new VideoGameDirectorAgent();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                ["llmModel"] = JsonSerializer.SerializeToElement("test-model") }))), runtime.CreateContext(), default);
        Assert.True(configured.Succeeded, configured.Error);
        var result = await agent.ReviewProducerPitchAsync(request, state,
            new("pitch", document, revision, "draft-hash", producerReady, producerReady ? [] : ["What is the release platform?"], "Planning assessment"), runtime.CreateContext(), default);
        Assert.Equal(AgentCoordinationDispositions.Continue, result.Disposition);
        var reply = result.Artifact!.Payload.Deserialize<PitchReply>(PitchProtocol.Json)!;
        Assert.Equal(expectedAcceptance, reply.Accepted); Assert.Equal(expectedAcceptance ? 1 : 0, decisions);
        Assert.Equal(cached ? 0 : 1, modelCalls);
        if (!cached) Assert.Equal("", reply.SuggestedMarkdown);
    }
}
