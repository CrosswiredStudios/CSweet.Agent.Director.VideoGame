using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

// When Naomi's review uses up its bounded attempts, the owner decides what happens next instead of the review
// staying blocked: retry it with a fresh round, have Gabriel revise the brief, or keep it paused. Each choice
// starts a fresh bounded round, so a review that failed on an agent defect can recover once the agent is fixed.
public sealed partial class VideoGameDirectorAgent
{
    internal const string CreativeReviewDecisionType = "video-game.creative-review-recovery.v1";
    internal const string CreativeReviewWaitMarker = "Creative review paused for owner decision";
    internal const string RetryCreativeReviewOption = "retry-creative-review";
    internal const string ReviseBriefOption = "revise-production-brief";
    internal const string KeepCreativeReviewPausedOption = "keep-paused";
    internal const int CreativeReviewAttempts = 3;

    internal sealed record CreativeReviewDecisionContext(Guid SessionId, Guid ParentSessionId, Guid DocumentId,
        Guid RevisionId, string RevisionSha256, string? Reason);

    internal static CreativeReviewDecisionContext? CreativeReviewDecision(DecisionRecord decision)
    {
        if (decision.TypeKey != CreativeReviewDecisionType || decision.TypeData is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("creativeReview", out var value)) return null;
        try { return value.Deserialize<CreativeReviewDecisionContext>(ProjectApprovalJson); }
        catch (JsonException) { return null; }
    }

    // Failures made after the owner's latest choice for this review; each choice gets a fresh bounded round.
    internal static int CreativeReviewFailuresSince(IEnumerable<AgentCoordinationTurn> turns, Guid creative, DateTimeOffset? since) =>
        turns.Count(x => x.SpeakerOrganizationUserId == creative && IsInvalidCreativeReview(x.Content) && (since is null || x.CreatedAt > since));

    // The latest owner choice for this review session, or null when none has been requested.
    private static async Task<(DecisionRecord? Latest, int Count)> ReadCreativeReviewDecisionAsync(Guid workstream, Guid sessionId,
        AgentRuntimeContext context, CancellationToken token)
    {
        var bound = (await context.Platform.ReadDecisionsAsync(new(WorkstreamId: workstream), token))
            .Where(x => CreativeReviewDecision(x)?.SessionId == sessionId).OrderBy(x => x.CreatedAt).ToList();
        return (bound.LastOrDefault(), bound.Count);
    }

    // Where a review stands under the owner's latest choice: null when another attempt may run now,
    // otherwise the blocked result to return.
    internal static string? CreativeReviewHold(DecisionRecord? latest) => latest switch
    {
        { Status: DecisionStatuses.Pending } => $"{CreativeReviewWaitMarker} {latest.Id:D}. Naomi's review is waiting for the owner's choice.",
        { Status: DecisionStatuses.Decided, SelectedOptionId: ReviseBriefOption } =>
            $"The owner chose a revised production brief (decision {latest.Id:D}); Gabriel will submit a new revision for a new creative review.",
        { Status: DecisionStatuses.Decided, SelectedOptionId: not RetryCreativeReviewOption } =>
            $"{CreativeReviewWaitMarker} {latest.Id:D}: the owner kept the creative review paused.",
        _ => null
    };

    private static DateTimeOffset? CreativeReviewRoundStart(DecisionRecord? latest) =>
        latest is { Status: DecisionStatuses.Decided, SelectedOptionId: RetryCreativeReviewOption } ? latest.UpdatedAt : null;

    private static async Task<AgentCoordinationTurnResult> RaiseCreativeReviewDecisionAsync(AgentCoordinationTurnRequest request,
        CreativeReviewRequest input, string? reason, int round, AgentRuntimeContext context, CancellationToken token)
    {
        if (input.ParentSessionId is not { } parent || !Guid.TryParse(context.Identity?.ManagerEmployeeId, out _))
            return AgentCoordinationTurnResult.Blocked(
                "Naomi's creative review response is incomplete; the exact evidence is retained for bounded recovery. " + InvalidCreativeReview);
        var because = string.IsNullOrWhiteSpace(reason) ? "" : $" The last response was rejected because {reason}.";
        var summary = $"Naomi's creative review of the production brief could not complete after {CreativeReviewAttempts} attempts.{because} " +
            "Producer planning and staffing wait for this review. Choose how to continue.";
        var decision = await context.Platform.RequestDecisionAsync(new(input.WorkstreamId, CreativeReviewDecisionType,
            summary[..Math.Min(summary.Length, 3000)], "material-strategy-change",
            [new(RetryCreativeReviewOption, "Retry the creative review", "Naomi reviews the same brief again with a fresh round of attempts."),
             new(ReviseBriefOption, "Ask Gabriel to revise the brief", "Gabriel submits a new brief revision, which gets a new creative review."),
             new(KeepCreativeReviewPausedOption, "Keep it paused", "Planning and staffing stay blocked until you choose again.")],
            RetryCreativeReviewOption,
            [new("artifact", input.DocumentId, input.RevisionId, input.Sha256, PitchProtocol.DocumentType, "Draft")], null,
            "Only the production brief's creative review and the planning that depends on it are paused.", null,
            $"creative-review-recovery:{request.SessionId:N}:{round}",
            JsonSerializer.SerializeToElement(new { creativeReview = new CreativeReviewDecisionContext(request.SessionId, parent,
                input.DocumentId, input.RevisionId, input.Sha256, reason) }, ProjectApprovalJson)), token);
        return AgentCoordinationTurnResult.Blocked($"{CreativeReviewWaitMarker} {decision.Id:D}. Naomi's review could not complete " +
            $"after {CreativeReviewAttempts} attempts; I have asked my manager how to continue.");
    }

