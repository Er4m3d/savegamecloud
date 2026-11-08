using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud.Core.Launch;

public enum ConflictChoice
{
    UseCloud,
    UseLocal,
    Cancel,
}

public enum LaunchOutcome
{
    Completed,
    Cancelled,
    AlreadyRunning,
    Failed,
}

/// <summary>Ce que l'interface doit fournir pendant un lancement.</summary>
public interface ILaunchUi
{
    void ReportStatus(string message);
    Task<ConflictChoice> ResolveConflictAsync(GameEntry game, SyncAnalysis analysis);
    Task<bool> ConfirmAsync(string title, string message);
    void ShowError(string message);
    void GameStarted(GameEntry game);
    void GameExited(GameEntry game);
}

/// <summary>
/// Déroulé d'un lancement :
/// 1. compare local / cloud ; cloud plus récent → téléchargement ; local plus récent → on demande ;
/// 2. lance le jeu et attend sa fermeture ;
/// 3. envoie les fichiers vers le cloud.
/// </summary>
public sealed class LaunchCoordinator(
    Func<CancellationToken, Task<ICloudStorage?>> cloudProvider,
    BackupService backups,
    ConfigService config)
{
    public async Task<LaunchOutcome> RunAsync(GameEntry game, ILaunchUi ui, CancellationToken ct = default)
    {
        using var guard = GameRunGuard.TryAcquire(game.Id);
        if (guard is null)
        {
            ui.ShowError($"« {game.Name} » est déjà en cours d'exécution.");
            return LaunchOutcome.AlreadyRunning;
        }

        var exe = game.ResolvedExePath;
        if (!File.Exists(exe))
        {
            ui.ShowError($"Exécutable introuvable :\n{exe}");
            return LaunchOutcome.Failed;
        }

        // 1. Synchronisation avant le lancement.
        ui.ReportStatus("Connexion à Google Drive…");
        ICloudStorage? cloud = null;
        try { cloud = await cloudProvider(ct); }
        catch (Exception ex) { Log.Error("Connexion au cloud impossible", ex); }

        bool preSyncDone;
        if (cloud is null)
        {
            if (!await ui.ConfirmAsync("Pas de connexion",
                    "Vous n'êtes pas connecté à Google Drive.\n\nLancer le jeu quand même, sans synchronisation ?"))
                return LaunchOutcome.Cancelled;
            preSyncDone = false;
        }
        else
        {
            var pre = await PreLaunchSyncAsync(game, cloud, ui, ct);
            if (pre is null) return LaunchOutcome.Cancelled;
            preSyncDone = pre.Value;
        }

        // 2. Lancement.
        ui.ReportStatus($"En jeu : {game.Name}");
        ui.GameStarted(game);
        try
        {
            await GameLauncher.RunAndWaitAsync(exe, game.Arguments, null, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Lancement de « {game.Name} » impossible", ex);
            ui.GameExited(game);
            ui.ShowError($"Impossible de lancer le jeu :\n{ex.Message}");
            return LaunchOutcome.Failed;
        }
        ui.GameExited(game);
        config.UpdateGame(game.Id, g => g.LastPlayedUtc = DateTime.UtcNow);

        // 3. Envoi vers le cloud.
        if (!preSyncDone)
        {
            // On n'a pas pu vérifier le cloud avant de jouer : on évite d'écraser une sauvegarde plus récente sans accord.
            if (!await ui.ConfirmAsync("Envoyer la sauvegarde ?",
                    "La sauvegarde cloud n'a pas pu être vérifiée avant le lancement.\n\n" +
                    "Envoyer maintenant votre sauvegarde locale vers Google Drive (elle remplacera celle du cloud) ?\n\n" +
                    "Si vous répondez Non, la question sera reposée au prochain lancement."))
                return LaunchOutcome.Completed;
            cloud ??= await TryGetCloudAsync(ct);
            if (cloud is null)
            {
                ui.ShowError("Toujours pas de connexion à Google Drive. La sauvegarde sera proposée au prochain lancement.");
                RecordSync(game, failed: true, "Envoi non effectué (hors ligne)");
                return LaunchOutcome.Completed;
            }
        }

        await PostGameUploadAsync(game, cloud!, ui, ct);
        return LaunchOutcome.Completed;
    }

    /// <returns>true si synchronisé, false si l'utilisateur joue sans synchro, null si annulé.</returns>
    private async Task<bool?> PreLaunchSyncAsync(GameEntry game, ICloudStorage cloud, ILaunchUi ui, CancellationToken ct)
    {
        var engine = new SyncEngine(cloud, backups);
        var progress = new Progress<string>(ui.ReportStatus);
        try
        {
            ui.ReportStatus("Vérification de la sauvegarde cloud…");
            var analysis = await engine.AnalyzeAsync(game, ct);
            Log.Info($"Analyse « {game.Name} » : {analysis.State} (local {analysis.LocalLatestUtc:u}, cloud {analysis.CloudLatestUtc:u})");

            var download = analysis.State switch
            {
                SyncState.CloudNewer => true,
                SyncState.LocalNewer => await ui.ResolveConflictAsync(game, analysis) switch
                {
                    ConflictChoice.UseCloud => true,
                    ConflictChoice.UseLocal => false,
                    _ => (bool?)null,
                },
                _ => false,
            };
            if (download is null) return null;

            if (download.Value)
            {
                ui.ReportStatus("Téléchargement de la sauvegarde cloud…");
                var result = await engine.DownloadAsync(game, progress, ct);
                RecordSync(game, failed: false, $"Récupéré depuis le cloud ({result.Transferred} fichier(s))",
                    (await Task.Run(() => SizeGuard.Measure(game), ct)).TotalBytes);
            }
            else if (analysis.State == SyncState.InSync)
            {
                RecordSync(game, failed: false, "Déjà à jour", (await Task.Run(() => SizeGuard.Measure(game), ct)).TotalBytes);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Synchronisation avant lancement de « {game.Name} » impossible", ex);
            RecordSync(game, failed: true, $"Échec de la vérification : {ex.Message}");
            return await ui.ConfirmAsync("Synchronisation impossible",
                $"La sauvegarde cloud n'a pas pu être récupérée :\n{ex.Message}\n\nLancer le jeu quand même avec la sauvegarde locale ?")
                ? false
                : null;
        }
    }

    private async Task PostGameUploadAsync(GameEntry game, ICloudStorage cloud, ILaunchUi ui, CancellationToken ct)
    {
        var engine = new SyncEngine(cloud, backups);
        var progress = new Progress<string>(ui.ReportStatus);

        // Garde-fou : une sauvegarde soudain volumineuse est souvent un mauvais dossier sélectionné.
        var size = await Task.Run(() => SizeGuard.Measure(game), ct);
        if (SizeGuard.NeedsUploadConfirmation(size, game.LastSyncedBytes) &&
            !await ui.ConfirmAsync("Sauvegarde volumineuse", LargeUploadMessage(game, size)))
        {
            RecordSync(game, failed: true, $"Envoi annulé : sauvegarde volumineuse ({size})");
            return;
        }

        while (true)
        {
            try
            {
                ui.ReportStatus("Envoi de la sauvegarde vers Google Drive…");
                var result = await engine.UploadAsync(game, progress, ct);
                RecordSync(game, failed: false, result.NothingToDo
                    ? "Aucun changement à envoyer"
                    : $"Envoyé vers le cloud ({result.Transferred} fichier(s), {result.Deleted} supprimé(s))", size.TotalBytes);
                ui.ReportStatus("Sauvegarde envoyée.");
                return;
            }
            catch (Exception ex) when (CloudStorageFullException.IsStorageFull(ex))
            {
                Log.Error($"Drive plein pendant l'envoi de « {game.Name} »", ex);
                var full = new CloudStorageFullException(ex);
                RecordSync(game, failed: true, "Échec de l'envoi : Google Drive plein");
                ui.ShowError(full.Message);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Envoi de « {game.Name} » impossible", ex);
                if (!await ui.ConfirmAsync("Échec de l'envoi",
                        $"La sauvegarde n'a pas pu être envoyée vers Google Drive :\n{ex.Message}\n\nRéessayer ?"))
                {
                    RecordSync(game, failed: true, $"Échec de l'envoi : {ex.Message}");
                    return;
                }
            }
        }
    }

    private async Task<ICloudStorage?> TryGetCloudAsync(CancellationToken ct)
    {
        try { return await cloudProvider(ct); }
        catch (Exception ex) { Log.Error("Connexion au cloud impossible", ex); return null; }
    }

    public static string LargeUploadMessage(GameEntry game, SizeInfo size) =>
        $"La sauvegarde de « {game.Name} » est volumineuse : {size}." +
        (game.LastSyncedBytes is { } last ? $"\nLors de la dernière synchro, elle pesait {SizeGuard.Format(last)}." : "") +
        "\n\nVérifiez que vous n'avez pas sélectionné tout le dossier du jeu au lieu du dossier de sauvegardes " +
        "(bouton « Modifier… »).\n\nEnvoyer quand même vers Google Drive ?";

    private void RecordSync(GameEntry game, bool failed, string message, long? syncedBytes = null)
    {
        game.LastSyncUtc = DateTime.UtcNow;
        game.LastSyncFailed = failed;
        game.LastSyncMessage = message;
        if (syncedBytes is not null) game.LastSyncedBytes = syncedBytes;
        config.UpdateGame(game.Id, g =>
        {
            g.LastSyncUtc = game.LastSyncUtc;
            g.LastSyncFailed = failed;
            g.LastSyncMessage = message;
            if (syncedBytes is not null) g.LastSyncedBytes = syncedBytes;
        });
    }
}

/// <summary>Empêche de lancer deux fois le même jeu (y compris depuis un raccourci, dans un autre processus).</summary>
public sealed class GameRunGuard : IDisposable
{
    private readonly Semaphore _semaphore;

    private GameRunGuard(Semaphore semaphore) => _semaphore = semaphore;

    public static GameRunGuard? TryAcquire(string gameId)
    {
        var semaphore = new Semaphore(1, 1, @"Local\SaveGameCloud_game_" + gameId);
        if (semaphore.WaitOne(0)) return new GameRunGuard(semaphore);
        semaphore.Dispose();
        return null;
    }

    public void Dispose()
    {
        _semaphore.Release();
        _semaphore.Dispose();
    }
}
