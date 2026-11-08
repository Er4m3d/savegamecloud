using System.Globalization;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Sync;

public readonly record struct SizeInfo(int FileCount, long TotalBytes)
{
    public static SizeInfo operator +(SizeInfo a, SizeInfo b) => new(a.FileCount + b.FileCount, a.TotalBytes + b.TotalBytes);

    public override string ToString() =>
        $"{FileCount.ToString("N0", CultureInfo.CurrentCulture)} fichier(s), {SizeGuard.Format(TotalBytes)}";
}

/// <summary>
/// Garde-fous de taille. Ce ne sont pas des limites : le stockage est dans le Drive de l'utilisateur.
/// Ils évitent surtout d'envoyer par erreur un dossier de jeu entier à chaque fermeture.
/// </summary>
public static class SizeGuard
{
    public const long WarnBytes = 200L * 1024 * 1024;
    public const int WarnFiles = 2000;

    public static bool IsLarge(SizeInfo size) => size.TotalBytes >= WarnBytes || size.FileCount >= WarnFiles;

    /// <summary>
    /// Confirmation avant un envoi : sauvegarde volumineuse, jamais envoyée à cette taille
    /// (aucune taille connue, ou taille au moins doublée depuis la dernière synchro).
    /// </summary>
    public static bool NeedsUploadConfirmation(SizeInfo current, long? lastSyncedBytes) =>
        IsLarge(current) && (lastSyncedBytes is null || current.TotalBytes > lastSyncedBytes.Value * 2);

    /// <summary>Taille d'un élément sans lire le contenu des fichiers (rapide).</summary>
    public static SizeInfo Measure(SyncItemKind kind, string path)
    {
        try
        {
            if (kind == SyncItemKind.File)
                return File.Exists(path) ? new SizeInfo(1, new FileInfo(path).Length) : default;
            if (!Directory.Exists(path)) return default;

            var count = 0;
            long bytes = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                count++;
                bytes += file.Length;
            }
            return new SizeInfo(count, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static SizeInfo Measure(GameEntry game) =>
        game.SyncItems.Aggregate(default(SizeInfo), (total, item) => total + Measure(item.Kind, game.ResolveItemPath(item)));

    public static string Format(long bytes)
    {
        string[] units = ["octets", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} octets" : value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
