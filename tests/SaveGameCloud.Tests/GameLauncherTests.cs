using System.Diagnostics;
using SaveGameCloud.Core.Launch;

namespace SaveGameCloud.Tests;

public sealed class GameLauncherTests
{
    [Fact]
    public async Task WaitsForChildProcesses_AfterLauncherExits()
    {
        // cmd se termine tout de suite mais laisse tourner « ping » ~2 s : comme un petit lanceur de vieux jeu.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var sw = Stopwatch.StartNew();

        await GameLauncher.RunAndWaitAsync(cmd, "/c start \"\" /min ping -n 3 127.0.0.1", null, CancellationToken.None);

        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5), $"Retour trop tôt : {sw.Elapsed}");
    }
}
