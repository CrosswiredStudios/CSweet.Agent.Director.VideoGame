using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string PitchWaitMarker = "Production brief paused for manager decision";
    internal sealed record PitchDecisionContext(Guid SessionId, Guid DocumentId, Guid RevisionId,
        string RevisionSha256, Guid ProducerId, string PitchDigest);

    internal static PitchDecisionContext? PitchDecision(DecisionRecord decision)
    {
        if (decision.TypeKey != "video-game.management-direction.v1" || decision.TypeData is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("pitchRefinement", out var value)) return null;
        try { return value.Deserialize<PitchDecisionContext>(PitchProtocol.Json); }
        catch (JsonException) { return null; }
    }

    private async Task<AgentCoordinationTurnResult?> ReadPitchOwnerWaitAsync(AgentCoordinationTurnRequest request,
        GameDirectorOperatingState state, PitchReview review, AgentRuntimeContext context, CancellationToken token)
    {
        var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: state.WorkstreamId), token);
        foreach (var decision in decisions.OrderByDescending(x => x.CreatedAt))
        {
            var binding = PitchDecision(decision);
            if (binding is null || binding.SessionId != request.SessionId || binding.DocumentId != review.DocumentId ||
                binding.ProducerId != request.Counterpart.OrganizationUserId || binding.PitchDigest != review.PitchDigest) continue;
            if (decision.Status == DecisionStatuses.Pending)
                return AgentCoordinationTurnResult.Blocked($"{PitchWaitMarker} {decision.Id:D}. Gabriel, pause brief refinement. I own the escalation and will relay the recorded answer before resuming.");
            if (decision.Status != DecisionStatuses.Decided) continue;
            if (decision.SelectedOptionId == "pause-affected-work")
                return AgentCoordinationTurnResult.Blocked($"{PitchWaitMarker} {decision.Id:D}: the manager retained the pause. {decision.Rationale}");
            if (request.Transcript.Any(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
                    x.Artifact?.Type == PitchProtocol.ReplyType &&
                    x.Artifact.Payload.Deserialize<PitchReply>(PitchProtocol.Json)?.OwnerDecisionId == decision.Id)) continue;
            // Relay trusted current state verbatim before asking the Producer to revise or assess readiness.
            var guidance = $"Authoritative manager decision {decision.Id:D}: {decision.SelectedOptionId}. " +
                $"Recorded answer: {decision.Rationale}\nGabriel, incorporate this direction into the brief and reassess planning readiness. " +
                "Missing dates do not block planning. This decision does not itself approve the brief or spending.";
            return AgentCoordinationTurnResult.Continue(guidance, PitchProtocol.Artifact(PitchProtocol.ReplyType,
                review.PitchDigest, new PitchReply(review.PitchDigest, review.DocumentId, review.RevisionId,
                    review.RevisionSha256, false, guidance, "") { OwnerDecisionId = decision.Id }));
        }
        return null;
    }

    private async Task<AgentCoordinationTurnResult> EscalatePitchAsync(AgentCoordinationTurnRequest request,
        GameDirectorOperatingState state, PitchReview review, string question,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.WorkstreamId is not { } project || !Guid.TryParse(context.Identity?.ManagerEmployeeId, out _))
            return AgentCoordinationTurnResult.Blocked("The production brief needs an authorized manager decision, but no manager is available. The draft is retained.");
        var binding = new PitchDecisionContext(request.SessionId, review.DocumentId, review.RevisionId,
            review.RevisionSha256, request.Counterpart.OrganizationUserId, review.PitchDigest);
        var priorAnswer = request.Transcript.Where(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId &&
                x.Artifact?.Type == PitchProtocol.ReplyType)
            .Select(x => x.Artifact!.Payload.Deserialize<PitchReply>(PitchProtocol.Json)?.OwnerDecisionId)
            .LastOrDefault(x => x.HasValue);
        var decision = await context.Platform.RequestDecisionAsync(new(project, "video-game.management-direction.v1",
            question[..Math.Min(question.Length, 3000)], "material-strategy-change",
            [new("provide-direction", "Provide direction", "Supply the specific binding answer in your response."),
             new("continue-current-plan", "Continue current plan", "Proceed within accepted scope and existing resource authority, using completion forecasts."),
             new("pause-affected-work", "Keep affected work paused", "Retain this decision dependency while other authorized work continues.")],
            "continue-current-plan", [new("artifact", review.DocumentId, review.RevisionId, review.RevisionSha256,
                PitchProtocol.DocumentType, "Draft")], null,
            "Only the affected brief refinement is paused. Marcus owns the decision and relay to Gabriel.", null,
            $"pitch-owner:{request.SessionId:N}:{review.RevisionSha256}:{priorAnswer:N}",
            JsonSerializer.SerializeToElement(new { pitchRefinement = binding }, PitchProtocol.Json)), token);
        return AgentCoordinationTurnResult.Blocked($"{PitchWaitMarker} {decision.Id:D}. Gabriel, pause refinement and retain this exact draft. I have raised the decision to my manager and will relay the answer before resuming.");
    }

    internal async Task<bool> ReconcilePitchDecisionAsync(GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.WorkstreamId is not { } project || state.HandoffSessionId is not { } sessionId) return false;
        var decisions = await context.Platform.ReadDecisionsAsync(new(WorkstreamId: project), token);
        var bound = decisions.Where(x => PitchDecision(x) is { } binding && binding.SessionId == sessionId &&
            binding.ProducerId == state.ProducerEmployeeId && binding.PitchDigest == state.AcceptedVision?.Digest).ToList();
        if (bound.Count == 0) return false;
        var session = await context.Platform.Communication.ReadCoordinationAsync(sessionId, token);
        if (session.WorkContext?.WorkstreamId != project || session.Initiator.OrganizationUserId.ToString("D") != context.Identity?.EmployeeId ||
            session.Target.OrganizationUserId != state.ProducerEmployeeId) return false;
        if (bound.Any(x => x.Status == DecisionStatuses.Pending)) return true;
        var latest = bound.Where(x => x.Status == DecisionStatuses.Decided).OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        if (latest is null) return false;
        if (latest.SelectedOptionId == "pause-affected-work") return true;
        if (session.Status == "Blocked" && session.FinalSummary?.StartsWith(PitchWaitMarker, StringComparison.Ordinal) == true)
            await context.Platform.Communication.ResumeCoordinationAsync(session.Id, session.Revision,
                $"Manager decision {latest.Id:D} is recorded. Marcus must read it and relay its authoritative answer to Gabriel.",
                $"pitch-decision-resume:{latest.Id:N}:{latest.Revision}", token);
        return false;
    }
}
