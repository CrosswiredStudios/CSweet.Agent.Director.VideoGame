using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string InitialGameDirectionPrompt =
        "What kind of game would you like to make? Share your initial game direction: the player experience or core loop, target platforms and deployment targets, and any engine, technical, budget or resource constraints. Include any real external deadline and why it matters. You can ask me to propose starting concepts. I’ll turn our agreed direction into an official game pitch for your review; hiring waits for your approval.";

    // The platform supersedes an agent's previous ordinary question in the same chat.
    // Keep discovery ahead of reporting, and repair the question lost by releases 0.1.0–0.1.2.
    internal async Task ContinueDirectorOnboardingAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var reporting = await context.Platform.ReadOperatingStateAsync<ReportingPreferences>(ReportingKey, token);
        if (reporting is not null && (reporting.Payload.Cadence is null ||
            reporting.Payload.Format is null || reporting.Payload.TimeZone is null)) return;

        var portfolio = await ReadPortfolioIndexAsync(context, token);
        foreach (var entry in portfolio.Projects)
        {
            var current = await ReadStateByKeyAsync(entry.StateKey, context, token);
            var state = current.State;
            if (state.OnboardingEventId is not { } onboarding || state.IntakeConversationId is not { } chat) continue;
            if (state.OnboardingFlowVersion == 0 && state.Phase is GameDirectorPhase.Discovery or GameDirectorPhase.InvolvementConfirmation &&
                state.Proposals.Count == 0 && state.VisionTodoId is null && state.HighLevelArtifactId is null)
            {
                var key = $"director-discovery-resume:{onboarding:N}";
                var anchor = await context.Platform.Communication.SendMessageAsync(chat,
                    "Let’s continue game discovery. We’ll agree on the kind of game, deployment targets and constraints, then I’ll create an official game pitch for your review. Your approval of that exact document gates the Creative Director and Producer hiring plan.", key + ":message", token);
                var explicitMode = state.ManagerPreferences.InvolvementWasExplicit;
                await context.Platform.AskUserAsync(new(chat, null,
                    explicitMode ? InitialGameDirectionPrompt : "How involved do you want to be in creative direction?",
                    explicitMode ? [] : InvolvementOptions,
                    explicitMode ? string.Empty : "milestone-review", key + ":question", anchor.Id), token);
                await SaveStateAsync(state with { OnboardingFlowVersion = 1, IntakeChoiceAsked = true,
                    Phase = GameDirectorPhase.InvolvementConfirmation }, current.Revision, onboarding, key + ":state", context, token);
            }
            // Reporting is a business preference, never a substitute for game discovery or pitch approval.
            if (state.AcceptedVision is not null && reporting is null)
            {
                await AskReportingCadenceAsync(chat, onboarding, context, token);
                await SaveReportingAsync(new ReportingPreferences(chat), $"reporting-start:{onboarding:N}", context, token);
                return;
            }
        }
    }
}
