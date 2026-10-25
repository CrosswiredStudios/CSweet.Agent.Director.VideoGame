using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class InitialTechnicalLeadershipTests
{
    [Theory]
    [InlineData("game-technical-director,game-engineer,game-quality-assurance", null, true)]
    [InlineData("game-technical-director,game-quality-assurance", "game-engineer", true)]
    [InlineData("game-technical-director,game-engineer", null, false)]
    [InlineData("game-technical-director,game-engineer,game-quality-assurance,game-artist", null, false)]
    public void Initial_team_requires_exact_missing_baseline_roles(string roles, string? existingRole, bool expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var requested = roles.Split(',');
        var request = JsonSerializer.Deserialize<ResourceChangeRequestResponse>(JsonSerializer.Serialize(new {
            deltas = requested.Select(role => new { changeKind = "Add", role = new { roleKey = role, headcount = 1 } }),
            evidence = new[] { new { kind = "scope-capability-gap", sourceRevision = "brief-sha", summary = "Accepted delivery scope" } }
        }), options)!;
        var roster = JsonSerializer.Deserialize<AgentTeamContext>(JsonSerializer.Serialize(new {
            members = existingRole is null ? Array.Empty<object>() :
                new object[] { new { isAvailable = true, declaredRoleKeys = new[] { existingRole } } }
        }), options)!;
        var review = new PitchReview("pitch", Guid.NewGuid(), Guid.NewGuid(), "brief-sha", true, [], "Scope is sufficiently defined.");
        var reply = new PitchReply("pitch", review.DocumentId, review.RevisionId, review.RevisionSha256, true, "Agreed", "");
        Assert.Equal(expected, VideoGameDirectorAgent.ValidateInitialTechnicalLeadership(request, roster, review, reply) is null);
    }

    [Theory]
    [InlineData("game-technical-director", 1, true, "brief-sha", false)]
    [InlineData("game-engineer", 1, true, "brief-sha", false)]
    [InlineData("game-technical-director", 2, true, "brief-sha", false)]
    [InlineData("game-technical-director", 1, false, "brief-sha", false)]
    [InlineData("game-technical-director", 1, true, "stale", false)]
    public void InitialHireRequiresOneTechnicalLeadAndExactAcceptedBrief(string role, int count,
        bool approved, string evidence, bool expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<ResourceChangeRequestResponse>(JsonSerializer.Serialize(new {
            deltas = new[] { new { changeKind = "Add", role = new { roleKey = role, headcount = count } } },
            evidence = new[] { new { kind = "scope-capability-gap", sourceRevision = evidence, summary = "Technical decomposition" } }
        }), options)!;
        var roster = JsonSerializer.Deserialize<AgentTeamContext>("{\"members\":[]}", options)!;
        var review = new PitchReview("pitch", Guid.NewGuid(), Guid.NewGuid(), "brief-sha", true, [], "Scope is sufficiently defined.");
        var reply = new PitchReply("pitch", review.DocumentId, review.RevisionId, review.RevisionSha256, approved, "Agreed", "");
        var reason = VideoGameDirectorAgent.ValidateInitialTechnicalLeadership(request, roster, review, reply);
        Assert.Equal(expected, reason is null);
    }
}
