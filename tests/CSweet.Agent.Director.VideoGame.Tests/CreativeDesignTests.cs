using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class CreativeDesignTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Blocked_revision_supersedes_an_older_submission_without_review_or_acceptance(bool finalization, bool olderAccepted)
    {
        var f = new Fixture(olderAccepted);
        var request = f.Request(finalization);
        var first = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        var replay = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal("Blocked", first.Disposition);
        Assert.Equal(first, replay);
        Assert.Contains(Fixture.IncidentBlocker, first.Content);
        Assert.Contains("Video Game Director owns recovery", first.Content);
        Assert.Null(first.Artifact);
        Assert.Equal(0, f.ArtifactReads);
        Assert.Equal(0, f.Decisions);
    }

    [Fact]
    public async Task Finalization_of_a_fresh_rejected_submission_retains_findings_without_continuing()
    {
        var f = new Fixture(false);
        await f.Configure();
        var request = f.Request(true);
        request = request with { Transcript = request.Transcript.Take(2).ToArray() };
        var result = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal("Blocked", result.Disposition);
        Assert.Contains("still requires changes", result.Content);
        Assert.Contains("Video Game Director", result.Content);
        Assert.Null(result.Artifact);
        Assert.Equal(1, f.ArtifactReads);
        Assert.Equal(1, f.Decisions);
    }

    [Fact]
    public async Task Ordinary_review_can_still_request_a_revision()
    {
        var f = new Fixture(false);
        await f.Configure();
        var request = f.Request(false);
        request = request with { Transcript = request.Transcript.Take(2).ToArray() };
        var result = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal("Continue", result.Disposition);
        Assert.Equal("video-game.creative-design.feedback.v1", result.Artifact!.Type);
    }

    [Fact]
    public async Task Explicit_resume_relays_exact_feedback_after_invalid_design_instead_of_replaying_the_old_review()
    {
        var f = new Fixture(false);
        var request = f.Request(false);
        var feedback = request.Transcript[2].Artifact! with { Key = new string('a',64) };
        request = request with { Transcript = [..request.Transcript.Take(2), request.Transcript[2] with { Artifact = feedback }, request.Transcript[3]] };
        var result = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal("Continue", result.Disposition);
        Assert.Equal(feedback.Type, result.Artifact!.Type);
        Assert.Equal(feedback.Payload.GetRawText(), result.Artifact.Payload.GetRawText());
        Assert.Equal(0, f.ArtifactReads);
        Assert.Equal(0, f.Decisions);
    }

    [Theory]
    [InlineData("valid",false,"Continue")]
    [InlineData("valid",true,"Blocked")]
    [InlineData("project",false,"Blocked")]
    [InlineData("team",false,"Blocked")]
    [InlineData("pitch",false,"Blocked")]
    [InlineData("code",false,"Blocked")]
    [InlineData("malformed",false,"Blocked")]
    public async Task Typed_generation_failure_drives_retry_without_matching_human_prose(string fault,bool finalization,string expected)
    {
        var f=new Fixture(false);
        var request=f.Request(finalization);
        var pitchHash=new string('a',64);
        var failure=new GameCreativeDesignFailureV1(fault=="project" ? Guid.NewGuid() : request.WorkContext!.WorkstreamId,
            fault=="team" ? Guid.NewGuid() : request.WorkContext!.TeamId!.Value, fault=="pitch" ? new string('c',64) : pitchHash,
            fault=="code" ? "vendor.unknown" : GameCreativeDesignFailureCodes.EmptyGeneration,"Aucun contenu fourni.");
        var feedback=request.Transcript[2].Artifact! with {Key=pitchHash};
        var artifact=new AgentCoordinationArtifact(GameCreativeDesignProtocolV1.FailureType,"1.0",pitchHash,1,true,
            fault=="malformed" ? JsonSerializer.SerializeToElement("invalid") : JsonSerializer.SerializeToElement(failure,new JsonSerializerOptions(JsonSerializerDefaults.Web)),"failure");
        request=request with {Transcript=[..request.Transcript.Take(2),request.Transcript[2] with {Artifact=feedback},request.Transcript[3] with {Content="Aucun contenu fourni.",Artifact=artifact}]};
        var result=await f.Agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal(expected,result.Disposition);
        Assert.Equal(0,f.ArtifactReads);
        Assert.Equal(0,f.Decisions);
    }

    [Theory]
    [InlineData("counterpart")]
    [InlineData("team")]
    public async Task A_blocked_reply_from_another_scope_is_not_treated_as_authoritative(string mismatch)
    {
        var f = new Fixture(false);
        var request = f.Request(true);
        request = mismatch == "counterpart"
            ? request with { Counterpart = request.Counterpart with { OrganizationUserId = Guid.NewGuid() } }
            : request with { WorkContext = request.WorkContext! with { TeamId = Guid.NewGuid() } };
        var result = await f.Agent.HandleCoordinationTurnAsync(request, f.Context, default);
        Assert.Equal("Blocked", result.Disposition);
        Assert.DoesNotContain(Fixture.IncidentBlocker, result.Content);
        Assert.Equal(0, f.ArtifactReads);
    }

    private sealed class Fixture
    {
        // Super Awesome Games, 2026-11-12: this terminal reply followed a submitted GDD
        // and Director feedback. Finalization then replayed the earlier submission three times.
        public const string IncidentBlocker = "The GDD is missing required creative sections; no document was submitted.";
        private readonly Guid _director = Guid.NewGuid(), _creative = Guid.NewGuid(), _project = Guid.NewGuid(), _team = Guid.NewGuid();
        private readonly Guid _session = Guid.NewGuid(), _document = Guid.NewGuid(), _revision = Guid.NewGuid();
        private readonly GameDirectorOperatingState _state;
        public readonly VideoGameDirectorAgent Agent = new();
        public AgentRuntimeContext Context { get; }
        public int ArtifactReads, Decisions;
        private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        public Fixture(bool accepted)
        {
            _state = new() { WorkstreamId = _project, TeamId = _team, CreativeDirectorEmployeeId = _creative,
                AcceptedVision = new(1, "pitch", "Approved game", Guid.NewGuid(), Guid.NewGuid(), new string('a',64),
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow) };
            var document = new ArtifactDocument(_document, "GDD", "video-game.gdd.v1", "InReview", _revision, _revision, null, null, null,
                [new(_revision,1,null,"## Gameplay\nIncomplete",new string('b',64),accepted ? "Accepted" : "Submitted",DateTimeOffset.UtcNow,null,null)])
                { WorkstreamId = _project, TeamId = _team };
            var runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory([], _director)))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(r.StateKey != VideoGameDirectorAgent.ProjectStateKey(_project,null) ? null :
                        new(Guid.NewGuid(),r.StateKey,"fixture",1,"Active",new Dictionary<string,string>(),[],"fixture",[],Guid.NewGuid(),E(_state),1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (r, _) => Task.FromResult(new AgentOperatingStateResponse(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,
                        r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)))
                .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => { ArtifactReads++; return Task.FromResult(document); })
                .RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,(_,_)=>Task.FromResult(E(new {text=MissingCoverage(),role="assistant"})))
                .RegisterCapability<StructuredArtifactDecisionRequest, ArtifactDocument>(PlatformCapabilities.ArtifactDecideV2,
                    (_, _) => { Decisions++; return Task.FromResult(document); });
            Context = runtime.CreateContext(identity: new(_director.ToString(),"Marcus",null,"Video Game Director",null,[],null,Guid.NewGuid().ToString(),"CEO"));
        }

        public async Task Configure() => Assert.True((await Agent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,
            E(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement> { ["llmProviderId"]=E(Guid.NewGuid()),["llmModel"]=E("review") }))),Context,default)).Succeeded);
        private static string MissingCoverage()
        {
            var missing = new { sufficient=false,evidence="",reason="Describe the planned game and its acceptance methods." };
            return JsonSerializer.Serialize(new { summary="The GDD needs substantive planning coverage.",creativeIntent=missing,playerExperience=missing,gameplay=missing,
                presentation=missing,acceptanceCriteria=missing,constraints=missing,openDecisions=missing,scopeConcerns=Array.Empty<object>() });
        }

        public AgentCoordinationTurnRequest Request(bool finalization) => new(_session, 5, 4, "Develop creative direction and GDD", "Develop approved game", [],
            new(_director,Guid.NewGuid(),"Marcus","Initiator"),new(_creative,Guid.NewGuid(),"Naomi","Target"),finalization,
            [new(Guid.NewGuid(),0,_director,"Continue","Develop GDD",DateTimeOffset.UtcNow,
                new("video-game.creative-design.request.v1","1.0","pitch",1,true,E(new {}),"request")),
             new(Guid.NewGuid(),1,_creative,"Continue","GDD submitted",DateTimeOffset.UtcNow,
                new("video-game.creative-design.result.v1","1.0","pitch",1,true,E(new {pitchSha256=_state.AcceptedVision!.ArtifactRevisionHash,
                    documentId=_document,revisionId=_revision,revisionSha256=new string('b',64)}),"submission")),
             new(Guid.NewGuid(),2,_director,"Continue","Revise against retained findings",DateTimeOffset.UtcNow,
                new("video-game.creative-design.feedback.v1","1.0","pitch",1,true,E(new {}),"feedback")),
             new(Guid.NewGuid(),3,_creative,"Blocked",IncidentBlocker,DateTimeOffset.UtcNow)])
            { WorkContext = new(Guid.NewGuid(),_project,_team,null,null,null,null,_session,null,null) };
    }
}
