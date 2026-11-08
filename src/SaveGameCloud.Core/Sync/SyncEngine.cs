using System.Reflection;
using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Sync;

public enum SyncState
{
    /// <summary>Rien dans le cloud pour ce jeu.</summary>
    NoCloudData,

    /// <summary>Contenu identique en local et dans le cloud.</summary>
    InSync,

    /// <summary>Le cloud est plus récent : on télécharge sans demander.</summary>
    CloudNewer,

    /// <summary>Le local est plus récent (ou dates équivalentes) : on demande à l'utilisateur.</summary>
    LocalNewer,
}

public sealed class SyncAnalysis
{
    public required SyncState State { get; init; }
    public DateTime? LocalLatestUtc { get; init; }
    public DateTime? CloudLatestUtc { get; init; }
    public CloudSyncInfo? CloudInfo { get; init; }
    public int LocalFileCount { get; init; }
    public int CloudFileCount { get; init; }
    public int DifferentFileCount { get; init; }
}

public sealed record SyncResult(int Transferred, int Deleted, string? BackupDir)
{
    public bool NothingToDo => Transferred == 0 && Deleted == 0;
}

public sealed class SyncEngine(ICloudStorage cloud, BackupService backups)
{
    /// <summary>Marge sous laquelle deux dates sont considérées identiques (précision des systèmes de fichiers).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public async Task<SyncAnalysis> AnalyzeAsync(GameEntry game, CancellationToken ct)
    {
        var local = await Task.Run(() => LocalScanner.Scan(game), ct).ConfigureAwait(false);
        var folder = await cloud.OpenGameFolderAsync(game.Name, createIfMissing: false, ct).ConfigureAwait(false);
        return Analyze(game, local, folder);
    }

    internal static SyncAnalysis Analyze(GameEntry game, List<LocalFile> local, ICloudGameFolder? folder)
    {
        var cloudFiles = ManagedCloudFiles(game, folder);
        if (folder is null || (cloudFiles.Count == 0 && folder.Info is null))
        {
            return new SyncAnalysis
            {
                State = SyncState.NoCloudData,
                LocalFileCount = local.Count,
                LocalLatestUtc = local.Count > 0 ? local.Max(f => f.ModifiedUtc) : null,
            };
        }

        var localByPath = local.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = localByPath.Keys.Union(cloudFiles.Keys, StringComparer.OrdinalIgnoreCase);

        // On ne compare les dates que sur les fichiers qui diffèrent : c'est ce qui dit quel côté a « bougé ».
        DateTime? localLatest = null, cloudLatest = null;
        var different = 0;
        foreach (var path in paths)
        {
            localByPath.TryGetValue(path, out var l);
            cloudFiles.TryGetValue(path, out var c);
            if (l is not null && c is not null && l.Md5.Equals(c.Md5, StringComparison.OrdinalIgnoreCase)) continue;
            different++;
            if (l is not null && (localLatest is null || l.ModifiedUtc > localLatest)) localLatest = l.ModifiedUtc;
            if (c is not null && (cloudLatest is null || c.ModifiedUtc > cloudLatest)) cloudLatest = c.ModifiedUtc;
        }

        SyncState state;
        if (different == 0) state = SyncState.InSync;
        else if (cloudLatest is not null && (localLatest is null || cloudLatest.Value > localLatest.Value + Tolerance))
            state = SyncState.CloudNewer;
        else state = SyncState.LocalNewer;

        return new SyncAnalysis
        {
            State = state,
            LocalLatestUtc = localLatest ?? (local.Count > 0 ? local.Max(f => f.ModifiedUtc) : null),
            CloudLatestUtc = cloudLatest ?? (cloudFiles.Count > 0 ? cloudFiles.Values.Max(f => f.ModifiedUtc) : null),
            CloudInfo = folder.Info,
            LocalFileCount = local.Count,
            CloudFileCount = cloudFiles.Count,
            DifferentFileCount = different,
        };
    }

    /// <summary>Remplace les fichiers locaux par ceux du cloud (après une copie de secours).</summary>
    public async Task<SyncResult> DownloadAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        var folder = await cloud.OpenGameFolderAsync(game.Name, createIfMissing: false, ct).ConfigureAwait(false);
        if (folder is null) return new SyncResult(0, 0, null);

