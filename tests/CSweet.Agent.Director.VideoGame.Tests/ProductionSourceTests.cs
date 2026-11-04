using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ProductionSourceTests
{
    [Fact]
    public void LegacyCreativeChangesHaveStableCandidateBoundCorrectionReceipts()
    {
        var review = new VideoGameDirectorAgent.CreativeReviewResult("changes-required", "Record the validation plan",
            ["Planned acceptance method is missing"], ["Name the owner and future observation method"], false);
        var first = VideoGameDirectorAgent.CorrectionFor(review, "candidate")!;
        var replay = VideoGameDirectorAgent.CorrectionFor(review, "candidate")!;
        Assert.True(first.Valid("candidate")); Assert.False(first.Valid("other"));
        Assert.Equal(first.ReviewDigest, replay.ReviewDigest);
        Assert.Equal(first.Issues[0].Id, replay.Issues[0].Id);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("content")]
    [InlineData("gdd-identity")]
    [InlineData("project")]
    [InlineData("hash")]
    [InlineData("unaccepted-original")]
    public async Task SourceEvidenceMustMatchAcceptedOriginalsBeforeAnyAcceptance(string fault)
    {
        var director = Guid.NewGuid(); var producer = Guid.NewGuid(); var project = Guid.NewGuid(); var team = Guid.NewGuid();
        var documents = new Dictionary<Guid, ArtifactDocument>();
        ArtifactDocument Doc(string type, string content, string status = "Accepted")
        {
            var id = Guid.NewGuid(); var revision = Guid.NewGuid();
            var doc = new ArtifactDocument(id, type, type, status, revision, status == "Submitted" ? revision : null,
                status == "Accepted" ? revision : null, null, null,
                [new(revision, 1, null, content, new string('a',64), status, DateTimeOffset.UtcNow, null, null)]) { WorkstreamId = project, TeamId = team };
            documents.Add(id, doc); return doc;
        }
        var pitch = Doc("video-game.vision.v1", "Approved arcade promise");
        var gdd = Doc("video-game.gdd.v1", "Accepted keyboard and mouse rules", fault == "unaccepted-original" ? "Draft" : "Accepted");
        ProductionSource Source(ArtifactDocument d) => new(d.Id,d.LatestRevisionId!.Value,d.Revisions[0].ContentSha256,d.DocumentType,d.Revisions[0].Content);
        var sourceData = new ProductionSources(1, project, team, Source(pitch), Source(gdd));
        if (fault == "content") sourceData = sourceData with { Gdd = sourceData.Gdd with { Content = "Changed design" } };
        if (fault == "gdd-identity") sourceData = sourceData with { Gdd = sourceData.Gdd with { DocumentId = Guid.NewGuid() } };
        var source = Doc(ProductionSources.DocumentType, sourceData.Serialize(), "Submitted");
        if (fault == "project") documents[source.Id] = source with { WorkstreamId = Guid.NewGuid() };
        var body = string.Join("\n", new[] {"Scope","Player experience","Non-goals","Acceptance criteria","Deliverables","Constraints","Risks","Open questions"}.Select(x => $"## {x}\nApproved explicit requirements"));
        var brief = Doc(PitchProtocol.DocumentType, body, "Submitted");
        var state = new GameDirectorOperatingState { WorkstreamId=project, TeamId=team, ProducerEmployeeId=producer,
            CreativeDirectorEmployeeId=Guid.NewGuid(), CreativeDesignArtifactId=gdd.Id, CreativeDesignRevisionId=gdd.LatestRevisionId,
            AcceptedVision=new(1,"pitch",pitch.Revisions[0].Content,pitch.Id,pitch.LatestRevisionId!.Value,pitch.Revisions[0].ContentSha256,
                Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow) };
        var review = new PitchReview("pitch",brief.Id,brief.LatestRevisionId!.Value,brief.Revisions[0].ContentSha256,true,[],"Ready for staffing") {
            SourceEvidence = new(source.Id,source.LatestRevisionId!.Value,fault == "hash" ? "wrong" : source.Revisions[0].ContentSha256) };
        var decisions = new List<Guid>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(r,_)=>Task.FromResult(documents[r.GetProperty("artifactId").GetGuid()]))
            .RegisterCapability<ReadDecisionRequest,IReadOnlyList<DecisionRecord>>(DecisionCapabilityNames.ReadV1,(_,_)=>Task.FromResult<IReadOnlyList<DecisionRecord>>([]))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(
                new(Guid.NewGuid(),r.StateKey,"test",1,"Active",new Dictionary<string,string>(),[],r.StateKey,[],Guid.NewGuid(),
                    r.StateKey.StartsWith("director-creative-review:") ? JsonSerializer.SerializeToElement(new { Review=new VideoGameDirectorAgent.CreativeReviewResult("accepted","Scope preserved",[],[],false) },PitchProtocol.Json)
                        : JsonSerializer.SerializeToElement(new DirectorReview(true,"Accepted exact scope",""),PitchProtocol.Json),1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow))))
            .RegisterCapability<DecideArtifactRevision,ArtifactDocument>(PlatformCapabilities.ArtifactDecide,(r,_)=> {
                decisions.Add(r.ArtifactId); var d=documents[r.ArtifactId];
                return Task.FromResult(documents[d.Id]=d with {AcceptedRevisionId=r.RevisionId,Revisions=[d.Revisions[0] with {Status="Accepted"}]});
            });
        var context = runtime.CreateContext(identity:new(director.ToString(),"Marcus",null,"Director",null,[],null,Guid.NewGuid().ToString(),"CEO"));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(),1,1,"Brief","Review",[],new(director,Guid.NewGuid(),"Marcus","Director"),
            new(producer,Guid.NewGuid(),"Gabriel","Producer"),false,[]) { WorkContext=new(Guid.NewGuid(),project,team,null,null,null,null,Guid.NewGuid(),null,null) };
        var agent = new VideoGameDirectorAgent();
        if (fault != "valid")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>agent.ReviewProducerPitchAsync(request,state,review,context,default));
            Assert.Empty(decisions); return;
        }
        var result = await agent.ReviewProducerPitchAsync(request,state,review,context,default);
        Assert.True(result.Artifact!.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(new[]{source.Id,brief.Id},decisions);
        await agent.ReviewProducerPitchAsync(request,state,review,context,default);
        Assert.Equal(2,decisions.Count);
    }
}
