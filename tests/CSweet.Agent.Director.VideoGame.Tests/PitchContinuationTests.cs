using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PitchContinuationTests
{
    [Theory]
    [InlineData("Review milestones")]
    [InlineData("Delegate decisions")]
    [InlineData("Collaborate closely")]
    public async Task Fresh_onboarding_preserves_discovery_then_official_pitch_approval_before_hiring(string involvement)
    {
        var f = new Journey();
        f.States.Clear();
        await f.OnboardAsync();
        await f.OnboardAsync();
        Assert.Single(f.Questions);
        Assert.Contains("How involved", f.PendingQuestion!.Prompt);
        Assert.Equal(0, f.ModelCalls);
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Single(f.Questions);

        await f.AnswerAsync(involvement);
        Assert.Contains("What kind of game", f.PendingQuestion!.Prompt);
        Assert.Equal(0, f.ModelCalls);
        await f.AnswerAsync(Journey.Direction);
        Assert.Null(f.PendingQuestion);
        Assert.Equal("Official Game Pitch", f.Document!.Title);
        Assert.Equal("Submitted", f.Document.Status);
        Assert.Equal(f.Manager, f.Reviewer);
        Assert.Contains("Phaser", f.Document.Revisions.Single().Content);
        Assert.Empty(f.Proposals);
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Null(f.PendingQuestion); // Reporting cannot replace discovery or refinement.
        await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        Assert.Empty(f.Proposals);

        f.AcceptDocument();
        await f.ApprovalEventAsync();
        await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsStaffing), f.Context, default);
        Assert.Equal(new[] { "creative-director", "game-producer" }, Assert.Single(f.Proposals).Roles.Select(x => x.RoleKey).Order());
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Contains("How often", f.PendingQuestion!.Prompt);
        var count = f.Questions.Count;
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Equal(count, f.Questions.Count);
        await f.AnswerAsync("Reports at milestones");
        await f.AnswerAsync("Report format: messages");
        Assert.Null(f.PendingQuestion);
        Assert.NotNull(f.State.AcceptedVision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_reporting_only_onboarding_resumes_once_on_reconnect_or_final_reporting_answer(bool finishReporting)
    {
        var f = new Journey();
        await f.OnboardAsync();
        f.SetState(new() { IntakeConversationId = f.Chat, OnboardingEventId = Guid.NewGuid(),
            Phase = GameDirectorPhase.InvolvementConfirmation, IntakeChoiceAsked = true });
        await f.SendAsync("Decision: How often should I send portfolio updates?\nAnswer: Reports at milestones");
        Assert.Contains("Which format", f.PendingQuestion!.Prompt);
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Contains("Which format", f.PendingQuestion!.Prompt); // Don't supersede an unfinished report choice.
        if (finishReporting)
            await f.AnswerAsync("Report format: messages");
        else
        {
            // Reproduce 0.1.2: completed reporting, no discovery question left.
            var stored = f.States["director-ceo-reporting"];
            f.States[stored.StateKey] = stored with { Payload = JsonSerializer.SerializeToElement(
                new VideoGameDirectorAgent.ReportingPreferences(f.Chat, "Milestones", "Messages", "UTC")) };
            f.PendingQuestion = null;
            await f.Agent.HandleAttentionReviewAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5), "reconnect"), f.Context, default);
        }
        Assert.Contains("How involved", f.PendingQuestion!.Prompt);
        var count = f.Questions.Count;
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Equal(count, f.Questions.Count);
        Assert.Equal(0, f.ModelCalls);
        Assert.Empty(f.Proposals);
        await f.AnswerAsync("Review milestones");
        Assert.Contains("What kind of game", f.PendingQuestion!.Prompt);
        await f.AnswerAsync(Journey.Direction);
        Assert.Equal("Submitted", f.Document!.Status);
        Assert.Empty(f.Proposals);
    }

    [Fact]
    public async Task Legacy_explicit_involvement_resumes_with_game_direction_not_another_preference_question()
    {
        var f = new Journey();
        await f.OnboardAsync();
        f.SetState(f.State with { OnboardingEventId = Guid.NewGuid(), OnboardingFlowVersion = 0 });
        await f.Agent.ContinueDirectorOnboardingAsync(f.Context, default);
        Assert.Contains("What kind of game", f.PendingQuestion!.Prompt);
        Assert.Empty(f.PendingQuestion.Options);
        Assert.Equal(0, f.ModelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direction_to_exact_approval_to_both_leadership_hires_survives_failed_generation(bool failFirst)
    {
        var f = new Journey { FailModel = failFirst };
        await f.SendDirectionAsync();
        Assert.Empty(f.Proposals);
        Assert.Single(f.Tasks.Values, GameDirectorAgenda.IsVision);
        if (failFirst)
        {
            Assert.Null(f.Document);
            Assert.NotNull(f.State.VisionTodoId);
            Assert.Equal(f.Message, f.State.HighLevelSourceMessageId);
            f.FailModel = false;
            await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        }
        Assert.NotNull(f.Document);
        Assert.Equal("Submitted", f.Document.Status);
        Assert.Null(f.State.AcceptedVision);
        Assert.Equal(GameDirectorPhase.HighLevelReview, f.State.Phase);
        Assert.Equal(f.Manager, f.Reviewer);
        Assert.Empty(f.Proposals);
        var calls = f.ModelCalls;
        await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        Assert.Equal(calls, f.ModelCalls);
        Assert.Equal(1, f.DocumentCreates);
        f.AcceptDocument();
        await f.ApprovalEventAsync();
        await f.ApprovalEventAsync();
        Assert.Equal(f.Document.LatestRevisionId, f.State.AcceptedVision!.ArtifactRevisionId);
        Assert.Equal(f.Document.Revisions.Single().ContentSha256, f.State.AcceptedVision.ArtifactRevisionHash);
        var staffing = Assert.Single(f.Tasks.Values, GameDirectorAgenda.IsStaffing);
        Assert.Empty(f.Proposals);
        await f.Agent.HandlePersonalTodoAsync(staffing, f.Context, default);
        var proposal = Assert.Single(f.Proposals);
        Assert.Equal(2, proposal.Roles.Count);
        Assert.Equal(new[] { "creative-director", "game-producer" }, proposal.Roles.Select(x => x.RoleKey).Order());
        Assert.All(proposal.Roles, x => Assert.Equal(f.Employee, x.ReportsToOrganizationUserId));
    }

    [Fact]
    public async Task Legacy_pitch_task_recovers_without_saved_task_or_source_ids()
    {
        var f = new Journey { FailModel = true };
        await f.SendDirectionAsync();
        f.SetState(f.State with { VisionTodoId = null, HighLevelSourceMessageId = null, HighLevelSourceTurnId = null });
        f.FailModel = false;
        await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        Assert.Equal(GameDirectorPhase.HighLevelReview, f.State.Phase);
        Assert.NotNull(f.Document);
        Assert.Empty(f.Proposals);
    }

    [Fact]
    public async Task Lost_document_response_reuses_cached_pitch_without_another_model_call()
    {
        var f = new Journey { FailDocument = true };
        await Assert.ThrowsAsync<TimeoutException>(() => f.SendDirectionAsync());
        Assert.Equal(1, f.ModelCalls);
        f.FailDocument = false;
        await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        Assert.Equal(1, f.ModelCalls);
        Assert.Equal(1, f.DocumentCreates);
        Assert.NotNull(f.Document);
        Assert.Equal("Submitted", f.Document.Status);
    }

    [Fact]
    public async Task Attention_review_wakes_the_legacy_stalled_pitch_and_anchors_review_tasks()
    {
        var f = new Journey { FailModel = true };
        await f.SendDirectionAsync();
        f.SetState(f.State with { VisionTodoId = null, HighLevelSourceMessageId = null, HighLevelSourceTurnId = null });
        var key = f.Tasks.Single(x => GameDirectorAgenda.IsVision(x.Value)).Key;
        f.Tasks[key] = f.Tasks[key] with { Status = PersonalTodoStatuses.Running,
            Wait = new(DateTimeOffset.UtcNow.AddHours(4), "The game direction is durable; waiting for the next chat turn to produce or retry the high-level GDD.") };
        await f.Agent.HandleAttentionReviewAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "reconnect"), f.Context, default);
        Assert.Equal(1, f.Requeues);
        var review = Assert.Single(f.Tasks.Values, GameDirectorAgenda.IsProjectReview);
        Assert.Equal(f.Chat, review.SourceConversationId);
        Assert.Equal(f.Message, review.SourceMessageId);
        Assert.Equal(PersonalTodoStatuses.Ready, f.Tasks[key].Status);
    }

    [Fact]
    public async Task Recovery_does_not_generate_for_unavailable_source()
    {
        var f = new Journey { FailModel = true };
        await f.SendDirectionAsync();
        f.SourceAvailable = false;
        f.FailModel = false;
        var result = await f.Agent.HandlePersonalTodoAsync(f.Tasks.Values.Single(GameDirectorAgenda.IsVision), f.Context, default);
        Assert.Equal(PersonalTodoResult.Blocked("The original CEO direction is unavailable or no longer authoritative."), result);
        Assert.Equal(1, f.ModelCalls);
        Assert.Null(f.Document);
    }

    [Theory]
    [InlineData("Reports weekly", "Weekly")]
    [InlineData("Reports at milestones", "Milestones")]
    public void Reporting_handles_actual_decision_answer_envelope(string answer, string expected) =>
        Assert.Equal(expected, VideoGameDirectorAgent.ReportingCadence(
            VideoGameDirectorAgent.ReportingAnswer($"Decision: How often should I send portfolio updates?\nAnswer: {answer}")));

    private sealed class Journey
    {
        public Guid Org = Guid.NewGuid(), Employee = Guid.NewGuid(), Manager = Guid.NewGuid(), Chat = Guid.NewGuid(),
            Message = Guid.NewGuid(), Turn = Guid.NewGuid(), Installation = Guid.NewGuid();
        public bool FailModel, FailDocument, SourceAvailable = true;
        public int ModelCalls, DocumentCreates, Requeues;
        public Guid? Reviewer;
        public ArtifactDocument? Document;
        public readonly VideoGameDirectorAgent Agent = new();
        public readonly Dictionary<string, AgentOperatingStateResponse> States = [];
        public readonly Dictionary<string, PersonalTodoItem> Tasks = [];
        public readonly List<ResourceChangeProposalRequest> Proposals = [];
        public readonly Dictionary<string, AskUserRequest> Questions = [];
        public AskUserRequest? PendingQuestion;
        private readonly Guid onboardingEvent = Guid.NewGuid();
        public AgentRuntimeContext Context;
        public const string Direction = "Create a Breakout game in Phaser for desktop web, with polished effects and chiptune music. No budget or time constraint.";
        private string Key => VideoGameDirectorAgent.ProjectStateKey(null, Chat);
        public GameDirectorOperatingState State => States[Key].Payload.Deserialize<GameDirectorOperatingState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        public void SetState(GameDirectorOperatingState state) => States[Key] = new(Guid.NewGuid(), Key, "test", 1, "Active", new Dictionary<string,string>(), [], "test", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(state), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Journey()
        {
            SetState(new() { IntakeConversationId = Chat, IntakeChoiceAsked = true, Phase = GameDirectorPhase.InvolvementConfirmation,
                ManagerPreferences = new() { InvolvementMode = ManagerInvolvementMode.MilestoneReview, InvolvementWasExplicit = true } });
            var runtime = new AgentTestRuntime()
                .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(AgentLifecycleCapabilities.CompleteOnboarding,
                    (_, _) => Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow)))
                .RegisterCapability<AskUserRequest, UserQuestionResponse>(PlatformCapabilities.UserInputRequest, (r, _) => {
                    Assert.NotEqual(r.ChatTurnId.HasValue, r.ConversationMessageId.HasValue);
                    // Match ExecutiveDecisionService: a new ordinary question supersedes the previous one.
                    if (Questions.TryAdd(r.IdempotencyKey, r)) PendingQuestion = r;
                    return Task.FromResult(new UserQuestionResponse(Guid.NewGuid(), r.Prompt, "Pending", [], r.RecommendedOptionId,
                        null, null, DateTimeOffset.UtcNow, null));
                })
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    // Match the production broker's object-only schema; the old cached string must fail here.
                    Assert.Equal(JsonValueKind.Object, r.Payload.ValueKind);
                    return Task.FromResult(States[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
                        r.Status, r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations,
                        r.AttentionReviewId, r.Payload, (States.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                })
                .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => {
                    Assert.Equal(r.SourceConversationId.HasValue, r.SourceMessageId.HasValue);
                    if (!Tasks.TryGetValue(r.IdempotencyKey, out var task))
                        Tasks[r.IdempotencyKey] = task = new(Guid.NewGuid(), Guid.NewGuid(), Org, Employee, "Director", r.Title, r.Description ?? "",
                            PersonalTodoStatuses.Ready, WorkPriorities.High, 1024, 1, null, r.SourceConversationId, r.SourceMessageId,
                            [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = r.CorrelationId, WorkContext = r.WorkContext };
                    return Task.FromResult(task);
                })
                .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) => {
                    var entry = Tasks.Single(x => x.Value.Id == r.ItemId);
                    Requeues++;
                    return Task.FromResult(Tasks[entry.Key] = entry.Value with { Status = PersonalTodoStatuses.Ready, Wait = null });
                })
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new(Guid.NewGuid(), Employee, "Director", null, null, 1, Tasks.Values.ToArray())], Employee)))
                .RegisterCapability<JsonElement, JsonElement>("platform.project-approval.read.v1", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>())))
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (r, _) => {
                    ModelCalls++;
                    if (FailModel) throw new TimeoutException("Temporary provider outage");
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { text = "# Prism Break\n\n## Platform\nDesktop web using Phaser.\n## Concept\nJuicy brick-breaking with a chiptune soundtrack.", role = "assistant" }));
                })
                .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages(
                    SourceAvailable ? [new(Message, 1, Chat, Manager, "CEO", "Human", Direction, DateTimeOffset.UtcNow, Turn)] : [])))
                .RegisterCapability<JsonElement, CommunicationMessage>(CommunicationCapabilities.MessageSend, (r, _) => Task.FromResult(new CommunicationMessage(
                    Guid.NewGuid(), 2, Chat, Employee, "Director", "Agent", r.GetProperty("content").GetString()!, DateTimeOffset.UtcNow)))
                .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate, (r, _) => {
                    if (Document is null) {
                        DocumentCreates++;
                        var revision = new ArtifactRevision(Guid.NewGuid(), 1, null, r.Content, VideoGameDirectorAgent.Digest(r.Content), "Draft", DateTimeOffset.UtcNow, null, null);
                        Document = new(Guid.NewGuid(), r.Title, r.DocumentType, "Draft", revision.Id, null, null, Chat, null, [revision]);
                    }
                    if (FailDocument) throw new TimeoutException("Lost create response");
                    return Task.FromResult(Document);
                })
                .RegisterCapability<SubmitArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactSubmit, (r, _) => {
                    Reviewer = r.ReviewerOrganizationUserId;
                    Document = Document! with { Status = "Submitted", SubmittedRevisionId = r.RevisionId };
                    return Task.FromResult(Document);
                })
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(Document!))
                .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])))
                .RegisterCapability<ResourceChangeProposalRequest, ResourceChangeRequestResponse>(PlatformCapabilities.ResourceChangePropose, (r, _) => {
                    Proposals.Add(r);
                    return Task.FromResult(new ResourceChangeRequestResponse(Guid.NewGuid(), Org, Employee, Installation, Manager,
                        r.ConversationId, r.ChatTurnId, r.ProductGoal, r.Rationale, r.ContextRevision, r.Roles, [], r.Assumptions,
                        r.Constraints, null, "Pending", "Pending", null, DateTimeOffset.UtcNow, null));
                });
            Context = runtime.CreateContext(Org.ToString(), Installation.ToString(), new(Employee.ToString(), "Director", null, "Video Game Director", null, [], null, Manager.ToString(), "CEO"));
        }
        public Task SendDirectionAsync() => Agent.HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
            JsonSerializer.SerializeToElement(new CommunicationMessageReceivedEvent(Guid.NewGuid(), Chat.ToString(), Manager.ToString(), Direction,
                new Dictionary<string,string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = Manager.ToString() }, Turn, 1, Message)), DateTimeOffset.UtcNow), Context, default);
        public Task OnboardAsync() => Agent.HandleEventAsync(new(Guid.NewGuid(), onboardingEvent, AgentLifecycleEvents.Onboarded,
            JsonSerializer.SerializeToElement(new AgentOnboardedEvent(Org, Employee, Manager, Chat, DateTimeOffset.UtcNow)), DateTimeOffset.UtcNow), Context, default);
        public Task SendAsync(string text)
        {
            Message = Guid.NewGuid(); Turn = Guid.NewGuid();
            return Agent.HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
                JsonSerializer.SerializeToElement(new CommunicationMessageReceivedEvent(Guid.NewGuid(), Chat.ToString(), Manager.ToString(), text,
                    new Dictionary<string,string> { [CommunicationMessageContextKeys.SenderOrganizationUserId] = Manager.ToString() }, Turn, 1, Message)), DateTimeOffset.UtcNow), Context, default);
        }
        public Task AnswerAsync(string text)
        {
            var prompt = Assert.IsType<AskUserRequest>(PendingQuestion).Prompt;
            PendingQuestion = null;
            return SendAsync($"Decision: {prompt}\nAnswer: {text}");
        }
        public void AcceptDocument() => Document = Document! with { Status = "Approved", AcceptedRevisionId = Document.LatestRevisionId,
            Revisions = [Document.Revisions.Single() with { Status = "Accepted", DecidedAt = DateTimeOffset.UtcNow }] };
        public Task ApprovalEventAsync()
        {
            var resource = new GenericResourceEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, new(Org, Guid.Empty, null, null, null, null, null, Guid.NewGuid(), null, null),
                "ArtifactRevision", Document!.LatestRevisionId!.Value, 1, "video-game.game-vision.v1", "accepted",
                JsonSerializer.SerializeToElement(new { artifactId = Document.Id, originConversationId = Chat }));
            return Agent.HandleEventAsync(new(Guid.NewGuid(), Guid.NewGuid(), WorkstreamEventNames.ArtifactRevisionDecidedV1,
                JsonSerializer.SerializeToElement(resource), DateTimeOffset.UtcNow), Context, default);
        }
    }
}
