using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public enum GameDirectorPhase
{
    Discovery = 0,
    InvolvementConfirmation = 1,
    HighLevelReview = 2,
    HighLevelAccepted = 3,
    TeamPlanPending = 4,
    TeamStaffingPending = 5,
    WorkstreamPlanPending = 6,
    ProjectSetup = 7,
    DetailedDesign = 8,
    PackageReview = 9,
    Oversight = 10
}

public enum ManagerInvolvementMode
{
    Unspecified,
    Delegated,
    MilestoneReview,
    Collaborative
}

public sealed record ManagerPreferenceProfile
{
    public ManagerInvolvementMode InvolvementMode { get; init; } = ManagerInvolvementMode.Unspecified;
    public bool InvolvementWasExplicit { get; init; }
    public int InvolvementEvidenceCount { get; init; }
    public IReadOnlyList<string> PlatformConstraints { get; init; } = [];
    public IReadOnlyList<string> EnginePreferences { get; init; } = [];
    public string? AssetStrategyPreference { get; init; }
    public IReadOnlyList<string> GenreConstraints { get; init; } = [];
    public IReadOnlyList<string> NarrativeConstraints { get; init; } = [];
    public string? StoryParticipation { get; init; }
    public string? ApprovalPreference { get; init; }
    public IReadOnlyList<PlanningConstraintEvidence> PlanningConstraints { get; init; } = [];
    public IReadOnlyList<string> ReferenceGuidance { get; init; } = [];
    public IReadOnlyList<Guid> SupportingMessageIds { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record PlanningConstraintEvidence(string Text, Guid MessageId, bool StandingPreference);

public sealed record GamePitchRevision(
    int Revision,
    string Markdown,
    string Digest,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> PositiveConstraints,
    IReadOnlyList<string> ReferenceDigests);

public sealed record AcceptedGameVision(
    int Revision,
    string Digest,
    string Markdown,
    Guid ArtifactId,
    Guid ArtifactRevisionId,
    string ArtifactRevisionHash,
    Guid ConversationId,
    Guid ChatTurnId,
    Guid MessageId,
    DateTimeOffset AcceptedAt);

public sealed record PendingCreativeEscalation(
    Guid RequestingEmployeeId,
    Guid ConversationId,
    Guid SourceMessageId,
    string Question,
    DateTimeOffset EscalatedAt,
    Guid? DecisionId = null,
    bool Relayed = false);

public sealed record GameDirectorPortfolioEntry(
    string StateKey,
    Guid? WorkstreamId,
    Guid ConversationId,
    Guid? TeamId,
    Guid? BoardId,
    string WorkingTitle,
    GameDirectorPhase Phase,
    DateTimeOffset UpdatedAt)
{
    public Guid? IntakeId { get; init; }
}

public sealed record GameDirectorPortfolioIndex
{
    public IReadOnlyList<GameDirectorPortfolioEntry> Projects { get; init; } = [];
}

public sealed record GameDirectorOperatingState
{
    public Guid? IntakeId { get; init; }
    public Guid? IntakeConversationId { get; init; }
    public Guid? WorkstreamId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? BoardId { get; init; }
    public GameDirectorPhase Phase { get; init; } = GameDirectorPhase.Discovery;
    public bool IntakeChoiceAsked { get; init; }
    public int OnboardingFlowVersion { get; init; }
    public Guid? OnboardingEventId { get; init; }
    public DateTimeOffset? OnboardingCompletedAt { get; init; }
    public ManagerPreferenceProfile ManagerPreferences { get; init; } = new();
    public IReadOnlyList<string> DiscoveryInputs { get; init; } = [];
    public IReadOnlyList<GamePitchRevision> Proposals { get; init; } = [];
    public AcceptedGameVision? AcceptedVision { get; init; }
    public Guid? VisionTodoId { get; init; }
    public Guid? HighLevelArtifactId { get; init; }
    public Guid? HighLevelLatestRevisionId { get; init; }
    public Guid? HighLevelAcceptedRevisionId { get; init; }
    public Guid? HighLevelSourceTurnId { get; init; }
    public Guid? HighLevelSourceMessageId { get; init; }
    public Guid? DetailedDesignPackageId { get; init; }
    public IReadOnlyList<ReferenceEvidence> References { get; init; } = [];
    public Guid? StaffingTodoId { get; init; }
    public Guid? StaffingRequestId { get; init; }
    public Guid? ProducerEmployeeId { get; init; }
    public Guid? CreativeDirectorEmployeeId { get; init; }
    public Guid? CreativeDesignSessionId { get; init; }
    public Guid? CreativeDesignArtifactId { get; init; }
    public Guid? CreativeDesignRevisionId { get; init; }
    public IReadOnlyDictionary<string, Guid> SpecialistEmployeeIds { get; init; } =
        new Dictionary<string, Guid>(StringComparer.Ordinal);
    public Guid? WorkstreamProposalId { get; init; }
    public Guid? WorkstreamProposalSessionId { get; init; }
    public string? WorkingTitle { get; init; }
    public Guid? HandoffSessionId { get; init; }
    public Guid? AssetStrategyDecisionId { get; init; }
    public Guid? AssetStrategyBlockerDecisionId { get; init; }
    public string? AssetStrategyMode { get; init; }
    public Guid? ToolchainSelectionDecisionId { get; init; }
    public Guid? ToolchainBlockerDecisionId { get; init; }
    public string? SelectedToolchainRecipeKey { get; init; }
    public Guid? ToolchainFeasibilitySessionId { get; init; }
    public ToolchainFeasibilityEvidenceV1? ToolchainFeasibilityEvidence { get; init; }
    public IReadOnlyList<PendingCreativeEscalation> PendingEscalations { get; init; } = [];
    public IReadOnlyList<ManagementStatusReport> SubordinateReports { get; init; } = [];
    public IReadOnlyList<string> NotificationFingerprints { get; init; } = [];
    public DateOnly? LastDailyReportDate { get; init; }
}

internal sealed record SemanticArtifactReview(
    string Disposition,
    string Summary,
    IReadOnlyList<SemanticArtifactFinding> Findings);

internal sealed record SemanticArtifactFinding(
    string Code,
    string Section,
    string Severity,
    bool Blocking,
    string Summary,
    string? RequiredFollowUp);
