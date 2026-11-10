using System.Runtime.InteropServices;

namespace SaveGameCloud.Core.Infrastructure;

/// <summary>
/// Remplace les dossiers connus par des jetons ({Documents}, {SavedGames}, {GameDir}…)
/// pour que la configuration reste valable d'un PC à l'autre.
/// </summary>
public static class PathTokens
{
    public const string GameDir = "{GameDir}";

    private static readonly Lazy<IReadOnlyList<(string Token, string Path)>> KnownFolders = new(BuildKnownFolders);

    private static IReadOnlyList<(string Token, string Path)> BuildKnownFolders()
    {
        var list = new List<(string, string)>
        {
            ("{SavedGames}", GetSavedGamesFolder()),
            ("{Documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("{LocalLow}", GetLocalLowFolder()),
            ("{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("{AppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("{PublicDocuments}", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)),
            ("{ProgramFilesX86}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("{ProgramFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("{ProgramData}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("{UserProfile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        };
        return list.Where(t => !string.IsNullOrEmpty(t.Item2))
                   .Select(t => (t.Item1, Path.TrimEndingDirectorySeparator(t.Item2)))
                   .ToList();
    }

    /// <summary>Remplace le préfixe le plus long connu par son jeton.</summary>
    public static string Tokenize(string fullPath, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
        var path = Path.GetFullPath(fullPath);

        var candidates = KnownFolders.Value.ToList();
        if (!string.IsNullOrWhiteSpace(gameDir))
            candidates.Add((GameDir, Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir))));

        (string Token, string Path)? best = null;
        foreach (var c in candidates)
        {
            if (IsUnder(path, c.Path) && (best is null || c.Path.Length > best.Value.Path.Length))
                best = c;
        }
        if (best is null) return path;
        return best.Value.Token + path[best.Value.Path.Length..];
    }

    public static string Expand(string tokenized, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(tokenized) || tokenized[0] != '{') return tokenized;
        var end = tokenized.IndexOf('}');
        if (end < 0) return tokenized;
        var token = tokenized[..(end + 1)];
        var rest = tokenized[(end + 1)..];

        string? root = token == GameDir
            ? gameDir
            : KnownFolders.Value.FirstOrDefault(k => k.Token.Equals(token, StringComparison.OrdinalIgnoreCase)).Path;
        return string.IsNullOrEmpty(root) ? tokenized : root + rest;
    }

    private static bool IsUnder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == folder.Length || path[folder.Length] == Path.DirectorySeparatorChar;
    }

    private static readonly Guid SavedGamesId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");
    private static readonly Guid LocalLowId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    private static string GetSavedGamesFolder() =>
        GetKnownFolder(SavedGamesId) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");

    private static string GetLocalLowFolder() =>
        GetKnownFolder(LocalLowId) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }
}
