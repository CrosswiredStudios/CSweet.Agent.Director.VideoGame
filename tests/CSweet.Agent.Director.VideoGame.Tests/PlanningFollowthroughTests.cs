using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PlanningFollowthroughTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    private static bool Complete(PersonalTodoResult result) => (bool)typeof(PersonalTodoResult)
        .GetProperty("IsCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;

    private sealed class Journey
    {
        public readonly Guid Project = Guid.NewGuid(), Board = Guid.NewGuid(), Sprint = Guid.NewGuid(), Repo = Guid.NewGuid();
        public readonly string Commit = new('a', 40);
        public readonly VideoGameDirectorAgent Agent = new();
        public readonly AgentTestRuntime Runtime = new();
        public DecisionRecord Decision;
        public WorkItem Item;
        public WorkItemExecutionResponse Execution;
        public int Decisions, Models;
        public bool RequireEvidence;
        public Journey()
        {
            var artifact = Guid.NewGuid(); var revision = Guid.NewGuid(); var author = Guid.NewGuid(); var qa = Guid.NewGuid();
            Decision = Empty<DecisionRecord>() with { Id = Guid.NewGuid(), WorkstreamId = Project,
                TypeKey = "video-game.management-direction.v1", AuthorityRuleKey = "work-planning", Status = "Pending", Revision = 1,
                Summary = "Select an exact Phaser version", Options = [new("continue-current-plan", "Continue", null), new("request-more-evidence", "Evidence", null)],
                Evidence = [new("artifact", artifact, revision, "accepted-sha", "video-game.production-plan.v1", "Accepted")],
                TypeData = JsonSerializer.SerializeToElement(new { boardId = Board }) };
            var state = new GameDirectorOperatingState { WorkstreamId = Project,
                AcceptedVision = new(1, "vision", "Phaser game", Guid.NewGuid(), Guid.NewGuid(), "vision-sha", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
            Item = Empty<WorkItem>() with { Id = Guid.NewGuid(), SprintId = Sprint, Status = "Completed", Identifier = "GAME-11",
                Delivery = new(Repo, ["Pin Phaser"], ["Exact installed version verified"]) };
            var review = new WorkDeliveryReviewResult(Commit, true, "Verified Phaser pin", [new("Exact installed version verified", true, "Observed dependency tree")], []) {
                Validations = [new(Repo, Commit, "npm list phaser", 0, true, "phaser@4.2.1")] };
            var quality = Empty<WorkStageExecutionResponse>() with { Id = Guid.NewGuid(), StageKey = "quality", Status = "Completed", AgentInstallationId = qa,
                LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Completed, "passed", "Independent QA", JsonSerializer.SerializeToElement(review, Web), [], []) };
            var integration = Empty<WorkStageExecutionResponse>() with { StageKey = "task-integration", Status = "Completed",
                LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Completed, "merged", "Integrated", JsonSerializer.SerializeToElement(new { mergeCommitSha = Commit }), [], []) };
            Execution = Empty<WorkItemExecutionResponse>() with { WorkItemId = Item.Id, Status = "Completed",
                Stages = [Empty<WorkStageExecutionResponse>() with { StageKey = "specialist-execution", AgentInstallationId = author }, integration, quality] };
            Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(new(Guid.NewGuid(), r.StateKey, "test", 1, "Active", new Dictionary<string, string>(), [], "test", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))))
                .RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (_, _) => Task.FromResult<IReadOnlyList<DecisionRecord>>([Decision]))
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(new ArtifactDocument(artifact, "Plan", "video-game.production-plan.v1", "Accepted", revision, null, revision, null, null,
                    [new(revision, 1, null, "Accepted plan", "accepted-sha", "Accepted", DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = Project }))
                .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(new WorkBoardDetail(new(Board, "Game", "", false, false, 1, []) { WorkstreamId = Project }, [], [Item])))
                .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse?>(WorkOrchestrationCapabilities.Read, (_, _) => Task.FromResult<WorkSprintExecutionResponse?>(Empty<WorkSprintExecutionResponse>() with { BoardId = Board, SprintId = Sprint, Items = [Execution] }))
                .RegisterCapability<DecideDecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionDecide, (r, _) => {
                    Decisions++; Assert.Equal(Decision.Id, r.DecisionId); Assert.Equal(Decision.Revision, r.ExpectedRevision);
                    Decision = Decision with { Status = "Decided", SelectedOptionId = r.SelectedOptionId, Rationale = r.Rationale, Revision = Decision.Revision + 1 }; return Task.FromResult(Decision);
                })
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                    Models++; var text = r.GetRawText(); Assert.Contains("completedEvidence", text);
                    if (RequireEvidence) { Assert.Contains("phaser@4.2.1", text); Assert.Contains(Commit, text); }
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant", text = JsonSerializer.Serialize(new {
                        optionId = RequireEvidence ? "continue-current-plan" : "request-more-evidence", rationale = RequireEvidence ? "The integrated pin passed independent exact-source QA within the accepted engine constraints." : "Independent executed evidence is still missing.", requiresOwner = false }) }));
                });
        }
        public PersonalTodoItem Todo() => Empty<PersonalTodoItem>() with { Id = Guid.NewGuid(), CorrelationId = VideoGameDirectorAgent.PlanningFollowthroughPrefix + Decision.Id.ToString("N"), WorkContext = new(WorkstreamId: Project, BoardId: Board, DecisionId: Decision.Id) };
        public async Task Configure() => Assert.True((await Runtime.ExecuteCapabilityAsync(Agent, AgentConfigurationCapabilities.Update,
            new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") }))).Succeeded);
    }

    [Fact]
    public async Task CompletedIndependentEvidenceResolvesTheActualDecisionAndReplayDoesNotDecideAgain()
    {
        var j = new Journey { RequireEvidence = true }; await j.Configure(); var todo = j.Todo();
        Assert.True(Complete(await j.Agent.HandlePlanningFollowthroughAsync(todo, j.Runtime.CreateContext(), default)));
        Assert.Equal("continue-current-plan", j.Decision.SelectedOptionId); Assert.Equal(1, j.Decisions);
        Assert.True(Complete(await j.Agent.HandlePlanningFollowthroughAsync(todo, j.Runtime.CreateContext(), default)));
        Assert.Equal(1, j.Decisions); Assert.Equal(1, j.Models);
    }

    [Fact]
    public async Task SuccessfulCallbackRequestingEvidenceKeepsTheCardOpen()
    {
        var j = new Journey(); await j.Configure(); j.Item = j.Item with { Status = "Running" };
        Assert.False(Complete(await j.Agent.HandlePlanningFollowthroughAsync(j.Todo(), j.Runtime.CreateContext(), default)));
        Assert.Equal("request-more-evidence", j.Decision.SelectedOptionId);
    }

    [Theory]
    [InlineData("pending")][InlineData("no-rationale")][InlineData("evidence-request")][InlineData("superseded")]
    public void ARecommendationOrUnansweredReplacementIsNotARecordedDirection(string scenario)
    {
        var d = new Journey().Decision with { Status = "Decided", SelectedOptionId = "continue-current-plan", Rationale = "Accepted direction" };
        d = scenario switch { "pending" => d with { Status = "Pending" }, "no-rationale" => d with { Rationale = "" },
            "evidence-request" => d with { SelectedOptionId = "request-more-evidence" }, _ => d with { SupersededByDecisionId = Guid.NewGuid() } };
        Assert.False(VideoGameDirectorAgent.PlanningDirectionRecorded(d));
    }

    [Fact]
    public async Task NewCompletedEvidenceReopensAnEvidenceRequestThroughGovernedSupersession()
    {
        var j = new Journey { RequireEvidence = true }; await j.Configure();
        j.Decision = j.Decision with { Status = "Decided", SelectedOptionId = "request-more-evidence" };
        var old = j.Decision.Id; var todo = j.Todo(); var replacements = 0;
        j.Runtime.RegisterCapability<DecisionRequest, DecisionRecord>(PlatformCapabilities.DecisionRequest, (r, _) => {
            replacements++; Assert.Equal(old, r.SupersedesDecisionId); Assert.Equal("work-planning", r.AuthorityRuleKey);
            j.Decision = j.Decision with { Id = Guid.NewGuid(), Status = "Pending", SelectedOptionId = null, TypeData = r.TypeData };
            return Task.FromResult(j.Decision);
        });
        Assert.True(Complete(await j.Agent.HandlePlanningFollowthroughAsync(todo, j.Runtime.CreateContext(), default)));
        Assert.Equal(1, replacements); Assert.NotEqual(old, j.Decision.Id); Assert.Equal("continue-current-plan", j.Decision.SelectedOptionId);
    }

    [Fact]
    public async Task ProjectEvidenceWakeIsIdempotentAndDoesNotWakeUnrelatedBoards()
    {
        var j = new Journey(); var todo = j.Todo() with { Status = PersonalTodoStatuses.Running,
            Wait = new(DateTimeOffset.UtcNow.AddHours(1), "Waiting for execution evidence") };
        var requeues = 0;
        j.Runtime.RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(
            new PersonalTodoDirectory([new(Guid.NewGuid(), Guid.NewGuid(), "Naomi", null, null, 1, [todo])], Guid.NewGuid())))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) => {
                requeues++; Assert.Equal(todo.Id, r.ItemId); todo = todo with { Status = PersonalTodoStatuses.Ready, Wait = null }; return Task.FromResult(todo);
            });
        await j.Runtime.DeliverEventAsync(j.Agent, "com.csweet.work.item.changed.v1", new { context = new { workstreamId = j.Project, boardId = Guid.NewGuid() } });
        Assert.Equal(0, requeues);
        for (var i = 0; i < 2; i++)
            await j.Runtime.DeliverEventAsync(j.Agent, "com.csweet.work.item.changed.v1", new { context = new { workstreamId = j.Project, boardId = j.Board } });
        Assert.Equal(1, requeues);
    }

    [Theory]
    [InlineData("stale-commit")][InlineData("missing-output")][InlineData("same-author")][InlineData("unfinished")]
    public void StaleOrUnexecutedEvidenceCannotInformACompletedPlanningResult(string scenario)
    {
        var j = new Journey(); var stages = j.Execution.Stages.ToArray(); var quality = stages[2];
        var report = quality.LatestOutcome!.Output.Deserialize<WorkDeliveryReviewResult>(Web)!;
        if (scenario == "stale-commit") report = report with { CandidateDigest = new string('b', 40) };
        if (scenario == "missing-output") report = report with { Validations = [report.Validations[0] with { Output = "" }] };
        if (scenario == "same-author") quality = quality with { AgentInstallationId = stages[0].AgentInstallationId };
        stages[2] = quality with { LatestOutcome = quality.LatestOutcome! with { Output = JsonSerializer.SerializeToElement(report, Web) } };
        var item = scenario == "unfinished" ? j.Item with { Status = "Running" } : j.Item;
        Assert.Null(VideoGameDirectorAgent.VerifiedPlanningEvidence(item, j.Execution with { Stages = stages }));
    }
}
