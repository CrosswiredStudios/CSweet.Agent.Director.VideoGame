using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PackageAccessTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(PlatformCapabilities.ArtifactRead, "artifact.read")]
    [InlineData(PlatformCapabilities.ArtifactDecideV2, "artifact.decide")]
    public async Task PackageReviewRequestsOnlyDeniedAccess(string? deniedCapability, string? expectedAction)
    {
        var artifactId = Guid.NewGuid(); var packageId = Guid.NewGuid();
        var requests = new List<RequestArtifactAccess>(); var reviews = 0;
        var document = new ArtifactDocument(artifactId, "Brief", "production-brief", "Approved",
            null, null, null, null, null, []);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) =>
                deniedCapability == PlatformCapabilities.ArtifactRead
                    ? throw new PlatformCapabilityException(deniedCapability, PlatformCapabilityErrorCode.Denied, "Missing read grant")
                    : Task.FromResult(document))
            .RegisterCapability<RequestArtifactAccess, ArtifactAccessRequest>(PlatformCapabilities.ArtifactRequestAccess, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ArtifactAccessRequest(Guid.NewGuid(), artifactId, "AgentInstallation", Guid.NewGuid(),
                    "Naomi", request.Actions, request.Justification, "Pending", DateTimeOffset.UtcNow, null, null));
            });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await VideoGameDirectorAgent.ReviewPackageMemberWithAccessAsync(packageId, artifactId,
                runtime.CreateContext(), actual =>
                {
                    Assert.Equal(document.Id, actual.Id);
                    Assert.Equal(document.Status, actual.Status); reviews++;
                    if (deniedCapability == PlatformCapabilities.ArtifactDecideV2)
                        throw new PlatformCapabilityException(deniedCapability, PlatformCapabilityErrorCode.Denied, "Missing decision grant");
                    return Task.CompletedTask;
                }, default);
            Assert.Equal(deniedCapability is null, result);
        }
        Assert.Equal(deniedCapability == PlatformCapabilities.ArtifactRead ? 0 : 2, reviews);
        if (expectedAction is null) Assert.Empty(requests);
        else
        {
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request => Assert.Equal(expectedAction, Assert.Single(request.Actions)));
            Assert.Equal(requests[0].IdempotencyKey, requests[1].IdempotencyKey);
        }
    }

    [Theory]
    [InlineData(PlatformCapabilityErrorCode.NotFound)]
    [InlineData(PlatformCapabilityErrorCode.Conflict)]
    public async Task OtherFailuresDoNotBecomePermissionRequests(PlatformCapabilityErrorCode code)
    {
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead,
            (_, _) => throw new PlatformCapabilityException(PlatformCapabilities.ArtifactRead, code, "Other failure"));
        var exception = await Assert.ThrowsAsync<PlatformCapabilityException>(() =>
            VideoGameDirectorAgent.ReviewPackageMemberWithAccessAsync(Guid.NewGuid(), Guid.NewGuid(),
                runtime.CreateContext(), _ => Task.CompletedTask, default));
        Assert.Equal(code, exception.Code);
    }
}