    // Acts on the owner's choice: a retry resumes the review, a revision resumes the brief collaboration so I can
    // ask Gabriel for a new revision, and keeping it paused changes nothing.
    internal static async Task ReconcileCreativeReviewDecisionAsync(DecisionRecord decision, AgentRuntimeContext context, CancellationToken token)
    {
        if (decision.Status != DecisionStatuses.Decided || CreativeReviewDecision(decision) is not { } binding) return;
        if (decision.SelectedOptionId == RetryCreativeReviewOption)
        {
            var session = await context.Platform.Communication.ReadCoordinationAsync(binding.SessionId, token);
            if (session.Status == "Blocked" && session.Initiator.OrganizationUserId.ToString("D") == context.Identity?.EmployeeId)
                await context.Platform.Communication.ResumeCoordinationAsync(session.Id, session.Revision,
                    $"The owner chose to retry the creative review (decision {decision.Id:D}).",
                    $"creative-review-retry:{decision.Id:N}", token);
        }
        else if (decision.SelectedOptionId == ReviseBriefOption)
        {
            var parent = await context.Platform.Communication.ReadCoordinationAsync(binding.ParentSessionId, token);
            if (parent.Status == "Blocked" && parent.Initiator.OrganizationUserId.ToString("D") == context.Identity?.EmployeeId &&
                parent.Turns.LastOrDefault()?.Content.StartsWith("Awaiting Naomi's creative review", StringComparison.Ordinal) == true)
                await context.Platform.Communication.ResumeCoordinationAsync(parent.Id, parent.Revision,
                    $"The owner chose a revised production brief (decision {decision.Id:D}). Marcus relays it to Gabriel.",
                    $"creative-review-revise:{decision.Id:N}", token);
        }
    }

    // In the brief collaboration: after the owner chose a revision, ask Gabriel for a new brief revision once.
    private static async Task<AgentCoordinationTurnResult?> RequestBriefRevisionAsync(AgentCoordinationTurnRequest request,
        GameDirectorOperatingState state, ArtifactDocument document, ArtifactRevision revision, string pitchDigest,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.WorkstreamId is not { } project) return null;
        var chosen = (await context.Platform.ReadDecisionsAsync(new(WorkstreamId: project), token))
            .Where(x => x.Status == DecisionStatuses.Decided && x.SelectedOptionId == ReviseBriefOption &&
                CreativeReviewDecision(x) is { } binding && binding.ParentSessionId == request.SessionId &&
                binding.DocumentId == document.Id && binding.RevisionId == revision.Id)
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        if (chosen is null || request.Transcript.Any(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
                x.Artifact?.Type == PitchProtocol.ReplyType &&
                x.Artifact.Payload.Deserialize<PitchReply>(PitchProtocol.Json)?.OwnerDecisionId == chosen.Id)) return null;
        var reason = CreativeReviewDecision(chosen)!.Reason;
        var guidance = $"Authoritative manager decision {chosen.Id:D}: revise the production brief. Naomi's creative review of this " +
            "revision could not complete" + (string.IsNullOrWhiteSpace(reason) ? "." : $"; the last response was rejected because {reason}.") +
            "\nGabriel, submit a new revision of the brief within the approved pitch and GDD; it will receive a new creative review. " +
            "This decision does not itself approve the brief or spending.";
        return AgentCoordinationTurnResult.Continue(guidance, PitchProtocol.Artifact(PitchProtocol.ReplyType, pitchDigest,
            new PitchReply(pitchDigest, document.Id, revision.Id, revision.ContentSha256, false, guidance, "") { OwnerDecisionId = chosen.Id }));
    }
}
