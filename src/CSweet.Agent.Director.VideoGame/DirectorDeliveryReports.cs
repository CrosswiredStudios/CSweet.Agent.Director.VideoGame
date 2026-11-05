using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    // Direct conversations deliberately have no project scope. Resolve a named blocked
    // ticket from authorized current reads, never by trusting a scope claim in chat text.
    internal async Task<bool> RecoverUnscopedDeliveryReportAsync(CommunicationMessageReceivedEvent incoming,
        Guid chat, AgentTurnStreamWriter stream, AgentRuntimeContext context, CancellationToken token)
    {
        if (incoming.WorkContext is not null ||
            incoming.Context?.GetValueOrDefault(CommunicationMessageContextKeys.SenderEmployeeType) != "Agent" ||
            !Guid.TryParse(incoming.Context.GetValueOrDefault(CommunicationMessageContextKeys.SenderOrganizationUserId), out var sender)) return false;
        var identifiers = Regex.Matches(ExtractCurrentMessage(incoming.Message), @"(?<![A-Z0-9-])[A-Z][A-Z0-9]{1,31}-[1-9][0-9]{0,8}(?![A-Z0-9-])",
            RegexOptions.IgnoreCase).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identifiers.Length != 1 || !Regex.IsMatch(incoming.Message, @"\b(blocked|blocker|failed|can't start)\b", RegexOptions.IgnoreCase)) return false;
        var boards = (await context.Platform.Work.ListBoardsAsync(cancellationToken: token))
            .Where(x => !x.IsArchived && x.ManagerOrganizationUserId == sender && x.WorkstreamId.HasValue && x.TeamId.HasValue).ToArray();
        if (boards.Length > 8) return false;
        var matches = new List<(WorkBoardSummary Board, GameDirectorOperatingState State)>();
        foreach (var board in boards)
        {
            var detail = await context.Platform.Work.ReadBoardAsync(board.Id, token);
            if (detail.Board.Id != board.Id || detail.Board.ManagerOrganizationUserId != sender ||
                detail.Board.WorkstreamId != board.WorkstreamId || detail.Board.TeamId != board.TeamId || detail.Board.IsArchived) continue;
            if (!detail.Items.Any(x => string.Equals(x.Identifier, identifiers[0], StringComparison.OrdinalIgnoreCase) &&
                                      x.Status is "Blocked" or "Failed")) continue;
            var state = (await ReadStateAsync(context, token, board.WorkstreamId)).State;
            if (state.AcceptedVision is null || state.WorkstreamId != board.WorkstreamId ||
                state.TeamId != board.TeamId || state.ProducerEmployeeId != sender) continue;
            matches.Add((board, state));
        }
        if (matches.Count != 1) return false;
        var match = matches[0];
        var task = await AddChatActionTodoAsync(incoming, chat,
            $"Assess the current delivery blocker on {identifiers[0]} using board {match.Board.Id:D}. " +
            "Re-read the stopped stage, installed executor version and actual compute receipts. Coordinate recovery with the Producer within existing authority. " +
            "Preserve completed implementation, reviews and source evidence. Retain independent QA; do not assume missing capacity from a historical error. " +
            "Use governed decisions only for an evidenced scope or resource question outside existing authority.",
            match.State, context, token);
        await stream.CommitAsync($"I verified the blocked ticket and your Producer assignment against the approved project. " +
            $"I recorded Director follow-through as task {task.Id:D}; recovery is not yet confirmed.", token);
        return true;
    }
}
