using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal sealed record ProducerCorrectionRecovery(Guid SessionId, long ExpectedRevision, Guid ProducerId,
        Guid DocumentId, Guid RevisionId, string Sha256, string PitchDigest);

    private static string CorrectionRecoveryKey(Guid session, string hash) =>
        $"producer-correction-recovery:{session:N}:{Digest(hash)[..16]}";

    // Only the old exhausted-draft handoff with a retained, ready candidate and an unstructured
    // manager reply qualifies. New correction receipts keep their existing bounded policy.
    internal static PitchReview? RetainedCorrectionReview(GameDirectorOperatingState state, Guid director,
        Guid sessionId, AgentWorkContext? work, IReadOnlyList<AgentCoordinationTurn> turns)
    {
        if (state.HandoffSessionId != sessionId || state.WorkstreamId is not { } project || state.TeamId is not { } team ||
            work?.WorkstreamId != project || work.TeamId != team || state.ProducerEmployeeId is not { } producer ||
            state.AcceptedVision is not { } pitch || director == producer) return null;
        var blocked = turns.LastOrDefault(x => x.SpeakerOrganizationUserId == producer);
        if (blocked is not { Disposition: "Blocked", Artifact: null } ||
            !blocked.Content.StartsWith("Planning refinement needs a concrete manager answer or a new recorded owner decision.", StringComparison.Ordinal)) return null;
        var initial = turns.FirstOrDefault(x => x.SpeakerOrganizationUserId == director && x.Artifact?.Type == PitchProtocol.BriefType)?.Artifact;
        var brief = initial?.Payload.Deserialize<PitchBrief>(PitchProtocol.Json);
        if (brief is null || initial!.Key != pitch.Digest || brief.PitchArtifactId != pitch.ArtifactId ||
            brief.PitchRevisionId != pitch.ArtifactRevisionId || brief.PitchSha256 != pitch.ArtifactRevisionHash ||
            brief.Vision.HighLevelGddArtifactId != state.CreativeDesignArtifactId ||
            brief.Vision.HighLevelGddAcceptedRevisionId != state.CreativeDesignRevisionId) return null;
        var reviewArtifact = turns.LastOrDefault(x => x.Ordinal < blocked.Ordinal && x.SpeakerOrganizationUserId == producer && x.Artifact?.Type == PitchProtocol.ReviewType)?.Artifact;
        var review = reviewArtifact?.Payload.Deserialize<PitchReview>(PitchProtocol.Json);
        var reply = turns.LastOrDefault(x => x.Ordinal < blocked.Ordinal && x.SpeakerOrganizationUserId == director && x.Artifact?.Type == PitchProtocol.ReplyType)
            ?.Artifact?.Payload.Deserialize<PitchReply>(PitchProtocol.Json);
        return review is { Ready: true, Questions.Count: 0 } && !string.IsNullOrWhiteSpace(review.Rationale) &&
            review.PitchDigest == pitch.Digest && reviewArtifact!.Key == pitch.Digest && reply is { Accepted: false, Correction: null, OwnerDecisionId: null } &&
            reply.PitchDigest == pitch.Digest && reply.DocumentId == review.DocumentId && reply.RevisionId == review.RevisionId &&
            reply.RevisionSha256 == review.RevisionSha256 && !string.IsNullOrWhiteSpace(reply.Guidance) ? review : null;
    }

    internal static async Task<bool> RecoverProducerCorrectionAsync(GameDirectorOperatingState state,
        AgentCoordinationSession session, AgentRuntimeContext context, CancellationToken token)
    {
        if (session.Status != "Blocked" || session.SourceKind != "Chat" ||
            session.MaximumTurns is { } turnLimit && session.NextTurnOrdinal >= turnLimit ||
            !Guid.TryParse(context.Identity?.EmployeeId, out var director) || session.Initiator.OrganizationUserId != director ||
            session.Target.OrganizationUserId != state.ProducerEmployeeId) return false;
        var review = RetainedCorrectionReview(state, director, session.Id, session.WorkContext, session.Turns);
        if (review is null) return false;
        var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: state.WorkstreamId), token);
        var bound = decisions.Where(x => PitchDecision(x) is { } d && d.SessionId == session.Id &&
            d.DocumentId == review.DocumentId && d.ProducerId == state.ProducerEmployeeId && d.PitchDigest == review.PitchDigest).ToArray();
        if (bound.Any(x => x.Status == DecisionStatuses.Pending) || bound.Where(x => x.Status == DecisionStatuses.Decided)
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefault()?.SelectedOptionId == "pause-affected-work") return false;
        var document = await context.Platform.Artifacts.GetAsync(review.DocumentId, token);
        var revision = document.Revisions.SingleOrDefault(x => x.Id == review.RevisionId && x.ContentSha256 == review.RevisionSha256);
        if (document.WorkstreamId != state.WorkstreamId || document.TeamId != state.TeamId || document.DocumentType != PitchProtocol.DocumentType ||
            document.LatestRevisionId != review.RevisionId || revision?.Status != "Submitted" || !PitchProtocol.ValidDocument(revision.Content)) return false;
        var pitch = state.AcceptedVision!;
        await context.Platform.Artifacts.ReadAcceptedAsync(new(pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash), token);
        var brief = session.Turns.First(x => x.SpeakerOrganizationUserId == director && x.Artifact?.Type == PitchProtocol.BriefType)
            .Artifact!.Payload.Deserialize<PitchBrief>(PitchProtocol.Json)!;
        if (string.IsNullOrWhiteSpace(brief.Vision.HighLevelGddRevisionSha256)) return false;
        await context.Platform.Artifacts.ReadAcceptedAsync(new(state.CreativeDesignArtifactId!.Value,
            state.CreativeDesignRevisionId!.Value, brief.Vision.HighLevelGddRevisionSha256), token);
        var evidence = new CreativeReviewRequest(state.WorkstreamId!.Value, state.TeamId!.Value, pitch.ArtifactId,
            pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash, review.DocumentId, review.RevisionId, review.RevisionSha256,
            "Review creative consistency and player experience within the approved pitch.", session.Id);
        var receipt = await context.Platform.ReadOperatingStateAsync<CreativeReviewReceipt>(ReviewKey(evidence), token);
        if (receipt is null || receipt.Payload.Request != evidence || !ValidCreativeReview(receipt.Payload.Review) ||
            receipt.Payload.Review is not { Disposition: "changes-required", RequiresDirector: false } ||
            CorrectionFor(receipt.Payload.Review, review.RevisionSha256) is null) return false;
        // Persist the exact authorized retry before the external resume. An uncertain response can
        // retry this same key; a later blocked revision can never open another recovery window.
        var key = CorrectionRecoveryKey(session.Id, review.RevisionSha256);
        var marker = await PitchProtocol.CachedAsync(key, context, () => Task.FromResult(new ProducerCorrectionRecovery(
            session.Id, session.Revision, session.Target.OrganizationUserId, review.DocumentId, review.RevisionId,
            review.RevisionSha256, review.PitchDigest)), token);
        if (marker.SessionId != session.Id || marker.ExpectedRevision != session.Revision || marker.ProducerId != session.Target.OrganizationUserId ||
            marker.DocumentId != review.DocumentId || marker.RevisionId != review.RevisionId || marker.Sha256 != review.RevisionSha256 ||
            marker.PitchDigest != review.PitchDigest) return false;
        await context.Platform.Communication.ResumeCoordinationAsync(session.Id, marker.ExpectedRevision,
            "Reassess the retained ready production brief after the correction-policy upgrade. Relay its exact independent creative findings as a bounded correction; do not reset scope or approve staffing.", key, token);
        return true;
    }

    private static async Task<PitchReview?> ResumedCorrectionReviewAsync(GameDirectorOperatingState state,
        AgentCoordinationTurnRequest request, AgentRuntimeContext context, CancellationToken token)
    {
        if (request.IsFinalization || request.SourceKind != "Chat" || request.Counterpart.OrganizationUserId != state.ProducerEmployeeId ||
            request.Self.OrganizationUserId.ToString("D") != context.Identity?.EmployeeId) return null;
        var review = RetainedCorrectionReview(state, request.Self.OrganizationUserId, request.SessionId, request.WorkContext, request.Transcript);
        if (review is null) return null;
        var marker = await context.Platform.ReadOperatingStateAsync<ProducerCorrectionRecovery>(CorrectionRecoveryKey(request.SessionId, review.RevisionSha256), token);
        if (!(marker?.Payload is { } recovery && request.ExpectedRevision == recovery.ExpectedRevision + 1 &&
            recovery.SessionId == request.SessionId && recovery.ProducerId == request.Counterpart.OrganizationUserId &&
            recovery.DocumentId == review.DocumentId && recovery.RevisionId == review.RevisionId && recovery.Sha256 == review.RevisionSha256 &&
            recovery.PitchDigest == review.PitchDigest)) return null;
        // The resume event is a wake hint. Recheck the candidate after queueing rather than letting
        // a concurrent draft edit make the old ready review authoritative again.
        var document = await context.Platform.Artifacts.GetAsync(review.DocumentId, token);
        return document.WorkstreamId == state.WorkstreamId && document.TeamId == state.TeamId &&
            document.LatestRevisionId == review.RevisionId && document.Revisions.Any(x => x.Id == review.RevisionId &&
                x.ContentSha256 == review.RevisionSha256 && x.Status == "Submitted") ? review : null;
    }
}
