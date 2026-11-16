using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static string RequirementReviewBasis(GameDirectorOperatingState state, Guid reviewerInstallation,
        IReadOnlyList<EligibleToolchainAdapter> catalog, DecisionRecord? decision) =>
        AgentOperatingStateKey.Create("requirement-review:v1", new {
            contract = RequirementDecisions.Contract, reviewerInstallation, acceptedVision = state.AcceptedVision!.Digest,
            recipe = DetermineRequiredRecipe(state),
            catalog = catalog.Select(x => new { x.Definition.DefinitionDigest, x.Eligibility.ProviderInstallationId,
                x.Eligibility.CertificationRunId, x.Eligibility.EnvironmentProfileKey, x.Eligibility.EnvironmentImageDigest }).OrderBy(x => x.EnvironmentProfileKey),
            direction = decision is { Status: DecisionStatuses.Decided } ? new { decision.Id, decision.Revision } : null
        });

    internal async Task<(GameDirectorOperatingState State, long? Revision, bool Stop)> ReconcileRequirementReviewAsync(
        GameDirectorOperatingState state, long? revision, Guid reviewId, Guid reviewerInstallation,
        IReadOnlyList<EligibleToolchainAdapter> catalog, DecisionRecord? decision, AgentRuntimeContext context, CancellationToken ct)
    {
        if (state.ToolchainFeasibilityEvidence is not { } evidence || state.ToolchainFeasibilitySessionId is not Guid sessionId)
            return (state, revision, false);
        if (evidence.RecipeKey != DetermineRequiredRecipe(state)) return (state, revision, false);
        var basis = RequirementReviewBasis(state, reviewerInstallation, catalog, decision);
        if (decision is { Status: DecisionStatuses.Decided } && RequirementDecisions.IsGrounded(decision))
        {
            var follow = await RequirementDecisions.FollowThroughAsync(decision, state.AcceptedVision!.Markdown,
                JsonSerializer.SerializeToElement(new { evidence, catalog = catalog.Select(x => ToolchainReviewContext.Describe(x, evidence.RecipeKey)) }),
                context, new(Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure the Director's provider."), Settings.GetString("llmModel")), null, ct);
            if (follow.HoldAffectedWork) return (state, revision, true);
        }
        if (evidence.Feasible) return (state, revision, false);
        // A changed review contract, participant installation, certified environment or owner
        // direction permits one reassessment. Unchanged events never reopen the same review.
        if (state.ToolchainReviewBasis == basis) return (state, revision, false);
        if (decision is { Status: DecisionStatuses.Decided } && !RequirementDecisions.IsGrounded(decision))
            return (state, revision, true); // Preserve an answered legacy direction for explicit reconciliation.
        var session = await context.Platform.Communication.ReadCoordinationAsync(sessionId, ct);
        if (session.WorkContext is not { } work || work.WorkstreamId != state.WorkstreamId || work.TeamId != state.TeamId ||
            !state.SpecialistEmployeeIds.Values.Contains(session.Target.OrganizationUserId)) return (state, revision, false);
        if (session.Status == AgentCoordinationStatuses.Blocked)
        {
            await context.Platform.Communication.ResumeCoordinationAsync(sessionId, session.Revision,
                "Reassess the actual accepted requirements against current certified alternatives and any recorded owner direction. Distinguish product targets, build hosts and verification coverage. Preserve accepted obligations and identify independently useful work.",
                AgentOperatingStateKey.Create("requirement-reassess:v1", new { sessionId, basis }), ct);
            var saved = await SaveStateAsync(state with { ToolchainReviewBasis = basis }, revision, reviewId, basis, context, ct);
            return (saved.State, saved.Revision, true);
        }
        return (state, revision, session.Status is not (AgentCoordinationStatuses.Completed or AgentCoordinationStatuses.Failed));
    }

    private async Task<DecisionRecord> RequestRequirementDecisionAsync(GameDirectorOperatingState state,
        DecisionRecord? previous, AgentRuntimeContext context, CancellationToken ct)
    {
        var evidence = state.ToolchainFeasibilityEvidence!;
        var facts = JsonSerializer.SerializeToElement(new { evidence, state.ToolchainFeasibilitySessionId,
            requirementOrigin = "Technical feasibility findings; verify which statements are accepted obligations, inferred requirements or implementation defects.",
            scope = state.AcceptedVision!.Markdown });
        var key = AgentOperatingStateKey.Create("requirement-decision:v1", new { state.WorkstreamId, state.AcceptedVision.Digest,
            evidence.RecipeKey, evidence.Findings, state.ToolchainReviewBasis });
        var draft = await DurableWorkHandoff.GetOrCreateAsync(key, "work.requirement-decision-draft.v1", async () => {
            using var client = context.CreateChatClient(new(Settings.GetGuid("llmProviderId") ??
                throw new InvalidOperationException("Configure the Director's provider."), Settings.GetString("llmModel")));
            return await RequirementDecisions.AssessAsync(client, state.AcceptedVision.Markdown, facts, null, ct);
        }, context, ct);
        var request = RequirementDecisions.Bind(draft, state.WorkstreamId!.Value,
            CSweet.Agent.SDK.VideoGame.Contracts.VideoGameDecisionTypeKeys.ToolchainSelection, "technical-feasibility-blocker",
            FeasibilitySources(evidence, state.AcceptedVision, "Submitted"),
            previous is { Status: DecisionStatuses.Pending } ? previous.Id : null, key, facts);
        // Preserve the existing domain correlation independently of the model-authored question.
        var data = System.Text.Json.Nodes.JsonNode.Parse(request.TypeData!.Value.GetRawText())!;
        data["recipeKey"] = evidence.RecipeKey;
        return await context.Platform.RequestDecisionAsync(request with { TypeData = JsonSerializer.SerializeToElement(data) }, ct);
    }
}
