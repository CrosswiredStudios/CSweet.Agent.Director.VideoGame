using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal async Task<IReadOnlyList<(GameDirectorOperatingState State, long? Revision)>> FindProducerKickoffsAsync(
        CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken token)
    {
        // Resolve the sender from broker-authenticated context, never from message content.
        if (incoming.Context is null || !incoming.Context.TryGetValue(CommunicationMessageContextKeys.SenderOrganizationUserId, out var senderText) ||
            !Guid.TryParse(senderText, out var sender))
            return [];
        var result = new List<(GameDirectorOperatingState, long?)>();
        var index = await ReadPortfolioIndexAsync(context, token);
        foreach (var entry in index.Projects)
        {
            var current = await ReadStateByKeyAsync(entry.StateKey, context, token);
            var state = current.State;
            if (state.AcceptedVision is null || state.HandoffSessionId.HasValue || !state.StaffingRequestId.HasValue)
                continue;
            if (incoming.WorkContext?.WorkstreamId is Guid workstream && state.WorkstreamId != workstream)
                continue;
            var resource = (await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(state.StaffingRequestId), token))
                .Requests.SingleOrDefault();
            if (resource?.Status is not ("Approved" or "Superseded") || resource.TeamId is not Guid teamId)
                continue;
            var roster = (await context.Platform.ReadTeamRosterAsync(new TeamRosterV2Request(teamId, null, 1, 100), token)).Team;
            if (roster?.Members.Any(x => x.EmployeeId == sender.ToString("D") && x.Presence == "Active" &&
                x.IsAvailable && x.AgentInstallationId.HasValue &&
                x.DeclaredRoleKeys.Contains(VideoGameRoleKeys.Producer) &&
                x.EffectiveCapabilities.Contains("work.execution.run")) == true)
                result.Add(current);
        }
        return result;
    }
}