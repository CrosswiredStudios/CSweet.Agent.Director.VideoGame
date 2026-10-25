using System.Text.Json;

namespace CrosswiredStudios.VideoGame.Contracts;

public sealed record ReferenceEvidence(
    Guid AttachmentId,
    Guid ConversationId,
    Guid MessageId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Observation);

public sealed record GameVisionBrief(
    string AcceptedPitchDigest,
    string PlayerAndProductOutcome,
    string GameplayLoopAndCreativePillars,
    string PlatformAndStackConstraints,
    string ArtNarrativeAudioAndToneDirection,
    string MvpScopeAndNonGoals,
    IReadOnlyList<ReferenceEvidence> ReferenceSummaries,
    string SuccessCriteriaRisksAndAssumptions,
    IReadOnlyList<string> OpenDecisions)
{
    public Guid? HighLevelGddArtifactId { get; init; }
    public Guid? HighLevelGddAcceptedRevisionId { get; init; }
    public string? HighLevelGddRevisionSha256 { get; init; }
}

public sealed record GameVisionAcknowledgement(
    string AcceptedPitchDigest,
    bool Acknowledged,
    IReadOnlyList<string> Blockers,
    DateTimeOffset AcknowledgedAt)
{
    public Guid? HighLevelGddArtifactId { get; init; }
    public Guid? HighLevelGddAcceptedRevisionId { get; init; }
    public string? HighLevelGddRevisionSha256 { get; init; }
    public Guid? PlanningPackageId { get; init; }
    public int? PlanningPackageVersion { get; init; }
}

public sealed record GameProductionPlanningCycleV1(
    Guid WorkstreamId,
    Guid TeamId,
    long TeamRevision,
    Guid BoardId,
    string ProfileDigest,
    Guid ApprovedPackageId,
    int ApprovedPackageVersion,
    string ApprovedPackageDigest,
    string LifecyclePhase,
    string TargetMilestoneKey,
    string PlanningFingerprint);

public sealed record GameProposedWorkItemV1(
    string ProposalKey,
    string WorkItemTypeKey,
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    string AccountableRoleKey,
    IReadOnlyList<string> RequiredSpecializationKeys,
    IReadOnlyList<string> PreferredSpecializationKeys,
    IReadOnlyList<string> RequiredCapabilityKeys,
    IReadOnlyList<string> DependencyProposalKeys)
{
    public string? ParentProposalKey { get; init; }
}

public sealed record GameDesignerBacklogProposalV1(
    GameProductionPlanningCycleV1 Cycle,
    IReadOnlyList<GameProposedWorkItemV1> PlayerOutcomes,
    IReadOnlyList<string> DesignConstraints,
    IReadOnlyList<string> OpenCreativeDecisions,
    string ProposalDigest);

public sealed record GameTechnicalDeliveryProposalV1(
    GameProductionPlanningCycleV1 Cycle,
    IReadOnlyList<GameProposedWorkItemV1> DeliveryItems,
    IReadOnlyList<string> FeasibilityFindings,
    IReadOnlyList<string> TechnicalConstraints,
    IReadOnlyList<string> OpenFeasibilityDecisions,
    string ProposalDigest);

public sealed record ReconciledGameProductionPlanV1(
    GameProductionPlanningCycleV1 Cycle,
    string DesignerProposalDigest,
    string TechnicalProposalDigest,
    IReadOnlyList<GameProposedWorkItemV1> CanonicalItems,
    IReadOnlyList<string> OutstandingAuthorityDecisions,
    string ReconciledDigest);

public sealed record GameRoleEstimateCapacityProposalV1(
    Guid BoardId,
    string RoleKey,
    long PlanningRevision,
    string PlanningDigest,
    IReadOnlyList<GameWorkItemEstimateV1> Estimates,
    decimal AvailableSprintCapacity,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Blockers,
    string ProposalDigest);

public sealed record GameWorkItemEstimateV1(Guid WorkItemId, decimal EstimatePoints, string Confidence);

public sealed record GameRoleEstimateCapacityRequestV1(
    Guid BoardId,
    string RoleKey,
    long PlanningRevision,
    string PlanningDigest,
    IReadOnlyList<GameSprintCandidateV1> WorkItems,
    string RequestFingerprint);

