using System.Security.Cryptography;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Sync;

public sealed record LocalFile(string RelativePath, string FullPath, string Md5, long Size, DateTime ModifiedUtc);

/// <summary>
/// Fait le lien entre les éléments à synchroniser et leurs chemins dans le cloud :
/// un fichier « save.dat » devient « save.dat », un dossier « Saves » devient « Saves/… ».
/// </summary>
public static class LocalScanner
{
    public static List<LocalFile> Scan(GameEntry game)
    {
        var result = new List<LocalFile>();
        foreach (var item in game.SyncItems)
        {
            var path = game.ResolveItemPath(item);
            if (item.Kind == SyncItemKind.File)
            {
                if (File.Exists(path)) result.Add(Describe(item.CloudName, path));
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(path, file).Replace('\\', '/');
                    result.Add(Describe(item.CloudName + "/" + rel, file));
                }
            }
        }
        return result;
    }

    /// <summary>Chemin local correspondant à un chemin cloud, ou null si aucun élément ne le gère.</summary>
    public static string? ResolveLocalPath(GameEntry game, string relativePath)
    {
        var item = FindItem(game, relativePath);
        if (item is null) return null;
        var root = game.ResolveItemPath(item);
        if (item.Kind == SyncItemKind.File) return root;
        var rest = relativePath[(item.CloudName.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, rest));
        // Protection contre un chemin cloud contenant des « .. ».
        return full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static bool IsManaged(GameEntry game, string relativePath) => FindItem(game, relativePath) is not null;

    private static SyncItem? FindItem(GameEntry game, string relativePath) =>
        game.SyncItems.FirstOrDefault(i => i.Kind == SyncItemKind.File
            ? relativePath.Equals(i.CloudName, StringComparison.OrdinalIgnoreCase)
            : relativePath.StartsWith(i.CloudName + "/", StringComparison.OrdinalIgnoreCase));

    public static string ComputeMd5(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    private static LocalFile Describe(string rel, string path)
    {
        var info = new FileInfo(path);
        return new LocalFile(rel, path, ComputeMd5(path), info.Length, info.LastWriteTimeUtc);
    }
}
