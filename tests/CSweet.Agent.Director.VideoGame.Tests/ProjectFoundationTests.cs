using CSweet.Agent.SDK;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectFoundationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedReplyAfterApprovalRecoversTheExactCreatedProjectWithoutReplayingApproval(bool proposalAlreadyIndexed)
    {
        var team = Guid.NewGuid(); var producer = Guid.NewGuid(); var director = Guid.NewGuid();
        var sessionId = Guid.NewGuid(); var proposalId = Guid.NewGuid(); var projectId = Guid.NewGuid();
        var state = new GameDirectorOperatingState { TeamId = team, ProducerEmployeeId = producer,
            WorkstreamProposalSessionId = sessionId, WorkstreamProposalId = proposalAlreadyIndexed ? proposalId : null,
            AcceptedVision = new(1, "digest", "# Game", Guid.NewGuid(), Guid.NewGuid(), "hash",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, AgentCoordinationSession>("communication.coordination.read.v1", (_, _) => Task.FromResult(
                new AgentCoordinationSession(sessionId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    new(director, Guid.NewGuid(), "Director", "Initiator"), new(producer, Guid.NewGuid(), "Producer", "Target"),
                    "Project", "Create", [], "Failed", 5, 3, director, false, "Response interrupted", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    [new(Guid.NewGuid(), 1, producer, "Continue", "Submitted", DateTimeOffset.UtcNow,
                        new("video-game.project-foundation.proposal.v1", "1.0", "digest", 1, true,
                            JsonSerializer.SerializeToElement(new { proposalId }), "receipt"))])))
            .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1", (r, _) => {
                Assert.Equal(proposalId, r.GetProperty("proposalId").GetGuid());
                return Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { proposalId, status = "Approved", requesterId = producer,
                    decision = new { decision = "Approve", projectId } } })); })
            .RegisterCapability<ReadPortfolioRequest, JsonElement>("platform.management.portfolio.read.v1", (r, _) => {
                Assert.Equal(projectId, Assert.Single(r.WorkstreamIds!));
                return Task.FromResult(JsonSerializer.SerializeToElement(new { workstreams = new[] {
                    new { workstream = new { id = projectId, name = "Game", accountableManagerOrganizationUserId = producer }, activeTeam = new { teamId = team } } } })); })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
                    r.Status, r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations,
                    r.AttentionReviewId, r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var context = runtime.CreateContext(identity: new(director.ToString(), "Director", null, "Director", null, [], null, null, null));
        var result = await new VideoGameDirectorAgent().EnsureProjectFoundationAsync(state, 1, team, producer, Guid.NewGuid(), context, default);
        Assert.True(result.Ready);
        Assert.Equal(projectId, result.State.WorkstreamId);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Blocked")]
    [InlineData("Cancelled")]
    public async Task TerminalProposalSessionRetainsOwnedWaitInsteadOfFailingTheCallbackOrStartingAgain(string status)
    {
        var team = Guid.NewGuid(); var producer = Guid.NewGuid(); var director = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        var state = new GameDirectorOperatingState {
            TeamId = team, ProducerEmployeeId = producer, WorkstreamProposalSessionId = sessionId,
            AcceptedVision = new(1, "digest", "# Game", Guid.NewGuid(), Guid.NewGuid(), "hash",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        var session = new AgentCoordinationSession(sessionId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(director, Guid.NewGuid(), "Director", "Initiator"), new(producer, Guid.NewGuid(), "Producer", "Target"),
            "Project proposal", "Create", [], status, 2, 1, producer, false,
            "Proposal outcome is invalid", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var reads = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, AgentCoordinationSession>("communication.coordination.read.v1",
            (r, _) => { Assert.Equal(sessionId, r.GetProperty("sessionId").GetGuid()); reads++; return Task.FromResult(session); })
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
                    r.Status, r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations,
                    r.AttentionReviewId, r.Payload, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)));
        var context = runtime.CreateContext(identity: new(director.ToString(), "Director", null, "Director", null, [], null, null, null));
        var result = await new VideoGameDirectorAgent().EnsureProjectFoundationAsync(state, 1, team, producer, Guid.NewGuid(), context, default);
        Assert.False(result.Ready);
        Assert.Equal(status, result.State.ProposalWait!.Status);
        Assert.Contains("Proposal outcome is invalid", result.State.ProposalWait.Reason);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ActiveProposalSessionWaitsWithoutCreatingAnotherSession()
    {
        var team = Guid.NewGuid(); var producer = Guid.NewGuid(); var director = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        var state = new GameDirectorOperatingState {
            TeamId = team, WorkstreamProposalSessionId = sessionId,
            AcceptedVision = new(1, "digest", "# Game", Guid.NewGuid(), Guid.NewGuid(), "hash",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, AgentCoordinationSession>("communication.coordination.read.v1",
            (_, _) => Task.FromResult(new AgentCoordinationSession(sessionId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new(director, Guid.NewGuid(), "Director", "Initiator"), new(producer, Guid.NewGuid(), "Producer", "Target"),
                "Project proposal", "Create", [], "Active", 2, 1, producer, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])));
        var context = runtime.CreateContext(identity: new(director.ToString(), "Director", null, "Director", null, [], null, null, null));
        var result = await new VideoGameDirectorAgent().EnsureProjectFoundationAsync(state, 1, team, producer, Guid.NewGuid(), context, default);
        Assert.False(result.Ready);
        Assert.Equal(sessionId, result.State.WorkstreamProposalSessionId);
    }

    [Fact]
    public async Task ApprovedProjectCanHandoffWithoutBoardCreationAuthority()
    {
        var team = Guid.NewGuid(); var producer = Guid.NewGuid();
        var state = new GameDirectorOperatingState {
            WorkstreamId = Guid.NewGuid(), TeamId = team, WorkingTitle = "Grid Blast",
            AcceptedVision = new(1, "digest", "# Grid Blast", Guid.NewGuid(), Guid.NewGuid(), "hash",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        // No board capability is registered: the Director is a supervisor, not the board manager.
        var context = new AgentTestRuntime().CreateContext(identity: new AgentIdentity(Guid.NewGuid().ToString(),
            "Director", null, "Video Game Director", null, [], null, Guid.NewGuid().ToString(), "Owner"));
        var result = await new VideoGameDirectorAgent().EnsureProjectFoundationAsync(state, 1, team,
            producer, Guid.NewGuid(), context, default);
        Assert.True(result.Ready);
        Assert.Null(result.State.BoardId);
    }
}