        var local = await Task.Run(() => LocalScanner.Scan(game), ct).ConfigureAwait(false);
        var localByPath = local.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var cloudFiles = ManagedCloudFiles(game, folder);

        var toDownload = cloudFiles.Values
            .Where(c => !localByPath.TryGetValue(c.RelativePath, out var l) || !l.Md5.Equals(c.Md5, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var toDelete = local.Where(l => !cloudFiles.ContainsKey(l.RelativePath)).ToList();
        if (toDownload.Count == 0 && toDelete.Count == 0) return new SyncResult(0, 0, null);

        var touched = toDelete.Concat(toDownload.Select(c => localByPath.GetValueOrDefault(c.RelativePath)).OfType<LocalFile>()).ToList();
        var backupDir = backups.Backup(game, touched);

        var done = 0;
        foreach (var file in toDownload)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Téléchargement {++done}/{toDownload.Count} : {file.RelativePath}");
            var target = LocalScanner.ResolveLocalPath(game, file.RelativePath)
                         ?? throw new InvalidOperationException($"Chemin cloud invalide : {file.RelativePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".sgc-download";
            try
            {
                await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    await folder.DownloadFileAsync(file, stream, ct).ConfigureAwait(false);
                ClearReadOnly(target);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            File.SetLastWriteTimeUtc(target, file.ModifiedUtc);
        }

        foreach (var file in toDelete)
        {
            progress?.Report($"Suppression locale : {file.RelativePath}");
            ClearReadOnly(file.FullPath);
            File.Delete(file.FullPath);
        }

        Log.Info($"Téléchargement « {game.Name} » : {toDownload.Count} fichier(s) reçus, {toDelete.Count} supprimé(s)");
        return new SyncResult(toDownload.Count, toDelete.Count, backupDir);
    }

    /// <summary>Envoie les fichiers locaux vers le cloud (le cloud devient le miroir du local).</summary>
    public async Task<SyncResult> UploadAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        var local = await Task.Run(() => LocalScanner.Scan(game), ct).ConfigureAwait(false);
        var folder = await cloud.OpenGameFolderAsync(game.Name, createIfMissing: local.Count > 0, ct).ConfigureAwait(false);
        if (folder is null) return new SyncResult(0, 0, null);

        var cloudFiles = ManagedCloudFiles(game, folder);
        var localPaths = local.Select(l => l.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toUpload = local
            .Where(l => !cloudFiles.TryGetValue(l.RelativePath, out var c) || !c.Md5.Equals(l.Md5, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var toDelete = cloudFiles.Values.Where(c => !localPaths.Contains(c.RelativePath)).ToList();

        var done = 0;
        foreach (var file in toUpload)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Envoi {++done}/{toUpload.Count} : {file.RelativePath}");
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await folder.UploadFileAsync(file.RelativePath, stream, file.ModifiedUtc, ct).ConfigureAwait(false);
        }

        foreach (var file in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Suppression dans le cloud : {file.RelativePath}");
            await folder.DeleteFileAsync(file.RelativePath, ct).ConfigureAwait(false);
        }

        if (toUpload.Count > 0 || toDelete.Count > 0 || folder.Info is null)
        {
            await folder.WriteInfoAsync(new CloudSyncInfo
            {
                UploadedUtc = DateTime.UtcNow,
                Machine = Environment.MachineName,
                AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            }, ct).ConfigureAwait(false);
        }

        Log.Info($"Envoi « {game.Name} » : {toUpload.Count} fichier(s) envoyés, {toDelete.Count} supprimé(s) du cloud");
        return new SyncResult(toUpload.Count, toDelete.Count, null);
    }

    /// <summary>Seuls les fichiers cloud correspondant à un élément configuré sont pris en compte.</summary>
    private static Dictionary<string, CloudFile> ManagedCloudFiles(GameEntry game, ICloudGameFolder? folder) =>
        folder is null
            ? new Dictionary<string, CloudFile>(StringComparer.OrdinalIgnoreCase)
            : folder.Files.Values.Where(f => LocalScanner.IsManaged(game, f.RelativePath))
                .ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
