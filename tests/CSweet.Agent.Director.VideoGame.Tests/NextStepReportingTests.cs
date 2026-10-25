using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class NextStepReportingTests
{
    [Theory]
    [InlineData(GameDirectorPhase.Discovery, false, false, "CEO provides initial game direction")]
    [InlineData(GameDirectorPhase.HighLevelReview, false, false, "CEO decides")]
    [InlineData(GameDirectorPhase.ProjectSetup, false, false, "Producer becomes active")]
    [InlineData(GameDirectorPhase.ProjectSetup, true, false, "share the accepted pitch")]
    [InlineData(GameDirectorPhase.WorkstreamPlanPending, true, false, "share the accepted pitch")]
    [InlineData(GameDirectorPhase.DetailedDesign, true, false, "start the production-brief session")]
    [InlineData(GameDirectorPhase.DetailedDesign, true, true, "converge on the shared production brief")]
    public void EveryPhaseNamesItsNextStep(GameDirectorPhase phase, bool producer, bool session, string expected)
    {
        var state = new GameDirectorOperatingState
        {
            Phase = phase,
            ProducerEmployeeId = producer ? Guid.NewGuid() : null,
            HandoffSessionId = session ? Guid.NewGuid() : null
        };
        Assert.Contains(expected, GameDirectorNextStep.Describe(state));
    }

    [Fact]
    public void RepeatedFailuresOfTheSameStepAccumulateAndEscalateOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var step = ReconcileStallPolicy.Fingerprint("share docs");
        var stall = ReconcileStallPolicy.Record(null, step, "boom", now);
        stall = ReconcileStallPolicy.Record(stall, step, "boom with another id", now.AddMinutes(5));
        Assert.False(ReconcileStallPolicy.ShouldEscalate(stall));
        stall = ReconcileStallPolicy.Record(stall, step, "boom", now.AddMinutes(10));
        Assert.Equal(3, stall.Failures);
        Assert.Equal(now, stall.FirstFailedAt);
        Assert.True(ReconcileStallPolicy.ShouldEscalate(stall));
        Assert.False(ReconcileStallPolicy.ShouldEscalate(stall with { Escalated = true }));
        var otherStep = ReconcileStallPolicy.Record(stall, ReconcileStallPolicy.Fingerprint("next"), "x", now.AddMinutes(15));
        Assert.Equal(1, otherStep.Failures);
        Assert.Contains("Next step: share docs", ReconcileStallPolicy.WaitingReason("share docs", stall, true));
    }

    [Fact]
    public void SummariesAreSingleLineAndBounded()
    {
        Assert.Equal("a b c", ReconcileStallPolicy.Summarize("a\n  b\r\nc"));
        Assert.Equal(300, ReconcileStallPolicy.Summarize(new string('x', 1000)).Length);
    }

    [Theory]
    [InlineData("""{"accept":true,"guidance":"ok","suggestedMarkdown":""}""")]
    [InlineData("```json\n{\"accept\":true,\"guidance\":\"ok\",\"suggestedMarkdown\":\"\"}\n```")]
    [InlineData("""{"accept":true,"guidance":"ok","suggestedMarkdown":""}<think>trailing</think>""")]
    public void DirectorReviewToleratesProviderWrapping(string response)
    {
        var review = PitchProtocol.ParseDirectorReview(response);
        Assert.NotNull(review);
        Assert.True(review!.Accept);
        Assert.Equal("ok", review.Guidance);
    }

    [Fact]
    public async Task PersistentReconcileFailureEscalatesToTheCeoExactlyOnce()
    {
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var messages = new Dictionary<string, string>();
        var ceo = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) =>
                {
                    var revision = (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1;
                    var written = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations,
                        r.AttentionReviewId, r.Payload, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    states[r.StateKey] = written;
                    return Task.FromResult(written);
                })
            .RegisterCapability<JsonElement, JsonElement>("communication.chat.read.v1", (_, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement(new { chats = Array.Empty<object>() })))
            .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (_, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    succeeded = true, message = "Created", chat = new { id = Guid.NewGuid(), isDirect = true, isPrivate = true,
                        participants = new[] { new { organizationUserId = ceo, employeeType = "Human", displayName = "CEO", role = "Member" } } }
                })))
            .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) =>
            {
                var content = r.GetProperty("content").GetString()!;
                messages[r.GetProperty("idempotencyKey").GetString()!] = content;
                return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, r.GetProperty("chatId").GetGuid(),
                    Guid.NewGuid(), "Director", "Agent", content, DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
            });
        var context = runtime.CreateContext(Guid.NewGuid().ToString(), identity: new AgentIdentity(Guid.NewGuid().ToString(),
            "Naomi", null, "Video Game Director", null, [], null, ceo.ToString(), "CEO"));
        var agent = new VideoGameDirectorAgent();
        var conversation = Guid.NewGuid();

        for (var attempt = 0; attempt < 5; attempt++)
            await agent.ReportReconcileFailureAsync(conversation,
                new InvalidOperationException($"Handoff task rejected ({Guid.NewGuid()})."), context, default);

        var message = Assert.Single(messages.Values);
        Assert.Contains("Next step:", message);
        Assert.Contains("Handoff task rejected", message);
        var stall = states[ReconcileStallPolicy.StateKey(conversation)].Payload.Deserialize<ReconcileStall>()!;
        Assert.Equal(5, stall.Failures);
        Assert.True(stall.Escalated);

        await VideoGameDirectorAgent.ClearReconcileStallAsync(conversation, context, default);
        stall = states[ReconcileStallPolicy.StateKey(conversation)].Payload.Deserialize<ReconcileStall>()!;
        Assert.Equal(0, stall.Failures);
        Assert.False(stall.Escalated);
    }
}
