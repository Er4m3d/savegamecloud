using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;

namespace SaveGameCloud.Core.Library;

/// <summary>Récupère la bibliothèque cloud, la fusionne avec la config locale, puis renvoie le résultat si besoin.</summary>
public sealed class LibrarySync(ConfigService config)
{
    public async Task<LibraryMergeReport> SyncAsync(ICloudStorage cloud, CancellationToken ct)
    {
        var document = await cloud.ReadLibraryAsync(ct).ConfigureAwait(false);
        LibraryMergeResult? result = null;
        config.Update(c => result = LibraryMerger.Merge(c, document, DateTime.UtcNow, Environment.MachineName));
        if (result!.CloudChanged)
            await cloud.WriteLibraryAsync(result.Document, ct).ConfigureAwait(false);
        Log.Info($"Bibliothèque : {result.Report.Summary} (cloud {(result.CloudChanged ? "mis à jour" : "inchangé")})");
        return result.Report;
    }
}
