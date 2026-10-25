using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal const string PlanningFollowthroughPrefix = "creative-planning-followthrough:";
    internal static bool PlanningDirectionRecorded(DecisionRecord decision) =>
        decision.Status == DecisionStatuses.Decided && decision.SupersededByDecisionId is null &&
        decision.SelectedOptionId is "continue-current-plan" or "provide-direction" && !string.IsNullOrWhiteSpace(decision.Rationale);

    private static Guid? PlanningBoard(DecisionRecord decision) => decision.TypeData is { ValueKind: JsonValueKind.Object } data &&
        data.TryGetProperty("boardId", out var board) && board.TryGetGuid(out var id) ? id : null;

    private static async Task QueuePlanningFollowthroughAsync(DecisionRecord decision, GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.AcceptedVision is not { } vision || state.WorkstreamId != decision.WorkstreamId) return;
        var correlation = PlanningFollowthroughPrefix + decision.Id.ToString("N");
        await context.Platform.PersonalTodo.AddAsync(new AddPersonalTodoItemRequest(
            "Resolve planning direction: " + decision.Summary,
            "Review the current accepted plan and completed independent execution evidence. Record the authoritative project decision; the Producer resumes from its durable decision event. A recommendation or completed callback alone cannot finish this commitment.",
            "High", null, correlation, SourceConversationId: vision.ConversationId, SourceMessageId: vision.MessageId, CorrelationId: correlation)
            { WorkContext = new(WorkstreamId: decision.WorkstreamId, BoardId: PlanningBoard(decision), DecisionId: decision.Id) }, token);
    }

    internal async Task<PersonalTodoResult> HandlePlanningFollowthroughAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        if (item.WorkContext?.DecisionId is not { } id || item.CorrelationId != PlanningFollowthroughPrefix + id.ToString("N") ||
            item.WorkContext.WorkstreamId is not { } project)
            return PersonalTodoResult.Blocked("Planning follow-through requires its exact project and decision binding.");
        var current = await ReadStateAsync(context, token, project);
        var decision = (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(id), token)).Single(x => x.Id == id);
        if (decision.WorkstreamId != project || current.State.WorkstreamId != project || decision.TypeKey != "video-game.management-direction.v1")
            return PersonalTodoResult.Blocked("The planning decision belongs to a different project or authority workflow.");
        // A replacement still needs an answer; supersession alone cannot close this commitment.
        for (var depth = 0; decision.SupersededByDecisionId is { } replacement && depth < 4; depth++)
        {
            decision = (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(replacement), token)).Single(x => x.Id == replacement);
            if (decision.WorkstreamId != project || decision.TypeKey != "video-game.management-direction.v1")
                return PersonalTodoResult.Blocked("The replacement decision belongs to another project or workflow.");
        }
        if (decision.Status == DecisionStatuses.Decided && decision.SelectedOptionId == "request-more-evidence" && decision.AuthorityRuleKey == "work-planning")
        {
            var evidence = await ReadCompletedPlanningEvidenceAsync(decision, context, token);
            if (evidence.Count > 0)
            {
                var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence.OrderBy(x => x.WorkItemId))));
                var data = decision.TypeData is { ValueKind: JsonValueKind.Object } payload
                    ? System.Text.Json.Nodes.JsonNode.Parse(payload.GetRawText())!.AsObject() : new System.Text.Json.Nodes.JsonObject();
                if (data["completedEvidenceDigest"]?.GetValue<string>() != digest)
                {
                    data["completedEvidenceDigest"] = digest;
                    decision = await context.Platform.RequestDecisionAsync(new DecisionRequest(project, decision.TypeKey, decision.Summary,
                        "work-planning", decision.Options, decision.RecommendedOptionId, decision.Evidence, decision.DueAt,
                        "Affected work remains gated until the current evidence resolves this planning question.", decision.Id,
                        $"creative-planning-evidence:{decision.Id:N}:{digest}", JsonSerializer.SerializeToElement(data)), token);
                }
            }
        }
        if (decision.Status == DecisionStatuses.Pending && decision.AuthorityRuleKey == "work-planning")
            await DecideProducerPlanningAsync(decision, current.State, context, token);
        decision = (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(decision.Id), token)).Single(x => x.Id == decision.Id);
        return PlanningDirectionRecorded(decision)
            ? PersonalTodoResult.Completed($"Project decision {decision.Id:D} is recorded as {decision.SelectedOptionId} with a rationale. Its durable decision event resumes Producer reconciliation; sprint startup still requires estimates, independent readiness and preflight.")
            : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(15),
                $"Project decision {decision.Id:D} remains unresolved ({decision.Status}, {decision.SelectedOptionId ?? "no selection"}). Waiting for completed evidence or required authority; Creative Direction retains this commitment.");
    }

    private static async Task WakePlanningFollowthroughAsync(Guid project, Guid? board, AgentRuntimeContext context, CancellationToken token)
    {
        var directory = await context.Platform.PersonalTodo.ListAsync(token);
        foreach (var item in directory.Boards.SelectMany(x => x.Items).Where(x =>
            x.CorrelationId?.StartsWith(PlanningFollowthroughPrefix, StringComparison.Ordinal) == true &&
            x.WorkContext?.WorkstreamId == project && (board is null || x.WorkContext.BoardId == board)).Take(25))
            await TryRequeuePersonalTodoAsync(item.Id, context, token);
    }

    internal sealed record CompletedPlanningEvidence(Guid WorkItemId, string? Identifier, Guid RepositoryId, string CommitSha,
        string Summary, IReadOnlyList<WorkDeliveryValidationEvidence> Validations);

    internal static CompletedPlanningEvidence? VerifiedPlanningEvidence(WorkItem item, WorkItemExecutionResponse execution)
    {
        if (item.Status != "Completed" || execution.Status != "Completed" || execution.WorkItemId != item.Id || item.Delivery is not { } delivery) return null;
        var quality = execution.Stages.SingleOrDefault(x => x.StageKey == "quality" && x.Status == "Completed");
        var integration = execution.Stages.SingleOrDefault(x => x.StageKey == "task-integration" && x.Status == "Completed");
        if (quality?.LatestOutcome is not { Disposition: WorkExecutionDispositions.Completed, OutcomeCode: "passed" } outcome ||
            integration?.LatestOutcome?.Output is not { ValueKind: JsonValueKind.Object } integrated ||
            !integrated.TryGetProperty("mergeCommitSha", out var merge) || merge.ValueKind != JsonValueKind.String) return null;
        var authors = execution.Stages.Where(x => x.StageKey is "specialist-execution" or "development").ToArray();
        if (quality.AgentInstallationId is null || authors.Length == 0 || authors.Any(x => x.AgentInstallationId == quality.AgentInstallationId)) return null;
        WorkDeliveryReviewResult? review;
        try { review = outcome.Output.Deserialize<WorkDeliveryReviewResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
        if (review is not { Approved: true } || review.CandidateDigest != merge.GetString() || review.CandidateDigest.Length is not (40 or 64) ||
            !review.CandidateDigest.All(Uri.IsHexDigit) || review.Findings.Count != 0 || review.Validations.Count == 0 ||
            review.Criteria.Any(x => !x.Satisfied || string.IsNullOrWhiteSpace(x.Evidence)) ||
            !review.Criteria.Select(x => x.Criterion).Order().SequenceEqual(delivery.AcceptanceCriteria.Order()) ||
            review.Validations.Any(x => x.RepositoryId != delivery.RepositoryId || x.CommitSha != review.CandidateDigest ||
                !x.Succeeded || x.ExitCode != 0 || string.IsNullOrWhiteSpace(x.Command) || string.IsNullOrWhiteSpace(x.Output))) return null;
        return new(item.Id, item.Identifier, delivery.RepositoryId, review.CandidateDigest, review.Summary, review.Validations);
    }

    private static async Task<IReadOnlyList<CompletedPlanningEvidence>> ReadCompletedPlanningEvidenceAsync(DecisionRecord decision,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (PlanningBoard(decision) is not { } boardId) return [];
        var board = await context.Platform.Work.ReadBoardAsync(boardId, token);
        if (board.Board.WorkstreamId != decision.WorkstreamId) throw new InvalidOperationException("Planning evidence board belongs to another project.");
        var completed = board.Items.Where(x => x.Status == "Completed" && x.Delivery is not null && x.SprintId is not null).Take(24).ToArray();
        var evidence = new List<CompletedPlanningEvidence>();
        foreach (var sprint in completed.Select(x => x.SprintId!.Value).Distinct().Take(4))
        {
            var execution = await context.Platform.Work.ReadOrchestrationAsync(new(boardId, sprint), token);
            if (execution is null || execution.BoardId != boardId || execution.SprintId != sprint) continue;
            foreach (var item in completed.Where(x => x.SprintId == sprint))
            {
                var result = execution.Items.SingleOrDefault(x => x.WorkItemId == item.Id);
                if (result is not null && VerifiedPlanningEvidence(item, result) is { } verified) evidence.Add(verified);
            }
        }
        return evidence;
    }
}
