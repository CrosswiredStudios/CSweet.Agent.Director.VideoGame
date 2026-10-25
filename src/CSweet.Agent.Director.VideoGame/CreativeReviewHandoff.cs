using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal const string CreativeReviewRequestType = "video-game.creative-review.request.v1";
    internal const string CreativeReviewResultType = "video-game.creative-review.result.v1";
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
        if (initial.ParentSessionId is { } parent) await ResumeCreativeParentAsync(parent, context, token);
        await QueueProducerDocumentationAsync(state, context, token);
        return AgentCoordinationTurnResult.Completed("Naomi's creative findings are recorded; business approval and delivery remain with their accountable owners.");
    }

    internal static bool ValidCreativeReview(CreativeReviewResult review) =>
        !string.IsNullOrWhiteSpace(review.Summary) && review.Findings is not null && review.RequiredChanges is not null &&
        (review.Disposition == "accepted" ? !review.RequiresDirector && review.Findings.Count == 0 && review.RequiredChanges.Count == 0 :
         review.Disposition == "changes-required" ? !review.RequiresDirector && review.RequiredChanges.Count > 0 :
         review.Disposition == "escalate" && review.RequiresDirector && review.Findings.Count > 0);

    private static async Task ResumeCreativeParentAsync(Guid parent, AgentRuntimeContext context, CancellationToken token)
    {
        var session = await context.Platform.Communication.ReadCoordinationAsync(parent, token);
        if (session.Status == "Blocked" && session.Turns.LastOrDefault()?.Content.StartsWith("Awaiting Naomi's creative review", StringComparison.Ordinal) == true)
            await context.Platform.Communication.ResumeCoordinationAsync(parent, session.Revision,
                "Exact creative review evidence is available.", $"creative-review-resume:{parent:N}:{session.Revision}", token);
    }

    private static async Task RecoverCreativeReviewParentsAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var index = await context.Platform.ReadOperatingStateAsync<CreativeReviewIndex>(CreativeReviewIndexKey, token);
        if (index is null) return;
        // Bounded reconnect discovery repairs a completion racing its parent's blocked transition.
        var sessions = await context.Platform.Communication.ListCoordinationAsync(token: token);
        foreach (var child in sessions.Sessions.Where(x => x.Status == "Completed"))
        {
            var input = child.Turns.FirstOrDefault(x => x.Artifact?.Type == CreativeReviewRequestType)?.Artifact?.Payload.Deserialize<CreativeReviewRequest>(ProjectApprovalJson);
            if (input?.ParentSessionId is { } parent && index.Payload.Parents.Contains(parent) &&
                await context.Platform.ReadOperatingStateAsync<CreativeReviewReceipt>(ReviewKey(input), token) is not null)
                await ResumeCreativeParentAsync(parent, context, token);
        }
    }
}
