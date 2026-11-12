using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string CreativeDesignRequestType = GameCreativeDesignProtocolV1.RequestType;
    private const string CreativeDesignResultType = GameCreativeDesignProtocolV1.ResultType;

    private async Task<(GameDirectorOperatingState State, long? Revision, bool Ready)> EnsureCreativeDesignAsync(
        GameDirectorOperatingState state, long? revision, AgentRuntimeContext context, CancellationToken token)
    {
        if (state.CreativeDesignArtifactId is not null && state.CreativeDesignRevisionId is not null)
            return (state, revision, true);
        if (state.AcceptedVision is not { } pitch || state.WorkstreamId is not { } project ||
            state.TeamId is not { } team || state.CreativeDirectorEmployeeId is not { } creative ||
            state.ProducerEmployeeId is not { } producer || !Guid.TryParse(context.Identity?.EmployeeId, out var director))
            return (state, revision, false);
        if (state.CreativeDesignSessionId is null)
        {
            var input = new GameCreativeDesignRequestV1(project, team, director, creative, producer,
                pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash);
            var session = await context.Platform.Communication.StartCoordinationAsync(new(
                creative, "Develop creative direction and GDD", "Develop the exact approved pitch within its constraints.",
                ["A GDD reviewed against the CEO-approved pitch"],
                "Prepare the GDD and return its exact revision. Do not change approved platforms, budget or scope.",
                pitch.ConversationId, pitch.ChatTurnId, pitch.MessageId,
                $"creative-design:{project:N}:{pitch.ArtifactRevisionId:N}",
                CollaborationActions.WithDocuments(new(CreativeDesignRequestType, "1.0", pitch.ArtifactRevisionHash, 1, true,
                    JsonSerializer.SerializeToElement(input, ProjectApprovalJson)),
                    [new CollaborationDocumentReference(pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash)]))
                { WorkContext = ProjectWorkContext(state, context, pitch.ArtifactRevisionId) }, token);
            await QueueCreativeDesignCardAsync(state, project, team, session, pitch, context, token);
            var saved = await SaveStateAsync(state with { CreativeDesignSessionId = session.Id }, revision,
                session.Id, $"creative-design-session:{session.Id:N}", context, token);
            return (saved.State, saved.Revision, false);
        }
        return (state, revision, false);
    }

    private async Task<AgentCoordinationTurnResult> ReviewCreativeDesignAsync(AgentCoordinationTurnRequest request,
        AgentRuntimeContext context, CancellationToken token)
    {
        var current = await ReadStateForCoordinationAsync(request, context, token);
        var state = current.State;
        if (state.AcceptedVision is not { } pitch ||
            request.Counterpart.OrganizationUserId != state.CreativeDirectorEmployeeId ||
            request.WorkContext?.WorkstreamId != state.WorkstreamId || request.WorkContext?.TeamId != state.TeamId)
            return AgentCoordinationTurnResult.Blocked("Creative design must come from the assigned Creative Director and match the approved project and pitch.");
        // A terminal reply supersedes older submissions. In particular, a blocked revision must
        // never replay the previous review and return Continue during the initiator's finalization.
        var latest = request.Transcript.Where(x => x.SpeakerOrganizationUserId == state.CreativeDirectorEmployeeId)
            .OrderByDescending(x => x.Ordinal).FirstOrDefault();
        if (latest?.Disposition == AgentCoordinationDispositions.Blocked)
        {
            // An explicit resume of a blocked chat returns to its initiator. Relay the retained
            // assignment/feedback only on that non-final turn; do not re-review the stale result.
            var invalidDesign = IsRecoverableDesignGeneration(latest, state, pitch.ArtifactRevisionHash);
            var retry = request.Transcript.Where(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
                    x.Artifact?.Key == pitch.ArtifactRevisionHash &&
                    x.Artifact.Type is CreativeDesignRequestType or GameCreativeDesignProtocolV1.FeedbackType)
                .OrderByDescending(x => x.Ordinal).FirstOrDefault()?.Artifact;
            if (!request.IsFinalization && invalidDesign && retry is not null)
                return AgentCoordinationTurnResult.Continue("Retry the complete GDD within the same approved pitch and retained review findings. " +
                    "Correct the reported generation failure; do not resubmit the prior rejected revision.",
                    new(retry.Type, retry.SchemaVersion, retry.Key, retry.PageOrdinal, retry.IsFinalPage, retry.Payload));
            return AgentCoordinationTurnResult.Blocked("Creative design is blocked: " + latest.Content +
                " The Video Game Director owns recovery in Communications after the Creative Director's reported cause is corrected; the prior draft and findings are retained. No new GDD revision was accepted.");
        }
        var submission = latest?.Artifact?.Type == CreativeDesignResultType ? latest.Artifact : null;
        var result = submission?.Payload.Deserialize<GameCreativeDesignResultV1>(ProjectApprovalJson);
        if (result is null || result.PitchSha256 != pitch.ArtifactRevisionHash)
            return AgentCoordinationTurnResult.Blocked("Creative design must come from the assigned Creative Director and match the approved pitch.");
        var document = await context.Platform.Artifacts.GetAsync(result.DocumentId, token);
        var exact = document.Revisions.SingleOrDefault(x => x.Id == result.RevisionId && x.ContentSha256 == result.RevisionSha256);
        if (exact is null || document.WorkstreamId != state.WorkstreamId || document.TeamId != state.TeamId ||
            document.DocumentType != GameCreativeDesignProtocolV1.DocumentType)
            return AgentCoordinationTurnResult.Blocked("The GDD does not match the project and exact submitted revision.");
        if (exact.Status is not ("Accepted" or "AcceptedWithFindings"))
        {
            SemanticArtifactReview review;
            try
            {
                var reviewKey = AgentOperatingStateKey.Create("director-gdd-semantic.v1", new {
                    document.Id, RevisionId = exact.Id, exact.ContentSha256, pitch.ArtifactRevisionHash, Version,
                    Provider = Settings.GetGuid("llmProviderId"), Model = Settings.GetString("llmModel") });
                review = await CrosswiredStudios.VideoGame.PitchCollaboration.PitchProtocol.CachedAsync(reviewKey, context,
                    () => ReviewGddSemanticallyAsync(exact.Content, state, context, token), token);
            }
            catch (Exception error) when (error is AgentModelJsonException or GddReviewUnavailableException)
            {
                var failure = new GameCreativeDesignFailureV1(state.WorkstreamId!.Value, state.TeamId!.Value,
                    pitch.ArtifactRevisionHash, error is AgentModelJsonException ? GameCreativeDesignFailureCodes.ReviewInvalid : GameCreativeDesignFailureCodes.ReviewUnavailable, error.Message);
                return AgentCoordinationTurnResult.Blocked("GDD semantic review could not complete: " + error.Message +
                    " The Video Game Director must correct review availability or output and retry this exact revision. No acceptance was recorded.",
                    new(GameCreativeDesignProtocolV1.FailureType, "1.0", pitch.ArtifactRevisionHash, 1, true,
                        JsonSerializer.SerializeToElement(failure, ProjectApprovalJson)));
            }
            var findings = review.Findings.Select(x => new ReviewFinding(x.Code, x.Section,
                NormalizeFindingSeverity(x.Severity), x.Blocking, x.Summary, x.RequiredFollowUp)).ToArray();
            var disposition = NormalizeArtifactDisposition(review.Disposition, findings);
            var decided = await context.Platform.Artifacts.DecideStructuredAsync(new(document.Id, exact.Id, exact.ContentSha256,
                RubricForArtifact(document.DocumentType, GameCreativeDesignProtocolV1.DocumentType), disposition, findings, review.Summary,
                $"director-gdd-decision:{exact.Id:N}:{exact.ContentSha256}"), token);
            if (disposition is "accepted" or "accepted-with-findings" &&
                (decided.Id != document.Id || decided.WorkstreamId != state.WorkstreamId || decided.TeamId != state.TeamId ||
                 !decided.Revisions.Any(x => x.Id == exact.Id && x.ContentSha256 == exact.ContentSha256 &&
                    x.Status is "Accepted" or "AcceptedWithFindings")))
                return AgentCoordinationTurnResult.Blocked("The platform did not accept this exact GDD revision. " +
                    "The Video Game Director must inspect its current decision and obtain a new submitted revision if an earlier rejection is final. No accepted GDD or Producer handoff was recorded.");
            if (disposition is not ("accepted" or "accepted-with-findings"))
                return request.IsFinalization || request.Transcript.Count(x => x.Artifact?.Type == GameCreativeDesignProtocolV1.FeedbackType) >= 3
                    ? AgentCoordinationTurnResult.Blocked("The GDD still requires changes: " + review.Summary +
                        " The Video Game Director must resolve the retained findings before resuming creative design in Communications.")
                    : AgentCoordinationTurnResult.Continue("Revise the GDD against these findings, preserving the CEO-approved pitch.",
                        new(GameCreativeDesignProtocolV1.FeedbackType, "1.0", pitch.ArtifactRevisionHash, exact.Number, true,
                            JsonSerializer.SerializeToElement(new GameCreativeDesignFeedbackV1(document.Id, exact.Id, review.Summary,
                                findings.Select(x => new GameCreativeDesignFindingV1(x.Code,x.Section,x.Severity,x.Blocking,x.Summary,x.RequiredFollowUp)).ToArray()), ProjectApprovalJson)));
        }
        await SaveStateAsync(state with { CreativeDesignArtifactId = document.Id, CreativeDesignRevisionId = exact.Id },
            current.Revision, request.SessionId, $"director-gdd-accepted:{exact.Id:N}", context, token);
        await QueueProducerDocumentationAsync(state, context, token);
        return AgentCoordinationTurnResult.Completed("The GDD is accepted within the approved pitch. Producer delivery planning can continue.");
    }

    private static bool IsRecoverableDesignGeneration(AgentCoordinationTurn latest, GameDirectorOperatingState state, string pitchHash)
    {
        if (latest.Artifact is { Type: GameCreativeDesignProtocolV1.FailureType } artifact)
        {
            try
            {
                var failure = artifact.Payload.Deserialize<GameCreativeDesignFailureV1>(ProjectApprovalJson);
                return artifact.Key == pitchHash && failure is not null && failure.WorkstreamId == state.WorkstreamId &&
                    failure.TeamId == state.TeamId && failure.PitchSha256 == pitchHash && failure.Code is
                        GameCreativeDesignFailureCodes.EmptyGeneration or GameCreativeDesignFailureCodes.IncompleteGeneration;
            }
            catch (JsonException) { return false; }
        }
        // Compatibility only for persisted pre-contract failures. New agents use the typed artifact.
        return latest.Artifact is null && (latest.Content.StartsWith("The GDD is missing required creative sections;", StringComparison.Ordinal) ||
            latest.Content.StartsWith("The GDD could not be validated after one corrective generation:", StringComparison.Ordinal));
    }
}
