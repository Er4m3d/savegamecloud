namespace SaveGameCloud.Core.Cloud;

/// <summary>Fichier présent dans le dossier cloud d'un jeu. Le chemin relatif utilise des '/'.</summary>
public sealed record CloudFile(string RelativePath, string Md5, long Size, DateTime ModifiedUtc, string? Id = null);

/// <summary>Métadonnées écrites à chaque envoi (.savegamecloud.json).</summary>
public sealed class CloudSyncInfo
{
    public DateTime UploadedUtc { get; set; }
    public string Machine { get; set; } = "";
    public string? AppVersion { get; set; }
}

public interface ICloudStorage
{
    /// <summary>Ouvre le dossier cloud du jeu ; null s'il n'existe pas et que <paramref name="createIfMissing"/> est faux.</summary>
    Task<ICloudGameFolder?> OpenGameFolderAsync(string gameName, bool createIfMissing, CancellationToken ct);

    /// <summary>Renomme le dossier cloud. Retourne false si l'ancien n'existe pas ; lève une exception si le nouveau existe déjà.</summary>
    Task<bool> RenameGameFolderAsync(string oldName, string newName, CancellationToken ct);
}

public interface ICloudGameFolder
{
    /// <summary>Fichiers du dossier (récursif), clé = chemin relatif, insensible à la casse.</summary>
    IReadOnlyDictionary<string, CloudFile> Files { get; }

    CloudSyncInfo? Info { get; }

    Task DownloadFileAsync(CloudFile file, Stream destination, CancellationToken ct);
    Task UploadFileAsync(string relativePath, Stream content, DateTime modifiedUtc, CancellationToken ct);
    Task DeleteFileAsync(string relativePath, CancellationToken ct);
    Task WriteInfoAsync(CloudSyncInfo info, CancellationToken ct);
}
