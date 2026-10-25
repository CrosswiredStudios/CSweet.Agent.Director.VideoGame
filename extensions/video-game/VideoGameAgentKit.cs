using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CrosswiredStudios.VideoGame.AgentKit;

public sealed record SpecialistDelivery(
    string Summary,
    Guid ArtifactId,
    Guid RevisionId,
    string Sha256,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> RemainingRisks);

public sealed record SpecialistOperatingState
{
    public Guid WorkstreamId { get; init; }
    public Guid WorkItemId { get; init; }
    public Guid StageExecutionId { get; init; }
    public Guid AttemptId { get; init; }
    public long AssignmentRevision { get; init; }
    public string RoleKey { get; init; } = string.Empty;
    public string Status { get; init; } = "Assigned";
    public IReadOnlyDictionary<Guid, string> ExactInputDigests { get; init; } = new Dictionary<Guid, string>();
    public SpecialistDelivery? Delivery { get; init; }
    public string? Blocker { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public static class ProjectStateKeys
{
    public static string Portfolio(string roleKey) => $"video-game/{roleKey}/portfolio";
    public static string Workstream(string roleKey, Guid workstreamId) => $"video-game/{roleKey}/workstreams/{workstreamId:N}";
    public static string WorkItem(string roleKey, Guid workstreamId, Guid workItemId) =>
        $"video-game/{roleKey}/workstreams/{workstreamId:N}/items/{workItemId:N}";
}

public static class SpecialistAssignmentValidator
{
    public static WorkExecutionInputV1 Validate(WorkExecutionAssignment assignment, string expectedRoleKey)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.TaskSprintExecutionId == Guid.Empty || assignment.ExecutionId == Guid.Empty ||
            assignment.StageExecutionId == Guid.Empty || assignment.AttemptId == Guid.Empty ||
            assignment.OrganizationId == Guid.Empty || assignment.BoardId == Guid.Empty ||
            assignment.TaskSprintId == Guid.Empty || assignment.TaskItemId == Guid.Empty)
            throw new ArgumentException("Execution requires authoritative sprint, item, stage, attempt, organization, and board identity.");
        if (assignment.AssignmentRevision < 1 || assignment.Traversal < 0 || assignment.Attempt < 1)
            throw new ArgumentException("Assignment revision, traversal, and attempt must be positive.");
        if (assignment.Deadline <= DateTimeOffset.UtcNow)
            throw new ArgumentException("The authoritative execution deadline has expired.");
        var input = assignment.Input.Deserialize<WorkExecutionInputV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("The canonical work execution input is required.");
        if (input.WorkstreamId is null || input.WorkstreamId == Guid.Empty || input.TeamId is null || input.TeamId == Guid.Empty)
            throw new ArgumentException("Canonical workstream and team context are required.");
        if (input.Planning is null || input.PlanningRevision < 1)
            throw new ArgumentException("An authoritative planning revision is required.");
        var owner = input.Planning.DelegationRecommendations.SingleOrDefault(x =>
            string.Equals(x.StageKey, assignment.StageKey, StringComparison.Ordinal));
        if (owner is null || !string.Equals(owner.RequiredRoleKey, expectedRoleKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The assignment is not owned by this specialist role.");
        var requirements = input.AssignmentRequirements
            ?? throw new ArgumentException("Canonical role, skill, and capability requirements are required.");
        var selection = input.AssignmentSelection
            ?? throw new ArgumentException("Canonical assignment-selection evidence is required.");
        if (!string.Equals(requirements.RequiredRoleKey, expectedRoleKey, StringComparison.Ordinal) ||
            selection.AgentInstallationId == Guid.Empty || selection.TeamRosterRevision < 1 ||
            !IsSha256(selection.ProfileDefinitionDigest) || !IsSha256(selection.DecisionFingerprint) ||
            !requirements.RequiredCapabilityKeys.Contains("work.execution.run", StringComparer.Ordinal) ||
            requirements.RequiredSpecializationKeys.Except(selection.MatchedSpecializationKeys, StringComparer.Ordinal).Any())
            throw new UnauthorizedAccessException("The canonical assignment does not prove exact role, skill, and execution eligibility.");
        var package = input.Planning.ArtifactPackageDigest
            ?? throw new ArgumentException("An approved artifact package is required.");
        if (package.PackageId == Guid.Empty || package.Version < 1 || !IsSha256(package.Sha256) ||
            package.Members.Count == 0 || package.Members.Any(member =>
                member.ArtifactId == Guid.Empty || member.AcceptedRevisionId == Guid.Empty || !IsSha256(member.Sha256)))
            throw new ArgumentException("Every package input must bind an exact artifact revision and SHA-256 digest.");
        var calculated = ArtifactPackageDigestCalculator.Calculate(package.PackageId, package.Version, package.Members);
        if (!string.Equals(calculated, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The approved artifact package digest does not match its members.");
        return input;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public static class SubstantiveOutputValidator
{
    // Only unmistakable unresolved markers count. Ordinary prose ("placeholder art", "to be decided by
    // QA", a review rule forbidding `TODO` comments) is legitimate deliverable content, and fenced or
    // inline code is never scanned.
    private static readonly Regex[] PlaceholderMarkers =
    [
        new(@"(?m)(^|[\s(\[|*_])(TODO|TBD|FIXME)(?=\s*([:)\]|]|$))", RegexOptions.CultureInvariant),
        new(@"lorem ipsum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\[\s*(insert|add|fill in|placeholder)\b[^\]\n]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"<\s*(insert|fill in|placeholder)\b[^>\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\binsert (here|text|details|content)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];
    private static readonly Regex CodeBlocks = new(@"(```|~~~)[\s\S]*?(\1|$)", RegexOptions.CultureInvariant);
    private static readonly Regex InlineCode = new(@"`[^`\n]*`", RegexOptions.CultureInvariant);

    /// <summary>Returns why a deliverable is not substantive, or null when it passes.</summary>
    public static string? FindIssue(string markdown, params string[] requiredSections)
    {
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Length < 800)
            return "The durable deliverable is too short to be substantive.";
        var prose = InlineCode.Replace(CodeBlocks.Replace(markdown, " "), " ");
        foreach (var marker in PlaceholderMarkers)
        {
            var match = marker.Match(prose);
            if (match.Success)
                return $"The deliverable contains unresolved placeholder text: {match.Value.Trim()}.";
        }
        var normalized = markdown.ToLowerInvariant();
        foreach (var section in requiredSections)
            if (!normalized.Contains(section.ToLowerInvariant(), StringComparison.Ordinal))
                return $"The deliverable is missing required section '{section}'.";
        return null;
    }

    public static void RequireSubstantiveMarkdown(string markdown, params string[] requiredSections)
    {
        if (FindIssue(markdown, requiredSections) is { } issue) throw new InvalidOperationException(issue);
    }
}

public sealed class SpecialistBoardReporter(PlatformCapabilityClient platform)
{
    public Task<WorkItemComment> ProgressAsync(WorkExecutionAssignment assignment, string summary, CancellationToken token) =>
        platform.Work.CommentAsync(new CommentOnWorkItemRequest(assignment.BoardId, assignment.TaskItemId, summary,
            $"stage-progress:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}")
        { Kind = "progress", CausationId = assignment.StageExecutionId.ToString("D") }, token);

    public Task<WorkItemComment> BlockAsync(WorkExecutionAssignment assignment, string blocker, CancellationToken token) =>
        platform.Work.CommentAsync(new CommentOnWorkItemRequest(assignment.BoardId, assignment.TaskItemId, blocker,
            $"stage-block:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}:{Digest(blocker)}")
        { Kind = "blocker", CausationId = assignment.StageExecutionId.ToString("D") }, token);

    public Task<WorkItemComment> EvidenceAsync(WorkExecutionAssignment assignment, SpecialistDelivery delivery, CancellationToken token)
    {
        if (delivery.ArtifactId == Guid.Empty || delivery.RevisionId == Guid.Empty || delivery.Evidence.Count == 0 ||
            delivery.Sha256.Length != 64 || delivery.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Completion requires an exact durable artifact revision and attached evidence.");
        return platform.Work.CommentAsync(new CommentOnWorkItemRequest(assignment.BoardId, assignment.TaskItemId, delivery.Summary,
            $"stage-evidence:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}:{delivery.RevisionId:N}")
        { Kind = "evidence", ArtifactDigest = delivery.Sha256, CausationId = assignment.StageExecutionId.ToString("D") }, token);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

public sealed class RevisionSafeProjectState(PlatformCapabilityClient platform)
{
    public async Task<AgentOperatingState<T>> MergeAsync<T>(
        string stateKey, string schemaId, int schemaVersion, Func<T?, T> merge,
        IReadOnlyDictionary<string, string> sourceRevisions, string idempotencyKey, CancellationToken token)
        where T : class
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            AgentOperatingState<T>? current;
            try { current = await platform.ReadOperatingStateAsync<T>(stateKey, token); }
            catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound)
            { current = null; }
            var payload = merge(current?.Payload);
            try
            {
                var written = await platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(
                    stateKey, schemaId, schemaVersion, "Active", sourceRevisions, [], Fingerprint(payload), [], Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(payload), current?.Revision, $"{idempotencyKey}:{attempt}"), token);
                return new AgentOperatingState<T>(written.Id, written.StateKey, written.SchemaId, written.SchemaVersion,
                    written.Status, written.SourceRevisions, written.ConditionCodes, written.DecisionFingerprint,
                    written.OpenCommitmentCorrelations, written.AttentionReviewId,
                    written.Payload.Deserialize<T>()!, written.Revision, written.CreatedAt, written.UpdatedAt);
            }
            catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.Conflict && attempt < 3) { }
        }
        throw new InvalidOperationException("Project state could not be merged after four revision conflicts.");
    }

    private static string Fingerprint<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload))).ToLowerInvariant();
}

public abstract class VideoGameSpecialistAgentBase : CSweetAgentBase
{
    protected abstract string RoleKey { get; }
    public string DeclaredRoleKey => RoleKey;
    protected abstract string ArtifactTypeKey { get; }
    protected abstract string RolePrompt { get; }
    protected abstract IReadOnlyList<string> RequiredSections { get; }
    public string PrimaryCapability => WorkManagementCapabilityNames.ExecutionRun;

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) => builder
        .LlmProvider("llmProviderId", "LLM provider", required: true,
            description: "Brokered model used to produce role-owned project deliverables.")
        .LlmModel("llmModel", "Model", "llmProviderId", required: true,
            description: "Model used for grounded specialist work.");

    public override Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(
        AgentCoordinationTurnRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var estimateArtifact = request.Transcript.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.role-estimate-request.v1")?.Artifact;
        var estimateRequest = estimateArtifact?.Payload.Deserialize<CrosswiredStudios.VideoGame.Contracts.GameRoleEstimateCapacityRequestV1>();
        if (estimateRequest is not null)
        {
            if (!string.Equals(estimateRequest.RoleKey, RoleKey, StringComparison.Ordinal) ||
                estimateRequest.WorkItems.Count == 0 || estimateRequest.PlanningRevision <= 0 ||
                string.IsNullOrWhiteSpace(estimateRequest.PlanningDigest) ||
                estimateRequest.WorkItems.Any(x => x.AccountableRoleKey != RoleKey ||
                    x.Requirements.Count == 0 || x.AcceptanceCriteria.Count == 0 ||
                    string.IsNullOrWhiteSpace(x.ArtifactPackageDigest) ||
                    string.IsNullOrWhiteSpace(x.AssignmentDecisionFingerprint)))
                return Task.FromResult(AgentCoordinationTurnResult.Blocked(
                    "The estimate request is stale, empty, or addressed to a different accountable role."));
            var estimates = estimateRequest.WorkItems.OrderBy(x => x.WorkItemId)
                .Select(x => new CrosswiredStudios.VideoGame.Contracts.GameWorkItemEstimateV1(x.WorkItemId,
                    Math.Clamp(1m + x.AcceptanceCriteria.Count + x.DependencyWorkItemIds.Count, 1m, 13m),
                    x.Constraints.Count + x.DependencyWorkItemIds.Count > 3 ? "low" : "medium"))
                .ToList();
            var digest = CoordinationFingerprint(new
            {
                estimateRequest.RequestFingerprint,
                RoleKey,
                Items = estimates.Select(x => new { x.WorkItemId, x.EstimatePoints, x.Confidence })
            });
            var proposal = new CrosswiredStudios.VideoGame.Contracts.GameRoleEstimateCapacityProposalV1(
                estimateRequest.BoardId, RoleKey, estimateRequest.PlanningRevision,
                estimateRequest.PlanningDigest, estimates, estimates.Sum(x => x.EstimatePoints),
                ["Initial role-owned estimate assumes approved inputs, available toolchain, and no unresolved dependency."],
                [], digest);
            return Task.FromResult(AgentCoordinationTurnResult.Completed(
                $"Submitted {estimates.Count} role-owned estimates and current sprint capacity.",
                new AgentCoordinationArtifactSubmission("video-game.production.role-estimate-capacity-proposal.v1",
                    "1.0", estimateRequest.RequestFingerprint, 1, true,
                    JsonSerializer.SerializeToElement(proposal))));
        }

        var qaArtifact = request.Transcript.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.qa-readiness-request.v1")?.Artifact;
        var qaRequest = qaArtifact?.Payload.Deserialize<CrosswiredStudios.VideoGame.Contracts.GameQaSprintReadinessRequestV1>();
        if (qaRequest is not null)
        {
            if (RoleKey != CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.QualityAssurance ||
                qaRequest.Candidates.Count == 0 || qaRequest.PlanningRevision <= 0 ||
                string.IsNullOrWhiteSpace(qaRequest.PlanningDigest))
                return Task.FromResult(AgentCoordinationTurnResult.Blocked(
                    "Only the QA readiness authority may answer a current, non-empty readiness request."));
            var findings = qaRequest.Candidates.SelectMany(candidate =>
                new[]
                {
                    candidate.Requirements.Count == 0
                        ? new CrosswiredStudios.VideoGame.Contracts.GameReadinessFindingV1(candidate.WorkItemId,
                            "requirements-missing", true, "Testable requirements are missing.") : null,
                    candidate.AcceptanceCriteria.Count == 0
                        ? new CrosswiredStudios.VideoGame.Contracts.GameReadinessFindingV1(candidate.WorkItemId,
                            "acceptance-missing", true, "Testable acceptance criteria are missing.") : null,
                    candidate.EstimatePoints is null or <= 0 || string.IsNullOrWhiteSpace(candidate.EstimateSourceDigest)
                        ? new CrosswiredStudios.VideoGame.Contracts.GameReadinessFindingV1(candidate.WorkItemId,
                            "estimate-provenance-missing", true, "A positive role-owned estimate with source evidence is required.") : null,
                    string.IsNullOrWhiteSpace(candidate.ArtifactPackageDigest) ||
                    string.IsNullOrWhiteSpace(candidate.AssignmentDecisionFingerprint)
                        ? new CrosswiredStudios.VideoGame.Contracts.GameReadinessFindingV1(candidate.WorkItemId,
                            "assignment-evidence-missing", true, "Artifact-package and exact assignment evidence are required.") : null
                }.Where(x => x is not null).Cast<CrosswiredStudios.VideoGame.Contracts.GameReadinessFindingV1>()).ToList();
            var readyIds = qaRequest.Candidates.Where(candidate => findings.All(x => x.WorkItemId != candidate.WorkItemId))
                .Select(x => x.WorkItemId).Distinct().OrderBy(x => x).ToList();
            var digest = CoordinationFingerprint(new { qaRequest.RequestFingerprint, ReadyIds = readyIds, Findings = findings });
            var assessment = new CrosswiredStudios.VideoGame.Contracts.GameQaSprintReadinessAssessmentV1(
                Guid.Empty, qaRequest.PlanningRevision, qaRequest.PlanningDigest,
                findings.Count == 0, readyIds, findings, digest);
            return Task.FromResult(AgentCoordinationTurnResult.Completed(
                "Recorded QA sprint-readiness evidence for the exact candidate scope.",
                new AgentCoordinationArtifactSubmission("video-game.production.qa-sprint-readiness-assessment.v1",
                    "1.0", qaRequest.RequestFingerprint, 1, true,
                    JsonSerializer.SerializeToElement(assessment))));
        }

        var artifact = request.Transcript.LastOrDefault(x =>
            x.Artifact?.Type == "video-game.production.planning-cycle.v1")?.Artifact;
        var cycle = artifact?.Payload.Deserialize<CrosswiredStudios.VideoGame.Contracts.GameProductionPlanningCycleV1>();
        if (cycle is null)
            return Task.FromResult(AgentCoordinationTurnResult.Blocked(
                "A current, typed production planning cycle is required."));

        if (RoleKey == CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.GameDesigner)
        {
            var items = new CrosswiredStudios.VideoGame.Contracts.GameProposedWorkItemV1[]
            {
                new("feature-core-player-loop", CrosswiredStudios.VideoGame.Contracts.VideoGameWorkItemTypeKeys.Feature,
                    "Core player loop", "Deliver the player-facing core loop defined by the accepted vision.",
                    ["The accepted vision's core-loop outcome is demonstrable and measurable."], "", [], [], [], []),
                new("design-core-player-loop", CrosswiredStudios.VideoGame.Contracts.VideoGameWorkItemTypeKeys.Task,
                    "Specify the core player loop", "Turn the accepted vision into falsifiable gameplay rules, states, feedback, failure, recovery, and tuning variables.",
                    ["Rules, state transitions, feedback, edge cases, instrumentation, and validation criteria are explicit."],
                    CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.GameDesigner,
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.GameDesign,
                     CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.Gameplay],
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.PrototypeDesign],
                    [WorkManagementCapabilityNames.ExecutionRun], [])
                { ParentProposalKey = "feature-core-player-loop" },
                new("research-core-player-loop", CrosswiredStudios.VideoGame.Contracts.VideoGameWorkItemTypeKeys.ResearchSpike,
                    "Plan core-loop player validation", "Define a consent-governed playtest that tests comprehension, engagement, failure, and recovery for the approved loop.",
                    ["Research questions, participant criteria, tasks, measures, consent, and decision thresholds are explicit."],
                    CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.PlaytestResearcher,
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.PlaytestPlanning,
                     CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.ConsentGovernance],
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.ResearchAnalysis],
                    [WorkManagementCapabilityNames.ExecutionRun], ["design-core-player-loop"])
                { ParentProposalKey = "feature-core-player-loop" }
            };
            var digest = ArtifactPackageDigestCalculator.Calculate(cycle.ApprovedPackageId,
                cycle.ApprovedPackageVersion, []);
            var proposal = new CrosswiredStudios.VideoGame.Contracts.GameDesignerBacklogProposalV1(cycle, items,
                ["Do not depart from the accepted player outcome, pillars, scope, or non-goals."], [], digest);
            return Task.FromResult(AgentCoordinationTurnResult.Completed(
                "Submitted a player-outcome backlog proposal; technical feasibility and estimates remain with their authorities.",
                new AgentCoordinationArtifactSubmission("video-game.production.designer-backlog-proposal.v1", "1.0",
                    cycle.PlanningFingerprint, 1, true, JsonSerializer.SerializeToElement(proposal))));
        }

        if (RoleKey == CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.TechnicalDirector)
        {
            var items = new CrosswiredStudios.VideoGame.Contracts.GameProposedWorkItemV1[]
            {
                new("implement-core-player-loop", CrosswiredStudios.VideoGame.Contracts.VideoGameWorkItemTypeKeys.Task,
                    "Implement the core-loop prototype", "Implement the approved gameplay-system specification inside the accepted engine and performance constraints.",
                    ["A runnable, instrumented build demonstrates the specified loop and automated checks cover its critical state transitions."],
                    CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.Engineer,
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.GameplayProgramming,
                     CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.EngineIntegration],
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.AutomatedTesting],
                    [WorkManagementCapabilityNames.ExecutionRun], ["design-core-player-loop"])
                { ParentProposalKey = "feature-core-player-loop" },
                new("qa-core-player-loop", CrosswiredStudios.VideoGame.Contracts.VideoGameWorkItemTypeKeys.Task,
                    "Validate the core-loop prototype", "Validate the runnable prototype against its accepted criteria and produce reproducible defects and regression evidence.",
                    ["Build identity, test coverage, results, defects, regressions, compatibility, and accessibility findings are evidence-backed."],
                    CrosswiredStudios.VideoGame.Contracts.VideoGameRoleKeys.QualityAssurance,
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.TestPlanning,
                     CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.BuildValidation],
                    [CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.RegressionTesting,
                     CrosswiredStudios.VideoGame.Contracts.VideoGameSpecializationKeys.AccessibilityTesting],
                    [WorkManagementCapabilityNames.ExecutionRun], ["implement-core-player-loop"])
                { ParentProposalKey = "feature-core-player-loop" }
            };
            var digest = ArtifactPackageDigestCalculator.Calculate(cycle.ApprovedPackageId,
                cycle.ApprovedPackageVersion, []);
            var proposal = new CrosswiredStudios.VideoGame.Contracts.GameTechnicalDeliveryProposalV1(cycle, items,
                ["Feasibility is conditional on the exact accepted engine, target, and performance constraints in the approved package."],
                ["No technical estimate is asserted by this planning proposal."], [], digest);
            return Task.FromResult(AgentCoordinationTurnResult.Completed(
                "Submitted technical decomposition; creative authority and specialist estimates remain unchanged.",
                new AgentCoordinationArtifactSubmission("video-game.production.technical-delivery-proposal.v1", "1.0",
                    cycle.PlanningFingerprint, 1, true, JsonSerializer.SerializeToElement(proposal))));
        }

        return Task.FromResult(AgentCoordinationTurnResult.Blocked(
            $"Role '{RoleKey}' is not a member of the detailed-backlog planning quorum."));
    }

    private static string CoordinationFingerprint<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload))).ToLowerInvariant();

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Capability != WorkManagementCapabilityNames.ExecutionRun)
            return AgentWorkResult.Failure($"Capability '{request.Capability}' is not supported.");
        WorkExecutionAssignment? assignment;
        try { assignment = DeserializePayload<WorkExecutionAssignment>(request.Arguments); }
        catch (JsonException) { return AgentWorkResult.Failure("The standard work execution assignment is invalid."); }
        if (assignment is null) return AgentWorkResult.Failure("The standard work execution assignment is required.");
        WorkExecutionInputV1 canonicalInput;
        try { canonicalInput = SpecialistAssignmentValidator.Validate(assignment, RoleKey); }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        { return AgentWorkResult.Failure(exception.Message); }

        var workstreamId = canonicalInput.WorkstreamId!.Value;
        var package = canonicalInput.Planning!.ArtifactPackageDigest!;
        var stateKey = ProjectStateKeys.WorkItem(RoleKey, workstreamId, assignment.TaskItemId);
        AgentOperatingState<SpecialistOperatingState>? prior;
        try { prior = await context.Platform.ReadOperatingStateAsync<SpecialistOperatingState>(stateKey, cancellationToken); }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound)
        { prior = null; }
        if (prior?.Payload is { Status: "Completed", Delivery: not null } completed &&
            completed.StageExecutionId == assignment.StageExecutionId && completed.AttemptId == assignment.AttemptId &&
            completed.AssignmentRevision == assignment.AssignmentRevision)
            return AgentWorkResult.Success(CompletedOutcome(assignment, completed.Delivery));

        var stateStore = new RevisionSafeProjectState(context.Platform);
        _ = await stateStore.MergeAsync<SpecialistOperatingState>(stateKey,
            "com.csweet.video-game.specialist-work-state.v1", 1,
            current => new SpecialistOperatingState
            {
                WorkstreamId = workstreamId,
                WorkItemId = assignment.TaskItemId,
                StageExecutionId = assignment.StageExecutionId,
                AttemptId = assignment.AttemptId,
                AssignmentRevision = assignment.AssignmentRevision,
                RoleKey = RoleKey,
                Status = current?.Status == "Completed" ? current.Status : "InProgress",
                ExactInputDigests = package.Members.ToDictionary(x => x.AcceptedRevisionId, x => x.Sha256),
                Delivery = current?.Delivery,
                Blocker = null,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            package.Members.ToDictionary(x => x.AcceptedRevisionId.ToString("D"), x => x.Sha256),
            $"specialist-start:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}", cancellationToken);

        var board = new SpecialistBoardReporter(context.Platform);
        await board.ProgressAsync(assignment,
            $"{RoleKey} accepted stage {assignment.StageExecutionId:D}, attempt {assignment.AttemptId:D}.", cancellationToken);
        try
        {
            var grounding = new List<object>();
            foreach (var input in package.Members)
            {
                var document = await context.Platform.Artifacts.GetAsync(input.ArtifactId, cancellationToken);
                var inputRevision = document.Revisions.SingleOrDefault(x => x.Id == input.AcceptedRevisionId)
                    ?? throw new InvalidOperationException($"Exact artifact revision {input.AcceptedRevisionId:D} is unavailable.");
                if (!string.Equals(inputRevision.ContentSha256, input.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Artifact revision {input.AcceptedRevisionId:D} no longer matches its assigned hash.");
                grounding.Add(new { input.TypeKey, input.ArtifactId, RevisionId = input.AcceptedRevisionId, input.Sha256, inputRevision.Content });
            }
            var provider = Settings.GetGuid("llmProviderId")
                ?? throw new InvalidOperationException("A brokered LLM provider must be configured.");
            var client = context.CreateChatClient(new AgentLlmSelection(provider, Settings.GetString("llmModel"),
                new AgentLlmInvocationContext(null, null, $"video-game-specialist:{RoleKey}")));
            List<ChatMessage> messages = [
                new ChatMessage(ChatRole.System, $"{RolePrompt}\nYou own only {RoleKey} accountability. Produce concrete, testable Markdown with explicit decisions, dependencies, acceptance evidence, and no placeholders. Do not absorb another specialist's accountability."),
                new ChatMessage(ChatRole.User, $"Authoritative stage instructions:\n{assignment.Instructions}\n\nRequirements:\n{JsonSerializer.Serialize(canonicalInput.Planning.Requirements)}\n\nAcceptance criteria:\n{JsonSerializer.Serialize(canonicalInput.Planning.AcceptanceCriteria)}\n\nExact approved package {package.PackageId:D} v{package.Version} ({package.Sha256}):\n{JsonSerializer.Serialize(grounding)}\n\nPrior outcomes:\n{JsonSerializer.Serialize(assignment.PriorOutcomes)}\n\nExisting evidence:\n{JsonSerializer.Serialize(assignment.Evidence)}")
            ];
            var response = await context.Platform.Calendar.GetResponseAsync(client, messages, cancellationToken: cancellationToken);
            var markdown = response.Text ?? string.Empty;
            if (SubstantiveOutputValidator.FindIssue(markdown, RequiredSections.ToArray()) is { } draftIssue)
            {
                // One bounded self-correction: the author fixes its own draft instead of blocking the sprint
                // on a problem it can resolve. A second failure still blocks with the exact reason.
                messages.Add(new ChatMessage(ChatRole.Assistant, markdown));
                messages.Add(new ChatMessage(ChatRole.User,
                    $"The draft was rejected: {draftIssue} Return the complete corrected document only. Replace every " +
                    $"unresolved marker with concrete content and keep all required sections ({string.Join(", ", RequiredSections)})."));
                response = await context.Platform.Calendar.GetResponseAsync(client, messages, cancellationToken: cancellationToken);
                markdown = response.Text ?? string.Empty;
            }
            SubstantiveOutputValidator.RequireSubstantiveMarkdown(markdown, RequiredSections.ToArray());
            var itemTypeKey = assignment.Item.TryGetProperty("typeKey", out var typeKeyElement)
                ? typeKeyElement.GetString() ?? assignment.StageKey
                : assignment.StageKey;
            var artifact = await context.Platform.Artifacts.CreateAsync(new CreateArtifactDocument(
                $"{RoleKey}: {itemTypeKey}", markdown, ArtifactTypeKey,
                $"artifact:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}",
                OriginWorkItemId: assignment.TaskItemId)
            { WorkstreamId = workstreamId, TeamId = canonicalInput.TeamId }, cancellationToken);
            var revision = artifact.Revisions.Single(x => x.Id == artifact.LatestRevisionId);
            await context.Platform.Artifacts.SubmitAsync(new SubmitArtifactRevision(artifact.Id, revision.Id,
                $"submit:{assignment.StageExecutionId:N}:{revision.Id:N}"), cancellationToken);
            var evidence = new EvidenceReference("ArtifactRevision", artifact.Id, revision.Id, revision.ContentSha256,
                ArtifactTypeKey, "Submitted");
            var delivery = new SpecialistDelivery(
                $"Submitted exact {ArtifactTypeKey} revision {revision.Id:D} ({revision.ContentSha256}).",
                artifact.Id, revision.Id, revision.ContentSha256, [evidence], []);
            await board.EvidenceAsync(assignment, delivery, cancellationToken);
            _ = await stateStore.MergeAsync<SpecialistOperatingState>(stateKey,
                "com.csweet.video-game.specialist-work-state.v1", 1,
                current => (current ?? new SpecialistOperatingState()) with
                {
                    WorkstreamId = workstreamId,
                    WorkItemId = assignment.TaskItemId,
                    StageExecutionId = assignment.StageExecutionId,
                    AttemptId = assignment.AttemptId,
                    AssignmentRevision = assignment.AssignmentRevision,
                    RoleKey = RoleKey,
                    Status = "Completed",
                    ExactInputDigests = package.Members.ToDictionary(x => x.AcceptedRevisionId, x => x.Sha256),
                    Delivery = delivery,
                    Blocker = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                package.Members.ToDictionary(x => x.AcceptedRevisionId.ToString("D"), x => x.Sha256),
                $"specialist-complete:{assignment.StageExecutionId:N}:{revision.Id:N}", cancellationToken);
            return AgentWorkResult.Success(CompletedOutcome(assignment, delivery));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await board.BlockAsync(assignment, $"{RoleKey} blocked: {exception.Message}", cancellationToken);
            _ = await stateStore.MergeAsync<SpecialistOperatingState>(stateKey,
                "com.csweet.video-game.specialist-work-state.v1", 1,
                current => (current ?? new SpecialistOperatingState()) with
                {
                    WorkstreamId = workstreamId,
                    WorkItemId = assignment.TaskItemId,
                    StageExecutionId = assignment.StageExecutionId,
                    AttemptId = assignment.AttemptId,
                    AssignmentRevision = assignment.AssignmentRevision,
                    RoleKey = RoleKey,
                    Status = "Blocked",
                    ExactInputDigests = package.Members.ToDictionary(x => x.AcceptedRevisionId, x => x.Sha256),
                    Blocker = exception.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                package.Members.ToDictionary(x => x.AcceptedRevisionId.ToString("D"), x => x.Sha256),
                $"specialist-blocked:{assignment.StageExecutionId:N}:{Digest(exception.Message)}", cancellationToken);
            return AgentWorkResult.Success(new WorkExecutionOutcomeV1(
                assignment.StageExecutionId, assignment.AttemptId, WorkExecutionDispositions.Blocked,
                "blocked", exception.Message, JsonSerializer.SerializeToElement(new { }), [], [exception.Message]));
        }
    }

    private static bool IsHierarchicalArtifact(WorkExecutionAssignment assignment) =>
        assignment.Item.Deserialize<WorkItem>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Delivery is
            { DeliveryPlanId: not null, DeliveryKind: "Artifact" };

    private static WorkExecutionOutcomeV1 CompletedOutcome(
        WorkExecutionAssignment assignment,
        SpecialistDelivery delivery) =>
        new(assignment.StageExecutionId, assignment.AttemptId, WorkExecutionDispositions.Completed,
            IsHierarchicalArtifact(assignment) ? "artifact-delivered" : "completed", delivery.Summary,
            new WorkArtifactDelivery(delivery.ArtifactId, delivery.RevisionId, delivery.Sha256)
            { RemainingRisks = delivery.RemainingRisks }.ToOutput(),
            [new WorkExecutionEvidence(IsHierarchicalArtifact(assignment) ? "artifact-revision" : "ArtifactRevision", delivery.ArtifactId.ToString("D"),
                delivery.RevisionId.ToString("D"), "application/json")], []);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

public static class VideoGameSpecialistConformance
{
    private static readonly string[] RequiredCapabilities =
    [
        "work.item.read", "work.item.comment",
        "platform.artifact.read.v1", "platform.artifact.create.v1", "platform.artifact.submit.v1",
        "platform.agent-operating-state.read.v1", "platform.agent-operating-state.write.v1"
    ];

    public static IReadOnlyList<string> ValidateManifest(
        string manifestPath,
        string expectedAgentId,
        string expectedRoleKey,
        string expectedCapability)
    {
        var errors = new List<string>();
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "agent")
            errors.Add("Specialist packages must declare kind=agent.");
        if (!root.TryGetProperty("id", out var id) || id.GetString() != expectedAgentId)
            errors.Add("The package id does not match the specialist implementation.");
        var roles = root.GetProperty("rolePolicy").GetProperty("declaredRoleKeys")
            .EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        if (!roles.SetEquals([expectedRoleKey]))
            errors.Add("Every required specialist package must declare exactly its one accountable role.");
        var provided = root.GetProperty("provides").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.Ordinal);
        var executionCapabilities = provided.Where(x =>
            x == WorkManagementCapabilityNames.ExecutionRun || x == WorkManagementCapabilityNames.ExecutionRun ||
            x.StartsWith("video-game.", StringComparison.Ordinal) && x.EndsWith(".execute.v1", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!executionCapabilities.SetEquals([WorkManagementCapabilityNames.ExecutionRun, WorkManagementCapabilityNames.ExecutionRun]))
            errors.Add("Specialists must preserve V1 and provide V2 task execution; legacy role-specific execution capabilities are rejected.");
        var required = root.GetProperty("requires").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in RequiredCapabilities)
            if (!required.Contains(capability)) errors.Add($"Required conformance capability '{capability}' is missing.");
        if (required.Any(x => x is "platform.publication.execute.v1" or "platform.publication.publish.v1"))
            errors.Add("Specialists cannot acquire direct public-publication authority.");
        var runtime = root.GetProperty("runtime");
        if (!runtime.GetProperty("supportsMultipleInstallations").GetBoolean())
            errors.Add("Specialist packages must support distinct project-scoped installations.");
        return errors;
    }

    public static bool StateKeysAreIsolated(string roleKey, Guid firstWorkstream, Guid firstItem,
        Guid secondWorkstream, Guid secondItem)
    {
        var values = new[]
        {
            ProjectStateKeys.Workstream(roleKey, firstWorkstream),
            ProjectStateKeys.WorkItem(roleKey, firstWorkstream, firstItem),
            ProjectStateKeys.Workstream(roleKey, secondWorkstream),
            ProjectStateKeys.WorkItem(roleKey, secondWorkstream, secondItem)
        };
        return values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    }
}
