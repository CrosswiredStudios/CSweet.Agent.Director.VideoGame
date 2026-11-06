using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    protected override bool SupportsResourceApproval => true;

    internal async Task ReconcileResourceReadinessAsync(JsonElement hint, AgentRuntimeContext context, CancellationToken token)
    {
        if (!hint.TryGetProperty("requestId", out var resourceId) || !resourceId.TryGetGuid(out var id)) return;
        var resources = await context.Platform.Resources.ReadAsync(new(RequestId: id), token);
        foreach (var resource in resources.Where(x => x.Status == "Ready" && x.ResourceType == ResourceCapabilities.BuildToolchain))
        {
            var current = await ReadStateAsync(context, token, resource.WorkstreamId);
            await WakeProductionCommitmentAsync(current.State, VideoGameDecisionTypeKeys.ToolchainSelection, context, token);
        }
    }
}
