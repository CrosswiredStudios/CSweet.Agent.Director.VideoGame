using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class IncidentResolutionTests
{
    [Fact]
    public async Task SelectedOperationalFixUsesSharedRecoveryBeforeCreativeIntake()
    {
        var actor = Guid.NewGuid(); var decision = Guid.NewGuid(); var turn = Guid.NewGuid(); var chat = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var incident = new ManagementIncident(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Open",
            "Scope reconciliation is blocked", Guid.NewGuid(), now, now, null, 4, "The authority decisions are recorded.",
            "The accepted brief still needs updating.", "Contractor and playtest facts.", "Coordinate recovery.", []);
        var calls = 0; var handled = false; UpdateManagementIncidentPresentation? saved = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadIncidentResolution, IncidentResolutionContext>(ProjectHealthCapabilities.Resolution,
                (_, _) => Task.FromResult(new IncidentResolutionContext(incident, decision, 1,
                    "Identify the missing contractor and playtest facts and coordinate the next step.", "collect-evidence", null, handled)
                    { RecordedOutcome = saved?.ResolutionOutcome }))
            .RegisterCapability<ReadManagementIncidents, ManagementIncidentPage>(ProjectHealthCapabilities.Incidents,
                (_, _) => Task.FromResult(new ManagementIncidentPage([incident], null)))
            .RegisterCapability<UpdateManagementIncidentPresentation, ManagementIncident>(ProjectHealthCapabilities.Presentation,
                (request, _) => { saved = request; handled = true; return Task.FromResult(incident); })
            .RegisterCapability<JsonElement, JsonElement>("platform.llm.chat-stream.v1", (request, _) =>
            {
                calls++;
                Assert.Equal("manager-incident", request.GetProperty("telemetry").GetProperty("invocationKind").GetString());
                Assert.Equal(2048, request.GetProperty("maxOutputTokens").GetInt32());
                Assert.Contains("Do not reject coordination", request.GetRawText());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant",
                    text = "The recorded decisions leave contractor identity and playtest logistics to confirm. Please provide those facts; I can coordinate the accepted brief update with the Producer." }));
            });
        var agent = new VideoGameDirectorAgent();
        var configured = await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test-model") }));
        Assert.True(configured.Succeeded, configured.Error);
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), chat.ToString(), actor.ToString(),
            "Decision: How should I help resolve this blocker?\nAnswer: Collect missing logistics facts",
            new Dictionary<string, string> { [ManagerIncidentCoordinator.DecisionContextKey] = decision.ToString() }, turn, 1, Guid.NewGuid());
        await runtime.DeliverEventAsync(agent, CommunicationEvents.MessageReceived, incoming);
        await runtime.DeliverEventAsync(agent, CommunicationEvents.MessageReceived, incoming);
        Assert.Equal(1, calls); Assert.Equal(decision, saved!.ResolutionDecisionId);
        Assert.Contains("coordinate the accepted brief", saved.ResolutionOutcome);
        Assert.DoesNotContain("outside Creative", string.Join(" ", runtime.Progress.Select(x => x.GetRawText())), StringComparison.OrdinalIgnoreCase);
    }
}
