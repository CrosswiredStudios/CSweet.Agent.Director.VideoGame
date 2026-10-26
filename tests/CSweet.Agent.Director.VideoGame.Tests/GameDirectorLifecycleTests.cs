using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Memory;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GameDirectorLifecycleTests
{
    [Fact]
    public async Task NonManagerCannotDirectOrAcceptVision()
    {
        var managerId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)));
        var context = runtime.CreateContext(
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(Guid.NewGuid().ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "CEO"));
        var incoming = new CommunicationMessageReceivedEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), workerId.ToString("D"),
            "Accept the pitch.",
            new Dictionary<string, string>
            {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = workerId.ToString("D")
            },
            Guid.NewGuid(), 1, Guid.NewGuid());

        await new VideoGameDirectorAgent().HandleEventAsync(
            new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
                JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow),
            context, CancellationToken.None);

        var output = Assert.Single(runtime.Progress);
        Assert.Contains("authoritative manager", output.GetProperty("delta").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagementCheckIn_ReturnsAttributedLifecycleReport()
    {
        var employeeId = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(null)));
        var context = runtime.CreateContext(
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Game Video Game Director", null,
                "Video Game Video Game Director", null, [], null, Guid.NewGuid().ToString("D"), "CEO"));
        var checkIn = new ManagementCheckInRequest(
            Guid.NewGuid(), "Daily", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            [], [], DateTimeOffset.UtcNow.AddHours(1));

        var result = await new VideoGameDirectorAgent().ExecuteCapabilityAsync(
            new AgentCapabilityRequest(Guid.NewGuid(), ManagementCapabilities.CheckIn,
                JsonSerializer.SerializeToElement(checkIn)), context, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var report = result.Value?.Deserialize<ManagementStatusReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(report);
        Assert.Equal(employeeId, report!.ReporterOrganizationUserId);
        Assert.Contains("Discovery", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_ExecutesOnlyGovernedTransitions()
    {
        var delegatedPath = new[]
        {
            GameDirectorPhase.Discovery,
            GameDirectorPhase.InvolvementConfirmation,
            GameDirectorPhase.HighLevelAccepted,
            GameDirectorPhase.TeamPlanPending,
            GameDirectorPhase.TeamStaffingPending,
            GameDirectorPhase.WorkstreamPlanPending,
            GameDirectorPhase.ProjectSetup,
            GameDirectorPhase.DetailedDesign,
            GameDirectorPhase.PackageReview,
            GameDirectorPhase.Oversight
        };

        Assert.All(delegatedPath.Zip(delegatedPath.Skip(1)), transition =>
            Assert.True(VideoGameDirectorAgent.IsAllowedPhaseTransition(
                transition.First, transition.Second), $"{transition.First} -> {transition.Second}"));
        Assert.False(VideoGameDirectorAgent.IsAllowedPhaseTransition(
            GameDirectorPhase.Discovery, GameDirectorPhase.Oversight));
        Assert.False(VideoGameDirectorAgent.IsAllowedPhaseTransition(
            GameDirectorPhase.Oversight, GameDirectorPhase.DetailedDesign));
    }

    [Fact]
    public void PitchDigest_IsStableAndSensitiveToRevisionContent()
    {
        var first = VideoGameDirectorAgent.Digest("A precise game pitch");
        Assert.Equal(first, VideoGameDirectorAgent.Digest("A precise game pitch"));
        Assert.NotEqual(first, VideoGameDirectorAgent.Digest("A different game pitch"));
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void ProjectStateKeysIsolateConcurrentWorkstreamsAndIntakeConversations()
    {
        var firstWorkstream = Guid.NewGuid();
        var secondWorkstream = Guid.NewGuid();
        var intake = Guid.NewGuid();

        var keys = new[]
        {
            VideoGameDirectorAgent.ProjectStateKey(firstWorkstream, null),
            VideoGameDirectorAgent.ProjectStateKey(secondWorkstream, null),
            VideoGameDirectorAgent.ProjectStateKey(null, intake),
            VideoGameDirectorAgent.PortfolioStateKey
        };

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(firstWorkstream.ToString("N"), keys[0]);
        Assert.Contains(intake.ToString("N"), keys[2]);
    }

    [Fact]
    public void DeterministicArtifactReviewRejectsPlaceholderAndUnstructuredContent()
    {
        var findings = VideoGameDirectorAgent.DeterministicArtifactFindings(
            "TODO: replace this placeholder with a real design.");

        Assert.Contains(findings, finding => finding.Code == "insufficient-substance" && finding.Blocking);
        Assert.Contains(findings, finding => finding.Code == "placeholder-content" && finding.Blocking);
        Assert.Contains(findings, finding => finding.Code == "missing-structure" && finding.Blocking);
    }

    [Fact]
    public void CurrentMessageExtraction_IgnoresQuotedHistory()
    {
        var prompt = "history says Accept\n<current_user_message>Replace the gameplay loop</current_user_message>";
        Assert.Equal("Replace the gameplay loop", VideoGameDirectorAgent.ExtractCurrentMessage(prompt));
    }

    [Fact]
    public void VisionBriefAndAcknowledgement_UseExactDigest()
    {
        const string digest = "abc123";
        var brief = new GameVisionBrief(digest, "outcome", "loop", "stack", "tone", "mvp", [], "risks", []);
        var acknowledgement = new GameVisionAcknowledgement(digest, true, [], DateTimeOffset.UtcNow);
        Assert.Equal(brief.AcceptedPitchDigest, acknowledgement.AcceptedPitchDigest);
        Assert.Empty(acknowledgement.Blockers);
    }

    [Fact]
    public void DiscoveryState_PreservesSparseManagerDirectionAndReferenceEvidence()
    {
        var attachmentId = Guid.NewGuid();
        var state = new GameDirectorOperatingState
        {
            IntakeChoiceAsked = true,
            DiscoveryInputs = ["A cooperative game about restoring a flooded clockwork city."],
            References = [new ReferenceEvidence(
                attachmentId, Guid.NewGuid(), Guid.NewGuid(), "concept.webp", "image/webp", 2048,
                new string('a', 64), "Supplied by the authoritative manager.")]
        };

        Assert.Equal(GameDirectorPhase.Discovery, state.Phase);
        Assert.Contains("clockwork city", Assert.Single(state.DiscoveryInputs), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(attachmentId, Assert.Single(state.References).AttachmentId);
    }

    [Theory]
    [InlineData("Delegate unspecified decisions to you.", ManagerInvolvementMode.Delegated)]
    [InlineData("I want to review major milestones.", ManagerInvolvementMode.MilestoneReview)]
    [InlineData("Let's collaborate closely.", ManagerInvolvementMode.Collaborative)]
    public void InvolvementCalibrationRecognizesAllLifecycleModes(
        string message,
        ManagerInvolvementMode expected) =>
        Assert.Equal(expected, VideoGameDirectorAgent.ParseInvolvementMode(message));

    [Theory]
    [InlineData("Decision: How involved do you want to be in creative direction?\nAnswer: Review milestones", true)]
    [InlineData("I just want to set the initial direction and then review milestones.", true)]
    [InlineData("Review milestones for a cozy horror game about restoring a flooded city.", false)]
    [InlineData("Collaborate closely on a PC strategy game.", false)]
    public void InteractionOnlyRepliesDoNotStartPitchProduction(string message, bool expected) =>
        Assert.Equal(expected, VideoGameDirectorAgent.IsInteractionPreferenceOnly(message, []));

    [Fact]
    public void PitchGenerationFailuresAreRecoverableUnlessTheWorkItemWasCancelled()
    {
        Assert.True(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new HttpRequestException("transport failed"), CancellationToken.None));
        Assert.True(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new TimeoutException("model timed out"), CancellationToken.None));
        Assert.True(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new OperationCanceledException("request timeout"), CancellationToken.None));
        Assert.True(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new InvalidOperationException("The configured model returned an empty game pitch."),
            CancellationToken.None));
        Assert.True(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new PlatformCapabilityException(
                PlatformCapabilities.LlmChatStream,
                PlatformCapabilityErrorCode.Unavailable,
                "model unavailable"),
            CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(VideoGameDirectorAgent.IsRecoverablePitchGenerationFailure(
            new OperationCanceledException(cancelled.Token), cancelled.Token));
    }

    [Fact]
    public void PitchOutputBudgetUsesConfiguredValueAndStaysBelowContextWindow()
    {
        Assert.Equal(128_000, VideoGameDirectorAgent.ResolvePitchOutputTokens(
            new AgentSettings(new Dictionary<string, JsonElement>())));
        Assert.Equal(128_000, VideoGameDirectorAgent.ResolvePitchOutputTokens(
            new AgentSettings(new Dictionary<string, JsonElement>
            {
                ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(220_000),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(128_000)
            })));
        Assert.Equal(24_999, VideoGameDirectorAgent.ResolvePitchOutputTokens(
            new AgentSettings(new Dictionary<string, JsonElement>
            {
                ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(25_000),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(32_000)
            })));
    }

    [Theory]
    [InlineData(ManagerInvolvementMode.Delegated, "AwaitExplicitMilestoneApproval")]
    [InlineData(ManagerInvolvementMode.MilestoneReview, "AwaitExplicitMilestoneApproval")]
    [InlineData(ManagerInvolvementMode.Collaborative, "IterateCollaboratively")]
    public void InvolvementModeSelectsIntendedVisionApprovalPath(
        ManagerInvolvementMode mode,
        string expected) =>
        Assert.Equal(expected, VideoGameDirectorAgent.InitialVisionDisposition(mode));

    [Fact]
    public void ManagerPreferencesPersistExplicitConstraintsAndEvidence()
    {
        var messageId = Guid.NewGuid();
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(
            new ManagerPreferenceProfile(),
            "Delegate decisions. Target PC and Steam for a cooperative strategy game. I do not want to be involved in story decisions.",
            messageId,
            [],
            applyDefault: true);

        Assert.Equal(ManagerInvolvementMode.Delegated, preferences.InvolvementMode);
        Assert.Contains("PC", preferences.PlatformConstraints);
        Assert.Contains("Steam", preferences.PlatformConstraints);
        Assert.Contains("strategy", preferences.GenreConstraints, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Delegate story", preferences.StoryParticipation, StringComparison.Ordinal);
        Assert.Contains(messageId, preferences.SupportingMessageIds);
        Assert.NotNull(preferences.UpdatedAt);
    }

    [Fact]
    public void PlatformParsingDoesNotMistakeConceptForPc()
    {
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(
            new ManagerPreferenceProfile(),
            "Use the current concept and make autonomous creative recommendations.",
            Guid.NewGuid(), [], applyDefault: true);

        Assert.DoesNotContain("PC", preferences.PlatformConstraints);
    }

    [Fact]
    public void ExplicitLatestPhaserIsRetainedAndCanBeReplaced()
    {
        var firstMessageId = Guid.NewGuid();
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(
            new ManagerPreferenceProfile(),
            "Make a polished 2D Breakout game for web using the latest Phaser engine.",
            firstMessageId, [], applyDefault: true);

        Assert.Equal(["latest Phaser"], preferences.EnginePreferences);
        Assert.Equal("latest Phaser", VideoGameDirectorAgent.PreferredEngineLabel(
            preferences with { EnginePreferences = ["JavaScript", "latest Phaser"] }));
        Assert.Contains(firstMessageId, preferences.SupportingMessageIds);
        Assert.Equal(VideoGameToolchainRecipeKeys.PhaserWeb2D,
            VideoGameDirectorAgent.DetermineRequiredRecipe(new GameDirectorOperatingState
            {
                ManagerPreferences = preferences
            }));

        var secondMessageId = Guid.NewGuid();
        preferences = VideoGameDirectorAgent.UpdateManagerPreferences(preferences,
            "Change the engine to Babylon.js instead.", secondMessageId, [], applyDefault: true);
        Assert.Equal(["Babylon.js"], preferences.EnginePreferences);
        Assert.Contains(secondMessageId, preferences.SupportingMessageIds);
    }

    [Fact]
    public void PitchReviewRejectsSilentEngineOrVersionSubstitution()
    {
        var preferences = new ManagerPreferenceProfile { EnginePreferences = ["latest Phaser"] };

        Assert.Null(VideoGameDirectorAgent.PitchEngineConflict(preferences,
            "A 2D web brick breaker using the latest compatible Phaser release. No Babylon or 3D."));
        Assert.NotNull(VideoGameDirectorAgent.PitchEngineConflict(preferences,
            "A 2D web brick breaker using Phaser 3."));
        Assert.NotNull(VideoGameDirectorAgent.PitchEngineConflict(preferences,
            "A 3D web brick breaker using Babylon.js."));
        Assert.NotNull(VideoGameDirectorAgent.PitchEngineConflict(preferences,
            "A 2D web brick breaker with no named game engine."));
    }

    [Theory]
    [InlineData("Phaser 4", "Web browsers via Phaser 4.\n**Non-goals:** 3D rendering, multiplayer, Babylon.js or Godot.\n**Guardrails:**\n- Engine: Phaser 4.")]
    [InlineData("Phaser 4", "## Engine\nPhaser 4.\n## Non-goals\n- Multiplayer\n- Babylon.js or Godot\n## Art\nNeon sprites.")]
    [InlineData("Phaser 4", "Phaser 4.\n### 9. Out of scope\nBabylon.js, Godot, and multiplayer.\n#### Reasons\nBabylon.js requires a different renderer.\n### 10. Delivery\nWeb.")]
    [InlineData("Phaser 4", "Phaser 4.\n- **Excluded engines:** Babylon.js and Godot.")]
    [InlineData("Phaser 4", "Phaser 4.\nNon-goals:\n- Multiplayer\n- Babylon.js\nEngine: Phaser 4.")]
    [InlineData("Babylon.js", "Engine: Babylon.js.\n**Non-goals:** Phaser, Godot, multiplayer.")]
    [InlineData("latest Phaser", "Engine: latest compatible Phaser.\nNon-goals: Phaser 3 or Babylon.js.")]
    public void PitchReviewAcceptsEngineNamesInExplicitExclusions(string requested, string pitch)
    {
        Assert.Null(VideoGameDirectorAgent.PitchEngineConflict(
            new ManagerPreferenceProfile { EnginePreferences = [requested] }, pitch));
    }

    [Theory]
    [InlineData("Phaser 4", "## Non-goals\nPhaser 4.\n## Engine\nBabylon.js.")]
    [InlineData("Phaser 4", "Phaser 4.\n## Non-goals\nMultiplayer.\n## Engine\nBabylon.js.")]
    [InlineData("Phaser 4", "Phaser 4.\nNon-goals: multiplayer; Engine: Babylon.js.")]
    [InlineData("Phaser 4", "Phaser 4.\nNon-goals: multiplayer. Use Babylon.js instead.")]
    [InlineData("Phaser 4", "Non-goals: Phaser 4 and Babylon.js.")]
    [InlineData("Phaser 4", "Phaser 4.\nNon-goals:\n- Multiplayer\nEngine: Babylon.js.")]
    [InlineData("latest Phaser", "Engine: Phaser 3.\nNon-goals: Babylon.js.")]
    [InlineData("Babylon.js", "Babylon.js for rendering and Phaser for gameplay.")]
    public void PitchReviewStillRejectsMissingOrSubstitutedEngines(string requested, string pitch)
    {
        Assert.NotNull(VideoGameDirectorAgent.PitchEngineConflict(
            new ManagerPreferenceProfile { EnginePreferences = [requested] }, pitch));
    }

    [Fact]
    public void ExplicitEngineIsIncludedInScopedProjectMemory()
    {
        var incoming = new CommunicationMessageReceivedEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), "manager-user",
            "Make a 2D web Breakout game using the latest Phaser engine.",
            new Dictionary<string, string>(), Guid.NewGuid(), 1, Guid.NewGuid());

        var proposals = VideoGameDirectorAgent.BuildExplicitMemoryProposals(
            incoming, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));

        Assert.Contains(proposals, proposal => proposal.Scope == MemoryScope.Tenant &&
            proposal.Content.Contains("latest Phaser", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitNarrativeDirectionIsProjectStateNotUserParticipation()
    {
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(
            new ManagerPreferenceProfile(),
            "The story follows siblings restoring a flooded clockwork city.",
            Guid.NewGuid(), [], applyDefault: true);

        Assert.Null(preferences.StoryParticipation);
        Assert.Contains(preferences.NarrativeConstraints,
            x => x.Contains("clockwork city", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitMemoryProposalsUseUserAndBusinessScopesWithoutRawAttachmentNames()
    {
        var messageId = Guid.NewGuid();
        var incoming = new CommunicationMessageReceivedEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), "manager-user",
            "Collaborate closely on a PC RPG; I want to review the story.",
            new Dictionary<string, string>(), Guid.NewGuid(), 1, messageId)
        {
            Attachments = [new CommunicationAttachment(
                Guid.NewGuid(), messageId, "C:\\private\\concept.png", "image/png", 4096, new string('a', 64))]
        };

        var proposals = VideoGameDirectorAgent.BuildExplicitMemoryProposals(
            incoming, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));

        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, x => Assert.Equal("csweet", x.Partition.ApplicationId));
        Assert.Contains(proposals, x => x.Scope == MemoryScope.User && x.Sensitivity == MemorySensitivity.Personal);
        Assert.Contains(proposals, x => x.Scope == MemoryScope.Tenant && x.Sensitivity == MemorySensitivity.Internal);
        Assert.All(proposals, x => Assert.DoesNotContain("C:\\private", x.Content, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(proposals, x => x.Content.Contains(new string('a', 64), StringComparison.Ordinal));
    }

    [Fact]
    public void MemoryRecallMatchesPlatformScopesWithoutReadingInstallationPrivateHistory()
    {
        var business = Guid.NewGuid().ToString("D");
        var installation = Guid.NewGuid().ToString("D");
        var employee = Guid.NewGuid().ToString("D");
        var user = Guid.NewGuid().ToString("D");
        var requests = VideoGameDirectorAgent.BuildMemoryRecallRequests(business, installation, employee, user);

        Assert.Equal(new[] {
            EmployeeMemoryNamespaces.UserRelationship(business, employee, user, "csweet").Partition,
            EmployeeMemoryNamespaces.Employee(business, employee, "csweet").Partition,
            EmployeeMemoryNamespaces.Organization(business, "csweet").Partition
        }, requests.Select(x => x.Partition));
        Assert.Equal(new[] { MemoryScope.User, MemoryScope.Agent, MemoryScope.Tenant }, requests.Select(x => x.Scope));
        Assert.Equal(2_000, requests.Sum(x => x.TokenBudget));
        Assert.All(requests, request => {
            Assert.Equal(employee, request.Access!.Principal.EmployeeId);
            Assert.NotEqual(installation, request.Partition.ApplicationId);
        });
        // Reinstallation changes attribution, never the employee/relationship storage audience.
        var replacement = VideoGameDirectorAgent.BuildMemoryRecallRequests(business,
            Guid.NewGuid().ToString("D"), employee, user);
        Assert.Equal(requests.Select(x => x.Partition), replacement.Select(x => x.Partition));
    }

    [Fact]
    public void StudioBootstrapRequiresSharedCreativeDirectorAndProducer()
    {
        var creativeDirectorId = Guid.NewGuid();
        var roles = VideoGameDirectorAgent.BuildRequiredStudioRoles(creativeDirectorId);

        Assert.Equal(2, roles.Count);
        Assert.Equal(2, roles.Select(x => x.RoleKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(roles, role => Assert.Equal(1, role.Headcount));
        Assert.All(roles, role => Assert.InRange(role.Timing.Length, 1, 32));
        var producer = Assert.Single(roles, role => role.RoleKey == VideoGameRoleKeys.Producer);
        Assert.Equal(creativeDirectorId, producer.ReportsToOrganizationUserId);
        Assert.All(roles, role => Assert.Equal(creativeDirectorId, role.ReportsToOrganizationUserId));
    }

    [Theory]
    [InlineData("web browser Phaser 2D", VideoGameToolchainRecipeKeys.PhaserWeb2D)]
    [InlineData("web browser Babylon 3D", VideoGameToolchainRecipeKeys.BabylonWeb3D)]
    [InlineData("Windows native Godot 2D", VideoGameToolchainRecipeKeys.GodotNative2DGdscript)]
    [InlineData("Linux native Godot 3D", VideoGameToolchainRecipeKeys.GodotNative3DGdscript)]
    public void ToolchainRecipesFollowLockedTargetAndDimensionalityRules(string direction, string expected)
    {
        var state = new GameDirectorOperatingState
        {
            ManagerPreferences = new ManagerPreferenceProfile { PlatformConstraints = [direction] }
        };

        Assert.Equal(expected, VideoGameDirectorAgent.DetermineRequiredRecipe(state));
    }

    [Theory]
    [InlineData("Top-down 2D web game. Phaser 3. No 3D or native builds.")]
    [InlineData("2D web game with 3D-like light effects, but without 3D gameplay.")]
    public void NegativeThreeDimensionalLanguageDoesNotSelectBabylon(string pitch)
    {
        var state = new GameDirectorOperatingState
        {
            AcceptedVision = new AcceptedGameVision(1, "digest", pitch, Guid.NewGuid(), Guid.NewGuid(),
                "hash", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        Assert.Equal(VideoGameToolchainRecipeKeys.PhaserWeb2D,
            VideoGameDirectorAgent.DetermineRequiredRecipe(state));
    }

    [Fact]
    public void ExplicitPhaserOutranksContradictoryPitchLanguage()
    {
        var state = new GameDirectorOperatingState
        {
            ManagerPreferences = new ManagerPreferenceProfile { EnginePreferences = ["latest Phaser"] },
            AcceptedVision = new AcceptedGameVision(1, "digest", "A 3D web game using Babylon.",
                Guid.NewGuid(), Guid.NewGuid(), "hash", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow)
        };

        Assert.Equal(VideoGameToolchainRecipeKeys.PhaserWeb2D,
            VideoGameDirectorAgent.DetermineRequiredRecipe(state));
        Assert.Contains("latest Phaser", VideoGameDirectorAgent.SystemPrompt);
        Assert.Contains("Chromium-only", VideoGameDirectorAgent.SystemPrompt);
        Assert.Contains("Preserve an explicit owner requirement", VideoGameDirectorAgent.SystemPrompt);
    }

    [Theory]
    [InlineData("Asset strategy: provided; use only uploaded project assets.", VideoGameAssetProductionModes.Provided)]
    [InlineData("Use procedural assets and code-native geometry.", VideoGameAssetProductionModes.Procedural)]
    [InlineData("Asset strategy: generative with configured providers.", VideoGameAssetProductionModes.Generative)]
    [InlineData("Use procedural and uploaded assets as a hybrid.", VideoGameAssetProductionModes.Hybrid)]
    public void AssetStrategyPreferenceIsExplicitAndProjectScoped(string direction, string expected)
    {
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(
            new ManagerPreferenceProfile(), direction, Guid.NewGuid(), [], applyDefault: true);

        Assert.Equal(expected, preferences.AssetStrategyPreference);
    }

    [Fact]
    public void ThreeProjectAcceptanceProgramIsIsolatedAndGovernedThroughStabilization()
    {
        var scenarios = new[]
        {
            Scenario("Phaser microgame", "web browser Phaser 2D", VideoGameAssetProductionModes.Procedural,
                VideoGameToolchainRecipeKeys.PhaserWeb2D, ["web"], 25_000m),
            Scenario("Babylon microgame", "web browser Babylon 3D", VideoGameAssetProductionModes.Hybrid,
                VideoGameToolchainRecipeKeys.BabylonWeb3D, ["web"], 35_000m),
            Scenario("Godot puzzle", "Windows Linux native Godot 3D", VideoGameAssetProductionModes.Provided,
                VideoGameToolchainRecipeKeys.GodotNative3DGdscript, ["windows-x64", "linux-x64"], 50_000m)
        };

        Assert.Equal(3, scenarios.Select(x => x.WorkstreamId).Distinct().Count());
        Assert.Equal(3, scenarios.Select(x => x.TeamId).Distinct().Count());
        Assert.Equal(3, scenarios.Select(x => x.BoardId).Distinct().Count());
        Assert.Equal(3, scenarios.Select(x => x.RepositoryId).Distinct().Count());
        Assert.Equal(3, scenarios.Select(x => x.StateKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, scenarios.Select(x => x.Budget).Distinct().Count());
        Assert.All(scenarios, scenario =>
        {
            Assert.Equal(scenario.ExpectedRecipe,
                VideoGameDirectorAgent.DetermineRequiredRecipe(scenario.State));
            Assert.Equal(2, VideoGameDirectorAgent.BuildRequiredStudioRoles(Guid.NewGuid())
                .Select(x => x.RoleKey).Distinct(StringComparer.Ordinal).Count());
        });

        var milestones = VideoGameDirectorAgent.BuildLifecycleMilestones(DateTimeOffset.UtcNow);
        var requiredStages = new[]
        {
            VideoGameLifecyclePhases.Concept, VideoGameLifecyclePhases.PreProduction,
            VideoGameLifecyclePhases.Prototype, VideoGameLifecyclePhases.VerticalSlice,
            VideoGameLifecyclePhases.Production, VideoGameLifecyclePhases.Alpha,
            VideoGameLifecyclePhases.Beta, VideoGameLifecyclePhases.ReleaseCandidate,
            VideoGameLifecyclePhases.Launch, VideoGameLifecyclePhases.PostLaunchStabilization
        };
        Assert.All(requiredStages, stage => Assert.Contains(milestones, x => x.LifecycleStage == stage));
        var launch = Assert.Single(milestones, x => x.Key == VideoGameMilestoneKeys.LaunchApproved);
        Assert.Equal(["human-owner"], launch.RequiredReviewerRoleKeys);
        Assert.Contains("video-game.release-readiness.v1", launch.RequiredEvidenceTypeKeys);
    }

    private static AcceptanceScenario Scenario(string title, string direction, string strategy,
        string recipe, IReadOnlyList<string> targets, decimal budget)
    {
        var workstreamId = Guid.NewGuid();
        return new AcceptanceScenario(workstreamId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            VideoGameDirectorAgent.ProjectStateKey(workstreamId, null), budget, strategy, recipe, targets,
            new GameDirectorOperatingState
            {
                WorkstreamId = workstreamId,
                WorkingTitle = title,
                ManagerPreferences = new ManagerPreferenceProfile
                {
                    PlatformConstraints = [direction],
                    AssetStrategyPreference = strategy
                }
            });
    }

    private sealed record AcceptanceScenario(Guid WorkstreamId, Guid TeamId, Guid BoardId, Guid RepositoryId,
        string StateKey, decimal Budget, string AssetStrategy, string ExpectedRecipe,
        IReadOnlyList<string> Targets, GameDirectorOperatingState State);
}