public sealed record GameQaSprintReadinessRequestV1(
    Guid BoardId,
    long PlanningRevision,
    string PlanningDigest,
    IReadOnlyList<GameSprintCandidateV1> Candidates,
    string RequestFingerprint);

public sealed record GameSprintCandidateV1(
    Guid WorkItemId,
    string Title,
    string AccountableRoleKey,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<Guid> DependencyWorkItemIds,
    string ArtifactPackageDigest,
    string AssignmentDecisionFingerprint,
    decimal? EstimatePoints = null,
    string? EstimateSourceDigest = null);

public sealed record GameQaSprintReadinessAssessmentV1(
    Guid SprintId,
    long PlanningRevision,
    string PlanningDigest,
    bool Ready,
    IReadOnlyList<Guid> ReadyWorkItemIds,
    IReadOnlyList<GameReadinessFindingV1> Findings,
    string AssessmentDigest);

public sealed record GameReadinessFindingV1(Guid? WorkItemId, string Code, bool Blocking, string Finding);

public sealed record VideoGameProjectMetadataV1(
    string WorkingTitle,
    string Genre,
    IReadOnlyList<string> TargetPlatforms,
    string PlayerAudience,
    string EnginePreference,
    string ContentRatingTarget,
    IReadOnlyList<string> CreativePillars,
    string BusinessModel,
    bool LiveOperationsExpected,
    bool OnlineMultiplayerExpected,
    IReadOnlyList<string> AccessibilityTargets,
    IReadOnlyList<string> LocalizationTargets);

public static class VideoGameProfileKeys
{
    public const string ProductionV2 = "video-game-production.v2";
    public const string ProductionBoardV2 = "video-game-production-board.v2";
}

public static class VideoGameLifecyclePhases
{
    public const string Intake = "intake";
    public const string Concept = "concept";
    public const string PreProduction = "pre-production";
    public const string Prototype = "prototype";
    public const string VerticalSlice = "vertical-slice";
    public const string Production = "production";
    public const string Alpha = "alpha";
    public const string Beta = "beta";
    public const string ReleaseCandidate = "release-candidate";
    public const string Launch = "launch";
    public const string PostLaunchStabilization = "post-launch-stabilization";
    public const string LiveOperations = "live-operations";
    public const string Closure = "closure";
}

public static class VideoGameMilestoneKeys
{
    public const string VisionApproved = "vision-approved";
    public const string PreProductionReady = "pre-production-ready";
    public const string PrototypeValidated = "prototype-validated";
    public const string VerticalSliceApproved = "vertical-slice-approved";
    public const string ProductionReady = "production-ready";
    public const string AlphaExit = "alpha-exit";
    public const string BetaExit = "beta-exit";
    public const string ReleaseCandidateApproved = "release-candidate-approved";
    public const string LaunchApproved = "launch-approved";
    public const string StabilizationExit = "stabilization-exit";
    public const string SunsetApproved = "sunset-approved";
}

public static class VideoGameRoleKeys
{
    public const string GameDirector = "game-director";
    public const string CreativeDirector = "creative-director";
    public const string Producer = "game-producer";
    public const string GameDesigner = "game-designer";
    public const string TechnicalDirector = "game-technical-director";
    public const string Engineer = "game-engineer";
    public const string QualityAssurance = "game-quality-assurance";
    public const string PlaytestResearcher = "playtest-researcher";
    public const string ArtDirector = "game-art-director";
    public const string Artist = "game-artist";
    public const string TechnicalArtist = "technical-artist";
    public const string NarrativeDesigner = "narrative-designer";
    public const string AudioDesigner = "audio-designer";
    public const string LevelDesigner = "level-designer";
    public const string UserExperienceDesigner = "game-ui-ux-accessibility-designer";
    public const string BuildReleaseEngineer = "game-build-release-engineer";
    public const string NetworkingEngineer = "game-networking-engineer";
    public const string EconomyDesigner = "game-economy-designer";
    public const string LocalizationSpecialist = "game-localization-specialist";
    public const string SecurityPrivacy = "game-security-privacy";
    public const string MarketingCommunity = "game-marketing-community";
    public const string PlatformCertification = "game-platform-certification";
    public const string LiveOperations = "game-live-operations";
}

