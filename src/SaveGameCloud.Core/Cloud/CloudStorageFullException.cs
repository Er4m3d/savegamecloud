using Google;

namespace SaveGameCloud.Core.Cloud;

/// <summary>Le Google Drive de l'utilisateur n'a plus de place : inutile de réessayer.</summary>
public sealed class CloudStorageFullException(Exception? inner = null) : IOException(
    "Votre Google Drive est plein. Libérez de l'espace (drive.google.com, ou la corbeille de Drive) " +
    "ou réduisez les fichiers synchronisés pour ce jeu, puis renvoyez la sauvegarde.", inner)
{
    private static readonly string[] FullReasons = ["storageQuotaExceeded", "quotaExceeded"];

    /// <summary>Reconnaît l'erreur « stockage plein » de l'API Drive, même enveloppée dans une autre exception.</summary>
    public static bool IsStorageFull(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is CloudStorageFullException) return true;
            if (e is GoogleApiException api)
            {
                if (api.Error?.Errors?.Any(err => FullReasons.Contains(err.Reason)) == true) return true;
                if (api.Message.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
