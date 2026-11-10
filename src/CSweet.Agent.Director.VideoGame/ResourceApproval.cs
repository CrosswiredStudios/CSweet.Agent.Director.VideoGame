using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    protected override bool SupportsResourceApproval => true;

    internal async Task<AgentCoordinationTurnResult> ReceiveResourceRequestAsync(AgentCoordinationTurnRequest request,
        AgentRuntimeContext context, CancellationToken token)
    {
        var turn = request.Transcript.LastOrDefault(x => x.Artifact is not null);
        var hint = turn?.Artifact?.Payload.Deserialize<ResourceRequestState>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (hint is null || hint.Id == Guid.Empty || request.WorkContext?.WorkstreamId is not { } project ||
            turn!.SpeakerOrganizationUserId != request.Counterpart.OrganizationUserId)
            return AgentCoordinationTurnResult.Blocked("Resource receipt must identify the assigned project's request and authenticated requesting specialist.");
        var current = (await context.Platform.Resources.ReadAsync(new(RequestId: hint.Id), token)).SingleOrDefault();
        if (current is null || current.WorkstreamId != project || current.RequesterId != request.Counterpart.OrganizationUserId)
            return AgentCoordinationTurnResult.Blocked("The current resource request does not match this project and requesting specialist.");
        return AgentCoordinationTurnResult.Completed($"Resource request {current.Id:D} is durably recorded: {current.Status}. {current.Condition} Approval and verified fulfillment remain on that request.");
    }

    internal async Task ReconcileResourceReadinessAsync(JsonElement hint, AgentRuntimeContext context, CancellationToken token)
    {
        if (!hint.TryGetProperty("requestId", out var resourceId) || !resourceId.TryGetGuid(out var id)) return;
        var resources = await context.Platform.Resources.ReadAsync(new(RequestId: id), token);
        foreach (var resource in resources.Where(x => x.ResourceType == ResourceCapabilities.BuildToolchain))
        {
            var current = await ReadStateAsync(context, token, resource.WorkstreamId);
            await WakeProductionCommitmentAsync(current.State, VideoGameDecisionTypeKeys.ToolchainSelection, context, token);
        }
    }
}
