using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GddSemanticValidationTests
{
    private const string Evidence = "Players break bricks with an expressive musical paddle.";
    private static GddSemanticReviewContent Complete() {
        var covered = new GddCoverageJudgment(true,Evidence,"The passage explains the intended design at this planning stage.");
        return new("Planning coverage is supported.",covered,covered,covered,covered,covered,covered,covered,[]);
    }

    [Fact]
    public void Missing_substance_blocks_even_when_the_document_has_every_house_heading()
    {
        var review = Complete() with { AcceptanceCriteria = new(false,"","Describe planned validation and its accountable role.") };
        var normalized = VideoGameDirectorAgent.ValidateGddReview(review,Evidence);
        var result = VideoGameDirectorAgent.GddReviewResult(normalized);
        Assert.Equal("changes-required",result.Disposition);
        var blocker = Assert.Single(result.Findings,x=>x.Blocking);
        Assert.Equal("gdd.coverage.acceptance-criteria",blocker.Code);
        Assert.Contains("planned validation",blocker.RequiredFollowUp);
    }

    [Fact]
    public void Unsupported_claims_of_coverage_are_corrected_instead_of_accepted()
    {
        var review = Complete() with { Gameplay = new(true,"Invented evidence absent from this document.","Enough gameplay.") };
        Assert.Contains("gameplay claims sufficient coverage",Assert.Throws<AgentModelJsonException>(()=>VideoGameDirectorAgent.ValidateGddReview(review,Evidence)).Message);
    }

    [Fact]
    public void Evidence_can_cross_line_breaks_and_does_not_depend_on_heading_names()
    {
        var review = VideoGameDirectorAgent.ValidateGddReview(Complete(),"Players break bricks\nwith an expressive\t musical paddle.");
        Assert.Equal("accepted-with-findings",VideoGameDirectorAgent.GddReviewResult(review).Disposition);
    }

    [Fact]
    public void Every_subject_needs_a_judgment_and_a_reason()
    {
        var review=Complete() with { PlayerExperience = null!, OpenDecisions = new(null,"","") };
        var message=Assert.Throws<AgentModelJsonException>(()=>VideoGameDirectorAgent.ValidateGddReview(review,Evidence)).Message;
        Assert.Contains("player-experience",message);
        Assert.Contains("open-decisions",message);
    }

    [Theory]
    [InlineData(true,"changes-required")]
    [InlineData(false,"accepted-with-findings")]
    public void Scope_concerns_cannot_be_hidden_by_sufficient_coverage(bool blocking,string expected)
    {
        var review=Complete() with { ScopeConcerns=[new(blocking,"Resolve a pitch constraint.",Evidence,"Record the scope decision.")] };
        Assert.Equal(expected,VideoGameDirectorAgent.GddReviewResult(VideoGameDirectorAgent.ValidateGddReview(review,Evidence)).Disposition);
    }

    [Fact]
    public void The_reviewer_contract_does_not_ask_the_model_for_authority_or_provenance()
    {
        var fields=VideoGameDirectorAgent.GddReviewSchema.GetProperty("properties").EnumerateObject().Select(x=>x.Name).ToArray();
        Assert.DoesNotContain("disposition",fields);
        Assert.DoesNotContain("documentId",fields);
        Assert.DoesNotContain("revisionSha256",fields);
        Assert.Contains("No heading, word count, template",VideoGameDirectorAgent.GddReviewInstructions);
    }
}