public static class VideoGameSpecializationKeys
{
    public const string Development = "video-game-development";
    public const string CreativeDirection = "game-creative-direction";
    public const string Production = "game-production";
    public const string Gameplay = "gameplay-systems";
    public const string Content = "game-content";
    public const string SprintPlanning = "sprint-planning";
    public const string Forecasting = "delivery-forecasting";
    public const string DependencyRiskCapacityManagement = "dependency-risk-capacity-management";
    public const string Reporting = "management-reporting";
    public const string GameDesign = "game-design";
    public const string ProgressionBalance = "progression-balance";
    public const string PrototypeDesign = "prototype-design";
    public const string TechnicalFeasibility = "technical-feasibility";
    public const string RuntimeArchitecture = "runtime-architecture";
    public const string EngineIntegration = "engine-integration";
    public const string PerformanceBudgets = "performance-budgets";
    public const string TechnicalDecomposition = "technical-decomposition";
    public const string GameplayProgramming = "gameplay-programming";
    public const string AutomatedTesting = "automated-testing";
    public const string BuildDiagnostics = "build-diagnostics";
    public const string TestPlanning = "test-planning";
    public const string BuildValidation = "build-validation";
    public const string DefectReproduction = "defect-reproduction";
    public const string RegressionTesting = "regression-testing";
    public const string CompatibilityTesting = "compatibility-testing";
    public const string AccessibilityTesting = "accessibility-testing";
    public const string PlaytestPlanning = "playtest-planning";
    public const string ResearchDesign = "research-design";
    public const string ConsentGovernance = "consent-governance";
    public const string ResearchAnalysis = "research-analysis";
    public const string ArtDirection = "art-direction";
    public const string AssetProduction = "asset-production";
    public const string AssetPipelines = "asset-pipelines";
    public const string VisualPerformance = "visual-performance";
    public const string NarrativeDesign = "narrative-design";
    public const string AudioDesign = "audio-design";
    public const string LevelDesign = "level-design";
    public const string UiUxAccessibility = "ui-ux-accessibility-design";
    public const string BuildRelease = "build-release-engineering";
    public const string Networking = "game-networking";
    public const string Economy = "game-economy";
    public const string Localization = "game-localization";
    public const string SecurityPrivacy = "game-security-privacy";
    public const string MarketingCommunity = "game-marketing-community";
    public const string PlatformCertification = "platform-certification";
    public const string LiveOperations = "live-operations";
}

public static class VideoGameWorkItemTypeKeys
{
    public const string Milestone = "video-game.milestone.v1";
    public const string Feature = "video-game.feature.v1";
    public const string Content = "video-game.content.v1";
    public const string Task = "video-game.task.v1";
    public const string Bug = "video-game.bug.v1";
    public const string ResearchSpike = "video-game.research-spike.v1";
    public const string CreativeReview = "video-game.creative-review.v1";
}

public static class VideoGameArtifactTypeKeys
{
    public const string Vision = "video-game.vision.v1";
    public const string GameDesignDocument = "video-game.gdd.v1";
    public const string TechnicalDesign = "video-game.technical-design.v1";
    public const string ProductionPlan = "video-game.production-plan.v1";
    public const string NarrativeBible = "video-game.narrative-bible.v1";
    public const string ArtBible = "video-game.art-bible.v1";
    public const string AudioBible = "video-game.audio-bible.v1";
    public const string LevelContentPlan = "video-game.level-content-plan.v1";
    public const string UserExperienceAccessibility = "video-game.ux-accessibility.v1";
    public const string QualityEvaluationPlan = "video-game.qa-evaluation-plan.v1";
    public const string ReleasePlan = "video-game.release-plan.v1";
    public const string RunnableBuild = "video-game.runnable-build.v1";
}

public static class VideoGameRubricTypeKeys
{
    public const string Vision = "video-game.rubric.vision.v1";
    public const string GameDesign = "video-game.rubric.game-design.v1";
    public const string Creative = "video-game.rubric.creative-quality.v1";
    public const string Technical = "video-game.rubric.technical-feasibility.v1";
    public const string Quality = "video-game.rubric.quality.v1";
    public const string Accessibility = "video-game.rubric.accessibility.v1";
    public const string Performance = "video-game.rubric.performance.v1";
    public const string Release = "video-game.rubric.release.v1";
}

public static class VideoGameEvaluationTypeKeys
{
    public const string Playtest = "video-game.playtest.v1";
    public const string Accessibility = "video-game.accessibility-evaluation.v1";
    public const string Performance = "video-game.performance-evaluation.v1";
    public const string Certification = "video-game.platform-certification.v1";
}

