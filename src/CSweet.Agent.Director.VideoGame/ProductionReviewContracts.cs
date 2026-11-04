using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CrosswiredStudios.VideoGame.ProductionReview;

// Publisher-owned, additive coordination data. These are evidence, never grants or approvals.
internal sealed record CreativeCheck(string Id, string Status, string Evidence);
internal sealed record CreativeIssue(string Id, string CheckId, string Severity, string Status,
    string Requirement, string Evidence, string Owner, string Milestone, string Change);

internal static class CreativeChecklist
{
    public const string Version = "production-brief.v1";
    public static readonly string[] CheckIds = ["scope", "player-loop", "controls-accessibility",
        "presentation-audio", "validation-plan", "constraints-non-goals"];

    public static bool Valid(IReadOnlyList<CreativeCheck>? checks, IReadOnlyList<CreativeIssue>? issues,
        IReadOnlyList<CreativeIssue>? previous = null)
    {
        if (checks is null || issues is null || checks.Count != CheckIds.Length || issues.Count > 40 || checks.Any(x => x is null) || issues.Any(x => x is null) ||
            !checks.Select(x => x.Id).Order().SequenceEqual(CheckIds.Order()) ||
            checks.Any(x => x.Status is not ("passed" or "blocking" or "advisory" or "not-applicable") || string.IsNullOrWhiteSpace(x.Evidence)) ||
            issues.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != issues.Count ||
            issues.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 80 || !CheckIds.Contains(x.CheckId) ||
                x.Severity is not ("blocking" or "advisory") || x.Status is not ("open" or "resolved") ||
                new[] { x.Requirement, x.Evidence, x.Owner, x.Milestone, x.Change }.Any(string.IsNullOrWhiteSpace))) return false;
        foreach (var check in checks)
        {
            var open = issues.Where(x => x.CheckId == check.Id && x.Status == "open").ToArray();
            var status = open.Any(x => x.Severity == "blocking") ? "blocking" : open.Length > 0 ? "advisory" : null;
            if (status is not null && check.Status != status || status is null && check.Status is "blocking" or "advisory") return false;
        }
        // Every previously blocking issue needs an explicit, evidenced disposition. No silent drops,
        // relabelling a blocker as advisory, or reusing its ID for a different requirement.
        return (previous ?? []).Where(x => x.Severity == "blocking").All(prior =>
            issues.Any(x => x.Id == prior.Id && x.CheckId == prior.CheckId && x.Requirement == prior.Requirement &&
                x.Severity == "blocking"));
    }

    public static CreativeIssue[] Blockers(IEnumerable<CreativeIssue> issues) =>
        issues.Where(x => x.Severity == "blocking" && x.Status == "open").ToArray();

    public static bool MatchesDisposition(string disposition, bool requiresDirector, IReadOnlyList<string> changes, IReadOnlyList<CreativeIssue> issues)
    {
        var blockers = Blockers(issues);
        return changes.SequenceEqual(blockers.Select(x => x.Change)) && (blockers.Length == 0
            ? disposition == "accepted" && !requiresDirector
            : disposition == "changes-required" || disposition == "escalate" && requiresDirector);
    }
}

internal sealed record ProductionCorrection(string ReviewDigest, IReadOnlyList<CreativeIssue> Issues)
{
    public static ProductionCorrection Create(string revisionHash, IReadOnlyList<CreativeIssue> issues) =>
        new(Digest(revisionHash, issues), issues);
    public bool Valid(string revisionHash) => Issues is { Count: > 0 and <= 40 } && !Issues.Any(x => x is null) &&
        Issues.Select(x => x.Id).Distinct().Count() == Issues.Count &&
        Issues.All(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Requirement) &&
            !string.IsNullOrWhiteSpace(x.Change) && x.Severity is "blocking" or "advisory" && x.Status is "open" or "resolved") &&
        CreativeChecklist.Blockers(Issues).Length > 0 && ReviewDigest == Digest(revisionHash, Issues);
    private static string Digest(string revisionHash, IReadOnlyList<CreativeIssue> issues) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            revisionHash, issues = issues.OrderBy(x => x.Id, StringComparer.Ordinal)
        })))).ToLowerInvariant();
}

