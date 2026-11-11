using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProducerCorrectionRecoveryTests
{
    [Fact]
    public async Task UpgradeRecoveryResumesTheRetainedReadyDraftThenRelaysItsActualCreativeChanges()
    {
        var f = new Fixture();
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State, f.Context, default);
        Assert.Equal(1,f.Resumes); Assert.Equal("Active",f.Session.Status);
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default);
        Assert.Equal(1,f.Resumes);
        var request = f.Request();
        var agent = new VideoGameDirectorAgent();
        var result = await agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal("Continue",result.Disposition);
        var reply = result.Artifact!.Payload.Deserialize<PitchReply>(PitchProtocol.Json)!;
        Assert.False(reply.Accepted); Assert.Null(reply.OwnerDecisionId);
        Assert.Equal(f.Review.RevisionId,reply.RevisionId);
        Assert.True(reply.Correction!.Valid(f.Review.RevisionSha256));
        Assert.Equal("Record separate flash and reduced-motion controls",Assert.Single(reply.Correction.Issues).Change);
        var replay = await agent.HandleCoordinationTurnAsync(request,f.Context,default);
        Assert.Equal(result.Artifact.Payload.GetRawText(),replay.Artifact!.Payload.GetRawText());
    }

    [Fact]
    public async Task ResumeWithUnknownResponseIsNotRepeatedAfterCurrentStateShowsItActive()
    {
        var f = new Fixture { LoseResumeResponse=true };
        await Assert.ThrowsAsync<TimeoutException>(() => VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default));
        Assert.Equal(1,f.Resumes);
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default);
        Assert.Equal(1,f.Resumes);
        Assert.Equal("Continue",(await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.Request(),f.Context,default)).Disposition);
    }

    [Fact]
    public async Task AnotherBlockedRevisionCannotResetTheRecoveryWindow()
    {
        var f = new Fixture();
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default);
        f.Session=f.Session with {Status="Blocked",Revision=f.Session.Revision+3};
        Assert.False(await VideoGameDirectorAgent.RecoverProducerCorrectionAsync(f.State,f.Session,f.Context,default));
        Assert.Equal(1,f.Resumes);
        Assert.Equal("Blocked",(await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.Request(),f.Context,default)).Disposition);
    }

    [Fact]
    public async Task AResumeFailureBeforeCommitRetriesOnlyTheJournaledIdempotencyKey()
    {
        var f = new Fixture { FailBeforeResume=true };
        await Assert.ThrowsAsync<TimeoutException>(() => VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default));
        f.FailBeforeResume=false;
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default);
        Assert.Equal(1,f.Resumes);Assert.Equal(2,f.ResumeKeys.Count);Assert.Single(f.ResumeKeys.Distinct());
    }

    [Fact]
    public async Task NewerDraftWhileResumeIsQueuedCannotBeReplacedByTheOldReadyReview()
    {
        var f = new Fixture();
        await VideoGameDirectorAgent.CheckProducerHandoffAsync(f.State,f.Context,default);
        f.AdvanceBrief();
        Assert.Equal("Blocked",(await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.Request(),f.Context,default)).Disposition);
        Assert.Equal(1,f.Resumes);
    }

    [Theory]
    [InlineData("other-director")]
    [InlineData("other-producer")]
    [InlineData("other-project")]
    [InlineData("other-team")]
    [InlineData("stale-pitch")]
    [InlineData("newer-draft")]
    [InlineData("not-ready")]
    [InlineData("pending-decision")]
    [InlineData("paused")]
    [InlineData("review-mismatch")]
    [InlineData("director-required")]
    [InlineData("no-review")]
    [InlineData("typed-correction")]
    [InlineData("cancelled")]
    [InlineData("real-blocker")]
    [InlineData("turn-limit")]
    public async Task DiscoveryNeverResumesForeignStalePausedOrAlreadyBoundedWork(string fault)
    {
        var f = new Fixture(fault);
        Assert.False(await VideoGameDirectorAgent.RecoverProducerCorrectionAsync(f.State,f.Session,f.Context,default));
        Assert.Equal(0,f.Resumes);
    }

    [Fact]
    public async Task UnjournaledCallbackCannotPretendTheBlockedSessionWasAuthorizedForRecovery()
    {
        var f = new Fixture();
        Assert.Equal("Blocked",(await new VideoGameDirectorAgent().HandleCoordinationTurnAsync(f.Request(),f.Context,default)).Disposition);
        Assert.Equal(0,f.Resumes);
    }

    private sealed class Fixture
    {
        public readonly GameDirectorOperatingState State;
        public AgentCoordinationSession Session;
        public readonly PitchReview Review;
        public AgentRuntimeContext Context;
        public int Resumes;
        public bool LoseResumeResponse;
        public bool FailBeforeResume;
        public List<string> ResumeKeys=[];
        private readonly Dictionary<Guid,ArtifactDocument> documents=[];
        public void AdvanceBrief()=>documents[Review.DocumentId]=documents[Review.DocumentId] with {LatestRevisionId=Guid.NewGuid()};
        private readonly Dictionary<string,AgentOperatingStateResponse> states=[];
        private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
        public Fixture(string fault="valid")
        {
            var director=Guid.NewGuid(); var producer=Guid.NewGuid(); var project=Guid.NewGuid(); var team=Guid.NewGuid();
            var sessionId=Guid.NewGuid(); var pitchId=Guid.NewGuid(); var pitchRev=Guid.NewGuid(); var gddId=Guid.NewGuid(); var gddRev=Guid.NewGuid();
            var document=Guid.NewGuid();var revision=Guid.NewGuid(); var hash=new string('a',64);
            State=new() {WorkstreamId=project,TeamId=team,ProducerEmployeeId=producer,CreativeDirectorEmployeeId=Guid.NewGuid(),
                CreativeDesignArtifactId=gddId,CreativeDesignRevisionId=gddRev,HandoffSessionId=sessionId,
                AcceptedVision=new(1,"pitch-digest","Exact accepted pitch",pitchId,pitchRev,hash,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow)};
            Review=new("pitch-digest",document,revision,hash,fault!="not-ready",[],"No remaining Producer planning questions");
            var vision=new GameVisionBrief("pitch-digest","outcome","loop","platform","art","MVP",[],"criteria",[]) {
                HighLevelGddArtifactId=gddId,HighLevelGddAcceptedRevisionId=gddRev,HighLevelGddRevisionSha256=hash};
            AgentCoordinationArtifact Artifact(AgentCoordinationArtifactSubmission a)=>new(a.Type,a.SchemaVersion,a.Key,1,true,a.Payload,"transport");
            var turns=new List<AgentCoordinationTurn>{new(Guid.NewGuid(),0,director,"Continue","Refine",DateTimeOffset.UtcNow,
                Artifact(PitchProtocol.Artifact(PitchProtocol.BriefType,"pitch-digest",new PitchBrief(vision,pitchId,pitchRev,hash))))};
            for(var i=0;i<4;i++) turns.Add(new(Guid.NewGuid(),1+i*3,producer,"Continue","Ready",DateTimeOffset.UtcNow,
                Artifact(PitchProtocol.Artifact(PitchProtocol.ReviewType,"pitch-digest",Review))));
            var legacyReply=new PitchReply("pitch-digest",document,revision,hash,false,"Apply the retained creative corrections","");
            var creativeReview=new VideoGameDirectorAgent.CreativeReviewResult("changes-required","Make accessibility wording explicit",
                ["Flash and motion controls are ambiguous"],["Record separate flash and reduced-motion controls"],fault=="director-required");
            if(fault=="typed-correction")legacyReply=legacyReply with {Correction=VideoGameDirectorAgent.CorrectionFor(creativeReview,hash)};
            turns.Add(new(Guid.NewGuid(),12,director,"Continue","Correct the brief",DateTimeOffset.UtcNow,Artifact(PitchProtocol.Artifact(PitchProtocol.ReplyType,"pitch-digest",legacyReply))));
            turns.Add(new(Guid.NewGuid(),13,producer,"Blocked",fault=="real-blocker"?"Unapproved scope requires owner decision":
                "Planning refinement needs a concrete manager answer or a new recorded owner decision. The saved brief is retained.",DateTimeOffset.UtcNow));
            turns.Add(new(Guid.NewGuid(),14,director,"Blocked","This request needs a supported project-scoped artifact.",DateTimeOffset.UtcNow));
            Session=new(sessionId,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new(director,Guid.NewGuid(),"Marcus","Director"),
                new(producer,Guid.NewGuid(),"Gabriel","Producer"),"Production brief","Refine",[],fault=="cancelled"?"Cancelled":"Blocked",19,15,null,true,
                turns[^1].Content,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,turns) {WorkContext=new(Guid.NewGuid(),project,team,null,null,null,null,sessionId,null,null)};
            if(fault=="other-producer")Session=Session with {Target=Session.Target with {OrganizationUserId=Guid.NewGuid()}};
            if(fault=="other-project")Session=Session with {WorkContext=Session.WorkContext with {WorkstreamId=Guid.NewGuid()}};
            if(fault=="other-team")Session=Session with {WorkContext=Session.WorkContext with {TeamId=Guid.NewGuid()}};
            if(fault=="turn-limit")Session=Session with {MaximumTurns=Session.NextTurnOrdinal};
            if(fault=="stale-pitch")State=State with {AcceptedVision=State.AcceptedVision! with {ArtifactRevisionHash="stale"}};
            var body=string.Join("\n",new[]{"Scope","Player experience","Non-goals","Acceptance criteria","Deliverables","Constraints","Risks","Open questions"}.Select(x=>$"## {x}\nExplicit approved scope"));
            var docs=documents;
            docs[document]=new(document,"Production brief",PitchProtocol.DocumentType,"InReview",fault=="newer-draft"?Guid.NewGuid():revision,revision,null,null,null,
                [new(revision,4,null,body,hash,"Submitted",DateTimeOffset.UtcNow,null,null)]) {WorkstreamId=project,TeamId=team};
            docs[pitchId]=new(pitchId,"Pitch","video-game.vision.v1","Approved",pitchRev,null,pitchRev,null,null,
                [new(pitchRev,1,null,"Exact accepted pitch",hash,"Accepted",DateTimeOffset.UtcNow,null,null)]);
            docs[gddId]=new(gddId,"GDD","video-game.gdd.v1","Approved",gddRev,null,gddRev,null,null,
                [new(gddRev,1,null,"Accepted GDD",hash,"Accepted",DateTimeOffset.UtcNow,null,null)]);
            AgentOperatingStateResponse Saved(string key,JsonElement value)=>new(Guid.NewGuid(),key,"test",1,"Active",new Dictionary<string,string>(),[],"test",[],Guid.NewGuid(),value,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
            var evidence=new VideoGameDirectorAgent.CreativeReviewRequest(project,team,pitchId,pitchRev,hash,document,
                fault=="review-mismatch"?Guid.NewGuid():revision,hash,"Review creative consistency and player experience within the approved pitch.",sessionId);
            if(fault!="no-review")states[$"director-creative-review:{project:N}:{revision:N}:{pitchRev:N}"]=Saved("review",JsonSerializer.SerializeToElement(new {Request=evidence,Review=creativeReview},Json));
            states[$"video-game-direction:workstream:{project:N}"]=Saved("project",JsonSerializer.SerializeToElement(State));
            var runtime=new AgentTestRuntime()
                .RegisterCapability<ReadAgentCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,(_,_)=>Task.FromResult(Session))
                .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(r,_)=>Task.FromResult(docs[r.GetProperty("artifactId").GetGuid()]))
                .RegisterCapability<ReadDecisionRequest,IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,(_,_)=>Task.FromResult<IReadOnlyList<DecisionRecord>>(Decisions(fault,sessionId,project,producer,Review)))
                .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=>Task.FromResult(states[r.StateKey]=Saved(r.StateKey,r.Payload)))
                .RegisterCapability<ResumeAgentCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationResume,(r,_)=>{
                    Assert.Contains(states.Keys,x=>x.StartsWith("producer-correction-recovery:")); Assert.Equal(Session.Revision,r.ExpectedRevision);
                    ResumeKeys.Add(r.IdempotencyKey);
                    if(FailBeforeResume)throw new TimeoutException("Resume did not commit");
                    Resumes++;Session=Session with {Status="Active",Revision=Session.Revision+1,IsFinalization=false};
                    if(LoseResumeResponse)throw new TimeoutException("Resume reply lost after commit");
                    return Task.FromResult(Session);
                });
            Context=runtime.CreateContext(identity:new((fault=="other-director"?Guid.NewGuid():director).ToString("D"),"Marcus",null,"Director",null,[],null,Guid.NewGuid().ToString("D"),"CEO"));
        }
        public AgentCoordinationTurnRequest Request()=>new(Session.Id,Session.Revision,Session.NextTurnOrdinal,Session.Subject,Session.Objective,[],
            Session.Initiator,Session.Target,false,Session.Turns) {WorkContext=Session.WorkContext};
        private static IReadOnlyList<DecisionRecord> Decisions(string fault,Guid session,Guid project,Guid producer,PitchReview review)
        {
            if(fault is not ("pending-decision" or "paused"))return [];
            // The concrete decision shape is supplied below by the shared contract serializer.
            return [JsonSerializer.Deserialize<DecisionRecord>(JsonSerializer.Serialize(new {
                id=Guid.NewGuid(),workstreamId=project,typeKey="video-game.management-direction.v1",
                status=fault=="paused"?"Decided":"Pending",selectedOptionId="pause-affected-work",createdAt=DateTimeOffset.UtcNow,updatedAt=DateTimeOffset.UtcNow,
                typeData=new {pitchRefinement=new {sessionId=session,documentId=review.DocumentId,revisionId=review.RevisionId,revisionSha256=review.RevisionSha256,producerId=producer,pitchDigest=review.PitchDigest}}
            }),Json)!];
        }
    }
}
