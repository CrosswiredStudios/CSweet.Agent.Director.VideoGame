using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class StaffingEventTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyLostCapacityRequestsReplacementAndRepeatedWakesReuseIt(bool previouslyStaffed)
    {
        var team = Guid.NewGuid(); var source = Guid.NewGuid(); var director = Guid.NewGuid();
        var resource = JsonSerializer.Deserialize<ResourceChangeRequestResponse>(JsonSerializer.Serialize(new {
            id = source, teamId = team, status = "Approved" }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var state = new GameDirectorOperatingState { TeamId = team, StaffingRequestId = source,
            ProducerEmployeeId = previouslyStaffed ? Guid.NewGuid() : null,
            AcceptedVision = new(1, "vision", "# Game", Guid.NewGuid(), Guid.NewGuid(), "hash", Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        StaffingReplenishmentResponse? saved = null; var submissions = 0;
        var runtime = new AgentTestRuntime();
        if (previouslyStaffed)
        {
            runtime.RegisterCapability<StaffingReplenishmentReadRequest, StaffingReplenishmentReadResponse>(
                "platform.management.staffing-replenishment.read.v1", (_, _) => Task.FromResult(new StaffingReplenishmentReadResponse(saved is null ? [] : [saved])))
                .RegisterCapability<StaffingReplenishmentProposalRequest, StaffingReplenishmentResponse>(
                    "platform.management.staffing-replenishment.propose.v1", (r, _) => {
                        submissions++;
                        Assert.Equal(source, r.SourceResourceChangeRequestId);
                        Assert.Equal(VideoGameRoleKeys.Producer, Assert.Single(r.Gaps).RoleKey);
                        saved = new(Guid.NewGuid(), Guid.NewGuid(), director, Guid.NewGuid(), Guid.NewGuid(), source, team,
                            state.AcceptedVision.ConversationId, r.Gaps, r.OperationalImpact, r.InterimControls, r.DecisionFingerprint,
                            "Pending", null, DateTimeOffset.UtcNow, null);
                        return Task.FromResult(saved);
                    });
        }
        // With initial hiring, no replenishment capabilities exist: even a read would fail this test.
        var context = runtime.CreateContext();
        var missing = VideoGameDirectorAgent.BuildRequiredStudioRoles(director);
        await VideoGameDirectorAgent.EnsureStaffingReplacementAsync(state, resource, missing, context, default);
        await VideoGameDirectorAgent.EnsureStaffingReplacementAsync(state, resource, missing, context, default);
        Assert.Equal(previouslyStaffed ? 1 : 0, submissions);
    }

    [Fact]
    public void InitialHiringAndDifferentTeamsAreNotLostCapacity()
    {
        var team = Guid.NewGuid();
        var state = new GameDirectorOperatingState { TeamId = team };
        Assert.False(VideoGameDirectorAgent.WasPreviouslyStaffed(state, team, VideoGameRoleKeys.Producer));
        state = state with { ProducerEmployeeId = Guid.NewGuid() };
        Assert.True(VideoGameDirectorAgent.WasPreviouslyStaffed(state, team, VideoGameRoleKeys.Producer));
        Assert.False(VideoGameDirectorAgent.WasPreviouslyStaffed(state, Guid.NewGuid(), VideoGameRoleKeys.Producer));
        Assert.False(VideoGameDirectorAgent.WasPreviouslyStaffed(state, team, VideoGameRoleKeys.TechnicalDirector));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttentionRecoversOnlyPendingRequestsAddressedToThisDirector(bool addressedToDirector)
    {
        var director = Guid.NewGuid(); var request = Guid.NewGuid(); Guid? readId = null;
        var pending = JsonSerializer.Deserialize<ResourceChangeRequestResponse>(JsonSerializer.Serialize(new
        { id = request, status = "Pending", managerOrganizationUserId = addressedToDirector ? director : Guid.NewGuid(),
          teamId = Guid.NewGuid(), workstreamId = Guid.NewGuid() }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var runtime = new AgentTestRuntime().RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(
            "platform.management.resource-change.read.v1", (r, _) => {
                if (r.RequestId.HasValue) { readId = r.RequestId; return Task.FromResult(new ResourceChangeReadResponse([])); }
                return Task.FromResult(new ResourceChangeReadResponse([pending]));
            });
        var context = runtime.CreateContext(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(director.ToString("D"), "Director", null, null, null, [], null, null, null));
        await VideoGameDirectorAgent.ReviewPendingStaffingAsync(context, default);
        Assert.Equal(addressedToDirector ? request : (Guid?)null, readId);
    }
    [Fact]
    public async Task CamelCaseHostEventReadsTheAuthoritativeRequest()
    {
        var director = Guid.NewGuid(); var request = Guid.NewGuid(); Guid? readId = null;
        var runtime = new AgentTestRuntime().RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(
            "platform.management.resource-change.read.v1", (r, _) =>
            { readId = r.RequestId; return Task.FromResult(new ResourceChangeReadResponse([])); });
        var context = runtime.CreateContext(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(director.ToString("D"), "Director", null, null, null, [], null, null, null));
        var payload = JsonSerializer.SerializeToElement(new { requestId = request,
            managerOrganizationUserId = director, teamId = Guid.NewGuid(), workstreamId = Guid.NewGuid(), status = "Pending" });
        await new VideoGameDirectorAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(),
            ManagementEvents.ResourceChangeRequested, payload, DateTimeOffset.UtcNow), context, default);
        Assert.Equal(request, readId);
    }

    [Fact]
    public void AssignedProducerCanBeReviewedWithoutChangingTheTeamLead()
    {
        var director = Guid.NewGuid(); var producer = Guid.NewGuid(); var installation = Guid.NewGuid();
        var member = new AgentTeammate(producer.ToString("D"), "Producer", "Agent", null, null, "DirectReport", "Available")
            { AgentInstallationId = installation, DeclaredRoleKeys = [VideoGameRoleKeys.Producer] };
        var roster = new AgentTeamContext(Guid.NewGuid().ToString("D"), "game", "Game", 1, director.ToString("D"), "Director", [member], [], 1, false);
        Assert.True(VideoGameDirectorAgent.CanReviewProducerCapacity(roster, director, producer, installation, director));
        Assert.False(VideoGameDirectorAgent.CanReviewProducerCapacity(roster, director, producer, Guid.NewGuid(), director));
        Assert.False(VideoGameDirectorAgent.CanReviewProducerCapacity(roster, director, producer, installation, Guid.NewGuid()));
        Assert.False(VideoGameDirectorAgent.CanReviewProducerCapacity(roster with { Members = [] }, director, producer, installation, director));
        Assert.False(VideoGameDirectorAgent.CanReviewProducerCapacity(roster with { Members = [member with { DeclaredRoleKeys = [] }] }, director, producer, installation, director));
    }
}
