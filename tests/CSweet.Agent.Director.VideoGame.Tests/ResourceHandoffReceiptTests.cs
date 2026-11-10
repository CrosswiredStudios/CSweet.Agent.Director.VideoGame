using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ResourceHandoffReceiptTests
{
    [Theory]
    [InlineData("valid", "Completed")] [InlineData("wrong-project", "Blocked")] [InlineData("wrong-requester", "Blocked")]
    public async Task Director_closes_the_handoff_using_current_resource_state_without_restarting_planning(string scenario, string disposition)
    {
        var project = Guid.NewGuid(); var specialist = Guid.NewGuid();
        var current = JsonSerializer.Deserialize<ResourceRequestState>("{}")! with { Id = Guid.NewGuid(),
            WorkstreamId = scenario == "wrong-project" ? Guid.NewGuid() : project,
            RequesterId = scenario == "wrong-requester" ? Guid.NewGuid() : specialist,
            Status = "WaitingDependency", Condition = "Configure catalog read access.", Plan = null };
        var artifact = new AgentCoordinationArtifactSubmission("platform.resource.requested.v1", "1.0", current.Id.ToString("N"), 1, true,
            JsonSerializer.SerializeToElement(current with { Status = "Ready", WorkstreamId = project, RequesterId = specialist }));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 3, 2, "Request build capacity", "Record resource", [],
            new(Guid.NewGuid(), Guid.NewGuid(), "Director", "Director"), new(specialist, Guid.NewGuid(), "Technical Director", "Technical Director"), true,
            [new(Guid.NewGuid(), 1, specialist, "Completed", "The request is recorded", DateTimeOffset.UtcNow,
                new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "digest"))])
            { WorkContext = new(Guid.NewGuid(), project, null, null, null, null, null, Guid.NewGuid(), null, null) };
        var runtime = new AgentTestRuntime().RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
            (read, _) => { Assert.Equal(current.Id, read.RequestId); return Task.FromResult<IReadOnlyList<ResourceRequestState>>([current]); });
        var result = await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(request, runtime.CreateContext(), default);
        Assert.Equal(disposition, result.Disposition);
        if (scenario == "valid") { Assert.Contains("WaitingDependency", result.Content); Assert.Contains("catalog read access", result.Content); }
    }
}
