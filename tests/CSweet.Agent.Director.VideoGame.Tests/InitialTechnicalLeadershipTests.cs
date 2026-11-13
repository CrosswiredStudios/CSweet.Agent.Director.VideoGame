using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class InitialTechnicalLeadershipTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Attention_resolves_existing_scoped_and_unscoped_requests(bool supported, bool unscoped)
    {
        var team = Guid.NewGuid(); var project = Guid.NewGuid(); var director = Guid.NewGuid(); var producer = Guid.NewGuid();
        var installation = Guid.NewGuid(); var doc = Guid.NewGuid(); var revision = Guid.NewGuid();
        var request = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Id = Guid.NewGuid(), ManagerOrganizationUserId = director, RequesterOrganizationUserId = producer,
            RequesterInstallationId = installation, TeamId = unscoped ? null : team, WorkstreamId = project,
            Status = "Pending", ExpectedTeamRevision = 4, Evidence = [new("scope-capability-gap", "approved", "Planning")],
            ExpectedEffect = "Unblock feasibility", AlternativesConsidered = ["Assigned team lacks technical coverage"],
            Deltas = [new("Add",new("game-technical-director","Game","Technical planner","Resolve collision feasibility",1,1,"Now",[],false,null,null),null)] };
        var projectState = new GameDirectorOperatingState { TeamId = team, WorkstreamId = project, ProducerEmployeeId = producer,
            HandoffSessionId = Guid.NewGuid(), AcceptedVision = new(1,"pitch","Game",doc,revision,"hash", Guid.NewGuid(),
                Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow) };
        var roster = new AgentTeamContext(team.ToString(),"game","Game",4,director.ToString(),"Director", [
            new AgentTeammate(producer.ToString(),"Producer","Agent",null,null,"Report","Available") {
                AgentInstallationId = installation, DeclaredRoleKeys = ["game-producer"] } ],[],1,false);
        var review = new PitchReview("pitch",doc,revision,"approved",true,[],"Ready");
        AgentCoordinationTurn Turn(Guid user,string type,object payload) => new(Guid.NewGuid(),1,user,"Continue","Review",
            DateTimeOffset.UtcNow,new(type,"1","pitch",1,true,JsonSerializer.SerializeToElement(payload),"transport"));
        var session = JsonSerializer.Deserialize<AgentCoordinationSession>("{}")! with { Turns = [
            Turn(producer, PitchProtocol.ReviewType,review),
            Turn(director, PitchProtocol.ReplyType,new PitchReply("pitch",doc,revision,"approved",true,"Accept","")) ] };
        var state = new Dictionary<string,AgentOperatingStateResponse>();
        var stateKey = VideoGameDirectorAgent.ProjectStateKey(project,null);
        AgentOperatingStateResponse State(string k, JsonElement payload) => new(Guid.NewGuid(),k,"test",1,"Active",
            new Dictionary<string,string>(),[],k,[],Guid.NewGuid(),payload,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
        state[stateKey] = State(stateKey,JsonSerializer.SerializeToElement(projectState));
        ResourceChangeDecisionRequest? decision = null; var judgments = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ResourceChangeReadRequest,ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                (_,_)=>Task.FromResult(new ResourceChangeReadResponse(decision is null ? [request] : [])))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(state.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (r,_)=>Task.FromResult(state[r.StateKey] = State(r.StateKey,r.Payload)))
            .RegisterCapability<TeamRosterV2Request,TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2,
                (_,_)=>Task.FromResult(new TeamRosterV2Response(roster,project)))
            .RegisterCapability<ReadWorkstreamRequest,WorkstreamDetail>(WorkstreamCapabilityNames.ReadV1,
                (_,_)=>Task.FromResult(JsonSerializer.Deserialize<WorkstreamDetail>("{}")! with {Id=project}))
            .RegisterCapability<WorkBoardListRequest,IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
                (_,_)=>Task.FromResult<IReadOnlyList<WorkBoardSummary>>([]))
            .RegisterCapability<ReadAgentCoordinationRequest,AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,(_,_)=>Task.FromResult(session))
            .RegisterCapability<JsonElement,ArtifactDocument>(PlatformCapabilities.ArtifactRead,(_,_)=>Task.FromResult(
                JsonSerializer.Deserialize<ArtifactDocument>("{}")! with {Id=doc,TeamId=team,WorkstreamId=project,
                    Revisions=[new(revision,1,null,"Test high-speed rebounds","approved","Accepted",DateTimeOffset.UtcNow,null,null)]}))
            .RegisterCapability<JsonElement,JsonElement>(PlatformCapabilities.LlmChatStream,(call,_)=>{
                judgments++;
                var input=call.GetProperty("messages")[1].GetProperty("text").GetString();
                Assert.Contains("Test high-speed rebounds",input);
                Assert.Contains("Resolve collision feasibility",input);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { role="assistant",text=JsonSerializer.Serialize(new {
                    justified=supported,reason="Review the high-speed collision workload." }) }));
            })
            .RegisterCapability<ResourceChangeDecisionRequest,ResourceChangeRequestResponse>(PlatformCapabilities.ResourceChangeDecide,(r,_)=>{
                decision=r; return Task.FromResult(request);
            });
        var context=runtime.CreateContext(identity:new AgentIdentity(director.ToString(),"Director",null,null,null,[],null,null,null));
        var agent=new VideoGameDirectorAgent();
        var configured=await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement> {
                ["llmProviderId"]=JsonSerializer.SerializeToElement(Guid.NewGuid()),["llmModel"]=JsonSerializer.SerializeToElement("test") }))),context,default);
        Assert.True(configured.Succeeded,configured.Error);
        await agent.ReviewPendingStaffingAsync(context,default);
        await agent.ReviewPendingStaffingAsync(context,default);
        Assert.Equal(unscoped ? 0 : 1,judgments);
        Assert.Equal(supported ? "Approve" : "RequestRevision",decision!.Decision);
        Assert.Contains(unscoped ? "no team scope" : "high-speed collision",decision.Comment);
    }

    [Theory]
    [InlineData("game-technical-director", true, "scope-capability-gap", "brief-sha", true)]
    [InlineData("game-designer,game-engineer", true, "scope-capability-gap", "brief-sha", true)]
    [InlineData("game-technical-director", false, "scope-capability-gap", "brief-sha", false)]
    [InlineData("game-technical-director", true, "scope-capability-gap", "stale", false)]
    [InlineData("game-technical-director", true, "unrelated", "brief-sha", false)]
    public void Evidence_gate_checks_the_accepted_brief_without_deciding_team_composition(
        string roles, bool approved, string kind, string digest, bool canReview)
    {
        var request = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
            Deltas = roles.Split(',').Select(r => new ResourceChangeRoleDelta("Add",
                new(r,"Game",r,"Cover the scoped work",1,1,"Now",[],false,null,null),null)).ToArray(),
            Evidence = [new(kind, digest, "Accepted production handoff")] };
        var review = new PitchReview("pitch", Guid.NewGuid(), Guid.NewGuid(), "brief-sha", true, [], "Ready");
        var reply = new PitchReply("pitch", review.DocumentId, review.RevisionId, review.RevisionSha256, approved, "Reviewed", "");
        Assert.Equal(canReview, VideoGameDirectorAgent.ValidateKickoffEvidence(request, review, reply) is null);
    }

    [Theory]
    [InlineData(null, "Ready", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "Technical feasibility is uncovered for this increment.", true)]
    [InlineData(false, "The current team can resolve the single tuning question.", true)]
    public void A_manager_decision_requires_an_explicit_judgment_and_reason(bool? justified, string reason, bool valid)
    {
        var judgment = new VideoGameDirectorAgent.StaffingJudgment(justified, reason);
        if (valid) Assert.Equal(judgment, VideoGameDirectorAgent.ValidateStaffingJudgment(judgment));
        else Assert.Throws<AgentModelJsonException>(() => VideoGameDirectorAgent.ValidateStaffingJudgment(judgment));
    }
}
