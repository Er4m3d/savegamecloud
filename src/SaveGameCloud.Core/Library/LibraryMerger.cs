using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Library;

/// <summary>
/// Bibliothèque partagée entre les PC, stockée dans « SaveGameCloud/.savegamecloud-library.json » du Drive.
/// Elle contient la définition des jeux (nom, arguments, éléments à synchroniser), pas l'état propre à un PC.
/// </summary>
public sealed class LibraryDocument
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; }
    public string? Machine { get; set; }
    public List<LibraryGame> Games { get; set; } = new();
    public List<RemovedGame> Removed { get; set; } = new();
}

public sealed class LibraryGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>Exécutable sur le dernier PC qui a modifié le jeu, utilisé pour pré-remplir les autres PC.</summary>
    public string DefaultExePath { get; set; } = "";

    public List<SyncItem> SyncItems { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class LibraryMergeReport
{
    public List<(string Name, bool Installed)> Added { get; } = new();
    public List<string> Updated { get; } = new();
    public List<string> Removed { get; } = new();
    public int Linked { get; set; }

    public bool HasLocalChanges => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0 || Linked > 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add($"{Added.Count} jeu(x) ajouté(s)");
            if (Updated.Count > 0) parts.Add($"{Updated.Count} mis à jour");
            if (Removed.Count > 0) parts.Add($"{Removed.Count} retiré(s)");
            if (Linked > 0) parts.Add($"{Linked} relié(s) à la bibliothèque");
            return parts.Count == 0 ? "Bibliothèque à jour." : "Bibliothèque cloud : " + string.Join(", ", parts) + ".";
        }
    }
}

public sealed record LibraryMergeResult(LibraryDocument Document, bool CloudChanged, LibraryMergeReport Report);

/// <summary>
/// Fusion entre la config locale et la bibliothèque cloud, jeu par jeu : la définition la plus récente gagne.
/// Ce qui est propre au PC (chemin réel de l'exe, état de synchro, dernière partie) n'est jamais écrasé.
/// </summary>
public static class LibraryMerger
{
    public static LibraryMergeResult Merge(AppConfig local, LibraryDocument? cloud, DateTime nowUtc, string machine)
    {
        var report = new LibraryMergeReport();
        var cloudChanged = cloud is null;
        cloud ??= new LibraryDocument();

        // Retraits connus des deux côtés (le plus récent l'emporte).
        var removedAt = new Dictionary<string, DateTime>();
        foreach (var r in local.RemovedGames.Concat(cloud.Removed))
            if (!removedAt.TryGetValue(r.Id, out var t) || r.RemovedUtc > t) removedAt[r.Id] = r.RemovedUtc;

        var cloudById = cloud.Games.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());

