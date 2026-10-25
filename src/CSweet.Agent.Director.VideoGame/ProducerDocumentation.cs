using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static string? ValidateInitialTechnicalLeadership(ResourceChangeRequestResponse resource,
        AgentTeamContext roster, CrosswiredStudios.VideoGame.PitchCollaboration.PitchReview? review,
        CrosswiredStudios.VideoGame.PitchCollaboration.PitchReply? approval)
    {
        if (review is null || approval is null ||
            !CrosswiredStudios.VideoGame.PitchCollaboration.PitchProtocol.Matches(review, approval) ||
            !resource.Evidence.Any(x => x.SourceRevision == approval.RevisionSha256))
            return "Initial staffing requires the Producer's confident review and the Director's exact accepted production brief.";
        string[] baselineRoles = [VideoGameRoleKeys.TechnicalDirector, VideoGameRoleKeys.Engineer,
            VideoGameRoleKeys.QualityAssurance];
        var missing = baselineRoles.Where(role => !roster.Members.Any(x => x.IsAvailable &&
            x.DeclaredRoleKeys.Contains(role, StringComparer.Ordinal))).ToHashSet(StringComparer.Ordinal);
        var additions = resource.Deltas.Where(x => x.ChangeKind == "Add").ToList();
        var proposed = additions.Select(x => x.Role.RoleKey).ToHashSet(StringComparer.Ordinal);
        // Propose every missing baseline role together. Other roles require scoped backlog evidence.
        if (!missing.Contains(VideoGameRoleKeys.TechnicalDirector) ||
            additions.Count != resource.Deltas.Count ||
            additions.Count != proposed.Count ||
            additions.Any(x => x.Role.Headcount != 1) ||
            !proposed.SetEquals(missing))
            return "Before board planning, propose the missing Technical Director and baseline Engineer and QA coverage from the accepted brief.";
        return null;
    }

    private const string ProducerDocumentsPrefix = "creative-producer-documents:";

    internal static async Task QueueProducerDocumentationAsync(GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token, bool requeueBlockedOnly = false)
    {
        if (state.AcceptedVision is not { } vision) return;
        var correlation = $"{ProducerDocumentsPrefix}{vision.ConversationId:N}:{vision.Digest}";
        var task = await context.Platform.PersonalTodo.AddAsync(new AddPersonalTodoItemRequest(
            "Prepare and share project documentation with the Producer",
            "Read the exact CEO-approved pitch, commission the Creative Director GDD, and share reviewed documents through the Producer handoff. Team-board creation is not a prerequisite.",
            "High", null, correlation, SourceConversationId: vision.ConversationId,
            SourceMessageId: vision.MessageId, CorrelationId: correlation)
        {
            WorkContext = TodoContext(state)
        }, token);
        if (!requeueBlockedOnly || task.Status == PersonalTodoStatuses.Blocked)
            await TryRequeuePersonalTodoAsync(task.Id, context, token);
    }

    private async Task<PersonalTodoResult> PrepareProducerDocumentationAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        var current = await ReadStateForTodoAsync(item, context, token);
        var state = current.State;
        if (state.AcceptedVision is not { } vision)
            return PersonalTodoResult.Blocked("An accepted project direction is required before preparing the Producer handoff.");
        // The approved source is immutable. Missing or superseded evidence cannot be reconstructed as approval.
        var pitch = await context.Platform.Artifacts.ReadAcceptedAsync(
            new(vision.ArtifactId, vision.ArtifactRevisionId, vision.ArtifactRevisionHash), token);
        if (pitch.Revision.ContentSha256 != vision.ArtifactRevisionHash)
            return PersonalTodoResult.Blocked("The exact CEO-approved pitch is required for the Producer handoff.");
        await ReconcileAsync(item.Id, context, token, state, current.Revision);
        var latest = await ReadStateForTodoAsync(item, context, token);
        return latest.State.HandoffSessionId.HasValue
            ? PersonalTodoResult.Completed("Shared the exact accepted pitch and GDD in the Producer collaboration for scope clarification and staffing discovery.")
            : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(5), "Documentation is ready; waiting for project setup to permit the scoped Producer handoff.");
    }
}
