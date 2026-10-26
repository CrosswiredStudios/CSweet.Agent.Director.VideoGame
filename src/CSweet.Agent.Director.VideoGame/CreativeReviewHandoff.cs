using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal const string CreativeReviewRequestType = "video-game.creative-review.request.v1";
    internal const string CreativeReviewResultType = "video-game.creative-review.result.v1";
    internal const string InvalidCreativeReview = "Creative review did not return actionable, consistent findings.";
    private const string CreativeReviewIndexKey = "director-creative-review-parents";
    internal sealed record CreativeReviewRequest(Guid WorkstreamId, Guid TeamId, Guid PitchArtifactId, Guid PitchRevisionId,
        string PitchSha256, Guid DocumentId, Guid RevisionId, string Sha256, string Purpose, Guid? ParentSessionId = null);
    internal sealed record CreativeReviewResult(string Disposition, string Summary, IReadOnlyList<string> Findings,
        IReadOnlyList<string> RequiredChanges, bool RequiresDirector);
    private sealed record CreativeReviewReceipt(CreativeReviewRequest Request, CreativeReviewResult Review);
    private sealed record CreativeReviewIndex(IReadOnlyList<Guid> Parents);
    private static string ReviewKey(CreativeReviewRequest input) => $"director-creative-review:{input.WorkstreamId:N}:{input.RevisionId:N}:{input.PitchRevisionId:N}";

    private async Task<CreativeReviewResult?> EnsureCreativeReviewAsync(GameDirectorOperatingState state,
        ArtifactDocument document, ArtifactRevision revision, Guid? parentSession,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.CreativeDirectorEmployeeId is not { } creative || state.AcceptedVision is not { } pitch ||
            state.WorkstreamId is not { } project || state.TeamId is not { } team)
            throw new InvalidOperationException("Creative acceptance requires the assigned Creative Director and approved project pitch.");
        var input = new CreativeReviewRequest(project, team, pitch.ArtifactId, pitch.ArtifactRevisionId,
            pitch.ArtifactRevisionHash, document.Id, revision.Id, revision.ContentSha256,
            "Review creative consistency and player experience within the approved pitch.", parentSession);
        var key = ReviewKey(input);
        var receipt = await context.Platform.ReadOperatingStateAsync<CreativeReviewReceipt>(key, token);
        if (receipt is not null) return receipt.Payload.Review;
        if (parentSession is { } parent)
        {
            var index = await context.Platform.ReadOperatingStateAsync<CreativeReviewIndex>(CreativeReviewIndexKey, token);
            if (index?.Payload.Parents.Contains(parent) != true)
                await context.Platform.WriteOperatingStateAsync(new(CreativeReviewIndexKey, "director.creative-review-index.v1", 1,
                    "Active", new Dictionary<string, string>(), [], key, [], parent,
                    JsonSerializer.SerializeToElement(new CreativeReviewIndex((index?.Payload.Parents ?? []).Append(parent).TakeLast(100).ToArray())),
                    index?.Revision, $"creative-review-parent:{parent:N}:{revision.Id:N}:{pitch.ArtifactRevisionId:N}"), token);
        }
        await context.Platform.Communication.StartCoordinationAsync(new(creative,
            "Creative review of exact project evidence", input.Purpose, ["An evidence-backed creative disposition"],
            "Return creative findings only. Do not approve budget, delivery, technical correctness, QA or launch.",
            pitch.ConversationId, pitch.ChatTurnId, pitch.MessageId, key + ":session",
            CollaborationActions.WithDocuments(new(CreativeReviewRequestType, "1.0", input.Sha256, 1, true,
                JsonSerializer.SerializeToElement(input, ProjectApprovalJson)),
                [new(pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash), new(document.Id, revision.Id, revision.ContentSha256)]))
            { WorkContext = ProjectWorkContext(state, context, document.Id) }, token);
        return null;
    }

    private async Task<AgentCoordinationTurnResult> ReceiveCreativeReviewAsync(AgentCoordinationTurnRequest request,
        AgentRuntimeContext context, CancellationToken token)
    {
        var state = (await ReadStateForCoordinationAsync(request, context, token)).State;
        var initial = request.Transcript.FirstOrDefault(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
            x.Artifact?.Type == CreativeReviewRequestType)?.Artifact?.Payload.Deserialize<CreativeReviewRequest>(ProjectApprovalJson);
        var target = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == state.CreativeDirectorEmployeeId);
        if (initial is not null && request.Counterpart.OrganizationUserId == state.CreativeDirectorEmployeeId &&
            initial.WorkstreamId == state.WorkstreamId && initial.TeamId == state.TeamId &&
            initial.PitchSha256 == state.AcceptedVision?.ArtifactRevisionHash &&
            target is { Disposition: "Blocked", Artifact: null, Content: InvalidCreativeReview })
        {
            var failures = request.Transcript.Count(x => x.SpeakerOrganizationUserId == state.CreativeDirectorEmployeeId && x.Content == InvalidCreativeReview);
            if (!request.IsFinalization && failures < 3)
                return AgentCoordinationTurnResult.Continue(
                    $"Naomi, retry the exact creative review after the response-contract failure (attempt {failures + 1}/3). Return substantive findings and a consistent disposition; unresolved Director questions may accompany changes-required. Do not approve incomplete evidence.",
                    request.Transcript.First(x => x.Artifact?.Type == CreativeReviewRequestType).Artifact is { } original
                        ? new(CreativeReviewRequestType, "1.0", initial.Sha256, 1, true, original.Payload) : null);
            return AgentCoordinationTurnResult.Blocked("Naomi's creative review response is incomplete; the exact evidence is retained for bounded recovery. " + target.Content);
        }
        var artifact = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == state.CreativeDirectorEmployeeId &&
            x.Artifact?.Type == CreativeReviewResultType)?.Artifact;
        var receipt = artifact?.Payload.Deserialize<CreativeReviewReceipt>(ProjectApprovalJson);
        if (initial is null || receipt is null || initial != receipt.Request || request.Counterpart.OrganizationUserId != state.CreativeDirectorEmployeeId ||
            initial.WorkstreamId != state.WorkstreamId || initial.TeamId != state.TeamId || artifact!.Key != initial.Sha256 ||
            initial.PitchSha256 != state.AcceptedVision?.ArtifactRevisionHash || !ValidCreativeReview(receipt.Review))
            return AgentCoordinationTurnResult.Blocked("Creative review must match the assigned reviewer, exact evidence and current approved pitch.");
        var document = await context.Platform.Artifacts.GetAsync(initial.DocumentId, token);
        if (document.WorkstreamId != initial.WorkstreamId || document.TeamId != initial.TeamId ||
            !document.Revisions.Any(x => x.Id == initial.RevisionId && x.ContentSha256 == initial.Sha256))
            return AgentCoordinationTurnResult.Blocked("The creative review evidence is unavailable or outside the project.");
        await PitchProtocol.CachedAsync(ReviewKey(initial), context, () => Task.FromResult(receipt), token);
        if (initial.ParentSessionId is { } parent) await ResumeCreativeParentAsync(parent, initial, context, token);
        await QueueProducerDocumentationAsync(state, context, token);
        return AgentCoordinationTurnResult.Completed("Naomi's creative findings are recorded; business approval and delivery remain with their accountable owners.");
    }

    internal static bool ValidCreativeReview(CreativeReviewResult review) =>
        !string.IsNullOrWhiteSpace(review.Summary) && review.Findings is not null && review.RequiredChanges is not null &&
        (review.Disposition == "accepted" ? !review.RequiresDirector && review.RequiredChanges.Count == 0 :
         review.Disposition == "changes-required" ? review.RequiredChanges.Count > 0 && (!review.RequiresDirector || review.Findings.Count > 0) :
         review.Disposition == "escalate" && review.RequiresDirector && review.Findings.Count > 0);

    private static async Task ResumeCreativeParentAsync(Guid parent, CreativeReviewRequest evidence, AgentRuntimeContext context, CancellationToken token)
    {
        var session = await context.Platform.Communication.ReadCoordinationAsync(parent, token);
        if (CanResumeCreativeParent(session, evidence))
            await context.Platform.Communication.ResumeCoordinationAsync(parent, session.Revision,
                "Exact creative review evidence is available.", $"creative-review-resume:{parent:N}:{session.Revision}", token);
    }

    internal static bool CanResumeCreativeParent(AgentCoordinationSession parent, CreativeReviewRequest evidence)
    {
        if (parent.Id != evidence.ParentSessionId || parent.WorkContext?.WorkstreamId != evidence.WorkstreamId ||
            parent.WorkContext.TeamId != evidence.TeamId || parent.Status != "Blocked" ||
            parent.Turns.LastOrDefault()?.Content.StartsWith("Awaiting Naomi's creative review", StringComparison.Ordinal) != true) return false;
        var current = parent.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == parent.Target.OrganizationUserId &&
            x.Artifact?.Type == PitchProtocol.ReviewType)?.Artifact?.Payload.Deserialize<PitchReview>(PitchProtocol.Json);
        return current is not null && current.DocumentId == evidence.DocumentId && current.RevisionId == evidence.RevisionId &&
            current.RevisionSha256 == evidence.Sha256;
    }

    internal static async Task RecoverIncompleteCreativeReviewAsync(Guid sessionId, AgentRuntimeContext context, CancellationToken token)
    {
        // Discovery is a wake hint. Re-read authoritative state and recheck exact ownership before resuming.
        var session = await context.Platform.Communication.ReadCoordinationAsync(sessionId, token);
        if (session.Status != "Blocked" || session.Initiator.OrganizationUserId.ToString("D") != context.Identity?.EmployeeId) return;
        var last = session.Turns.LastOrDefault(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId);
        if (last is not { Disposition: "Blocked", Artifact: null, Content: InvalidCreativeReview } ||
            session.Turns.Count(x => x.SpeakerOrganizationUserId == session.Target.OrganizationUserId && x.Content == InvalidCreativeReview) >= 3) return;
        var input = session.Turns.FirstOrDefault(x => x.SpeakerOrganizationUserId == session.Initiator.OrganizationUserId &&
            x.Artifact?.Type == CreativeReviewRequestType)?.Artifact?.Payload.Deserialize<CreativeReviewRequest>(ProjectApprovalJson);
        if (input is null || input.WorkstreamId != session.WorkContext?.WorkstreamId || input.TeamId != session.WorkContext?.TeamId) return;
        var current = await ReadStateByKeyAsync(ProjectStateKey(input.WorkstreamId, null), context, token);
        if (current.State.CreativeDirectorEmployeeId != session.Target.OrganizationUserId ||
            current.State.HandoffSessionId != input.ParentSessionId || current.State.AcceptedVision is not { } pitch ||
            pitch.ArtifactId != input.PitchArtifactId || pitch.ArtifactRevisionId != input.PitchRevisionId || pitch.ArtifactRevisionHash != input.PitchSha256) return;
        await context.Platform.Communication.ResumeCoordinationAsync(session.Id, session.Revision,
            "Recover the incomplete creative response for the same approved pitch and exact brief revision.",
            $"creative-response-recovery:{session.Id:N}:{session.Revision}", token);
    }

    private static async Task RecoverCreativeReviewParentsAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var index = await context.Platform.ReadOperatingStateAsync<CreativeReviewIndex>(CreativeReviewIndexKey, token);
        if (index is null) return;
        // Bounded reconnect discovery repairs a completion racing its parent's blocked transition.
        var sessions = await context.Platform.Communication.ListCoordinationAsync(token: token);
        foreach (var child in sessions.Sessions.Where(x => x.Status is "Completed" or "Blocked"))
        {
            var input = child.Turns.FirstOrDefault(x => x.Artifact?.Type == CreativeReviewRequestType)?.Artifact?.Payload.Deserialize<CreativeReviewRequest>(ProjectApprovalJson);
            if (input?.ParentSessionId is not { } parent || !index.Payload.Parents.Contains(parent)) continue;
            if (child.Status == "Blocked")
                await RecoverIncompleteCreativeReviewAsync(child.Id, context, token);
            else if (await context.Platform.ReadOperatingStateAsync<CreativeReviewReceipt>(ReviewKey(input), token) is not null)
                await ResumeCreativeParentAsync(parent, input, context, token);
        }
    }
}
