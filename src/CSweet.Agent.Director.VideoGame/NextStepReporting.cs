using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Director.VideoGame;

/// <summary>
/// Names the next step of a project from durable state alone, so any review, retry or chat turn
/// can say what happens next and who owns it, even after a crash, missed event or failed call.
/// </summary>
internal static class GameDirectorNextStep
{
    public static string Describe(GameDirectorOperatingState state) => state.Phase switch
    {
        GameDirectorPhase.Discovery or GameDirectorPhase.InvolvementConfirmation =>
            "the CEO provides initial game direction",
        GameDirectorPhase.HighLevelReview => "the CEO decides the submitted pitch revision",
        GameDirectorPhase.HighLevelAccepted or GameDirectorPhase.TeamPlanPending =>
            "I submit the governed studio staffing plan",
        GameDirectorPhase.TeamStaffingPending => state.StaffingRequestId is null
            ? "I submit the governed studio staffing plan"
            : "the staffing plan is approved and the Producer is hired",
        GameDirectorPhase.WorkstreamPlanPending or GameDirectorPhase.ProjectSetup => state.ProducerEmployeeId is null
            ? "the approved Producer becomes active on the team"
            : state.WorkstreamProposalId.HasValue
                ? "I review the Producer's project proposal and confirm the approved project exists"
                : state.WorkstreamProposalSessionId.HasValue
                    ? "the Producer submits a valid project proposal for my review; I check the collaboration for blockers"
                    : "I share the accepted pitch and high-level GDD with the Producer and start the project proposal",
        GameDirectorPhase.DetailedDesign => state.HandoffSessionId is null
            ? "I start the production-brief session with the Producer"
            : "the Producer and I converge on the shared production brief",
        GameDirectorPhase.PackageReview => "I review the Producer's detailed design package",
        GameDirectorPhase.Oversight => "the Producer leads delivery, Naomi leads creative direction, and I oversee business scope and resources",
        _ => "I reconcile the project's current state"
    };
}

/// <summary>Consecutive failures of the same next step for one project.</summary>
internal sealed record ReconcileStall(string Fingerprint, int Failures, DateTimeOffset FirstFailedAt,
    string LastError, bool Escalated);

internal static class ReconcileStallPolicy
{
    public const string SchemaId = "video-game.creative-director.reconcile-stall.v1";
    public const int EscalateAfterFailures = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    public static string StateKey(Guid conversationId) => $"creative-reconcile-stall:{conversationId:N}";

    // Error text often carries volatile IDs, so a stall is "the same next step keeps failing".
    public static string Fingerprint(string nextStep) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nextStep)))[..16].ToLowerInvariant();

    public static ReconcileStall Record(ReconcileStall? prior, string fingerprint, string error, DateTimeOffset now) =>
        prior is { Failures: > 0 } && prior.Fingerprint == fingerprint
            ? prior with { Failures = prior.Failures + 1, LastError = error }
            : new ReconcileStall(fingerprint, 1, now, error, false);

    public static bool ShouldEscalate(ReconcileStall stall) =>
        !stall.Escalated && stall.Failures >= EscalateAfterFailures;

    public static string Summarize(string? message)
    {
        var text = string.Join(' ', (message ?? "Unknown failure.").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 300 ? text : text[..297] + "...";
    }

    public static string WaitingReason(string nextStep, ReconcileStall stall, bool escalated) =>
        $"Next step: {nextStep}. It has failed on {stall.Failures} consecutive review(s) since " +
        $"{stall.FirstFailedAt:yyyy-MM-dd HH:mm} UTC: {stall.LastError} I retry automatically" +
        (escalated ? " and have told the CEO." : ".");

    public static string EscalationMessage(string? title, string nextStep, ReconcileStall stall) =>
        $"I'm stuck on **{title ?? "the current game"}**. Next step: {nextStep}. It has failed on " +
        $"{stall.Failures} consecutive project reviews since {stall.FirstFailedAt:yyyy-MM-dd HH:mm} UTC with: " +
        $"{stall.LastError}\n\nI keep retrying automatically every few minutes, and nothing needs to be resent. " +
        "This usually needs a platform fix, an approval or a configuration change. I'll continue as soon as the step succeeds.";
}

public sealed partial class VideoGameDirectorAgent
{
    /// <summary>
    /// Records a failed project reconciliation, escalates a persistent stall to the CEO once, and
    /// returns a waiting result that names the next step. Bookkeeping failures never hide the cause.
    /// </summary>
    internal async Task<PersonalTodoResult> ReportReconcileFailureAsync(Guid conversationId, Exception failure,
        AgentRuntimeContext context, CancellationToken token)
    {
        var error = ReconcileStallPolicy.Summarize(failure.Message);
        var now = DateTimeOffset.UtcNow;
        string nextStep;
        string? title;
        try
        {
            var state = (await ReadStateForConversationAsync(conversationId, context, token)).State;
            nextStep = GameDirectorNextStep.Describe(state);
            title = state.WorkingTitle;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            nextStep = "I reconcile the project's current state";
            title = null;
        }

        var stall = new ReconcileStall(ReconcileStallPolicy.Fingerprint(nextStep), 1, now, error, false);
        var escalated = false;
        try
        {
            var key = ReconcileStallPolicy.StateKey(conversationId);
            var store = new RevisionSafeProjectState(context.Platform);
            stall = (await store.MergeAsync<ReconcileStall>(key, ReconcileStallPolicy.SchemaId, 1,
                prior => ReconcileStallPolicy.Record(prior, stall.Fingerprint, error, now),
                new Dictionary<string, string>(), $"{key}:{now.UtcTicks}", token)).Payload;
            escalated = stall.Escalated;
            if (ReconcileStallPolicy.ShouldEscalate(stall) &&
                Guid.TryParse(context.Identity?.ManagerEmployeeId, out var manager) && manager != Guid.Empty)
            {
                await context.Platform.Communication.SendDirectMessageAsync(manager,
                    ReconcileStallPolicy.EscalationMessage(title, nextStep, stall),
                    $"creative-reconcile-stall:{conversationId:N}:{stall.Fingerprint}:{stall.FirstFailedAt.UtcTicks}", token);
                var reported = stall;
                stall = (await store.MergeAsync<ReconcileStall>(key, ReconcileStallPolicy.SchemaId, 1,
                    prior => (prior ?? reported) with { Escalated = true },
                    new Dictionary<string, string>(), $"{key}:escalated:{reported.FirstFailedAt.UtcTicks}", token)).Payload;
                escalated = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // The original failure stays the reported cause; stall bookkeeping is best effort.
        }
        return PersonalTodoResult.WaitingUntil(now.Add(ReconcileStallPolicy.RetryDelay),
            ReconcileStallPolicy.WaitingReason(nextStep, stall, escalated));
    }

    /// <summary>Clears a recorded stall after the project reconciles successfully.</summary>
    internal static async Task ClearReconcileStallAsync(Guid conversationId, AgentRuntimeContext context,
        CancellationToken token)
    {
        try
        {
            var key = ReconcileStallPolicy.StateKey(conversationId);
            var prior = await context.Platform.ReadOperatingStateAsync<ReconcileStall>(key, token);
            if (prior is null || prior.Payload.Failures == 0) return;
            await new RevisionSafeProjectState(context.Platform).MergeAsync<ReconcileStall>(key,
                ReconcileStallPolicy.SchemaId, 1,
                current => (current ?? prior.Payload) with { Failures = 0, Escalated = false },
                new Dictionary<string, string>(), $"{key}:cleared:{prior.Revision}", token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A stale stall record only affects the wording of a later failure.
        }
    }
}
