using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class HandoffReviewTests
{
    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Blocked", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Active", false)]
    [InlineData("Summarizing", false)]
    [InlineData("Completed", false)]
    public async Task ReviewsReadCurrentStatusAndSurfaceTerminalFailures(string status, bool failed)
    {
        var id = Guid.NewGuid();
        var participant = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Naomi", "Initiator");
        var session = new AgentCoordinationSession(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            participant, participant, "Brief", "Refine", [], status, 2, 2, null, false,
            "Memory validation failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var reads = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, AgentCoordinationSession>(
            "communication.coordination.read.v1", (request, _) =>
            {
                Assert.Equal(id, request.GetProperty("sessionId").GetGuid());
                reads++;
                return Task.FromResult(session);
            });
        var state = new GameDirectorOperatingState { HandoffSessionId = id };
        if (failed)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                VideoGameDirectorAgent.CheckProducerHandoffAsync(state, runtime.CreateContext(), default));
            Assert.Contains($"is {status}", error.Message);
            Assert.Contains("Memory validation failed", error.Message);
        }
        else await VideoGameDirectorAgent.CheckProducerHandoffAsync(state, runtime.CreateContext(), default);
        Assert.Equal(1, reads);
    }
}
