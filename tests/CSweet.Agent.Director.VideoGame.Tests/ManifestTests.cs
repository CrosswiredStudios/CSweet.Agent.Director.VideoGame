using CSweet.Agent.SDK;
using CSweet.Memory;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new VideoGameDirectorAgent();

        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Contains(manifest.Requires, x => x.Name == CommunicationCapabilities.CoordinationResume && x.Scope == "team");
        Assert.Equal(agent.Version, typeof(VideoGameDirectorAgent).Assembly.GetName().Version?.ToString(3));
        var contextWindow = Assert.Single(manifest.Configuration,
            field => field.Key == "maxContextWindowTokens");
        var outputTokens = Assert.Single(manifest.Configuration,
            field => field.Key == "maxOutputTokens");
        Assert.Equal(256_000, contextWindow.DefaultValue!.Value.GetInt32());
        Assert.Equal(128_000, outputTokens.DefaultValue!.Value.GetInt32());
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var configuration = document.RootElement.GetProperty("configuration").EnumerateArray().ToArray();
        Assert.False(configuration.Single(field => field.GetProperty("key").GetString() ==
            "maxContextWindowTokens").TryGetProperty("maximum", out _));
        Assert.False(configuration.Single(field => field.GetProperty("key").GetString() ==
            "maxOutputTokens").TryGetProperty("maximum", out _));
        Assert.Equal("maxContextWindowTokens", outputTokens.LessThanFieldKey);
        Assert.Contains(manifest.Requires, x => x.Name == "work.board.read" && x.Scope == "team");
        Assert.Contains(manifest.Requires, x => x.Name == "work.item.read" && x.Scope == "team");
        Assert.Contains(VideoGameDirectorAgent.GameVisionCapability, manifest.Capabilities);
        Assert.NotNull(manifest.RolePolicy);
        Assert.Equal("manager.v1", manifest.RolePolicy!.Profile);
        Assert.Equal(["manager", "game-director"], manifest.RolePolicy.DeclaredRoleKeys);
        Assert.Equal(AgentBaseTypes.Manager, manifest.RolePolicy.BaseType);
        Assert.IsAssignableFrom<CSweetManagerAgentBase>(agent);
        Assert.Equal(["video-game-development", "game-direction", "portfolio-management"], manifest.RolePolicy.SpecializationKeys);
        Assert.Equal("OnDemand", manifest.Runtime.DefaultActivationMode);
        Assert.Contains(manifest.Requires, x => x.Name == PlatformCapabilities.BusinessProfileRead);
        Assert.Contains(manifest.Requires, x => x.Name == "platform.user-action.suggest.v1");
        Assert.Contains(manifest.Requires, x => x.Name == PlatformCapabilities.FinanceProfileRead);
        Assert.Contains(manifest.Requires, x => x.Name == PlatformCapabilities.OrganizationSnapshotRead);
        Assert.Contains(manifest.Requires, x => x.Name == CSweetMemoryCapabilities.Query);
        Assert.Contains(manifest.Requires, x => x.Name == CSweetMemoryCapabilities.Write);
        Assert.DoesNotContain(manifest.Requires, x => x.Name == CSweetMemoryCapabilities.Manage ||
            x.Name == CSweetMemoryCapabilities.Export || x.Name.StartsWith("memory.", StringComparison.Ordinal));
        Assert.Contains(manifest.Requires, x => x.Name == AgentLifecycleCapabilities.CompleteOnboarding);
        Assert.Contains(AgentLifecycleEvents.Onboarded, manifest.Events.Subscribes);
        Assert.Contains(PersonalTodoEvents.Available, manifest.Events.Subscribes);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Read);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Add);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Requeue);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Claim);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Complete);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Block);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Release);
        Assert.Contains(manifest.Requires, x => x.Name == PersonalTodoCapabilities.Defer);
        Assert.Contains(WorkstreamEventNames.ArtifactPackageSubmittedV1, manifest.Events.Subscribes);
        Assert.Contains(WorkstreamEventNames.ArtifactPackageDecidedV1, manifest.Events.Subscribes);
        var profile = Assert.Single(manifest.WorkstreamProfiles.Provides, x => x.Version == 7);
        Assert.Equal("video-game-production.v2", profile.Key);
        Assert.Equal(7, profile.Version);
        Assert.True(File.Exists(Path.Combine(root,
            profile.DefinitionResource.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(manifest.Credentials);
        Assert.Equal("None", manifest.WebAccess.Mode);
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ProductionProfileOwnsLifecycleBoardTypesGatesAndStaffingDeclaratively()
    {
        using var profile = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "profiles", "video-game-production.v2.7.json")));
        var root = profile.RootElement;

        Assert.Equal("video-game-production.v2", root.GetProperty("key").GetString());
        var assignmentPolicy = System.Text.Json.JsonSerializer.Deserialize<WorkAssignmentPolicyTemplate>(
            root.GetProperty("assignmentPolicy"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(assignmentPolicy);
        Assert.Contains(assignmentPolicy.SkillMatchMode, WorkSkillMatchModes.All);
        Assert.Equal(WorkSkillMatchModes.RequiredThenPreferred, assignmentPolicy.SkillMatchMode);
        Assert.Equal("video-game-production-board.v2", root.GetProperty("defaultBoardProfileKey").GetString());
        Assert.True(root.GetProperty("lifecycle").GetProperty("stages").GetArrayLength() >= 13);
        Assert.True(root.GetProperty("workItemTypes").GetArrayLength() >= 7);
        Assert.True(root.GetProperty("milestones").GetArrayLength() >= 5);
        Assert.Equal("game-producer", Assert.Single(root.GetProperty("staffing").GetProperty("requiredRoleKeys").EnumerateArray()).GetString());
        Assert.DoesNotContain(root.GetProperty("workItemTypes").EnumerateArray(), item =>
            string.IsNullOrWhiteSpace(item.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Manifest_RequestsOneVideoGameRoleWithoutHiringOrSpendingAuthority()
    {
        var manifest = await AgentManifestLoader.LoadAsync(
            Path.Combine(RepositoryRoot(), "csweet-plugin.json"), CancellationToken.None);
        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "csweet-plugin.json")));
        Assert.Equal("game-director",
            document.RootElement.GetProperty("catalog").GetProperty("role").GetProperty("key").GetString());
        Assert.DoesNotContain(manifest.Requires, x =>
            x.Name.Contains("marketplace", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains("hiring.workflow", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CodeDeliveryRequiresReviewQualityAndGovernedMergeBeforeAcceptance()
    {
        using var profile = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "profiles", "video-game-production.v2.7.json")));
        var orchestration = profile.RootElement.GetProperty("orchestration");
        var transitions = orchestration.GetProperty("transitions").EnumerateArray().ToArray();
        string Next(string stage, string outcome) => Assert.Single(transitions, t =>
            t.GetProperty("fromStageKey").GetString() == stage && t.GetProperty("outcomeCode").GetString() == outcome)
            .GetProperty("toStageKey").GetString()!;
        var current = Next("specialist-execution", "code-published");
        Assert.Equal("technical-review", current);
        current = Next(current, "approved"); Assert.Equal("task-integration", current);
        current = Next(current, "merged"); Assert.Equal("quality", current);
        Assert.Equal("done", Next(current, "passed"));
        Assert.Equal("quality", Next("specialist-execution", "completed"));
        foreach (var stage in new[] { "technical-review", "quality" })
        {
            var retry = Assert.Single(transitions, t => t.GetProperty("fromStageKey").GetString() == stage &&
                t.GetProperty("outcomeCode").GetString() == (stage == "quality" ? "failed" : "rejected"));
            Assert.Equal("specialist-execution", retry.GetProperty("toStageKey").GetString());
            Assert.Equal(3, retry.GetProperty("maximumTraversals").GetInt32());
        }
        var merge = Assert.Single(orchestration.GetProperty("stages").EnumerateArray(),
            t => t.GetProperty("key").GetString() == "task-integration");
        Assert.Equal("TrustedPlatformAction", merge.GetProperty("stageType").GetString());
        Assert.Equal("source-control.task.integrate.v1", merge.GetProperty("platformAction").GetString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.Agent.Director.VideoGame.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
