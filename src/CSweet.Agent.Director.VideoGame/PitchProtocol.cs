using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CrosswiredStudios.VideoGame.PitchCollaboration;

internal sealed record PitchBrief(GameVisionBrief Vision, Guid PitchArtifactId, Guid PitchRevisionId, string PitchSha256)
{
    public IReadOnlyList<CollaborationDocumentReference> DocumentReferences => new[]
    {
        new CollaborationDocumentReference(PitchArtifactId, PitchRevisionId, PitchSha256),
        new CollaborationDocumentReference(Vision.HighLevelGddArtifactId!.Value, Vision.HighLevelGddAcceptedRevisionId!.Value, Vision.HighLevelGddRevisionSha256!)
    }.DistinctBy(x => x.DocumentId).ToArray();
}
internal sealed record PitchReview(string PitchDigest, Guid DocumentId, Guid RevisionId, string RevisionSha256,
    bool Ready, IReadOnlyList<string> Questions, string Rationale)
{
    public CollaborationDocumentReference? SourceEvidence { get; init; }
    public IReadOnlyList<CollaborationDocumentReference> DocumentReferences => SourceEvidence is null
        ? [new(DocumentId, RevisionId, RevisionSha256)] : [new(DocumentId, RevisionId, RevisionSha256), SourceEvidence];
}
internal sealed record PitchReply(string PitchDigest, Guid DocumentId, Guid RevisionId, string RevisionSha256,
    bool Accepted, string Guidance, string SuggestedMarkdown)
{
    public Guid? OwnerDecisionId { get; init; }
    public ProductionCorrection? Correction { get; init; }
}
internal sealed record ProducerReview(bool Ready, IReadOnlyList<string> Questions, string Rationale, string DraftMarkdown);
internal sealed record DirectorReview(bool Accept, string Guidance, string SuggestedMarkdown,
    bool RequiresOwner = false, string? OwnerQuestion = null);

internal static class PitchProtocol
{
    public const string BriefType = "video-game.production.pitch-brief.v1";
    public const string ReviewType = "video-game.production.pitch-review.v1";
    public const string ReplyType = "video-game.production.pitch-reply.v1";
    public const string DocumentType = "video-game.production-plan.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Derives an absent suggested replacement and names, for the model to correct, what code cannot settle.
    internal static DirectorReview NormalizeDirectorReview(DirectorReview review)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(review.Guidance)) problems.Add("guidance is empty");
        else if (review.Guidance.Length > 16000) problems.Add($"guidance has {review.Guidance.Length} characters; shorten it to at most 16000");
        var suggested = review.SuggestedMarkdown ?? "";
        if (suggested.Length > 48000) problems.Add($"suggestedMarkdown has {suggested.Length} characters; return an empty string or at most 48000");
        if (review.RequiresOwner && string.IsNullOrWhiteSpace(review.OwnerQuestion))
            problems.Add("requiresOwner needs one focused ownerQuestion; otherwise set requiresOwner to false");
        else if (review.RequiresOwner && review.OwnerQuestion!.Length > 3000)
            problems.Add($"ownerQuestion has {review.OwnerQuestion.Length} characters; ask one focused question of at most 3000");
        if (problems.Count > 0) throw new AgentModelJsonException(string.Join("; ", problems));
        return review with { SuggestedMarkdown = suggested, OwnerQuestion = review.RequiresOwner ? review.OwnerQuestion : null };
    }

    public static DirectorReview? ParseDirectorReview(string response)
    {
        // Providers may wrap the JSON in a code fence or append reasoning markup. Read the first
        // complete object; malformed or incomplete JSON still fails and the turn is retried.
        var start = response.IndexOf('{');
        if (start < 0) throw new JsonException("The Video Game Director response contains no JSON object.");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(response[start..]));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Deserialize<DirectorReview>(Json);
    }

    public static bool CanPlan(ProducerReview review) => CollaborationActions.IsReady(review.Ready, review.Questions, [], review.Rationale) && ValidDocument(review.DraftMarkdown);
    public static bool ValidDocument(string? markdown) => !string.IsNullOrWhiteSpace(markdown) && markdown.Length <= 192000 &&
        new[] { "Scope", "Player experience", "Non-goals", "Acceptance criteria", "Deliverables", "Constraints", "Risks", "Open questions" }
            .All(heading => markdown.Contains($"## {heading}", StringComparison.OrdinalIgnoreCase));
    public static bool Matches(PitchReview review, PitchReply reply) => review.PitchDigest == reply.PitchDigest && CollaborationActions.CanAcceptHandoff(
            new(new(review.DocumentId, review.RevisionId, review.RevisionSha256), review.Ready, review.Questions, [], review.Rationale),
            new(new(reply.DocumentId, reply.RevisionId, reply.RevisionSha256), reply.Accepted, reply.Guidance, []));
    public static AgentCoordinationArtifactSubmission Artifact<T>(string type, string key, T value)
    {
        var artifact = new AgentCoordinationArtifactSubmission(type, "1.0", key, 1, true, JsonSerializer.SerializeToElement(value, Json));
        return value switch
        {
            PitchBrief brief => CollaborationActions.WithDocuments(artifact, brief.DocumentReferences),
            PitchReview review => CollaborationActions.WithDocuments(artifact, review.DocumentReferences),
            _ => artifact
        };
    }

    // Cache model decisions before any document mutation so duplicate deliveries cannot change the decision.
    public static async Task<T> CachedAsync<T>(string key, AgentRuntimeContext context, Func<Task<T>> create, CancellationToken token) where T : class
    {
        var prior = await context.Platform.ReadOperatingStateAsync<T>(key, token);
        if (prior is not null) return prior.Payload;
        var value = await create();
        try
        {
            await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(key,
                "video-game.pitch-turn.v1", 1, "Active", new Dictionary<string, string>(), [],
                CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent.Digest(key), [], Guid.NewGuid(),
                JsonSerializer.SerializeToElement(value), null, key), token);
        }
        catch (PlatformCapabilityException ex) when (ex.Code == PlatformCapabilityErrorCode.Conflict)
        {
            var winner = await context.Platform.ReadOperatingStateAsync<T>(key, token);
            if (winner is null) throw;
            return winner.Payload;
        }
        return value;
    }

    public static Task<(ArtifactDocument Document, ArtifactRevision Revision)> ReadAcceptedAsync(
        Guid documentId, Guid revisionId, string digest, AgentRuntimeContext context, CancellationToken token) =>
        context.Platform.Artifacts.ReadAcceptedAsync(new(documentId, revisionId, digest), token);
}
