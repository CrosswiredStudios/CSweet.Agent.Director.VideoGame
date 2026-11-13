using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private static string[] ToolchainTargets(string recipe) =>
        recipe.StartsWith("godot.", StringComparison.Ordinal) ? ["windows-x64", "linux-x64"] : ["web"];

    private static ReadToolchainCatalogV2Request FeasibilityCatalogRequest(string recipe, string[] targets) =>
        new(VideoGameProfileKeys.ProductionV2, recipe, targets, ["scaffold", "import", "build", "test", "run", "capture", "package"]);

    private static AgentCoordinationArtifactSubmission FeasibilityRequestWithPitch(AcceptedGameVision pitch,
        string recipe, string[] targets, IReadOnlyList<EligibleToolchainAdapter> catalog) =>
        CollaborationActions.WithDocuments(new(ToolchainFeasibilityRequestType, "1.0", pitch.Digest, 1, true,
            JsonSerializer.SerializeToElement(new { acceptedVisionDigest = pitch.Digest, requiredRecipeKey = recipe,
                targetKeys = targets, eligibleDefinitions = catalog.Select(x => new { x.Definition.Id, x.Definition.Key,
                    x.Definition.Version, x.Definition.DefinitionDigest, x.Eligibility.ProviderInstallationId,
                    x.Eligibility.EnvironmentProfileKey, x.Eligibility.EnvironmentImageDigest, x.Eligibility.ExpiresAt }) })),
            [new(pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash)]);

    private static bool IsAssignedFeasibilityReview(AgentCoordinationTurnRequest request, GameDirectorOperatingState state) =>
        state.ToolchainFeasibilitySessionId == request.SessionId && state.WorkstreamId.HasValue &&
        request.WorkContext?.WorkstreamId == state.WorkstreamId && request.WorkContext.TeamId == state.TeamId &&
        state.SpecialistEmployeeIds.TryGetValue(VideoGameRoleKeys.TechnicalDirector, out var reviewer) &&
        reviewer == request.Counterpart.OrganizationUserId;

    internal static async Task<AgentCoordinationTurnResult> RouteFeasibilityReviewAsync(AgentCoordinationTurnRequest request,
        GameDirectorOperatingState state, AgentCoordinationArtifact retained, AgentRuntimeContext context, CancellationToken token)
    {
        if (request.IsFinalization)
        {
            var specialist = request.Transcript.LastOrDefault(t => t.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId);
            return AgentCoordinationTurnResult.Blocked("Technical Director feasibility review is incomplete. " + specialist?.Content);
        }
        if (!IsAssignedFeasibilityReview(request, state) || state.AcceptedVision is not { } pitch || retained.Key != pitch.Digest ||
            request.Transcript.LastOrDefault(t => t.Artifact is not null)?.SpeakerOrganizationUserId != request.Self.OrganizationUserId)
            return AgentCoordinationTurnResult.Blocked("The Director must start a feasibility review for the current project, pitch and Technical Director.");

        var recipe = DetermineRequiredRecipe(state);
        var targets = ToolchainTargets(recipe);
        var catalog = await context.Platform.ReadEligibleToolchainsAsync(FeasibilityCatalogRequest(recipe, targets), token);
        if (catalog.Count == 0)
            return AgentCoordinationTurnResult.Blocked($"Build capacity for {recipe} is currently unavailable. Retry the review once a certified environment is available.");
        return AgentCoordinationTurnResult.Continue(
            $"Reassess the retained build setup against the attached accepted pitch (review turn {request.TurnOrdinal}).",
            FeasibilityRequestWithPitch(pitch, recipe, targets, catalog));
    }

    private static IReadOnlyList<EvidenceReference> FeasibilitySources(ToolchainFeasibilityEvidenceV1 assessment,
        AcceptedGameVision pitch, string legacyDesignStatus) => assessment.EvidenceResourceIds.Select(id =>
            id == pitch.ArtifactId
                ? new EvidenceReference("artifact", id, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash, VideoGameArtifactTypeKeys.Vision, "Accepted")
                : new EvidenceReference("artifact", id, null, null, VideoGameArtifactTypeKeys.TechnicalDesign, legacyDesignStatus)).ToList();
}
