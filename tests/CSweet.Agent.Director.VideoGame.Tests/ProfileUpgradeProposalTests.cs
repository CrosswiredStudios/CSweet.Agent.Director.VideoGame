using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProfileUpgradeProposalTests
{
    [Theory]
    [InlineData(4, false, 1)]
    [InlineData(5, false, 1)]
    [InlineData(6, false, 1)]
    [InlineData(7, false, 0)]
    [InlineData(4, true, 0)]
    public async Task ReconciliationProposesOnceAndSkipsCurrentOrAlreadyBoardedProjects(int version, bool existingBoard, int expected)
    {
        var id = Guid.NewGuid(); var calls = new List<WorkstreamChangeProposalRequest>();
        var workstream = new WorkstreamDetail(id, "Game", "Deliver game", [], "Planning", "Active", Guid.NewGuid(),
            null, null, null, "video-game-production.v2", version, JsonSerializer.SerializeToElement(new { }), "old-digest", 7);
        var stored = new Dictionary<string, AgentOperatingStateResponse>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) => Task.FromResult(workstream))
            .RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkManagementCapabilityNames.BoardRead,
                (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>(existingBoard
                    ? [new WorkBoardSummary(Guid.NewGuid(), "Game", "", false, false, 1, []) { WorkstreamId = id }] : []))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var saved = new AgentOperatingStateResponse(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes, request.DecisionFingerprint,
                        request.OpenCommitmentCorrelations, request.AttentionReviewId, request.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved; return Task.FromResult(saved);
                })
            .RegisterCapability<WorkstreamChangeProposalRequest, MutationResponse>(PlatformCapabilities.WorkstreamChangePropose,
                (request, _) => { calls.Add(request); return Task.FromResult(new MutationResponse(false, 7, Guid.NewGuid(), "Pending approval")); });
        await VideoGameDirectorAgent.ProposeExecutionProfileUpgradeAsync(id, runtime.CreateContext(), default);
        await VideoGameDirectorAgent.ProposeExecutionProfileUpgradeAsync(id, runtime.CreateContext(), default);
        Assert.Equal(expected, calls.Count);
        if (expected == 1)
        {
            var request = calls[0]; Assert.Equal(7, request.ExpectedRevision);
            Assert.Equal(7, request.Changes.GetProperty("profileUpgrade").GetProperty("version").GetInt32());
            Assert.Equal(64, request.Changes.GetProperty("profileUpgrade").GetProperty("definitionDigest").GetString()!.Length);
            Assert.Single(stored);
        }
    }
}
