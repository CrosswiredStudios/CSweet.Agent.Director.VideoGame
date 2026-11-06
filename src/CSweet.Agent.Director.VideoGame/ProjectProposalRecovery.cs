using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private sealed record ProjectProposalResume(Guid SessionId, long ExpectedRevision, Guid ProducerId,
        string PitchDigest, string SchemaDigest);

    private async Task<(GameDirectorOperatingState State, long? Revision, bool Ready)> SaveProjectProposalWaitAsync(
        GameDirectorOperatingState state, long? revision, AgentCoordinationSession session, Guid reviewId,
        string reason, AgentRuntimeContext context, CancellationToken token)
    {
        var wait = new ProjectProposalWait(session.Id, session.Revision, session.Status, reason);
        if (state.ProposalWait == wait) return (state, revision, false);
        var saved = await SaveStateAsync(state with { ProposalWait = wait, Phase = GameDirectorPhase.WorkstreamPlanPending },
            revision, reviewId, $"proposal-wait:{session.Id:N}:{session.Revision}:{Digest(reason)[..16]}", context, token);
        return (saved.State, saved.Revision, false);
    }

    private Task<(GameDirectorOperatingState State, long? Revision, bool Ready)> WaitForProjectProposalAsync(
        GameDirectorOperatingState state, long? revision, AgentCoordinationSession session, Guid reviewId,
        AgentRuntimeContext context, CancellationToken token) => ContinueProjectProposalWaitAsync(state, revision, session,
            reviewId, context, async (current, cancellation) => {
                var tools = await current.GetModelToolsAsync([WorkstreamCapabilityNames.PlanProposeV2], cancellation);
                return ((AIFunctionDeclaration)tools.Single()).JsonSchema;
            }, token);

    // Separate the catalog read from continuation for deterministic role tests. Production
    // always supplies the authenticated current tool listing above; no model selects a schema.
    internal async Task<(GameDirectorOperatingState State, long? Revision, bool Ready)> ContinueProjectProposalWaitAsync(
        GameDirectorOperatingState state, long? revision, AgentCoordinationSession session, Guid reviewId,
        AgentRuntimeContext context, Func<AgentRuntimeContext, CancellationToken, Task<JsonElement>> readContract, CancellationToken token)
    {
        var reason = "Project creation is waiting for correction of the existing Producer collaboration: " +
            ReconcileStallPolicy.Summarize(session.FinalSummary);
        // A terminal outcome is durable work state, not an exception to retry on every wake.
        // Cancellation, unknown failures and already committed proposals never authorize a new submission.
        if (state.WorkstreamProposalId.HasValue || session.Status != "Blocked" || session.SourceKind != "Chat" ||
            !Guid.TryParse(context.Identity?.EmployeeId, out var director) || session.Initiator.OrganizationUserId != director ||
            session.Target.OrganizationUserId != state.ProducerEmployeeId || session.Id != state.WorkstreamProposalSessionId ||
            state.AcceptedVision is not { } pitch || session.MaximumTurns is { } maximum && session.NextTurnOrdinal >= maximum)
            return await SaveProjectProposalWaitAsync(state, revision, session, reviewId, reason, context, token);

        var blocked = session.Turns.LastOrDefault(t => t.SpeakerOrganizationUserId == session.Target.OrganizationUserId);
        var original = session.Turns.FirstOrDefault(t => t.SpeakerOrganizationUserId == director &&
            t.Artifact?.Type == "video-game.project-foundation.request.v1")?.Artifact;
        var match = Regex.Match(blocked?.Content ?? "", @"JSON Schema validation failed: \$\.([a-zA-Z][a-zA-Z0-9]*) is not allowed\.");
        if (blocked is not { Disposition: "Blocked", Artifact: null } || !match.Success || original?.Key != pitch.Digest)
            return await SaveProjectProposalWaitAsync(state, revision, session, reviewId, reason, context, token);

        try
        {
            var template = original.Payload.Deserialize<WorkstreamPlanProposalV2Request>(ProjectApprovalJson);
            if (template is null || template.AccountableManagerOrganizationUserId != session.Target.OrganizationUserId || template.InitialTeamId != state.TeamId ||
                !template.InitialEvidence.Any(e => e.ResourceId == pitch.ArtifactId && e.RevisionId == pitch.ArtifactRevisionId &&
                    e.Digest == pitch.ArtifactRevisionHash && e.Status == "Accepted"))
                return await SaveProjectProposalWaitAsync(state, revision, session, reviewId, reason, context, token);
            var schema = await readContract(context, token);
            var payload = JsonSerializer.SerializeToElement(template, ProjectApprovalJson);
            if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(match.Groups[1].Value, out _) ||
                payload.EnumerateObject().Any(p => !properties.TryGetProperty(p.Name, out var property) || !AllowsType(property, p.Value)))
                return await SaveProjectProposalWaitAsync(state, revision, session, reviewId, reason, context, token);

            await context.Platform.Artifacts.ReadAcceptedAsync(new(pitch.ArtifactId, pitch.ArtifactRevisionId, pitch.ArtifactRevisionHash), token);
            var schemaDigest = Digest(schema.GetRawText());
            var key = $"project-proposal-resume:{session.Id:N}:{schemaDigest[..24]}";
            // One recovery per actual advertised contract. Persist before resuming so an
            // uncertain response replays the same command, not a fresh recovery window.
            var receipt = await PitchProtocol.CachedAsync(key, context, () => Task.FromResult(new ProjectProposalResume(
                session.Id, session.Revision, session.Target.OrganizationUserId, pitch.Digest, schemaDigest)), token);
            if (receipt.SessionId != session.Id || receipt.ExpectedRevision != session.Revision || receipt.ProducerId != session.Target.OrganizationUserId ||
                receipt.PitchDigest != pitch.Digest || receipt.SchemaDigest != schemaDigest)
                return await SaveProjectProposalWaitAsync(state, revision, session, reviewId,
                    "The proposal remains blocked after recovery against the current contract. I must reconcile the retained result before another submission.", context, token);
            var resumed = await context.Platform.Communication.ResumeCoordinationAsync(session.Id, receipt.ExpectedRevision,
                "The current proposal contract accepts the previously rejected fields. Continue the same accepted pitch and retained proposal command; preserve any already submitted approval and do not regenerate scope.", key, token);
            return await SaveProjectProposalWaitAsync(state, revision, resumed, reviewId,
                "The existing project-proposal collaboration is resuming with the Producer against the corrected contract.", context, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is PlatformCapabilityException or HttpRequestException or TimeoutException or JsonException or InvalidOperationException)
        {
            return await SaveProjectProposalWaitAsync(state, revision, session, reviewId,
                "Project-proposal recovery is waiting for current authorized platform state: " + ReconcileStallPolicy.Summarize(error.Message), context, token);
        }
    }

    private static bool AllowsType(JsonElement schema, JsonElement value)
    {
        if (!schema.TryGetProperty("type", out var type)) return true; // Unconstrained profile metadata.
        var expected = value.ValueKind switch {
            JsonValueKind.Null => "null", JsonValueKind.Object => "object", JsonValueKind.Array => "array",
            JsonValueKind.String => "string", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "number"
        };
        bool Matches(string? name) => name == expected || name == "integer" && expected == "number" && value.TryGetInt64(out _);
        return type.ValueKind == JsonValueKind.String ? Matches(type.GetString()) :
            type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => Matches(t.GetString()));
    }
}
