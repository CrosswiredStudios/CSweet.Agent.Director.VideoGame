using CSweet.Agent.SDK;
using CSweet.Agent.Director.VideoGame;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var agent = new VideoGameDirectorAgent();
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "csweet-plugin.json")));
    var manifestId = manifest.RootElement.GetProperty("id").GetString();
    var manifestVersion = manifest.RootElement.GetProperty("version").GetString();
    var assemblyVersion = typeof(VideoGameDirectorAgent).Assembly.GetName().Version?.ToString(3);
    if (agent.AgentId != "com.csweet.video-game-director" ||
        agent.AgentId != manifestId ||
        agent.Version != manifestVersion ||
        agent.Version != assemblyVersion)
        throw new InvalidOperationException("Video Game Video Game Director identity self-test failed.");
    Console.WriteLine($"{agent.AgentId} {agent.Version} self-test passed.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<VideoGameDirectorAgent>();
await builder.Build().RunAsync();