        // Un jeu créé sur ce PC avant d'exister dans la bibliothèque, avec le même nom qu'un jeu du cloud :
        // c'est le même jeu. Il prend l'identifiant du cloud et sa définition ; son exe local est conservé.
        foreach (var c in cloud.Games)
        {
            if (local.Games.Any(l => l.HasId(c.Id)) || IsRemoved(removedAt, c.Id, c.UpdatedUtc)) continue;
            var twin = local.Games.FirstOrDefault(l => !cloudById.ContainsKey(l.Id) && l.UpdatedUtc is null &&
                                                       l.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            if (twin is null) continue;
            twin.AliasIds.Add(twin.Id);
            twin.Id = c.Id;
            twin.UpdatedUtc = DateTime.MinValue; // la définition du cloud sera appliquée ci-dessous
            report.Linked++;
        }

        // Jeux jamais partagés : ils entrent dans la bibliothèque maintenant.
        foreach (var game in local.Games.Where(g => g.UpdatedUtc is null)) game.UpdatedUtc = nowUtc;

        var result = new List<LibraryGame>();
        var ids = local.Games.Select(g => g.Id).Concat(cloud.Games.Select(g => g.Id)).Distinct().ToList();
        foreach (var id in ids)
        {
            var l = local.Games.FirstOrDefault(g => g.Id == id);
            cloudById.TryGetValue(id, out var c);
            var latest = Max(l?.UpdatedUtc, c?.UpdatedUtc);

            if (IsRemoved(removedAt, id, latest))
            {
                if (l is not null)
                {
                    local.Games.Remove(l);
                    report.Removed.Add(l.Name);
                }
                if (c is not null) cloudChanged = true;
                continue;
            }

            if (l is not null && c is not null)
            {
                if (c.UpdatedUtc > l.UpdatedUtc)
                {
                    Apply(c, l);
                    report.Updated.Add(l.Name);
                    result.Add(c);
                }
                else if (l.UpdatedUtc > c.UpdatedUtc)
                {
                    result.Add(ToLibrary(l, c.DefaultExePath));
                    cloudChanged = true;
                }
                else
                {
                    result.Add(c);
                }
                AdoptDefaultExeIfMissing(l, c.DefaultExePath);
            }
            else if (c is not null)
            {
                var added = FromLibrary(c);
                local.Games.Add(added);
                report.Added.Add((added.Name, added.IsInstalled));
                result.Add(c);
            }
            else if (l is not null)
            {
                result.Add(ToLibrary(l, null));
                cloudChanged = true;
            }
        }

        var removedList = removedAt.Select(kv => new RemovedGame { Id = kv.Key, RemovedUtc = kv.Value }).ToList();
        if (removedList.Count != cloud.Removed.Count) cloudChanged = true;
        local.RemovedGames = removedList.Select(r => new RemovedGame { Id = r.Id, RemovedUtc = r.RemovedUtc }).ToList();

        var document = new LibraryDocument
        {
            Version = 1,
            UpdatedUtc = cloudChanged ? nowUtc : cloud.UpdatedUtc,
            Machine = cloudChanged ? machine : cloud.Machine,
            Games = result,
            Removed = removedList,
        };
        return new LibraryMergeResult(document, cloudChanged, report);
    }

    private static bool IsRemoved(Dictionary<string, DateTime> removedAt, string id, DateTime? latestDefinition) =>
        removedAt.TryGetValue(id, out var removed) && removed >= (latestDefinition ?? DateTime.MinValue);

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static void Apply(LibraryGame source, GameEntry target)
    {
        target.Name = source.Name;
        target.Arguments = source.Arguments;
        target.SyncItems = source.SyncItems.Select(i => i.Clone()).ToList();
        target.UpdatedUtc = source.UpdatedUtc;
    }

    /// <summary>Si l'exe local est introuvable mais que celui du cloud existe sur ce PC, on l'utilise.</summary>
    private static void AdoptDefaultExeIfMissing(GameEntry game, string defaultExePath)
    {
        if (game.IsInstalled || string.IsNullOrWhiteSpace(defaultExePath)) return;
        if (File.Exists(PathTokens.Expand(defaultExePath, null))) game.ExePath = defaultExePath;
    }

    private static GameEntry FromLibrary(LibraryGame c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Arguments = c.Arguments,
        ExePath = c.DefaultExePath,
        SyncItems = c.SyncItems.Select(i => i.Clone()).ToList(),
        UpdatedUtc = c.UpdatedUtc,
    };

    private static LibraryGame ToLibrary(GameEntry l, string? previousDefaultExe) => new()
    {
        Id = l.Id,
        Name = l.Name,
        Arguments = l.Arguments,
        // Le chemin réel de ce PC sert de modèle aux autres, sous forme portable ({ProgramFiles}, {Documents}…).
        DefaultExePath = l.IsInstalled ? PathTokens.Tokenize(l.ResolvedExePath, null) : previousDefaultExe ?? l.ExePath,
        SyncItems = l.SyncItems.Select(i => i.Clone()).ToList(),
        UpdatedUtc = l.UpdatedUtc ?? DateTime.MinValue,
    };
}
