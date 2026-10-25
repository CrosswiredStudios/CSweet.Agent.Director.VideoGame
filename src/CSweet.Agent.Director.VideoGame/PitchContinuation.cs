using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private sealed record PitchDraft(string Markdown);

    private async Task<(GameDirectorOperatingState State, long? Revision)> PersistPitchAsync(
        string pitch, string currentMessage, GameDirectorOperatingState state, long? stateRevision,
        Guid conversationId, Guid sourceTurnId, Guid sourceMessageId, Guid? todoId, int revision,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var digest = Digest(pitch);
        var documentContent = pitch.Trim();
        var proposal = new GamePitchRevision(revision, documentContent, digest, DateTimeOffset.UtcNow,
            ExtractPositiveConstraints(currentMessage), state.References.Select(x => x.Sha256).Distinct().ToList());
        ArtifactDocument document;
        var managerOrganizationUserId = Guid.TryParse(
            context.Identity?.ManagerEmployeeId, out var parsedManagerOrganizationUserId)
                ? parsedManagerOrganizationUserId
                : (Guid?)null;
        if (!state.HighLevelArtifactId.HasValue)
        {
            document = await context.Platform.Artifacts.CreateAsync(new CreateArtifactDocument(
                "Official Game Pitch", documentContent, VideoGameArtifactTypeKeys.Vision,
                $"high-level-gdd-create:{state.IntakeId ?? conversationId:N}", OriginConversationId: conversationId,
                StewardOrganizationUserId: managerOrganizationUserId), cancellationToken);
        }
        else
        {
            _ = await context.Platform.Artifacts.ReviseAsync(new CreateArtifactRevision(
                state.HighLevelArtifactId.Value, state.HighLevelLatestRevisionId!.Value, documentContent,
                $"high-level-gdd-revision:{digest}"), cancellationToken);
            document = await context.Platform.Artifacts.GetAsync(state.HighLevelArtifactId.Value, cancellationToken);
        }
        var pendingRevision = document.Revisions.MaxBy(x => x.Number)!;
        document = await context.Platform.Artifacts.SubmitAsync(new SubmitArtifactRevision(
            document.Id, pendingRevision.Id, $"high-level-gdd-submit:{pendingRevision.Id:N}",
            conversationId, managerOrganizationUserId), cancellationToken);
        var latestArtifactRevision = document.Revisions.MaxBy(x => x.Number)!;
        state = state with
        {
            Phase = GameDirectorPhase.HighLevelReview,
            References = state.References,
            Proposals = state.Proposals.Append(proposal).TakeLast(20).ToList(),
            VisionTodoId = todoId,
            HighLevelArtifactId = document.Id,
            HighLevelLatestRevisionId = latestArtifactRevision.Id,
            HighLevelAcceptedRevisionId = state.HighLevelAcceptedRevisionId,
            HighLevelSourceTurnId = sourceTurnId,
            HighLevelSourceMessageId = sourceMessageId,
            AcceptedVision = state.AcceptedVision
        };
        return await SaveStateAsync(state, stateRevision, Guid.NewGuid(),
            $"pitch-revision:{digest}", context, cancellationToken);
    }

    private async Task<PersonalTodoResult> ContinuePitchTaskAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        var current = await ReadStateForTodoAsync(item, context, token);
        var state = current.State;
        if (state.AcceptedVision is { } accepted)
            return PersonalTodoResult.Completed($"Accepted high-level GDD revision {accepted.ArtifactRevisionId:D} ({accepted.ArtifactRevisionHash}).");
        if (state.HighLevelArtifactId.HasValue)
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddHours(4),
                "Waiting for the CEO to decide the exact official game pitch revision.");
        if (item.SourceConversationId is not { } chat ||
            (state.HighLevelSourceMessageId ?? item.SourceMessageId) is not { } messageId)
            return PersonalTodoResult.Blocked("Pitch recovery requires the original CEO direction message.");
        try
        {
            // Read current authorized source evidence, including for tasks created by 0.1.0.
            var messages = await context.Platform.Communication.ReadChatAsync(chat, token);
            var source = messages.Messages.SingleOrDefault(x => x.Id == messageId);
            if (source?.SenderOrganizationUserId is not { } sender || sender.ToString("D") != context.Identity?.ManagerEmployeeId)
                return PersonalTodoResult.Blocked("The original CEO direction is unavailable or no longer authoritative.");
            var turnId = source.ChatTurnId ?? state.HighLevelSourceTurnId;
            if (turnId is null || turnId == Guid.Empty)
                return PersonalTodoResult.Blocked("Pitch recovery requires the original direction's chat turn reference.");
            var incoming = new CommunicationMessageReceivedEvent(Settings.GetGuid("llmProviderId") ?? Guid.Empty, chat.ToString("D"),
                sender.ToString("D"), source.Content, null, turnId.Value, 1, messageId);
            var direction = ExtractCurrentMessage(source.Content);
            var revision = state.Proposals.Count == 0 ? 1 : state.Proposals.Max(x => x.Revision) + 1;
            var draft = await PitchProtocol.CachedAsync($"director-pitch-output:{messageId:N}:{revision}", context,
                async () => new PitchDraft(await GeneratePitchAsync(incoming, direction, state, chat, null, context, token)), token);
            await PersistPitchAsync(draft.Markdown, direction, state, current.Revision, chat,
                turnId.Value, messageId, item.Id, revision, context, token);
            await context.Platform.Communication.SendMessageAsync(chat, BuildPitchReviewMessage(revision),
                $"director-pitch-recovered:{messageId:N}:{revision}", token);
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddHours(4),
                "The official game pitch is submitted; waiting for exact CEO approval before leadership hiring.");
        }
        catch (Exception ex) when (IsRecoverablePitchGenerationFailure(ex, token) || IsRecoverableAgendaFailure(ex, token))
        {
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(15),
                $"Pitch creation will retry from saved CEO direction: {ex.Message}");
        }
        catch (PlatformCapabilityException ex)
        {
            return PersonalTodoResult.Blocked($"Pitch creation needs platform correction: {ex.Message}");
        }
    }
}