public static class VideoGameDecisionTypeKeys
{
    public const string AssetStrategy = "video-game.asset-strategy.v1";
    public const string ToolchainSelection = "video-game.toolchain-selection.v1";
    public const string MissingConditionalSpecialist = "video-game.missing-conditional-specialist.v1";
}

public static class VideoGameAssetProductionModes
{
    public const string Provided = "provided";
    public const string Procedural = "procedural";
    public const string Generative = "generative";
    public const string Hybrid = "hybrid";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Provided, Procedural, Generative, Hybrid };
}

public sealed record VideoGameAssetStrategyV1(
    string Mode,
    IReadOnlyList<string> PermittedModes,
    IReadOnlyList<Guid> ApprovedProviderInstallationIds,
    IReadOnlyList<string> FallbackOrder,
    decimal? BudgetAmount,
    string? BudgetCurrency,
    string QualityBar,
    IReadOnlyList<string> LicensingConstraints,
    bool ProceduralFallbackAllowed);

public static class VideoGameToolchainRecipeKeys
{
    public const string PhaserWeb2D = "phaser.web-2d.v1";
    public const string BabylonWeb3D = "babylon.web-3d.v1";
    public const string GodotNative2DGdscript = "godot.native-2d.gdscript.v1";
    public const string GodotNative3DGdscript = "godot.native-3d.gdscript.v1";
}

public sealed record ConditionalStaffingRuleV1(
    string RoleKey,
    string JsonPath,
    string Operator,
    JsonElement Value,
    string BlockingDecisionTypeKey);

public sealed record CreativeReviewRubric(
    string TypeKey,
    IReadOnlyList<CreativeReviewCriterion> Criteria,
    decimal PassingScore,
    bool BlockingFindingFailsReview);

public sealed record CreativeReviewCriterion(string Key, string Prompt, decimal Weight, bool Blocking);

public sealed record ToolchainRecommendation(
    string RecommendedAdapterKey,
    IReadOnlyList<ToolchainRecommendationOption> Options,
    string Rationale,
    IReadOnlyList<string> RequiredFeasibilityEvidenceTypeKeys,
    DateTimeOffset RecommendedAt);

public sealed record ToolchainRecommendationOption(
    string AdapterKey,
    IReadOnlyList<string> SupportedTargets,
    IReadOnlyList<string> Advantages,
    IReadOnlyList<string> Tradeoffs,
    IReadOnlyList<string> Risks,
    bool Eligible);

public sealed record ToolchainFeasibilityEvidenceV1(
    string AcceptedVisionDigest,
    string RecipeKey,
    IReadOnlyList<string> TargetKeys,
    bool Feasible,
    IReadOnlyList<string> Findings,
    IReadOnlyList<Guid> EvidenceResourceIds,
    DateTimeOffset AssessedAt);

public sealed record PlaytestPlanV1(
    string ResearchQuestion,
    string ParticipantProfile,
    int TargetParticipantCount,
    IReadOnlyList<PlaytestTaskV1> Tasks,
    IReadOnlyList<PlaytestQuestionV1> Questions,
    IReadOnlyList<string> TelemetryKeys,
    string ConsentPolicyKey,
    string PrivacyNotes);

public sealed record PlaytestTaskV1(string Key, string Instruction, string SuccessSignal);
public sealed record PlaytestQuestionV1(string Key, string Prompt, string ResponseType, bool Required);
public sealed record PlaytestReportV1(
    int ParticipantCount,
    IReadOnlyList<PlaytestFindingV1> Findings,
    IReadOnlyDictionary<string, JsonElement> Metrics,
    string Recommendation,
    IReadOnlyList<string> FollowUpWorkItemKeys);
public sealed record PlaytestFindingV1(string Code, string Severity, bool Blocking, string Observation, string Interpretation);

public sealed record VideoGameStatusReportExtensionV1(
    string LifecyclePhase,
    string CurrentBuildStatus,
    string CreativeHealth,
    string TechnicalHealth,
    string QualityHealth,
    string PlayerValidationHealth,
    IReadOnlyList<string> CurrentMilestones,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<Guid> OpenDecisionIds,
    IReadOnlyList<Guid> EvidenceResourceIds);
