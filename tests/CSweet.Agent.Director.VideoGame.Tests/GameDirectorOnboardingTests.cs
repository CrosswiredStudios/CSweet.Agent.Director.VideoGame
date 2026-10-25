using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GameDirectorOnboardingTests
{
    [Fact]
    public async Task ConcurrentProjectIntakesRemainIsolatedAndAppearInPortfolioIndex()
    {
        var organizationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversations = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var previous = stored.GetValueOrDefault(request.StateKey);
                    var saved = new AgentOperatingStateResponse(
                        previous?.Id ?? Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload, (previous?.Revision ?? 0) + 1,
                        previous?.CreatedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                })
            .RegisterCapability<JsonElement, CommunicationMessage>(
                CommunicationCapabilities.MessageSend,
                (request, _) => Task.FromResult(new CommunicationMessage(
                    Guid.NewGuid(), 1, request.GetProperty("chatId").GetGuid(), employeeId,
                    "Video Game Director", "Agent", request.GetProperty("content").GetString()!, DateTimeOffset.UtcNow)))
            .RegisterCapability<AskUserRequest, UserQuestionResponse>(
                PlatformCapabilities.UserInputRequest,
                (request, _) => Task.FromResult(new UserQuestionResponse(
                    Guid.NewGuid(), request.Prompt, "Pending",
                    request.Options.Select(option => new UserQuestionOptionResponse(
                        option.Id, option.Label, option.Description, option.Id == request.RecommendedOptionId)).ToList(),
                    request.RecommendedOptionId, null, null, DateTimeOffset.UtcNow, null)))
            .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(
                AgentLifecycleCapabilities.CompleteOnboarding,
                (_, _) => Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow)));
        var context = runtime.CreateContext(organizationId.ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null, "Video Game Director",
                null, [], null, managerId.ToString("D"), "Owner"));
        var agent = new VideoGameDirectorAgent();

        foreach (var conversationId in conversations)
            await agent.HandleEventAsync(new AgentEventEnvelope(
                Guid.NewGuid(), Guid.NewGuid(), AgentLifecycleEvents.Onboarded,
                JsonSerializer.SerializeToElement(new AgentOnboardedEvent(
                    organizationId, employeeId, managerId, conversationId, DateTimeOffset.UtcNow)),
                DateTimeOffset.UtcNow), context, CancellationToken.None);

        Assert.All(conversations, conversationId =>
            Assert.True(stored.ContainsKey(VideoGameDirectorAgent.ProjectStateKey(null, conversationId))));
        var portfolio = stored[VideoGameDirectorAgent.PortfolioStateKey].Payload
            .Deserialize<GameDirectorPortfolioIndex>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(portfolio);
        Assert.Equal(2, portfolio!.Projects.Count);
        Assert.Equal(conversations.Order(), portfolio.Projects.Select(x => x.ConversationId).Order());
    }

    [Fact]
    public async Task OnboardingIsIdempotentAndCompletesLifecycleWithoutStaffing()
    {
        var organizationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal);
        var messages = 0;
        string? onboardingMessage = null;
        var onboardingMessageId = Guid.NewGuid();
        AskUserRequest? onboardingQuestion = null;
        var completions = 0;
        var staffingProposals = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var previous = stored.GetValueOrDefault(request.StateKey);
                    var saved = new AgentOperatingStateResponse(
                        Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload, (previous?.Revision ?? 0) + 1,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                })
            .RegisterCapability<JsonElement, CommunicationMessage>(
                CommunicationCapabilities.MessageSend,
                (request, _) =>
                {
                    messages++;
                    onboardingMessage = request.GetProperty("content").GetString();
                    return Task.FromResult(new CommunicationMessage(
                        onboardingMessageId, messages, request.GetProperty("chatId").GetGuid(), employeeId,
                        "Video Game Director", "Agent", request.GetProperty("content").GetString()!,
                        DateTimeOffset.UtcNow));
                })
            .RegisterCapability<AskUserRequest, UserQuestionResponse>(
                PlatformCapabilities.UserInputRequest,
                (request, _) =>
                {
                    if (request.ConversationMessageId.HasValue) onboardingQuestion = request;
                    return Task.FromResult(new UserQuestionResponse(
                        Guid.NewGuid(), request.Prompt, "Pending",
                        request.Options.Select(option => new UserQuestionOptionResponse(
                            option.Id, option.Label, option.Description,
                            option.Id == request.RecommendedOptionId)).ToList(),
                        request.RecommendedOptionId, null, null, DateTimeOffset.UtcNow, null));
                })
            .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(
                AgentLifecycleCapabilities.CompleteOnboarding,
                (_, _) =>
                {
                    completions++;
                    return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow));
                })
            .RegisterCapability<ResourceChangeProposalRequest, ResourceChangeRequestResponse>(
                PlatformCapabilities.ResourceChangePropose,
                (_, _) =>
                {
                    staffingProposals++;
                    throw new InvalidOperationException("Onboarding must not submit staffing.");
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "Owner"));
        var envelope = new AgentEventEnvelope(
            Guid.NewGuid(), eventId, AgentLifecycleEvents.Onboarded,
            JsonSerializer.SerializeToElement(new AgentOnboardedEvent(
                organizationId, employeeId, managerId, conversationId, DateTimeOffset.UtcNow)),
            DateTimeOffset.UtcNow);
        var agent = new VideoGameDirectorAgent();

        await agent.HandleEventAsync(envelope, context, CancellationToken.None);
        await agent.HandleEventAsync(envelope, context, CancellationToken.None);

        Assert.Equal(1, messages);
        Assert.NotNull(onboardingMessage);
        Assert.Contains("Choose how closely", onboardingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(onboardingQuestion);
        Assert.Null(onboardingQuestion.ChatTurnId);
        Assert.Equal(onboardingMessageId, onboardingQuestion.ConversationMessageId);
        Assert.Equal("How involved do you want to be in creative direction?", onboardingQuestion.Prompt);
        Assert.Equal(3, onboardingQuestion.Options.Count);
        Assert.Equal(2, completions);
        Assert.Equal(0, staffingProposals);
        var projectStateKey = VideoGameDirectorAgent.ProjectStateKey(null, conversationId);
        var state = stored[projectStateKey].Payload.Deserialize<GameDirectorOperatingState>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(eventId, state!.OnboardingEventId);
        Assert.True(state.IntakeChoiceAsked);
        Assert.Equal(GameDirectorPhase.InvolvementConfirmation, state.Phase);
    }

    [Fact]
    public async Task FirstManagerTurnUsesStructuredMultipleChoiceForIntake()
    {
        var organizationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal);
        AskUserRequest? question = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var previous = stored.GetValueOrDefault(request.StateKey);
                    var saved = new AgentOperatingStateResponse(
                        Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload, (previous?.Revision ?? 0) + 1,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                })
            .RegisterCapability<AskUserRequest, UserQuestionResponse>(
                PlatformCapabilities.UserInputRequest,
                (request, _) =>
                {
                    question = request;
                    return Task.FromResult(new UserQuestionResponse(
                        Guid.NewGuid(), request.Prompt, "Pending",
                        request.Options.Select(option => new UserQuestionOptionResponse(
                            option.Id, option.Label, option.Description,
                            option.Id == request.RecommendedOptionId)).ToList(),
                        request.RecommendedOptionId, null, null, DateTimeOffset.UtcNow, null));
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "Owner"));
        var incoming = new CommunicationMessageReceivedEvent(
            Guid.NewGuid(), conversationId.ToString("D"), managerId.ToString("D"),
            "I have a concept sketch and want you to lead the game direction.",
            new Dictionary<string, string>
            {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = managerId.ToString("D")
            },
            turnId, 1, messageId);

        await new VideoGameDirectorAgent().HandleEventAsync(
            new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
                JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow),
            context, CancellationToken.None);

        Assert.NotNull(question);
        Assert.Equal(conversationId, question.ConversationId);
        Assert.Equal(turnId, question.ChatTurnId);
        Assert.InRange(question.Options.Count, 2, 4);
        Assert.Contains(question.Options, option => option.Id == question.RecommendedOptionId);
        Assert.Equal(question.Options.Count, question.Options.Select(option => option.Id).Distinct().Count());
        Assert.DoesNotContain(runtime.Progress,
            progress => progress.GetProperty("delta").GetString()?.TrimEnd().EndsWith('?') == true);
        Assert.Contains(runtime.Progress,
            progress => progress.GetProperty("kind").GetString() == AgentTurnStreamKinds.FinalCommit);
    }

    [Fact]
    public async Task InvolvementOnlyAnswerIsDurableAndAcknowledgedWithoutInvokingTheModel()
    {
        var organizationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var projectStateKey = VideoGameDirectorAgent.ProjectStateKey(null, conversationId);
        var initial = new AgentOperatingStateResponse(
            Guid.NewGuid(), projectStateKey, "test", 1,
            GameDirectorPhase.InvolvementConfirmation.ToString(), new Dictionary<string, string>(),
            [], "pending", [], Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new GameDirectorOperatingState
            {
                Phase = GameDirectorPhase.InvolvementConfirmation,
                IntakeChoiceAsked = true
            }), 1, now, now);
        var stored = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal)
        {
            [projectStateKey] = initial
        };
        AskUserRequest? directionQuestion = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AskUserRequest, UserQuestionResponse>(
                PlatformCapabilities.UserInputRequest,
                (request, _) =>
                {
                    directionQuestion = request;
                    return Task.FromResult(new UserQuestionResponse(Guid.NewGuid(), request.Prompt,
                        "Pending", [], request.RecommendedOptionId, null, null, DateTimeOffset.UtcNow, null));
                })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(
                    stored.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(
                PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var previous = stored.GetValueOrDefault(request.StateKey);
                    var saved = new AgentOperatingStateResponse(
                        previous?.Id ?? Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload, (previous?.Revision ?? 0) + 1,
                        previous?.CreatedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    stored[request.StateKey] = saved;
                    return Task.FromResult(saved);
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"),
            new AgentIdentity(employeeId.ToString("D"), "Video Game Director", null,
                "Video Game Director", null, [], null, managerId.ToString("D"), "Owner"));
        var incoming = new CommunicationMessageReceivedEvent(
            Guid.NewGuid(), conversationId.ToString("D"), managerId.ToString("D"),
            "Decision: How involved do you want to be in creative direction?\nAnswer: Review milestones",
            new Dictionary<string, string>
            {
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = managerId.ToString("D")
            }, Guid.NewGuid(), 1, Guid.NewGuid());

        await new VideoGameDirectorAgent().HandleEventAsync(
            new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), CommunicationEvents.MessageReceived,
                JsonSerializer.SerializeToElement(incoming), DateTimeOffset.UtcNow),
            context, CancellationToken.None);

        var state = stored[projectStateKey].Payload.Deserialize<GameDirectorOperatingState>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(ManagerInvolvementMode.MilestoneReview, state!.ManagerPreferences.InvolvementMode);
        Assert.True(state.ManagerPreferences.InvolvementWasExplicit);
        Assert.NotNull(directionQuestion);
        Assert.Equal(conversationId, directionQuestion.ConversationId);
        Assert.Equal(incoming.TurnId, directionQuestion.ChatTurnId);
        Assert.Empty(directionQuestion.Options);
        Assert.Empty(directionQuestion.RecommendedOptionId);
        Assert.Contains("initial game direction", directionQuestion.Prompt);
        Assert.Equal($"creative-initial-direction:{incoming.MessageId:N}", directionQuestion.IdempotencyKey);
        var response = Assert.Single(runtime.Progress,
            progress => progress.GetProperty("kind").GetString() == AgentTurnStreamKinds.FinalCommit);
        Assert.Contains("recorded milestone-review mode", response.GetProperty("delta").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("initial game direction", response.GetProperty("delta").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
