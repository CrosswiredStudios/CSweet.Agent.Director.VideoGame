using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static async Task ProposeExecutionProfileUpgradeAsync(Guid? workstreamId, AgentRuntimeContext context, CancellationToken token)
    {
        if (workstreamId is not { } id) return;
        var workstream = await context.Platform.ReadWorkstreamAsync(new(id), token);
        if (workstream.ProfileKey != "video-game-production.v2" || workstream.ProfileVersion is not < 7) return;
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        if (boards.Any(x => x.WorkstreamId == id && !x.IsArchived)) return;
        using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "profiles", "video-game-production.v2.7.json"), token));
        var request = BuildProfileUpgrade(workstream, definition.RootElement);
        if (request is null) return;
        var stateKey = "profile-upgrade:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey))).ToLowerInvariant();
        var previous = await context.Platform.ReadOperatingStateAsync<MutationResponse>(stateKey, token);
        if (previous is not null) return;
        var result = await context.Platform.ProposeWorkstreamChangeAsync(request, token);
        if (!result.Applied && result.ApprovalId is null)
            throw new InvalidOperationException("The profile upgrade did not return an approval request or applied result.");
        try
        {
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(stateKey,
                "video-game.profile-upgrade-proposal.v1", 1, "Active", new Dictionary<string, string>(), [],
                result.Message ?? "Profile upgrade proposed", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(result),
                null, request.IdempotencyKey), token);
        }
        catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.Conflict)
        {
            if (await context.Platform.ReadOperatingStateAsync<MutationResponse>(stateKey, token) is null) throw;
        }
    }

    internal static WorkstreamChangeProposalRequest? BuildProfileUpgrade(WorkstreamDetail workstream, JsonElement definition)
    {
        var key = definition.GetProperty("key").GetString();
        var version = definition.GetProperty("version").GetInt32();
        if (workstream.ProfileKey != key || workstream.ProfileVersion is null || workstream.ProfileVersion >= version) return null;
        var canonical = JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(workstream.Id, workstream.Revision,
            "Enable technical code review, independent QA and governed merge for game delivery",
            JsonSerializer.SerializeToElement(new { profileUpgrade = new { key, version, definitionDigest = digest } }),
            "Upgrade the execution workflow before creating the team board. Preserve the accepted game scope, project documents, lifecycle and approval authority.",
            $"game-profile-upgrade:{workstream.Id:N}:{workstream.Revision}:{version}:{digest}");
    }
}
