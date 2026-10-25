using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProducerKickoffTests
{
    [Theory]
    [InlineData("game-producer", "Active", true, true, true)]
    [InlineData("game-engineer", "Active", true, true, false)]
    [InlineData("game-producer", "Inactive", true, true, false)]
    [InlineData("game-producer", "Active", false, true, false)]
    [InlineData("game-producer", "Active", true, false, false)]
    public async Task KickoffRequiresTheApprovedTeamsActiveProducer(string role, string presence,
        bool approved, bool authenticatedSender, bool expected)
    {
        var sender = Guid.NewGuid(); var team = Guid.NewGuid(); var resource = Guid.NewGuid();
        var conversation = Guid.NewGuid(); const string key = "intake-state";
        var state = new GameDirectorOperatingState { StaffingRequestId = resource,
            AcceptedVision = new(1, "pitch", "Accepted pitch", Guid.NewGuid(), Guid.NewGuid(), "sha", conversation,
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var index = new GameDirectorPortfolioIndex { Projects = [new(key, null, conversation, null, null,
            "Game", GameDirectorPhase.TeamStaffingPending, DateTimeOffset.UtcNow)] };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active",
                    new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(), r.StateKey == key
                        ? JsonSerializer.SerializeToElement(state) : JsonSerializer.SerializeToElement(index), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
            .RegisterCapability<JsonElement, JsonElement>("platform.management.resource-change.read.v1", (_, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement(new { requests = new[] { new { id = resource, status = approved ? "Approved" : "Pending", teamId = team } } })))
            .RegisterCapability<JsonElement, JsonElement>("platform.team-roster.read.v2", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new {
                team = new { teamId = team.ToString(), members = new[] { new { employeeId = sender.ToString(), presence,
                    isAvailable = true, agentInstallationId = Guid.NewGuid(), declaredRoleKeys = new[] { role }, effectiveCapabilities = new[] { "work.execution.run" } } } }
            })));
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), sender.ToString(),
            "I have joined as Producer.", authenticatedSender ? new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = sender.ToString() } : [], Guid.NewGuid(), 1, Guid.NewGuid());
        var result = await new VideoGameDirectorAgent().FindProducerKickoffsAsync(incoming, runtime.CreateContext(), default);
        Assert.Equal(expected ? 1 : 0, result.Count);
        if (expected) Assert.Equal(resource, result[0].State.StaffingRequestId);
    }
}