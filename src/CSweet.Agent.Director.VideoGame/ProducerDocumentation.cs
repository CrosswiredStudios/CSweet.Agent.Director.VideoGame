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
        // Accept existing single-lead Producers during upgrade; current Producers propose
        // every missing baseline role together. Other roles require scoped backlog evidence.
        var legacyLeadOnly = proposed.SetEquals([VideoGameRoleKeys.TechnicalDirector]);
        if (!missing.Contains(VideoGameRoleKeys.TechnicalDirector) ||
            additions.Count != resource.Deltas.Count ||
            additions.Count != proposed.Count ||
            additions.Any(x => x.Role.Headcount != 1) ||
            !(proposed.SetEquals(missing) || legacyLeadOnly))
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
            "Retrieve the accepted pitch and high-level GDD. Reconstruct missing documentation from durable project direction, obtain exact-revision approval, and share the documents through the Producer handoff. Team-board creation is not a prerequisite.",
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
        ArtifactDocument? document = null;
        try { document = await context.Platform.Artifacts.GetAsync(state.HighLevelArtifactId ?? vision.ArtifactId, token); }
        catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.NotFound) { }

        if (document is null)
        {
            // Reconstruct the actual saved direction; never invent a different scope or an approval.
            document = await context.Platform.Artifacts.CreateAsync(new CreateArtifactDocument(
                "High-Level Game Design Document", vision.Markdown, VideoGameArtifactTypeKeys.Vision,
                $"producer-docs-restore:{vision.ConversationId:N}:{vision.Digest}",
                OriginConversationId: vision.ConversationId,
                StewardOrganizationUserId: Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) ? manager : null), token);
            var restored = document.Revisions.Single(x => x.Id == document.LatestRevisionId);
            current = await SaveStateAsync(state with { HighLevelArtifactId = document.Id,
                HighLevelLatestRevisionId = restored.Id, HighLevelAcceptedRevisionId = null },
                current.Revision, item.Id, $"producer-docs-restored:{restored.Id:N}", context, token);
            state = current.State;
        }
        var exact = document.Revisions.SingleOrDefault(x =>
            x.Id == (state.HighLevelAcceptedRevisionId ?? vision.ArtifactRevisionId) && x.Status == "Accepted");
        // A reconstructed document becomes usable only after its own exact revision is accepted.
        exact ??= document.Revisions.SingleOrDefault(x => x.Id == document.LatestRevisionId && x.Status == "Accepted");
        if (exact is null)
        {
            var revision = document.Revisions.Single(x => x.Id == document.LatestRevisionId);
            if (revision.Status == "Draft")
                await context.Platform.Artifacts.SubmitAsync(new SubmitArtifactRevision(document.Id, revision.Id,
                    $"producer-docs-submit:{revision.Id:N}", vision.ConversationId,
                    Guid.TryParse(context.Identity?.ManagerEmployeeId, out var reviewer) ? reviewer : null), token);
            await context.Platform.Communication.SendMessageAsync(vision.ConversationId,
                $"The Producer requested project documentation. I have prepared the [high-level GDD](/organizations/{context.BusinessId}/documents?artifact={document.Id:D}&revision={revision.Id:D}) and submitted it for review. I will share the accepted revision with the Producer.",
                $"producer-docs-review:{revision.Id:N}", token);
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(5), "Waiting for the exact reconstructed GDD revision to be accepted.");
        }
        if (state.HighLevelArtifactId != document.Id || state.HighLevelAcceptedRevisionId != exact.Id)
        {
            // Retain the original pitch when available; replacing a lost source requires the reviewed reconstruction.
            var restoredVision = vision;
            if (document.Id != vision.ArtifactId)
            {
                try { _ = await context.Platform.Artifacts.GetAsync(vision.ArtifactId, token); }
                catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.NotFound)
                {
                    restoredVision = vision with { ArtifactId = document.Id, ArtifactRevisionId = exact.Id,
                        ArtifactRevisionHash = exact.ContentSha256, Markdown = exact.Content, Digest = Digest(exact.Content) };
                }
            }
            current = await SaveStateAsync(state with { HighLevelArtifactId = document.Id,
                HighLevelLatestRevisionId = exact.Id, HighLevelAcceptedRevisionId = exact.Id, AcceptedVision = restoredVision },
                current.Revision, item.Id, $"producer-docs-ready:{exact.Id:N}", context, token);
            state = current.State;
        }
        await ReconcileAsync(item.Id, context, token, state, current.Revision);
        var latest = await ReadStateForTodoAsync(item, context, token);
        return latest.State.HandoffSessionId.HasValue
            ? PersonalTodoResult.Completed("Shared the exact accepted pitch and GDD in the Producer collaboration for scope clarification and staffing discovery.")
            : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(5), "Documentation is ready; waiting for project setup to permit the scoped Producer handoff.");
    }
}
