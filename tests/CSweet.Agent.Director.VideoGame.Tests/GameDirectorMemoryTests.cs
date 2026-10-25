using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Memory;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GameDirectorMemoryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RecallUsesDeclaredBrokerCapabilitiesAndRecordsAllThreeScopes()
    {
        var scopes = new List<MemoryPartition>();
        var uses = new List<MemoryUse>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<CSweetMemoryCommand, MemoryCandidate[]>(CSweetMemoryCapabilities.Query, (command, _) =>
            {
                Assert.Equal("search", command.Operation);
                var request = command.Payload.Deserialize<MemorySearchRequest>(JsonOptions)!;
                scopes.Add(request.Partition);
                return Task.FromResult(new[] { new MemoryCandidate(Guid.NewGuid(), MemoryLayer.Episodic,
                    $"{request.Scope} fixture evidence", 1, MemoryTrustTier.Authoritative,
                    MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, null, null, [], "fixture") });
            })
            .RegisterCapability<CSweetMemoryCommand, MemoryWriteResult>(CSweetMemoryCapabilities.Write, (command, _) =>
            {
                Assert.Equal("record-use", command.Operation);
                var use = command.Payload.Deserialize<MemoryUse>(JsonOptions)!;
                uses.Add(use);
                return Task.FromResult(new MemoryWriteResult(use.Id, true));
            });
        var employee = Guid.NewGuid().ToString("D");
        var user = Guid.NewGuid().ToString("D");
        var context = runtime.CreateContext(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employee, "Video Game Director", null, "Video Game Director", null, [], null, user, "CEO"));

        var rendered = await new VideoGameDirectorAgent().RecallApprovedMemoryAsync(user, context, default);

        Assert.Equal(3, scopes.Count);
        Assert.All(scopes, partition => Assert.Equal("csweet", partition.ApplicationId));
        Assert.Equal(scopes, uses.Select(x => x.Partition));
        Assert.Contains("User fixture evidence", rendered);
        Assert.Contains("Agent fixture evidence", rendered);
        Assert.Contains("Tenant fixture evidence", rendered);
    }

    [Fact]
    public async Task MissingBrokerGrantLeavesMemoryUnavailable()
    {
        var employee = Guid.NewGuid().ToString("D");
        var user = Guid.NewGuid().ToString("D");
        var context = new AgentTestRuntime().CreateContext(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employee, "Video Game Director", null, "Video Game Director", null, [], null, user, "CEO"));
        Assert.Equal("No approved memory was available.",
            await new VideoGameDirectorAgent().RecallApprovedMemoryAsync(user, context, default));
    }
}
