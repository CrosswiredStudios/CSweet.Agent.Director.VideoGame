using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    /// <summary>
    /// Gives the design-document commission an owning card on the Director's agenda, bound to the project,
    /// the game board and the exact session, through the SDK's tracked-collaboration helper. Best effort.
    /// </summary>
    internal static Task QueueCreativeDesignCardAsync(GameDirectorOperatingState state, Guid project, Guid team,
        AgentCoordinationSession session, AcceptedGameVision pitch, AgentRuntimeContext context, CancellationToken token) =>
        TrackedCollaboration.QueueCardAsync(context, session,
            new TrackedCollaborationCard(
                "Get the game design document from the Creative Director",
                "Tracks the Creative Director's design document for the approved pitch. This card is blocked while " +
                "the collaboration is blocked, and completes when the reviewed document is accepted.")
            {
                WorkstreamId = project, TeamId = team, BoardId = state.BoardId,
                SourceConversationId = pitch.ConversationId, SourceMessageId = pitch.MessageId
            }, token);

    /// <summary>Answers a tracked-collaboration card from durable state and the session. No model is asked.</summary>
    private async Task<PersonalTodoResult> ReviewTrackedCollaborationAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        var state = (await ReadStateForTodoAsync(item, context, token)).State;
        var designed = state.CreativeDesignArtifactId is not null && state.CreativeDesignRevisionId is not null;
        return await TrackedCollaboration.ReviewAsync(context, item, new TrackedCollaborationReview(designed)
        {
            CurrentSessionId = state.CreativeDesignSessionId,
            OutcomeSummary = "The reviewed game design document was accepted.",
            WaitingReason = "Waiting for the Creative Director to submit the game design document.",
            SubmittedReason = "The Creative Director submitted the game design document. Waiting for its review.",
            RetryHint = "Use Retry on the game board, or move this card back to To Do after fixing the cause."
        }, token);
    }

    /// <summary>
    /// A turn arriving on a collaboration means it is moving again. If its owning card is Blocked, return the
    /// card to the queue so it reports the current state. One read; never fails the turn.
    /// </summary>
    private static async Task WakeBlockedCollaborationCardAsync(Guid sessionId, AgentRuntimeContext context,
        CancellationToken token)
    {
        try
        {
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            foreach (var card in OwnCards(directory).Where(x => x.Status == PersonalTodoStatuses.Blocked &&
                         (x.CorrelationId == TrackedCollaboration.Correlation(sessionId) ||
                          x.CorrelationId == $"{ProposalCollaborationPrefix}{sessionId:N}")).Take(4))
                _ = await context.Platform.PersonalTodo.RequeueAsync(new RequeuePersonalTodoItemRequest(
                    card.Id, card.Revision, $"collaboration-moving:{card.Id:N}:{card.Revision}"), token);
        }
        catch (PlatformCapabilityException)
        {
            // Not granted, or another wake already moved the card.
        }
    }

    /// <summary>
    /// The blocked leadership cards for one game, for status answers. A status must not call a phase pending
    /// or active while the card that owns it is blocked.
    /// </summary>
    internal static IReadOnlyList<PersonalTodoItem> BlockedLeadershipCards(PersonalTodoDirectory directory,
        GameDirectorOperatingState state) =>
        OwnCards(directory).Where(x => x.Status == PersonalTodoStatuses.Blocked && x.WorkContext is { } work &&
                (state.WorkstreamId is { } project && work.WorkstreamId == project ||
                 state.BoardId is { } board && work.BoardId == board ||
                 state.IntakeId is { } intake && string.Equals(work.SourceFingerprint, intake.ToString("D"),
                     StringComparison.OrdinalIgnoreCase)))
            .Take(5).ToList();

    internal static string BlockedLeadershipStatus(IReadOnlyList<PersonalTodoItem> blocked)
    {
        if (blocked.Count == 0) return string.Empty;
        return " **Blocked leadership work:** " + string.Join(" ", blocked.Select(x =>
        {
            var reason = ReconcileStallPolicy.Summarize(x.BlockReason);
            return string.IsNullOrWhiteSpace(reason) ? $"\"{x.Title}\"." : $"\"{x.Title}\": {reason}";
        }));
    }

    private static async Task<string> BlockedLeadershipStatusAsync(GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token)
    {
        try { return BlockedLeadershipStatus(BlockedLeadershipCards(await context.Platform.PersonalTodo.ListAsync(token), state)); }
        catch (PlatformCapabilityException) { return string.Empty; }
    }

    private static IEnumerable<PersonalTodoItem> OwnCards(PersonalTodoDirectory directory) =>
        directory.Boards
            .Where(x => directory.CurrentOrganizationUserId is not { } self || x.OwnerOrganizationUserId == self)
            .SelectMany(x => x.Items).Where(x => x.ArchivedAt is null);
}
