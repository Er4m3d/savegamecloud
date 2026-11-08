using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Launch;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud;

/// <summary>Services partagés de l'application.</summary>
public static class AppServices
{
    public static GoogleAuthService Auth { get; } = new();
    public static ConfigService Config { get; } = new();

    public static BackupService Backups => new(AppPaths.BackupDir, Config.Load().MaxBackupsPerGame);

    /// <summary>Le stockage cloud, en se reconnectant silencieusement si un jeton est enregistré.</summary>
    public static async Task<ICloudStorage?> GetCloudAsync(CancellationToken ct) =>
        await Auth.TrySignInSilentlyAsync(ct) ? Auth.Storage : null;

    public static LaunchCoordinator CreateCoordinator() => new(GetCloudAsync, Backups, Config);

    public static SyncEngine? CreateEngine() => Auth.Storage is { } storage ? new SyncEngine(storage, Backups) : null;
}
