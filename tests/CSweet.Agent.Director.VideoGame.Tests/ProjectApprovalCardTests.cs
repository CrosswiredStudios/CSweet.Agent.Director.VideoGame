using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProjectApprovalCardTests
{
    [Fact]
    public async Task PresentsToolCardWithStableProposalAndMessageBindingOnRecovery()
    {
        var proposalId = Guid.NewGuid(); var messageId = Guid.NewGuid(); var chatId = Guid.NewGuid();
        var calls = new List<SuggestUserActionRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, CommunicationMessage>(CommunicationCapabilities.MessageSend, (request, _) =>
            {
                Assert.DoesNotContain("/approvals", request.GetProperty("content").GetString());
                Assert.Contains("Prism Break", request.GetProperty("content").GetString());
                return Task.FromResult(new CommunicationMessage(messageId, 1, chatId, Guid.NewGuid(), "Naomi", "Agent", "Review", DateTimeOffset.UtcNow));
            })
            .RegisterCapability<SuggestUserActionRequest, SuggestedUserActionResponse>("platform.user-action.suggest.v1", (request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new SuggestedUserActionResponse(Guid.NewGuid(), request.WorkflowType, request.Label, request.Description,
                    "/approvals", "Pending", DateTimeOffset.UtcNow));
            });
        var state = new GameDirectorOperatingState { WorkstreamProposalId = proposalId, WorkingTitle = "Prism Break",
            AcceptedVision = new(1, "digest", "# Prism Break", Guid.NewGuid(), Guid.NewGuid(), "hash", chatId, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var context = runtime.CreateContext();
        await VideoGameDirectorAgent.PresentProjectApprovalAsync(state, context, default);
        await VideoGameDirectorAgent.PresentProjectApprovalAsync(state, context, default);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, request =>
        {
            Assert.Equal("approval.review.v1", request.WorkflowType);
            Assert.Equal(proposalId, request.Parameters.GetProperty("approvalId").GetGuid());
            Assert.Equal(messageId, request.MessageId);
            Assert.Null(request.ChatTurnId);
        });
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey);
    }
}
