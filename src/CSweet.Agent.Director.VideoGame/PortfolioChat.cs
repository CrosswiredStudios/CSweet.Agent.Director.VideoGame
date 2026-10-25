using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private sealed record DirectorConversationRoute(Guid? IntakeId);
    private static string ConversationRouteKey(Guid chat) => $"director-conversation-route:{chat:N}";

    private async Task<bool> RoutePortfolioChatAsync(CommunicationMessageReceivedEvent incoming, Guid chat,
        AgentTurnStreamWriter stream, AgentRuntimeContext context, CancellationToken token)
    {
        if (!IsAuthoritativeManager(incoming, context.Identity) || incoming.WorkContext?.WorkstreamId is not null) return false;
        var text = ExtractCurrentMessage(incoming.Message);
        if (await HandleReportingPreferenceAsync(incoming, chat, text, stream, context, token)) return true;
        var index = await ReadPortfolioIndexAsync(context, token);
        var projects = index.Projects.Where(x => x.ConversationId == chat).ToArray();
        if (Regex.IsMatch(text, @"\b(start|create|develop|pitch|plan)\s+(?:a\s+)?(?:new|another|second|separate)\s+game\b", RegexOptions.IgnoreCase))
        {
            if (projects.Length == 1)
            {
                var initial = (await ReadStateByKeyAsync(projects[0].StateKey, context, token)).State;
                if (initial.WorkstreamId is null && initial.AcceptedVision is null && initial.Proposals.Count == 0 && initial.DiscoveryInputs.Count == 0)
                {
                    await SelectIntakeAsync(chat, initial.IntakeId, incoming.MessageId, context, token);
                    return false;
                }
            }
            var intake = incoming.MessageId;
            var key = ProjectStateKey(null, chat, intake);
            var existing = await ReadStateByKeyAsync(key, context, token);
            if (existing.Revision is null)
                await SaveStateAsync(new GameDirectorOperatingState { IntakeId = intake, IntakeConversationId = chat },
                    null, incoming.MessageId, $"director-intake:{intake:N}", context, token);
            await SelectIntakeAsync(chat, intake, incoming.MessageId, context, token);
            return false;
        }
        var matches = projects.Where(x => (x.WorkstreamId is { } id && text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)) ||
            text.Contains(x.IntakeId?.ToString("D") ?? x.ConversationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            (!x.WorkingTitle.StartsWith("Game ", StringComparison.Ordinal) && text.Contains(x.WorkingTitle, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 1)
        {
            await SelectIntakeAsync(chat, matches[0].IntakeId, incoming.MessageId, context, token);
            return false;
        }
        if (projects.Length <= 1) return false;
        var route = await context.Platform.ReadOperatingStateAsync<DirectorConversationRoute>(ConversationRouteKey(chat), token);
        var active = projects.SingleOrDefault(x => x.IntakeId == route?.Payload.IntakeId);
        // Keep answers during an explicitly selected concept's refinement attached to that concept.
        if (route is not null && active?.Phase is GameDirectorPhase.Discovery or GameDirectorPhase.InvolvementConfirmation or GameDirectorPhase.HighLevelReview)
            return false;
        await context.Platform.AskUserAsync(new(chat, incoming.TurnId,
            "Which game should this direction apply to? Select a game or enter its name or project ID.",
            projects.Take(4).Select(x => new AskUserOption((x.WorkstreamId ?? x.IntakeId ?? x.ConversationId).ToString("D"),
                x.WorkingTitle, $"Continue {x.WorkingTitle} in {x.Phase}.")).ToArray(),
            (projects[0].WorkstreamId ?? projects[0].IntakeId ?? projects[0].ConversationId).ToString("D"),
            $"director-project-choice:{incoming.MessageId:N}"), token);
        await stream.CommitAsync("I have multiple games in progress. Select the game for this direction before I change its plans.", token);
        return true;
    }

    private static async Task SelectIntakeAsync(Guid chat, Guid? intake, Guid message,
        AgentRuntimeContext context, CancellationToken token) =>
        _ = await new RevisionSafeProjectState(context.Platform).MergeAsync<DirectorConversationRoute>(ConversationRouteKey(chat),
            "video-game.director-conversation-route.v1", 1, _ => new(intake), new Dictionary<string, string>(),
            $"director-route:{message:N}", token);
}
