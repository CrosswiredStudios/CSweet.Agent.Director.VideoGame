using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static async Task EnsureStaffingReplacementAsync(GameDirectorOperatingState state,
        ResourceChangeRequestResponse resource, IReadOnlyList<ResourceChangeRole> missingRoles,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (resource.TeamId is not { } teamId || state.StaffingRequestId != resource.Id || state.AcceptedVision is null) return;
        // Initial hires remain owned by the approved plan. Prior staffed state is evidence to
        // request replacement; the platform still verifies actual fulfilled capacity and authority.
        var lostRoles = missingRoles.Where(role => WasPreviouslyStaffed(state, teamId, role.RoleKey)).ToArray();
        if (lostRoles.Length == 0) return;
        var lostKey = string.Join('|', lostRoles.Select(x => x.RoleKey).Order(StringComparer.Ordinal));
        var fingerprint = Digest($"{resource.Id:N}:{teamId:N}:{lostKey}");
        var existing = await context.Platform.ReadStaffingReplenishmentsAsync(
            new StaffingReplenishmentReadRequest(SourceResourceChangeRequestId: resource.Id), token);
        if (existing.Requests.Any(x => string.Equals(x.DecisionFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            x.Status is StaffingReplenishmentStatuses.Pending or StaffingReplenishmentStatuses.Approved)) return;
        _ = await context.Platform.ProposeStaffingReplenishmentAsync(new StaffingReplenishmentProposalRequest(
            resource.Id, teamId, state.AcceptedVision.ConversationId,
            lostRoles.Select(role => new StaffingReplenishmentGap(role.RoleKey, role.Title, 1, 0, 1,
                ["The previously staffed project role has no distinct active eligible installation on this team."])).ToArray(),
            "Producer-led planning is waiting for its approved Producer installation. Missing delivery specialists block only their dependent work.",
            ["No required specialist may absorb another required role; the Video Game Director remains a supervisor rather than a delivery-team member."],
            fingerprint, $"video-game-studio-replenishment:{fingerprint}"), token);
    }
}
