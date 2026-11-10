using System.Text.Json.Serialization;
using SaveGameCloud.Core.Infrastructure;

namespace SaveGameCloud.Core.Models;

/// <summary>Un jeu enregistré dans le launcher.</summary>
public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nom du jeu, utilisé aussi comme nom du dossier Google Drive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Chemin de l'exécutable (peut contenir des jetons, voir <see cref="PathTokens"/>).</summary>
    public string ExePath { get; set; } = "";

    public string Arguments { get; set; } = "";

    public List<SyncItem> SyncItems { get; set; } = new();

    public DateTime? LastSyncUtc { get; set; }
    public string? LastSyncMessage { get; set; }
    public bool LastSyncFailed { get; set; }
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Taille totale des éléments lors de la dernière synchro réussie (garde-fou de taille).</summary>
    public long? LastSyncedBytes { get; set; }

    /// <summary>
    /// Date de la dernière modification de la définition partagée du jeu (nom, arguments, éléments).
    /// Null tant que le jeu n'a jamais été partagé dans la bibliothèque cloud.
    /// </summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Anciens identifiants de ce jeu (après fusion avec la bibliothèque), pour les raccourcis existants.</summary>
    public List<string> AliasIds { get; set; } = new();

    [JsonIgnore]
    public string ResolvedExePath => PathTokens.Expand(ExePath, null);

    /// <summary>L'exécutable existe-t-il sur ce PC ?</summary>
    [JsonIgnore]
    public bool IsInstalled => !string.IsNullOrWhiteSpace(ExePath) && File.Exists(ResolvedExePath);

    public bool HasId(string id) => Id == id || AliasIds.Contains(id);

    [JsonIgnore]
    public string? GameDirectory
    {
        get
        {
            var exe = ResolvedExePath;
            return string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
        }
    }

    public string ResolveItemPath(SyncItem item) => PathTokens.Expand(item.Path, GameDirectory);

    public GameEntry Clone()
    {
        var copy = (GameEntry)MemberwiseClone();
        copy.SyncItems = SyncItems.Select(i => i.Clone()).ToList();
        copy.AliasIds = AliasIds.ToList();
        return copy;
    }
}

public enum SyncItemKind
{
    File,
    Directory,
}

/// <summary>Un fichier ou un dossier à synchroniser.</summary>
public sealed class SyncItem
{
    public SyncItemKind Kind { get; set; }

    /// <summary>Chemin local (peut contenir des jetons, voir <see cref="PathTokens"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>Nom de l'élément dans le dossier Drive du jeu (unique par jeu).</summary>
    public string CloudName { get; set; } = "";

    public SyncItem Clone() => (SyncItem)MemberwiseClone();
}

public sealed class AppConfig
{
    public List<GameEntry> Games { get; set; } = new();

    /// <summary>Nombre de sauvegardes de secours conservées par jeu avant un téléchargement.</summary>
    public int MaxBackupsPerGame { get; set; } = 10;

    /// <summary>Jeux retirés du launcher, pour propager le retrait aux autres PC.</summary>
    public List<RemovedGame> RemovedGames { get; set; } = new();
}

public sealed class RemovedGame
{
    public string Id { get; set; } = "";
    public DateTime RemovedUtc { get; set; }
}
