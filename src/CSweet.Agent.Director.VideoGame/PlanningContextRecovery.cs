using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string PlanningContextContract = "current-resources.v1";
    private sealed record PlanningBasis(string Contract, string Fingerprint);
    private static string PlanningBasisKey(Guid decision) => "planning-context:" + decision.ToString("N");

    private static Task<AgentOperatingState<PlanningBasis>> RememberPlanningBasisAsync(Guid decision, string fingerprint,
        AgentRuntimeContext context, CancellationToken ct) => DurableAgentState.UpdateAsync<PlanningBasis>(context.Platform,
            PlanningBasisKey(decision), "planning.context-basis.v1", 1, _ => new(PlanningContextContract, fingerprint), ct);

    private static async Task<PlanningResourceContext?> ReadPlanningResourcesAsync(DecisionRecord decision,
        AgentRuntimeContext context, CancellationToken ct)
    {
        if (PlanningBoard(decision) is not Guid boardId) return null;
        var board = await context.Platform.Work.ReadBoardAsync(boardId, ct);
        if (board.Board.WorkstreamId != decision.WorkstreamId) throw new InvalidOperationException("Planning context belongs to another project.");
        return board.Board.TeamId is Guid team ? await PlanningResourceContext.ReadAsync(context.Platform, decision.WorkstreamId, team, ct) : null;
    }

    private static async Task VerifyPlanningResourcesAsync(DecisionRecord decision, PlanningResourceContext? previous,
        AgentRuntimeContext context, CancellationToken ct)
    {
        if (previous is not null && (await ReadPlanningResourcesAsync(decision, context, ct))?.Fingerprint != previous.Fingerprint)
            throw new InvalidOperationException("Staffing or resource approvals changed during review. Reassess the current planning context before recording direction.");
    }

    // Recovery is limited to agent-owned planning judgments. It never replaces a human answer,
    // a reserved CEO hold, QA disposition, or unrelated scope/financial decision.
    internal static bool CanReassessPlanningContext(DecisionRecord decision, Guid manager,
        IReadOnlyList<DecisionRecord> decisions)
    {
        if (decision.TypeKey != "video-game.management-direction.v1" || decision.SupersededByDecisionId is not null ||
            decision.TypeData is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("kind", out var kind) || kind.GetString() != "escalate") return false;
        if (decision.AuthorityRuleKey == "work-planning")
            return decision.Status == DecisionStatuses.Decided && decision.DecidedByOrganizationUserId == manager;
        if (decision.AuthorityRuleKey != "material-strategy-change" || decision.Status != DecisionStatuses.Pending ||
            decision.RequestedByOrganizationUserId != manager || decision.SupersedesDecisionId is not Guid parent) return false;
        return decisions.Any(d => d.Id == parent && d.WorkstreamId == decision.WorkstreamId &&
            d.AuthorityRuleKey == "work-planning" && d.TypeKey == decision.TypeKey && d.Status == DecisionStatuses.Superseded &&
            (d.SelectedOptionId is null || d.DecidedByOrganizationUserId == manager));
    }

    internal async Task ReassessPlanningContextAsync(DecisionRecord decision, GameDirectorOperatingState state,
        IReadOnlyList<DecisionRecord> decisions, AgentRuntimeContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var manager) || !CanReassessPlanningContext(decision, manager, decisions) ||
            state.WorkstreamId != decision.WorkstreamId || state.AcceptedVision is null) return;
        var resources = await ReadPlanningResourcesAsync(decision, context, ct);
        if (resources is null) return;
        var basis = await context.Platform.ReadOperatingStateAsync<PlanningBasis>(PlanningBasisKey(decision.Id), ct);
        if (basis?.Payload == new PlanningBasis(PlanningContextContract, resources.Fingerprint)) return;
        var source = decision.Evidence.FirstOrDefault(e => e.TypeKey == "video-game.production-plan.v1");
        if (source?.RevisionId is not Guid revisionId) return;
        var document = await context.Platform.Artifacts.GetAsync(source.ResourceId, ct);
        var revision = document.Revisions.SingleOrDefault(r => r.Id == revisionId && r.Status == "Accepted" && r.ContentSha256 == source.Digest);
        if (document.WorkstreamId != decision.WorkstreamId || document.AcceptedRevisionId != revisionId || revision is null) return;
        var judgmentKey = AgentOperatingStateKey.Create("planning-context-judgment:v1", new {
            decision.Id, decision.Revision, resources.Fingerprint, source.Digest, vision = state.AcceptedVision.Digest, PlanningContextContract,
            directions = decisions.Where(d => d.Status == DecisionStatuses.Decided && d.SupersededByDecisionId is null)
                .OrderBy(d => d.Id).Select(d => new { d.Id, d.Revision }) });
        var review = await DurableWorkHandoff.GetOrCreateAsync(judgmentKey, "planning.context-judgment.v1",
            () => GeneratePlanningDirectionAsync(decision, state.AcceptedVision.Markdown, revision.Content, context, ct, resources), context, ct);
        await VerifyPlanningResourcesAsync(decision, resources, context, ct);
        var current = (await context.Platform.ReadDecisionsAsync(new(decision.Id), ct)).Single(d => d.Id == decision.Id);
        if (current.Revision != decision.Revision || !CanReassessPlanningContext(current, manager, decisions)) return;
        if (review.RequiresOwner && decision.AuthorityRuleKey == "material-strategy-change")
        {
            await RememberPlanningBasisAsync(decision.Id, resources.Fingerprint, context, ct); return;
        }
        var data = JsonNode.Parse(decision.TypeData!.Value.GetRawText())!.AsObject();
        data["planningResourceBasis"] = resources.Fingerprint;
        data["planningContextContract"] = PlanningContextContract;
        var replacement = await context.Platform.RequestDecisionAsync(new(decision.WorkstreamId, decision.TypeKey, decision.Summary,
            review.RequiresOwner ? "material-strategy-change" : "work-planning", decision.Options, decision.RecommendedOptionId,
            decision.Evidence, decision.DueAt, review.RequiresOwner ? "The specific reserved decision still requires the owner's direction." :
                "Reconcile current staffing and resource approvals; independent planning remains eligible.", decision.Id,
            AgentOperatingStateKey.Create("planning-context-reassessment:v1", new { decision.Id, decision.Revision, resources.Fingerprint, PlanningContextContract }),
            JsonSerializer.SerializeToElement(data)), ct);
        if (!review.RequiresOwner && replacement.Status == DecisionStatuses.Pending)
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
