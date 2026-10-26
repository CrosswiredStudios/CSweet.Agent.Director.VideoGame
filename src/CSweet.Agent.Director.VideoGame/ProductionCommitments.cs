using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal const string ProductionStrategyAuthority = "routine-project-production-strategy";
    internal const string CertifiedToolchainAuthority = "routine-certified-toolchain-selection";
    private const string ProductionCommitmentPrefix = "creative-production-decision:";

    internal static async Task QueueProductionCommitmentsAsync(GameDirectorOperatingState state,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (state.WorkstreamId is not { } project || state.AcceptedVision is not { } vision) return;
        foreach (var kind in new[] { "assets", "toolchain" })
        {
            var correlation = $"{ProductionCommitmentPrefix}{kind}:{project:N}:{vision.Digest}";
            await context.Platform.PersonalTodo.AddAsync(new AddPersonalTodoItemRequest(
                kind == "assets" ? "Resolve the project asset strategy" : "Resolve certified toolchain and technical feasibility",
                kind == "assets" ? "Select and record the asset strategy within approved project authority. Missing authority or provider readiness must remain an explicit blocker."
                    : "Obtain exact Technical Director feasibility and select a certified toolchain. Wait for missing technical leadership or capacity without blocking project documentation and staffing discovery.",
                "Medium", null, correlation, SourceConversationId: vision.ConversationId,
                SourceMessageId: vision.MessageId, CorrelationId: correlation)
            { WorkContext = new PersonalTodoWorkContext(WorkstreamId: project, TeamId: state.TeamId) }, token);
        }
    }

    private async Task<PersonalTodoResult> HandleProductionCommitmentAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken token)
    {
        var current = item.WorkContext?.WorkstreamId is { } projectId
            ? await ReadStateAsync(context, token, projectId)
            : await ReadStateForTodoAsync(item, context, token);
        if (current.State.WorkstreamId is null || current.State.AcceptedVision is null)
            return PersonalTodoResult.Blocked("An approved project and accepted direction are required for production decisions.");
        try
        {
            if (current.State.ToolchainBlockerDecisionId is { } blockerId &&
                item.CorrelationId!.StartsWith(ProductionCommitmentPrefix + "toolchain:", StringComparison.Ordinal))
            {
                var blocker = (await context.Platform.ReadDecisionsAsync(new ReadDecisionRequest(blockerId), token)).SingleOrDefault();
                if (blocker is not null)
                    await WithdrawMisroutedBuildDecisionAsync(blocker, current.State, context, token);
            }
            var outcome = item.CorrelationId!.StartsWith(ProductionCommitmentPrefix + "assets:", StringComparison.Ordinal)
                ? await EnsureAssetStrategyAsync(current.State, current.Revision, item.Id, context, token)
                : await EnsureToolchainDecisionAsync(current.State, current.Revision, item.Id, context, token);
            return outcome.Ready ? PersonalTodoResult.Completed("The project decision and its exact evidence are durably recorded.")
                : PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(15),
                    "Waiting for the required project decision, provider capacity, or Technical Director evidence. Documentation collaboration can continue independently.");
        }
        catch (PlatformCapabilityException error) when (!token.IsCancellationRequested)
        {
            return error.Retryable == true
                ? PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(5), "A production-decision dependency is temporarily unavailable: " + error.Message)
                : PersonalTodoResult.Blocked("Production decision requires correction or an authorized decision before retrying: " + error.Message);
        }
        catch (InvalidOperationException error) when (!token.IsCancellationRequested)
        {
            return PersonalTodoResult.Blocked(error.Message);
        }
    }

    internal static async Task<DecisionRecord> DecideProductionChoiceAsync(DecisionRecord decision, string option,
        string rationale, string key, AgentRuntimeContext context, CancellationToken token)
    {
        if (decision.Status == DecisionStatuses.Decided)
        {
            if (decision.SelectedOptionId != option)
                throw new InvalidOperationException($"Decision {decision.Id:D} selected a different option. Reconcile project preferences and evidence with that authorized decision before retrying.");
            return decision;
        }
        if (decision.Status != DecisionStatuses.Pending)
            throw new InvalidOperationException($"Decision {decision.Id:D} is {decision.Status}; use its current replacement before retrying.");
        try
        {
            return await context.Platform.DecideDecisionAsync(new DecideDecisionRequest(decision.Id,
                decision.Revision, option, rationale, key), token);
        }
        catch (PlatformCapabilityException error) when (error.Retryable != true)
        {
            throw new InvalidOperationException($"Decision {decision.Id:D} requires authorized resolution. The project's approved authority must permit '{decision.AuthorityRuleKey}', or an authorized person must resolve the pending decision. {error.Message}", error);
        }
    }
    private static async Task WakeProductionCommitmentAsync(GameDirectorOperatingState state, string decisionType,
        AgentRuntimeContext context, CancellationToken token)
    {
        var kind = decisionType == VideoGameDecisionTypeKeys.AssetStrategy ? "assets"
            : decisionType == VideoGameDecisionTypeKeys.ToolchainSelection ? "toolchain" : null;
        if (kind is null || state.WorkstreamId is null || state.AcceptedVision is null) return;
        var correlation = $"{ProductionCommitmentPrefix}{kind}:{state.WorkstreamId:N}:{state.AcceptedVision.Digest}";
        var directory = await context.Platform.PersonalTodo.ListAsync(token);
        foreach (var task in directory.Boards.SelectMany(x => x.Items).Where(x => x.CorrelationId == correlation))
            await TryRequeuePersonalTodoAsync(task.Id, context, token);
    }
}
