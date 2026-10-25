using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string CreativeDesignRequestType = "video-game.creative-design.request.v1";
    private const string CreativeDesignResultType = "video-game.creative-design.result.v1";
    private sealed record CreativeDesignRequest(Guid WorkstreamId, Guid TeamId, Guid DirectorEmployeeId,
        Guid CreativeDirectorEmployeeId, Guid ProducerEmployeeId, Guid PitchArtifactId, Guid PitchRevisionId, string PitchSha256);
    private sealed record CreativeDesignResult(string PitchSha256, Guid DocumentId, Guid RevisionId, string RevisionSha256);

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
            var input = new CreativeDesignRequest(project, team, director, creative, producer,
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
        var submission = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == state.CreativeDirectorEmployeeId &&
            x.Artifact?.Type == CreativeDesignResultType)?.Artifact;
        var result = submission?.Payload.Deserialize<CreativeDesignResult>(ProjectApprovalJson);
        if (result is null || state.AcceptedVision is not { } pitch ||
            request.Counterpart.OrganizationUserId != state.CreativeDirectorEmployeeId ||
            result.PitchSha256 != pitch.ArtifactRevisionHash)
            return AgentCoordinationTurnResult.Blocked("Creative design must come from the assigned Creative Director and match the approved pitch.");
        var document = await context.Platform.Artifacts.GetAsync(result.DocumentId, token);
        var exact = document.Revisions.SingleOrDefault(x => x.Id == result.RevisionId && x.ContentSha256 == result.RevisionSha256);
        if (exact is null || document.WorkstreamId != state.WorkstreamId || document.TeamId != state.TeamId)
            return AgentCoordinationTurnResult.Blocked("The GDD does not match the project and exact submitted revision.");
        if (exact.Status is not ("Accepted" or "AcceptedWithFindings"))
        {
            var review = await CrosswiredStudios.VideoGame.PitchCollaboration.PitchProtocol.CachedAsync(
                $"director-gdd-review:{exact.Id:N}:{exact.ContentSha256}", context,
                () => ReviewDetailedArtifactAsync(document, exact, "video-game.gdd.v1", state, context, token), token);
            var findings = review.Findings.Select(x => new ReviewFinding(x.Code, x.Section,
                NormalizeFindingSeverity(x.Severity), x.Blocking, x.Summary, x.RequiredFollowUp)).ToArray();
            var disposition = NormalizeArtifactDisposition(review.Disposition, findings);
            await context.Platform.Artifacts.DecideStructuredAsync(new(document.Id, exact.Id, exact.ContentSha256,
                RubricForArtifact(document.DocumentType, "video-game.gdd.v1"), disposition, findings, review.Summary,
                $"director-gdd-decision:{exact.Id:N}:{exact.ContentSha256}"), token);
            if (disposition is not ("accepted" or "accepted-with-findings"))
                return request.Transcript.Count(x => x.Artifact?.Type == "video-game.creative-design.feedback.v1") >= 3
                    ? AgentCoordinationTurnResult.Blocked("The GDD still requires changes after three review rounds: " + review.Summary)
                    : AgentCoordinationTurnResult.Continue("Revise the GDD against these findings, preserving the CEO-approved pitch.",
                        new("video-game.creative-design.feedback.v1", "1.0", pitch.ArtifactRevisionHash, exact.Number, true,
                            JsonSerializer.SerializeToElement(new { documentId = document.Id, revisionId = exact.Id,
                                summary = review.Summary, findings }, ProjectApprovalJson)));
        }
        await SaveStateAsync(state with { CreativeDesignArtifactId = document.Id, CreativeDesignRevisionId = exact.Id },
            current.Revision, request.SessionId, $"director-gdd-accepted:{exact.Id:N}", context, token);
        await QueueProducerDocumentationAsync(state, context, token);
        return AgentCoordinationTurnResult.Completed("The GDD is accepted within the approved pitch. Producer delivery planning can continue.");
    }
}
