using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static async Task CheckProducerHandoffAsync(GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.HandoffSessionId is not { } id) return;
        var session = await context.Platform.Communication.ReadCoordinationAsync(id, token);
        // The child review has its own durable recovery. Waiting for its result is not
        // a failed attention cycle; unrelated oversight and discovery must still run.
        if (session.Status == "Blocked" && session.FinalSummary?.StartsWith("Awaiting Naomi's creative review", StringComparison.Ordinal) == true) return;
        RequireProgressingHandoff(session, "Producer handoff");
    }

    internal static void RequireProgressingHandoff(AgentCoordinationSession session, string step)
    {
        if (session.Status is "Failed" or "Blocked" or "Cancelled")
            throw new InvalidOperationException(
                $"{step} {session.Id:D} is {session.Status}. " +
                ReconcileStallPolicy.Summarize(session.FinalSummary) +
                " Eligible execution failures resume on a ready replacement runtime with bounded retries; persistent failures require manager review in Communications.");
    }

    internal static bool WasPreviouslyStaffed(GameDirectorOperatingState state, Guid teamId, string roleKey) =>
        state.TeamId == teamId && (state.SpecialistEmployeeIds.ContainsKey(roleKey) ||
            roleKey == CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.Producer && state.ProducerEmployeeId.HasValue);
}
