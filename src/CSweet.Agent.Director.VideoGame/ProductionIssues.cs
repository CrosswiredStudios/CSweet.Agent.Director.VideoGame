using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private async Task ReconcileProductionIssuesAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var index = await ReadPortfolioIndexAsync(context, ct);
        await IndependentWork.RunAsync(index.Projects.Select(entry => (Func<CancellationToken, Task>)(async ct =>
        {
            var current = await ReadStateByKeyAsync(entry.StateKey, context, ct);
            var state = current.State;
            if (state.WorkstreamId is not Guid project || state.AcceptedVision is not { } vision || state.ToolchainFeasibilitySessionId is not Guid session) return;
            var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: ct);
            var candidates = boards.Where(b => b.WorkstreamId == project && b.TeamId == state.TeamId && !b.IsArchived).ToArray();
            var board = state.BoardId is Guid boardId ? candidates.SingleOrDefault(b => b.Id == boardId) : candidates.Length == 1 ? candidates[0] : null;
            if (board is null) return; // Initial feasibility can precede a project board; legacy sources remain visible.
            var directory = await context.Platform.Issues.ReadAllAsync(board.Id, true, ct);
            var key = AgentOperatingStateKey.Create("production-feasibility:v1", new { project });
            var issue = directory.Issues.Where(i => i.ObligationKey == key).OrderByDescending(i => i.Episode).FirstOrDefault();
            var source = new WorkApprovedSource(vision.ArtifactId, vision.ArtifactRevisionId, vision.ArtifactRevisionHash);
            var review = await context.Platform.Communication.ReadCoordinationAsync(session, ct);
            if (review.WorkContext?.WorkstreamId != project || review.Initiator.OrganizationUserId.ToString() != context.Identity?.EmployeeId) return;
            var required = state.ToolchainBlockerDecisionId.HasValue || review.Status is AgentCoordinationStatuses.Failed or AgentCoordinationStatuses.Blocked;
            var verified = state.ToolchainFeasibilityEvidence is { Feasible: true } evidence && evidence.AcceptedVisionDigest == vision.Digest &&
                evidence.RecipeKey == DetermineRequiredRecipe(state) && !ToolchainTargets(state).Except(evidence.TargetKeys).Any();
            var selection = state.ToolchainSelectionDecisionId is Guid selectionId ?
                (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(selectionId), ct)).SingleOrDefault() : null;
            var resolved = verified && selection is { Status: DecisionStatuses.Decided } && !state.ToolchainBlockerDecisionId.HasValue;
            if (issue is { Status: "Resolved" } && issue.Source == source && resolved)
            {
                await context.Platform.Issues.RetireTrackingAsync(issue, Guid.Parse(context.Identity!.EmployeeId), ct); return;
            }
            if (!required && issue is null) return;
            var links = new List<WorkIssueLink> { new("Coordination", session) };
            if (state.ToolchainBlockerDecisionId is Guid blocker) links.Add(new("Decision", blocker));
            if (selection is not null) links.Add(new("Decision", selection.Id));
            var update = new WorkIssueUpdate(key, "The approved project needs source-bound technical feasibility and certified toolchain selection.",
                JsonSerializer.Serialize(new { state.ToolchainFeasibilityEvidence, review.Status, review.FinalSummary }), []) {
                IssueId = issue?.Id, ExpectedRevision = issue?.Revision, Status = resolved ? "Resolved" : "Open",
                Phase = state.ToolchainBlockerDecisionId.HasValue ? "AwaitingDecision" : "AwaitingEvidence",
                BriefTitle = "Technical feasibility under review", BriefSummary = "The project manager is reconciling the technical review and recorded direction.",
                NextAction = resolved ? "Continue work within the accepted technical evidence" : "Reconcile the current technical review and its required decisions; unaffected planning may continue",
                Links = links, ResolutionEvidence = resolved ? JsonSerializer.Serialize(new { source, selection = selection!.Id, state.ToolchainFeasibilityEvidence }) : null
            };
            if (issue is not null && issue.Status == update.Status && issue.Phase == update.Phase && issue.Source == source && links.All(issue.Links.Contains)) return;
            var saved = await context.Platform.Issues.ReconcileOrProposeAsync(new(board.Id, source, [update], AgentOperatingStateKey.Create("production-issue-write:v1", new { board.Id, source, update })), ct);
            if (resolved && saved.Issues.SingleOrDefault() is { } retired) await context.Platform.Issues.RetireTrackingAsync(retired, Guid.Parse(context.Identity!.EmployeeId), ct);
        })), ct);
    }
}
