using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GameDirectorInteractionTests
{
    [Fact]
    public async Task InitialPitchCreatesItsSourceTaskBeforeModelDispatch_EvenWhenGenerationFails()
    {
        var org = Guid.NewGuid(); var employee = Guid.NewGuid(); var manager = Guid.NewGuid();
        var conversation = Guid.NewGuid(); var message = Guid.NewGuid(); var turn = Guid.NewGuid();
        var key = VideoGameDirectorAgent.ProjectStateKey(null, conversation);
        var stored = new Dictionary<string, AgentOperatingStateResponse>
        {
            [key] = OperatingState(key, new GameDirectorOperatingState
            {
                IntakeConversationId = conversation, IntakeChoiceAsked = true, Phase = GameDirectorPhase.Discovery
            })
        };
        AddPersonalTodoItemRequest? added = null;
        var calls = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(stored.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => Task.FromResult(stored[r.StateKey] = OperatingState(r.StateKey, r.Payload)))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) =>
            {
                added = r;
                return Task.FromResult(PersonalItem(r.Title, r.CorrelationId!, conversation) with { SourceMessageId = message });
            })
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (_, _) =>
            {
                Assert.NotNull(added);
                Assert.Equal(conversation, added.SourceConversationId);
                Assert.Equal(message, added.SourceMessageId);
                calls++;
                throw new TimeoutException("Provider unavailable after dispatch.");
            });
        var context = runtime.CreateContext(org.ToString(), Guid.NewGuid().ToString(),
            new AgentIdentity(employee.ToString(), "Video Game Director", null, "Video Game Director", null, [], null, manager.ToString(), "Owner"));
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), conversation.ToString(), manager.ToString(),
            "Create an arcade game pitch with a focused player fantasy and a clear core loop.",
            new Dictionary<string, string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = manager.ToString() }, turn, 1, message);
        await new VideoGameDirectorAgent().HandleEventAsync(new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(),
            CommunicationEvents.MessageReceived, JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow), context, default);
        Assert.Equal(1, calls);
        Assert.Equal($"director-pitch:{conversation:N}", added!.IdempotencyKey);
    }

    [Fact]
    public async Task ModelStreamingPublishesReasoningAndDraftToTheConversationTurn()
    {
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())));
        var turnId = Guid.NewGuid();
        string response;

        await using (var stream = runtime.CreateContext().CreateTurnStream(
                         Guid.NewGuid().ToString("D"), turnId, 1))
        {
            response = await VideoGameDirectorAgent.StreamAssistantResponseAsync(
                new ReasoningChatClient(),
                [new ChatMessage(ChatRole.User, "Create a concise game direction.")],
                new ChatOptions(),
                stream,
                CancellationToken.None);
        }

        Assert.Equal("A focused creative answer.", response);
        var reasoning = Assert.Single(runtime.Progress,
            progress => progress.GetProperty("kind").GetString() == AgentTurnStreamKinds.ReasoningDelta);
        Assert.Equal("I should preserve the requested tone and scope.",
            reasoning.GetProperty("delta").GetString());
        Assert.Contains(runtime.Progress,
            progress => progress.GetProperty("kind").GetString() == AgentTurnStreamKinds.ReasoningCompleted);
        var draft = Assert.Single(runtime.Progress,
            progress => progress.GetProperty("kind").GetString() == AgentTurnStreamKinds.DraftDelta);
        Assert.Equal(response, draft.GetProperty("delta").GetString());
    }

    [Theory]
    [InlineData("Thanks!", (int)GameDirectorInboundDisposition.Acknowledge)]
    [InlineData("What is the project status?", (int)GameDirectorInboundDisposition.StatusRequest)]
    [InlineData("How does the core loop support the player promise?", (int)GameDirectorInboundDisposition.InformationQuestion)]
    [InlineData("Please explore three sequel directions.", (int)GameDirectorInboundDisposition.DurableAction)]
    [InlineData("Decision: How involved?\nAnswer: Review milestones", (int)GameDirectorInboundDisposition.WorkflowInput)]
    [InlineData("Make it a cooperative puzzle game.", (int)GameDirectorInboundDisposition.WorkflowInput)]
    public void InboundPolicySeparatesConversationFromDurableWork(
        string message,
        int expected) =>
        Assert.Equal((GameDirectorInboundDisposition)expected, GameDirectorInteractionPolicy.Classify(message));

    [Fact]
    public void AgendaCorrelationsAndTitlesAreStableAndBounded()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        Assert.Equal(GameDirectorAgenda.VisionCorrelation(conversationId),
            GameDirectorAgenda.VisionCorrelation(conversationId));
        Assert.Equal(GameDirectorAgenda.StaffingCorrelation(conversationId),
            GameDirectorAgenda.StaffingCorrelation(conversationId));
        Assert.Equal(GameDirectorAgenda.ChatActionCorrelation(messageId),
            GameDirectorAgenda.ChatActionCorrelation(messageId));
        Assert.Equal(GameDirectorAgenda.ProjectReviewCorrelation(conversationId),
            GameDirectorAgenda.ProjectReviewCorrelation(conversationId));
        Assert.True(GameDirectorAgenda.TaskTitle(new string('a', 500)).Length <= 114);
        Assert.Equal(TimeSpan.FromDays(1),
            GameDirectorAgenda.ProjectReviewCadence(GameDirectorPhase.Oversight));
        Assert.Equal(TimeSpan.FromHours(4),
            GameDirectorAgenda.ProjectReviewCadence(GameDirectorPhase.DetailedDesign));
    }

    [Fact]
    public void StaffingSchemaRejectionIsActionableRatherThanTransient()
    {
        Assert.False(VideoGameDirectorAgent.IsRecoverableStaffingFailure(
            new InvalidOperationException("JSON Schema validation failed: $.roles[0].timing is too long."),
            CancellationToken.None));
        Assert.True(VideoGameDirectorAgent.IsRecoverableStaffingFailure(
            new PlatformCapabilityException(
                PlatformCapabilities.ResourceChangePropose,
                PlatformCapabilityErrorCode.Unavailable,
                "Temporarily unavailable."),
            CancellationToken.None));
    }

    [Fact]
    public async Task AcceptedVisionCompletesItsDurablePersonalCard()
    {
        var conversationId = Guid.NewGuid();
        var workstreamId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var artifactRevisionId = Guid.NewGuid();
        const string hash = "abc123";
        var stateKey = VideoGameDirectorAgent.ProjectStateKey(workstreamId, null);
        var state = new GameDirectorOperatingState
        {
            IntakeConversationId = conversationId,
            WorkstreamId = workstreamId,
            Phase = GameDirectorPhase.HighLevelAccepted,
            AcceptedVision = new AcceptedGameVision(
                1, "pitch-digest", "# Accepted game", artifactId, artifactRevisionId, hash,
                conversationId, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        var portfolio = new GameDirectorPortfolioIndex
        {
            Projects = [new GameDirectorPortfolioEntry(
                stateKey, workstreamId, conversationId, null, null, "Accepted game",
                GameDirectorPhase.HighLevelAccepted, DateTimeOffset.UtcNow)]
        };
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    request.StateKey == VideoGameDirectorAgent.PortfolioStateKey
                        ? OperatingState(request.StateKey, portfolio)
                        : request.StateKey == stateKey
                            ? OperatingState(request.StateKey, state)
                            : null)));
        var context = runtime.CreateContext(
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(Guid.NewGuid().ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, Guid.NewGuid().ToString("D"), "CEO"));
        var item = PersonalItem(
            "Build the high-level game design document",
            GameDirectorAgenda.VisionCorrelation(conversationId), conversationId);

        var result = await new VideoGameDirectorAgent().HandlePersonalTodoAsync(
            item, context, CancellationToken.None);

        Assert.Equal(PersonalTodoResult.Completed(
            $"Accepted high-level GDD revision {artifactRevisionId:D} ({hash})."), result);
    }

    [Fact]
    public async Task UnknownPersonalCardIsBlockedWithActionableReason()
    {
        var context = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()))).CreateContext();
        var item = PersonalItem("Unknown", "unknown.v1", Guid.NewGuid());

        var result = await new VideoGameDirectorAgent().HandlePersonalTodoAsync(
            item, context, CancellationToken.None);

        Assert.Equal(PersonalTodoResult.Blocked(
            "Personal agenda correlation 'unknown.v1' is not supported by this Video Game Director version."), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttentionReviewEnsuresOneCorrelatedAgendaCardPerProject(bool missedWake)
    {
        var conversationId = Guid.NewGuid();
        var stateKey = VideoGameDirectorAgent.ProjectStateKey(null, conversationId);
        var state = new GameDirectorOperatingState
        {
            IntakeConversationId = conversationId,
            WorkingTitle = "Clockwork Tides",
            HighLevelSourceMessageId = Guid.NewGuid(),
            Phase = GameDirectorPhase.Discovery
        };
        var portfolio = new GameDirectorPortfolioIndex
        {
            Projects = [new GameDirectorPortfolioEntry(
                stateKey, null, conversationId, null, null, state.WorkingTitle,
                state.Phase, DateTimeOffset.UtcNow)]
        };
        AddPersonalTodoItemRequest? captured = null;
        PersonalTodoItem? legacyCard = null;
        var requeued = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    request.StateKey == VideoGameDirectorAgent.PortfolioStateKey
                        ? OperatingState(request.StateKey, portfolio)
                        : request.StateKey == stateKey
                            ? OperatingState(request.StateKey, state)
                            : null)))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Add,
                (request, _) =>
                {
                    captured = request;
                    legacyCard = PersonalItem(request.Title, request.CorrelationId!, conversationId) with
                    {
                        Status = missedWake ? PersonalTodoStatuses.Running : PersonalTodoStatuses.Blocked,
                        SourceConversationId = null,
                        Wait = missedWake ? new PersonalTodoWaitState(DateTimeOffset.UtcNow.AddHours(4),
                            "Project reconciled. Next step: the CEO decides the submitted pitch revision. Waiting briefly for decisions.") : null,
                        BlockReason = "A portfolio review requires its source project conversation."
                    };
                    return Task.FromResult(legacyCard);
                })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(legacyCard!.BoardId, legacyCard.OwnerOrganizationUserId,
                        "Video Game Director", null, null, 1, [legacyCard])], legacyCard.OwnerOrganizationUserId)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue,
                (request, _) =>
                {
                    Assert.Equal(legacyCard!.Id, request.ItemId);
                    requeued++;
                    return Task.FromResult(legacyCard with { Status = PersonalTodoStatuses.Ready });
                });

        await new VideoGameDirectorAgent().HandleAttentionReviewAsync(
            new AgentAttentionReviewContext(
                Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "test"),
            runtime.CreateContext(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal($"director-project-review:{conversationId:N}", captured.IdempotencyKey);
        Assert.Equal(GameDirectorAgenda.ProjectReviewCorrelation(conversationId), captured.CorrelationId);
        Assert.Equal(conversationId, captured.SourceConversationId);
        Assert.Equal(state.HighLevelSourceMessageId, captured.SourceMessageId);
        Assert.Equal(1, requeued);
    }

    [Fact]
    public void LegacyProjectReviewRecoversConversationFromCorrelation()
    {
        var conversationId = Guid.NewGuid();
        var item = PersonalItem("Review creative direction", GameDirectorAgenda.ProjectReviewCorrelation(conversationId),
            conversationId) with { SourceConversationId = null };
        Assert.Equal(conversationId, GameDirectorAgenda.ProjectReviewConversationId(item));
        Assert.Null(GameDirectorAgenda.ProjectReviewConversationId(item with
        {
            SourceConversationId = Guid.NewGuid()
        }));
        Assert.Null(GameDirectorAgenda.ProjectReviewConversationId(item with
        {
            CorrelationId = $"{GameDirectorAgenda.ProjectReviewKind}:invalid"
        }));
    }

    [Fact]
    public void PitchReviewUsesTheDocumentAsItsSingleDecisionSurface()
    {
        var message = VideoGameDirectorAgent.BuildPitchReviewMessage(1);
        Assert.Contains("Open the attached document to review it", message);
        Assert.Contains("More menu for quick approval", message);
        Assert.Contains("wait for your decision", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PitchWorkAcknowledgementImmediatelySetsHumanExpectations()
    {
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())));
        await using var stream = runtime.CreateContext().CreateTurnStream(
            Guid.NewGuid().ToString("D"), Guid.NewGuid());

        await VideoGameDirectorAgent.PublishPitchWorkAcknowledgementAsync(
            stream, 1, CancellationToken.None);

        Assert.Equal(
            "Perfect—I can work with that. I’m going to start creating the first draft of the game pitch now.",
            VideoGameDirectorAgent.BuildPitchWorkAcknowledgement(1));
        Assert.Contains(
            "start revising the game pitch now",
            VideoGameDirectorAgent.BuildPitchWorkAcknowledgement(2),
            StringComparison.OrdinalIgnoreCase);
        var progress = Assert.Single(runtime.Progress);
        Assert.Equal(AgentTurnStreamKinds.DraftDelta, progress.GetProperty("kind").GetString());
        Assert.Contains("start creating the first draft", progress.GetProperty("delta").GetString());
    }

    [Fact]
    public async Task StaffingCardOwnsAndCompletesTheGovernedProposalSubmission()
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        var sourceMessageId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var artifactRevisionId = Guid.NewGuid();
        var staffingRequestId = Guid.NewGuid();
        var stateKey = VideoGameDirectorAgent.ProjectStateKey(null, conversationId);
        var state = new GameDirectorOperatingState
        {
            IntakeConversationId = conversationId,
            Phase = GameDirectorPhase.HighLevelAccepted,
            AcceptedVision = new AcceptedGameVision(
                1, "pitch-digest", "# Accepted pitch", artifactId, artifactRevisionId,
                "pitch-hash", conversationId, sourceTurnId, sourceMessageId, DateTimeOffset.UtcNow)
        };
        var portfolio = new GameDirectorPortfolioIndex
        {
            Projects = [new GameDirectorPortfolioEntry(
                stateKey, null, conversationId, null, null, "Accepted pitch",
                GameDirectorPhase.HighLevelAccepted, DateTimeOffset.UtcNow)]
        };
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal)
        {
            [stateKey] = OperatingState(stateKey, state),
            [VideoGameDirectorAgent.PortfolioStateKey] = OperatingState(
                VideoGameDirectorAgent.PortfolioStateKey, portfolio)
        };
        ResourceChangeProposalRequest? proposal = null;
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var saved = new AgentOperatingStateResponse(
                        Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload,
                        stored.GetValueOrDefault(request.StateKey)?.Revision + 1 ?? 1,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                })
            .RegisterCapability<ResourceChangeProposalRequest, ResourceChangeRequestResponse>(
                PlatformCapabilities.ResourceChangePropose,
                (request, _) =>
                {
                    proposal = request;
                    return Task.FromResult(new ResourceChangeRequestResponse(
                        staffingRequestId, organizationId, employeeId, installationId, managerId,
                        request.ConversationId, request.ChatTurnId, request.ProductGoal, request.Rationale,
                        request.ContextRevision, request.Roles, [], request.Assumptions, request.Constraints,
                        request.SupersedesRequestId, "Pending", "Pending", null,
                        DateTimeOffset.UtcNow, null)
                    {
                        TeamKey = request.TeamKey,
                        TeamName = request.TeamName,
                        TeamDescription = request.TeamDescription
                    });
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"), installationId.ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "CEO"));
        var item = PersonalItem(
            "Create and submit the game-studio staffing plan",
            GameDirectorAgenda.StaffingCorrelation(conversationId), conversationId);

        var result = await new VideoGameDirectorAgent().HandlePersonalTodoAsync(
            item, context, CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal(conversationId, proposal!.ConversationId);
        Assert.Equal(sourceTurnId, proposal.ChatTurnId);
        Assert.Equal(2, proposal.Roles.Count);
        Assert.Equal($"video-game-studio-plan:{state.AcceptedVision.Digest}", proposal.IdempotencyKey);
        Assert.Equal(PersonalTodoResult.Completed(
            $"Submitted governed game-studio staffing plan {staffingRequestId:D} for approval and fulfillment."), result);
        var savedState = stored[stateKey].Payload.Deserialize<GameDirectorOperatingState>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(staffingRequestId, savedState?.StaffingRequestId);
        Assert.Equal(GameDirectorPhase.TeamPlanPending, savedState?.Phase);
    }

    [Fact]
    public async Task DocumentApprovalEventLocksTheExactPitchAndContinuesTheAgenda()
    {
        var organizationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        var sourceMessageId = Guid.NewGuid();
        const string hash = "exact-revision-hash";
        var stateKey = VideoGameDirectorAgent.ProjectStateKey(null, conversationId);
        var state = new GameDirectorOperatingState
        {
            IntakeConversationId = conversationId,
            Phase = GameDirectorPhase.HighLevelReview,
            HighLevelArtifactId = artifactId,
            HighLevelLatestRevisionId = revisionId,
            HighLevelSourceTurnId = sourceTurnId,
            HighLevelSourceMessageId = sourceMessageId,
            Proposals = [new GamePitchRevision(1, "# Pitch", "pitch-digest", DateTimeOffset.UtcNow, [], [])]
        };
        var portfolio = new GameDirectorPortfolioIndex
        {
            Projects = [new GameDirectorPortfolioEntry(
                stateKey, null, conversationId, null, null, "Pitch",
                GameDirectorPhase.HighLevelReview, DateTimeOffset.UtcNow)]
        };
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal)
        {
            [stateKey] = OperatingState(stateKey, state),
            [VideoGameDirectorAgent.PortfolioStateKey] = OperatingState(
                VideoGameDirectorAgent.PortfolioStateKey, portfolio)
        };
        var confirmations = new List<string>();
        var reviewTodo = PersonalItem("Review creative direction", GameDirectorAgenda.ProjectReviewCorrelation(conversationId), conversationId) with
        {
            Status = PersonalTodoStatuses.Running,
            Wait = new PersonalTodoWaitState(DateTimeOffset.UtcNow.AddHours(4), "Waiting for the CEO to decide the pitch", null)
        };
        var unrelatedReview = reviewTodo with { Id = Guid.NewGuid(), CorrelationId = GameDirectorAgenda.ProjectReviewCorrelation(Guid.NewGuid()), SourceConversationId = null };
        var requeues = new List<RequeuePersonalTodoItemRequest>();
        AddPersonalTodoItemRequest? staffingTodoRequest = null;
        PersonalTodoItem? staffingTodo = null;
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1",
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(reviewTodo.BoardId, reviewTodo.OwnerOrganizationUserId,
                        "Video Game Director", null, null, 1, [reviewTodo, unrelatedReview])], reviewTodo.OwnerOrganizationUserId)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue,
                (request, _) => { requeues.Add(request); return Task.FromResult(reviewTodo with { Status = PersonalTodoStatuses.Ready, Wait = null }); })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var saved = new AgentOperatingStateResponse(
                        Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload,
                        stored.GetValueOrDefault(request.StateKey)?.Revision + 1 ?? 1,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                })
            .RegisterCapability<JsonElement, ArtifactDocument>(
                PlatformCapabilities.ArtifactRead,
                (_, _) => Task.FromResult(new ArtifactDocument(
                    artifactId, "Game pitch", "video-game.game-vision.v1", "Approved",
                    revisionId, null, revisionId, conversationId, null,
                    [new ArtifactRevision(revisionId, 1, null, "# Pitch", hash, "Accepted",
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)])))
            .RegisterCapability<JsonElement, CommunicationMessage>(
                CommunicationCapabilities.MessageSend,
                (request, _) =>
                {
                    confirmations.Add(request.GetProperty("content").GetString()!);
                    return Task.FromResult(new CommunicationMessage(
                        Guid.NewGuid(), 1, conversationId, employeeId, "Video Game Director", "Agent",
                        confirmations[^1], DateTimeOffset.UtcNow));
                })
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Add,
                (request, _) =>
                {
                    staffingTodoRequest = request;
                    staffingTodo = PersonalItem(request.Title, request.CorrelationId!, conversationId);
                    return Task.FromResult(staffingTodo);
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "CEO"));
        var eventId = Guid.NewGuid();
        var resourceEvent = new GenericResourceEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            new AgentWorkContext(organizationId, Guid.Empty, null, null, null, null, null,
                eventId, null, null),
            "ArtifactRevision", revisionId, 1, "video-game.game-vision.v1", "accepted",
            JsonSerializer.SerializeToElement(new { artifactId, originConversationId = conversationId }));

        await new VideoGameDirectorAgent().HandleEventAsync(
            new AgentEventEnvelope(Guid.NewGuid(), eventId,
                WorkstreamEventNames.ArtifactRevisionDecidedV1,
                JsonSerializer.SerializeToElement(resourceEvent), DateTimeOffset.UtcNow),
            context, CancellationToken.None);

        var savedState = stored[stateKey].Payload.Deserialize<GameDirectorOperatingState>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(savedState?.AcceptedVision);
        Assert.Equal(GameDirectorPhase.HighLevelAccepted, savedState!.Phase);
        Assert.Equal(revisionId, savedState.AcceptedVision!.ArtifactRevisionId);
        Assert.Equal(hash, savedState.AcceptedVision.ArtifactRevisionHash);
        Assert.Equal(sourceTurnId, savedState.AcceptedVision.ChatTurnId);
        Assert.Equal(sourceMessageId, savedState.AcceptedVision.MessageId);
        Assert.Equal(staffingTodo?.Id, savedState.StaffingTodoId);
        Assert.NotNull(staffingTodoRequest);
        Assert.Equal("Create and submit the game-studio staffing plan", staffingTodoRequest!.Title);
        Assert.Equal($"director-staffing-plan:{conversationId:N}", staffingTodoRequest.IdempotencyKey);
        Assert.Equal(GameDirectorAgenda.StaffingCorrelation(conversationId), staffingTodoRequest.CorrelationId);
        Assert.Equal(conversationId, staffingTodoRequest.SourceConversationId);
        Assert.Equal(sourceMessageId, staffingTodoRequest.SourceMessageId);
        Assert.Contains(confirmations, message => message.Contains("vision is locked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(confirmations, message => message.Contains("personal task", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(reviewTodo.Id, Assert.Single(requeues).ItemId);
        // Replay can recover a wake interrupted after the state write; the platform's stable
        // mutation key makes the same review revision idempotent.
        await new VideoGameDirectorAgent().HandleEventAsync(
            new AgentEventEnvelope(Guid.NewGuid(), eventId, WorkstreamEventNames.ArtifactRevisionDecidedV1,
                JsonSerializer.SerializeToElement(resourceEvent), DateTimeOffset.UtcNow), context, CancellationToken.None);
        Assert.Equal(2, requeues.Count);
        Assert.Equal(requeues[0].IdempotencyKey, requeues[1].IdempotencyKey);
    }

    private static AgentOperatingStateResponse OperatingState<T>(string key, T payload) =>
        new(Guid.NewGuid(), key, "test", 1, "Active", new Dictionary<string, string>(), [],
            "fingerprint", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(payload), 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static PersonalTodoItem PersonalItem(string title, string correlationId, Guid conversationId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Video Game Director",
            title, "Requested action:\nPrepare a bounded response.", PersonalTodoStatuses.Ready,
            WorkPriorities.High, 1024, 1, null, conversationId, Guid.NewGuid(), [], null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            CorrelationId = correlationId
        };

    private sealed class ReasoningChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent("I should preserve the requested tone and scope.")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextContent("A focused creative answer.")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
