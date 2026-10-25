using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PromotedProjectStateTests
{
    private static AgentOperatingStateResponse Saved<T>(string key, T value) => new(Guid.NewGuid(), key, "test", 1,
        "Active", new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(value),
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task BoardlessStaffingUsesAuthenticatedProjectEvenWhenDiscoveryPointsToIntake()
    {
        var project = Guid.NewGuid(); var conversation = Guid.NewGuid(); var producer = Guid.NewGuid();
        var projectKey = VideoGameDirectorAgent.ProjectStateKey(project, null);
        var intakeKey = VideoGameDirectorAgent.ProjectStateKey(null, conversation);
        var exact = new GameDirectorOperatingState { WorkstreamId = project, IntakeConversationId = conversation,
            ProducerEmployeeId = producer, Phase = GameDirectorPhase.DetailedDesign };
        var index = new GameDirectorPortfolioIndex { Projects = [Entry(null, conversation, intakeKey)] };
        var runtime = new AgentTestRuntime().RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
            PlatformCapabilities.AgentOperatingStateRead, (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                r.StateKey == projectKey ? Saved(projectKey, exact) : r.StateKey == VideoGameDirectorAgent.PortfolioStateKey
                    ? Saved(r.StateKey, index) : Saved(r.StateKey, exact with { WorkstreamId = null }))));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 4, 4, "Refine brief", "Accept exact brief", [],
            new(Guid.NewGuid(), Guid.NewGuid(), "Director", "Director"), new(producer, Guid.NewGuid(), "Producer", "Producer"), false, [])
        { WorkContext = new(Guid.NewGuid(), project, Guid.NewGuid(), null, null, null, null, Guid.NewGuid(), null, null) };
        var selected = await new VideoGameDirectorAgent().ReadStateForCoordinationAsync(request, runtime.CreateContext(), default);
        Assert.Equal(project, selected.State.WorkstreamId);
        Assert.Equal(producer, selected.State.ProducerEmployeeId);
    }

    [Fact]
    public async Task ExplicitConversationFollowsPromotionInsteadOfReadingRetainedIntake()
    {
        var project = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var projectKey = VideoGameDirectorAgent.ProjectStateKey(project, null);
        var reads = new List<string>();
        var index = new GameDirectorPortfolioIndex { Projects = [Entry(project, conversation, projectKey)] };
        var runtime = new AgentTestRuntime().RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
            PlatformCapabilities.AgentOperatingStateRead, (r, _) => {
                reads.Add(r.StateKey);
                return Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey == VideoGameDirectorAgent.PortfolioStateKey
                    ? Saved(r.StateKey, index) : Saved(r.StateKey, new GameDirectorOperatingState { WorkstreamId = project })));
            });
        var selected = await new VideoGameDirectorAgent().ReadStateAsync(runtime.CreateContext(), default, conversationId: conversation);
        Assert.Equal(project, selected.State.WorkstreamId);
        Assert.DoesNotContain(VideoGameDirectorAgent.ProjectStateKey(null, conversation), reads);
    }

    [Fact]
    public void DelayedIntakeCannotDemotePromotedRouteOrChangeOtherProjects()
    {
        var project = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var promoted = Entry(project, conversation, VideoGameDirectorAgent.ProjectStateKey(project, null));
        var other = Entry(Guid.NewGuid(), Guid.NewGuid(), "other-project");
        IReadOnlyList<GameDirectorPortfolioEntry> current = [promoted, other];
        var merged = VideoGameDirectorAgent.MergePortfolioEntry(current,
            Entry(null, conversation, VideoGameDirectorAgent.ProjectStateKey(null, conversation)));
        Assert.Same(current, merged);
        Assert.Equal(promoted, merged[0]); Assert.Equal(other, merged[1]);
    }

    [Fact]
    public async Task ConcurrentPromotionSurvivesTheIndexConflictRetry()
    {
        var project = Guid.NewGuid(); var conversation = Guid.NewGuid(); var reads = 0; var writes = 0;
        var intakeKey = VideoGameDirectorAgent.ProjectStateKey(null, conversation);
        var projectKey = VideoGameDirectorAgent.ProjectStateKey(project, null);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(Saved(r.StateKey, new GameDirectorPortfolioIndex {
                    Projects = [++reads == 1 ? Entry(null, conversation, intakeKey) : Entry(project, conversation, projectKey)] }))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (_, _) => { writes++; throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite,
                    PlatformCapabilityErrorCode.Conflict, "Another callback promoted the project."); });
        await VideoGameDirectorAgent.UpsertPortfolioIndexAsync(new GameDirectorOperatingState {
            IntakeConversationId = conversation, Phase = GameDirectorPhase.WorkstreamPlanPending }, intakeKey, runtime.CreateContext(), default);
        Assert.Equal(2, reads); Assert.Equal(1, writes);
    }

    [Fact]
    public void ExistingConversationCannotBeReboundToAnotherProject()
    {
        var conversation = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => VideoGameDirectorAgent.MergePortfolioEntry(
            [Entry(Guid.NewGuid(), conversation, "existing")], Entry(Guid.NewGuid(), conversation, "different")));
    }

    private static GameDirectorPortfolioEntry Entry(Guid? project, Guid conversation, string key) =>
        new(key, project, conversation, null, null, "Prism Break", GameDirectorPhase.DetailedDesign, DateTimeOffset.UtcNow);
}
