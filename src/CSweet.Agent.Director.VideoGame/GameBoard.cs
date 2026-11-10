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
            var title = (state.WorkingTitle ?? ExtractWorkingTitle(vision.Markdown)).Trim();
            if (title.Length == 0) title = "Video game";
            if (title.Length > 160) title = title[..160];
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
}
