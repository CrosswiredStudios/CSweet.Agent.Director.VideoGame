using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProducerDocumentationTests
{
    [Fact]
    public async Task AcceptedDocumentsRequeueOnlyAPreviouslyBlockedHandoff()
    {
        var conversation = Guid.NewGuid();
        var director = Guid.NewGuid();
        var state = new GameDirectorOperatingState
        {
            AcceptedVision = new(1, "digest", "# Accepted vision", Guid.NewGuid(), Guid.NewGuid(),
                "hash", conversation, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow),
            HighLevelArtifactId = Guid.NewGuid(),
            HighLevelAcceptedRevisionId = Guid.NewGuid()
        };
        var status = PersonalTodoStatuses.Blocked;
        var requeues = 0;
        PersonalTodoItem? task = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add,
                (request, _) =>
                {
                    task = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), director, director,
                        "Director", request.Title, request.Description!, status, request.Priority,
                        1, 1, null, request.SourceConversationId, request.SourceMessageId, [], null, null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                    {
                        CorrelationId = request.CorrelationId,
                        BlockReason = status == PersonalTodoStatuses.Blocked
                            ? "This task stopped after an execution failure exhausted automatic recovery."
                            : null
                    };
                    return Task.FromResult(task);
                })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(task!.BoardId, director, "Director", null, null, 1, [task])], director)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue,
                (_, _) =>
                {
                    requeues++;
                    return Task.FromResult(task! with { Status = PersonalTodoStatuses.Ready });
                });

        var context = runtime.CreateContext();
        await VideoGameDirectorAgent.QueueProducerDocumentationAsync(state, context, default,
            requeueBlockedOnly: true);
        Assert.Equal(1, requeues);
        status = PersonalTodoStatuses.Ready;
        await VideoGameDirectorAgent.QueueProducerDocumentationAsync(state, context, default,
            requeueBlockedOnly: true);
        Assert.Equal(1, requeues);
    }

    [Fact]
    public async Task MissingDocumentCreatesPersonalWorkAndReconstructsSavedScopeForReview()
    {
        var conversation = Guid.NewGuid(); var director = Guid.NewGuid(); var manager = Guid.NewGuid();
        var state = new GameDirectorOperatingState { IntakeConversationId = conversation,
            AcceptedVision = new(1, "digest", "# Gridlock\nOne local web arena; no online multiplayer.",
                Guid.NewGuid(), Guid.NewGuid(), "hash", conversation, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        AgentOperatingStateResponse Saved(string key, JsonElement payload) => new(Guid.NewGuid(), key, "test", 1, "Active",
            new Dictionary<string, string>(), [], "fingerprint", [], Guid.NewGuid(), payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        states[VideoGameDirectorAgent.ProjectStateKey(null, conversation)] = Saved("intake", JsonSerializer.SerializeToElement(state));
        PersonalTodoItem? todo = null; ArtifactDocument? document = null; var creates = 0; var submits = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r, _) => { var saved = Saved(r.StateKey, r.Payload); states[r.StateKey] = saved; return Task.FromResult(saved); })
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => {
                todo = new(Guid.NewGuid(), Guid.NewGuid(), director, director, "Director", r.Title, r.Description!,
                    "Ready", r.Priority, 1, 1, null, r.SourceConversationId, r.SourceMessageId, [], null, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = r.CorrelationId, WorkContext = r.WorkContext };
                return Task.FromResult(todo);
            })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([], director)))
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) =>
                document is null ? throw new PlatformCapabilityException(PlatformCapabilities.ArtifactRead, PlatformCapabilityErrorCode.NotFound, "Missing")
                    : Task.FromResult(document))
            .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate, (r, _) => {
                creates++; Assert.Equal(state.AcceptedVision.Markdown, r.Content); Assert.Equal(manager, r.StewardOrganizationUserId);
                var revision = new ArtifactRevision(Guid.NewGuid(), 1, null, r.Content, "restored-sha", "Draft", DateTimeOffset.UtcNow, null, null);
                document = new(Guid.NewGuid(), r.Title, r.DocumentType, "Draft", revision.Id, null, null, null, null, [revision]);
                return Task.FromResult(document);
            })
            .RegisterCapability<SubmitArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactSubmit, (r, _) => {
                submits++; Assert.Equal(manager, r.ReviewerOrganizationUserId);
                document = document! with { Revisions = document!.Revisions.Select(x => x with { Status = "Submitted" }).ToList() };
                return Task.FromResult(document);
            })
            .RegisterCapability<JsonElement, JsonElement>("communication.message.send.v1", (_, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid() })));
        var context = runtime.CreateContext(identity: new AgentIdentity(director.ToString(), "Director", null,
            "Video Game Director", null, [], null, manager.ToString(), "Owner"));
        await VideoGameDirectorAgent.QueueProducerDocumentationAsync(state, context, default);
        Assert.NotNull(todo); Assert.Contains("documentation", todo.Title);
        var agent = new VideoGameDirectorAgent();
        await agent.HandlePersonalTodoAsync(todo, context, default);
        await agent.HandlePersonalTodoAsync(todo, context, default);
        Assert.Equal(1, creates); Assert.Equal(1, submits);
        Assert.Null(document!.AcceptedRevisionId);
    }
}
