using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    /// <summary>
    /// Finds or opens the one board for a game whose studio team exists but whose project is not approved yet.
    /// The board is unbound and managed by the Director. When the Producer later requests the project board,
    /// the platform binds this board to the project and hands it to the Producer.
    /// Opening the board is best effort: a missing grant or an ambiguous team leaves the existing flow unchanged.
    /// </summary>
    internal static async Task<Guid?> EnsureGameBoardAsync(
        GameDirectorOperatingState state, Guid teamId, AgentRuntimeContext context, CancellationToken token)
    {
        if (state.AcceptedVision is not { } vision || state.WorkstreamId.HasValue) return state.BoardId;
        try
        {
            var open = (await context.Platform.Work.ListBoardsAsync(cancellationToken: token))
                .Where(x => x.TeamId == teamId && !x.IsArchived && !x.WorkstreamId.HasValue).ToList();
            if (open.Count == 1) return open[0].Id;
            // More than one unbound board is ambiguous. Never guess and never add another.
            if (open.Count > 1) return state.BoardId;
            var title = GameBoardName(state.WorkingTitle, ExtractWorkingTitle(vision.Markdown));
            var created = await context.Platform.Work.CreateBoardAsync(new CreateWorkBoardRequest(
                title,
                "Game board opened by the Video Game Director when the studio team was approved. " +
                "Leadership work for this game is tracked here; the Producer manages it once the project is approved.",
                $"director-game-board:{teamId:N}:{vision.Digest}")
            {
                TeamId = teamId
            }, token);
            return created.Id;
        }
        catch (PlatformCapabilityException)
        {
            // Not granted on this installation, or temporarily unavailable. The next review tries again.
            return state.BoardId;
        }
    }

    /// <summary>
    /// Names the board after the game. Before project identity inference has run, the only title is the pitch
    /// heading, which often reads "Game Pitch - "Title"" or "Title - Official Game Pitch". The document label and
    /// surrounding quotes are removed in code; no model is asked.
    /// </summary>
    internal static string GameBoardName(string? workingTitle, string pitchHeading)
    {
        var source = string.IsNullOrWhiteSpace(workingTitle) ? pitchHeading : workingTitle;
        var parts = Regex.Split(source ?? "", @"\s*[\u2014\u2013|]\s*|\s+-\s+|:\s+")
            .Select(x => x.Trim().Trim('"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '*', '_', '#', '`').Trim())
            .Where(x => x.Length > 0 && !Regex.IsMatch(x,
                @"^(the\s+)?(official\s+)?((video\s+)?game\s+)?(pitch|vision|concept)(\s+document)?$", RegexOptions.IgnoreCase))
            .ToArray();
        var title = parts.Length > 0 ? parts[0] : "Video game";
        return title.Length <= 160 ? title : title[..160];
    }

    /// <summary>
    /// Selects this Director's own agenda cards for one game that were created before its board existed:
    /// cards carrying the game's intake fingerprint with no board and no project. Pure; no platform call.
    /// </summary>
    internal static IReadOnlyList<PersonalTodoItem> CardsCreatedBeforeTheBoard(PersonalTodoDirectory directory, Guid? intakeId)
    {
        if (intakeId is not { } intake) return [];
        var fingerprint = intake.ToString("D");
        return directory.Boards
            .Where(x => directory.CurrentOrganizationUserId is not { } self || x.OwnerOrganizationUserId == self)
            .SelectMany(x => x.Items)
            .Where(x => x.ArchivedAt is null && x.WorkContext is { BoardId: null, WorkstreamId: null } work &&
                        string.Equals(work.SourceFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            .Take(25).ToList();
    }

    /// <summary>
    /// Binds the cards that predate the board (the pitch and the staffing plan) to it, so the board shows the
    /// game's history from the first direction. Only the work context changes. Best effort and repeatable:
    /// a bound card is no longer selected, and a missing grant or a concurrent edit leaves the card as it was.
    /// </summary>
    internal static async Task BindEarlierCardsToGameBoardAsync(GameDirectorOperatingState state, Guid teamId,
        Guid boardId, AgentRuntimeContext context, CancellationToken token)
    {
        IReadOnlyList<PersonalTodoItem> cards;
        try { cards = CardsCreatedBeforeTheBoard(await context.Platform.PersonalTodo.ListAsync(token), state.IntakeId); }
        catch (PlatformCapabilityException) { return; }
        foreach (var card in cards)
        {
            if (card.WorkContext is not { } work) continue;
            try
            {
                _ = await context.Platform.PersonalTodo.UpdateAsync(new UpdatePersonalTodoItemRequest(
                    card.Id, card.Title, card.Description, card.Priority, card.DueDate, card.Revision,
                    $"bind-game-board:{card.Id:N}:{boardId:N}")
                {
                    WorkContext = work with { TeamId = work.TeamId ?? teamId, BoardId = boardId }
                }, token);
            }
            catch (PlatformCapabilityException)
            {
                // Not granted, or the card changed meanwhile. The card stays on the Director's own board only.
            }
        }
    }

    private const string ProposalCollaborationPrefix = "project-proposal-collaboration:";

    /// <summary>
    /// Gives the board-sourced proposal collaboration an owning card on the Director's agenda, bound to the game
    /// board and the exact session. The platform's board recovery works through this card: when it is Blocked,
    /// requeueing it, or an update of the Producer's package, returns the turn to the Producer.
    /// </summary>
    internal static async Task QueueProposalCollaborationCardAsync(GameDirectorOperatingState state, Guid teamId,
        Guid boardId, AgentCoordinationSession session, AgentRuntimeContext context, CancellationToken token)
    {
        if (state.AcceptedVision is not { } vision) return;
        var correlation = $"{ProposalCollaborationPrefix}{session.Id:N}";
        try
        {
            _ = await context.Platform.PersonalTodo.AddAsync(new AddPersonalTodoItemRequest(
                "Get the Producer's project proposal approved",
                "Tracks the Producer's project proposal collaboration on the game board. This card is blocked while the " +
                "collaboration is blocked; moving it back to To Do asks the Producer for a fresh proposal.",
                "High", null, correlation, SourceConversationId: vision.ConversationId,
                SourceMessageId: vision.MessageId, CorrelationId: correlation)
            {
                // The intake fingerprint is how the card's callback finds this game's state before a project exists.
                WorkContext = new PersonalTodoWorkContext(TeamId: teamId, BoardId: boardId,
                    CoordinationSessionId: session.Id, SourceFingerprint: state.IntakeId?.ToString("D"))
            }, token);
        }
        catch (PlatformCapabilityException)
        {
            // The card is a tracking aid. Without it the collaboration still runs and can be retried from chat.
        }
    }

    private async Task<PersonalTodoResult> TrackProposalCollaborationAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParseExact(item.CorrelationId![ProposalCollaborationPrefix.Length..], "N", out var sessionId))
            return PersonalTodoResult.Blocked("The proposal collaboration reference on this card is invalid.");
        var state = (await ReadStateForTodoAsync(item, context, token)).State;
        if (state.WorkstreamId.HasValue)
            return PersonalTodoResult.Completed("The project was created from the approved proposal.");
        if (state.WorkstreamProposalSessionId is { } current && current != sessionId)
            return PersonalTodoResult.Completed("A newer proposal collaboration replaced this one.");
        var session = await context.Platform.Communication.ReadCoordinationAsync(sessionId, token);
        return session.Status switch
        {
            "Blocked" or "Failed" or "Cancelled" => PersonalTodoResult.Blocked(
                "The Producer's project proposal is blocked: " + ReconcileStallPolicy.Summarize(session.FinalSummary) +
                " Move this card back to To Do to ask the Producer for a fresh proposal."),
            "Completed" => PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(10),
                "The Producer submitted the project proposal. Waiting for its review and for the project to be created."),
            _ => PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(10),
                "Waiting for the Producer to submit the project proposal.")
        };
    }
}
