using System.Runtime.CompilerServices;

namespace CSweet.Agent.Director.VideoGame.Tests;

internal static class TestAssemblySetup
{
    // The Director retries opening the game board with a short delay. Tests have no reason to wait for it.
    [ModuleInitializer]
    internal static void Initialize() => VideoGameDirectorAgent.GameBoardRetryDelay = TimeSpan.Zero;
}