internal sealed record ProductionSource(Guid DocumentId, Guid RevisionId, string Sha256, string DocumentType, string Content);
internal sealed record ProductionSources(int Version, Guid WorkstreamId, Guid TeamId, ProductionSource Pitch, ProductionSource Gdd)
{
    public const string DocumentType = "video-game.production-sources.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Prefix = "# Exact approved production sources\n\n```json\n";
    private const string PitchStart = "\n```\n\n## Accepted pitch\n";
    private const string GddStart = "\n\n## Accepted GDD\n";
    private sealed record Header(int Version, Guid WorkstreamId, Guid TeamId, ProductionSource Pitch, ProductionSource Gdd,
        int PitchCharacters, int GddCharacters);
    public string Serialize() => Prefix + JsonSerializer.Serialize(new Header(Version, WorkstreamId, TeamId,
        Pitch with { Content = "" }, Gdd with { Content = "" }, Pitch.Content.Length, Gdd.Content.Length), Json) +
        PitchStart + Pitch.Content + GddStart + Gdd.Content;

    // Length-delimited originals stay readable to downstream paragraph/coverage tools, even when
    // the source itself contains headings or fence markers. Source content is never regenerated.
    public static ProductionSources? Parse(string content)
    {
        if (!content.StartsWith(Prefix, StringComparison.Ordinal)) return JsonSerializer.Deserialize<ProductionSources>(content, Json);
        var end = content.IndexOf(PitchStart, Prefix.Length, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Production source metadata is incomplete.");
        var header = JsonSerializer.Deserialize<Header>(content[Prefix.Length..end], Json);
        if (header is null || header.Pitch is null || header.Gdd is null || header.PitchCharacters is < 1 or > 192000 ||
            header.GddCharacters is < 1 or > 192000) throw new InvalidOperationException("Production source lengths are invalid.");
        var start = end + PitchStart.Length;
        var boundary = start + header.PitchCharacters;
        if (boundary + GddStart.Length + header.GddCharacters != content.Length ||
            !content.AsSpan(boundary, GddStart.Length).SequenceEqual(GddStart.AsSpan()))
            throw new InvalidOperationException("Production source content does not match its retained boundaries.");
        return new(header.Version, header.WorkstreamId, header.TeamId,
            header.Pitch with { Content = content.Substring(start, header.PitchCharacters) },
            header.Gdd with { Content = content.Substring(boundary + GddStart.Length, header.GddCharacters) });
    }
    public static string WorkingBrief(string content) => content.Replace("\r\n", "\n")
        .Split("\n<!-- accepted-pitch-sources -->", StringSplitOptions.None)[0]
        .Split("\n<!-- production-sources-reference -->", StringSplitOptions.None)[0];

    public static async Task<(ArtifactDocument Document, ArtifactRevision Revision, ProductionSources Sources)> ReadAsync(
        CollaborationDocumentReference reference, Guid project, Guid team, AgentRuntimeContext context, CancellationToken token)
    {
        var document = await context.Platform.Artifacts.GetAsync(reference.DocumentId, token);
        var revision = document.Revisions.SingleOrDefault(x => x.Id == reference.RevisionId && x.ContentSha256 == reference.ContentSha256);
        if (document.WorkstreamId != project || document.TeamId != team || document.DocumentType != DocumentType || revision is null)
            throw new InvalidOperationException("Production sources must match the exact project, team, document type, revision and hash.");
        var sources = Parse(revision.Content);
        if (sources is null || sources.Version != 1 || sources.WorkstreamId != project || sources.TeamId != team ||
            sources.Pitch is null || sources.Gdd is null ||
            new[] { sources.Pitch, sources.Gdd }.Any(x => x.DocumentId == Guid.Empty || x.RevisionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(x.Sha256) || string.IsNullOrWhiteSpace(x.Content)))
            throw new InvalidOperationException("Production source evidence is incomplete or belongs to another scope.");
        return (document, revision, sources);
    }
}
