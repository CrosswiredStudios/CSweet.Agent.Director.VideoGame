using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string PlanningContextContract = "selected-planning-action.v2";
    private sealed record PlanningBasis(string Contract, string Fingerprint);
    private static string PlanningBasisKey(Guid decision) => "planning-context:" + decision.ToString("N");

    private static Task<AgentOperatingState<PlanningBasis>> RememberPlanningBasisAsync(Guid decision, string fingerprint,
        AgentRuntimeContext context, CancellationToken ct) => DurableAgentState.UpdateAsync<PlanningBasis>(context.Platform,
            PlanningBasisKey(decision), "planning.context-basis.v1", 1, _ => new(PlanningContextContract, fingerprint), ct);

    private static async Task<PlanningResourceContext?> ReadPlanningResourcesAsync(WorkBoardDetail? board,
        AgentRuntimeContext context, CancellationToken ct)
    {
        return board?.Board.TeamId is Guid team && board.Board.WorkstreamId is Guid project
            ? await PlanningResourceContext.ReadAsync(context.Platform, project, team, ct) : null;
    }

    private static async Task VerifyPlanningResourcesAsync(DecisionRecord decision, GameDirectorOperatingState state, PlanningResourceContext? previous,
        AgentRuntimeContext context, CancellationToken ct)
    {
        if (previous is not null && (await ReadPlanningResourcesAsync(await ReadPlanningBoardAsync(decision, state, context, ct), context, ct))?.Fingerprint != previous.Fingerprint)
            throw new InvalidOperationException("Staffing or resource approvals changed during review. Reassess the current planning context before recording direction.");
    }

    // Recovery is limited to agent-owned planning judgments. It never replaces a human answer,
    // a reserved CEO hold, QA disposition, or unrelated scope/financial decision.
    internal static bool CanReassessPlanningContext(DecisionRecord decision, Guid manager,
        IReadOnlyList<DecisionRecord> decisions)
    {
        if (decision.TypeKey != "video-game.management-direction.v1" || decision.SupersededByDecisionId is not null) return false;
        var data = decision.TypeData;
        bool legacyEscalation = false;
        if (data is { ValueKind: JsonValueKind.Object } fields)
        {
            if (fields.TryGetProperty("proposedPlanningChange", out _) || ContributionAdmission.Read(data) is not null) return false;
            if (fields.TryGetProperty("kind", out var kind))
            {
                if (kind.ValueKind != JsonValueKind.String || kind.GetString() != "escalate") return false;
                legacyEscalation = true;
            }
        }
        if (decision.AuthorityRuleKey == "work-planning")
            return legacyEscalation && decision.Status == DecisionStatuses.Decided && decision.DecidedByOrganizationUserId == manager;
        if (decision.AuthorityRuleKey != "material-strategy-change" || decision.Status != DecisionStatuses.Pending ||
            decision.RequestedByOrganizationUserId != manager || decision.SupersedesDecisionId is not Guid parent) return false;
        var predecessor = decisions.FirstOrDefault(d => d.Id == parent && d.SupersededByDecisionId == decision.Id &&
            d.WorkstreamId == decision.WorkstreamId && d.TypeKey == decision.TypeKey && d.AuthorityRuleKey == "work-planning" &&
            d.Status == DecisionStatuses.Superseded && d.Summary == decision.Summary && d.Options.SequenceEqual(decision.Options));
        if (predecessor is null) return false;
        var visited = new HashSet<Guid>();
        while (visited.Add(predecessor.Id))
        {
            if (predecessor.SelectedOptionId != null && predecessor.DecidedByOrganizationUserId != manager) return false;
            if (predecessor.SupersedesDecisionId is not Guid previous) return true;
            var next = decisions.FirstOrDefault(d => d.Id == previous && d.WorkstreamId == decision.WorkstreamId &&
                d.TypeKey == decision.TypeKey && d.SupersededByDecisionId == predecessor.Id);
            if (next is null) return false;
            predecessor = next;
        }
        return false;
    }

    internal async Task ReassessPlanningContextAsync(DecisionRecord decision, GameDirectorOperatingState state,
        IReadOnlyList<DecisionRecord> decisions, AgentRuntimeContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var manager) || !CanReassessPlanningContext(decision, manager, decisions) ||
            state.WorkstreamId != decision.WorkstreamId || state.AcceptedVision is null) return;
        var board = await ReadPlanningBoardAsync(decision, state, context, ct);
        var resources = await ReadPlanningResourcesAsync(board, context, ct);
        if (board is null || resources is null) return;
        var basis = await context.Platform.ReadOperatingStateAsync<PlanningBasis>(PlanningBasisKey(decision.Id), ct);
        if (basis?.Payload == new PlanningBasis(PlanningContextContract, resources.Fingerprint)) return;
        var source = decision.Evidence.FirstOrDefault(e => e.Kind == "artifact" && e.Status == "Accepted" && e.TypeKey == "video-game.production-plan.v1");
        if (source?.RevisionId is not Guid revisionId) return;
        var document = await context.Platform.Artifacts.GetAsync(source.ResourceId, ct);
        var revision = document.Revisions.SingleOrDefault(r => r.Id == revisionId && r.Status == "Accepted" && r.ContentSha256 == source.Digest);
        if (document.WorkstreamId != decision.WorkstreamId || document.DocumentType != source.TypeKey || document.AcceptedRevisionId != revisionId || revision is null) return;
        var judgmentKey = AgentOperatingStateKey.Create("planning-context-judgment:v1", new {
            decision.Id, decision.Revision, resources.Fingerprint, source.Digest, vision = state.AcceptedVision.Digest, PlanningContextContract,
            directions = decisions.Where(d => d.Status == DecisionStatuses.Decided && d.SupersededByDecisionId is null)
                .OrderBy(d => d.Id).Select(d => new { d.Id, d.Revision }) });
        var review = await DurableWorkHandoff.GetOrCreateAsync(judgmentKey, "planning.context-judgment.v1",
            () => GeneratePlanningDirectionAsync(decision, state.AcceptedVision.Markdown, revision.Content, board, context, ct, resources), context, ct);
        await VerifyPlanningResourcesAsync(decision, state, resources, context, ct);
        var current = (await context.Platform.ReadDecisionsAsync(new(decision.Id), ct)).Single(d => d.Id == decision.Id);
        if (current.Revision != decision.Revision || !CanReassessPlanningContext(current, manager, decisions)) return;
        if (review.Change is not null && decision.AuthorityRuleKey == "material-strategy-change")
        {
            await RememberPlanningBasisAsync(decision.Id, resources.Fingerprint, context, ct); return;
        }
        var data = decision.TypeData is { ValueKind: JsonValueKind.Object } old ? JsonNode.Parse(old.GetRawText())!.AsObject() : new JsonObject();
        data["planningResourceBasis"] = resources.Fingerprint;
        data["planningContextContract"] = PlanningContextContract;
        var replacement = await context.Platform.RequestDecisionAsync(review.Change is not null ? ChangeRequest(decision, review) : new(decision.WorkstreamId, decision.TypeKey, decision.Summary,
            "work-planning", decision.Options, decision.RecommendedOptionId, decision.Evidence, decision.DueAt,
                "Reconcile current staffing and resource approvals; independent planning remains eligible.", decision.Id,
            AgentOperatingStateKey.Create("planning-context-reassessment:v1", new { decision.Id, decision.Revision, resources.Fingerprint, PlanningContextContract }),
            JsonSerializer.SerializeToElement(data)), ct);
        if (review.Change is null && replacement.Status == DecisionStatuses.Pending)
            replacement = await context.Platform.DecideDecisionAsync(new(replacement.Id, replacement.Revision, review.OptionId, review.Rationale,
                $"planning-context-direction:{replacement.Id:N}:{replacement.Revision}"), ct);
        await RememberPlanningBasisAsync(replacement.Id, resources.Fingerprint, context, ct);
        await QueuePlanningFollowthroughAsync(replacement, state, context, ct);
    }

    internal async Task ReconcilePlanningContextsAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var index = await ReadPortfolioIndexAsync(context, ct);
        foreach (var entry in index.Projects)
        {
            var current = await ReadStateByKeyAsync(entry.StateKey, context, ct);
            if (current.State.WorkstreamId is not Guid project) continue;
            var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: project), ct);
            foreach (var decision in decisions)
            {
                if (decision.TypeKey == "video-game.management-direction.v1" && decision.AuthorityRuleKey == "work-planning" &&
                    decision.Status == DecisionStatuses.Pending && decision.SupersededByDecisionId is null)
                {
                    // Discovery repairs a lost wake or a crash after replacement creation but before deciding/queuing.
                    await QueuePlanningFollowthroughAsync(decision, current.State, context, ct);
                    await DecideProducerPlanningAsync(decision, current.State, context, ct);
                }
                else await ReassessPlanningContextAsync(decision, current.State, decisions, context, ct);
            }
        }
    }
}
