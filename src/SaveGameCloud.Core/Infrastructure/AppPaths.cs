namespace SaveGameCloud.Core.Infrastructure;

public static class AppPaths
{
    private static string? _dataDirOverride;

    /// <summary>Dossier de données de l'application (%APPDATA%\SaveGameCloud).</summary>
    public static string DataDir
    {
        get
        {
            // SAVEGAMECLOUD_DATA_DIR : dossier de données alternatif (tests, mode portable).
            var dir = _dataDirOverride
                      ?? NullIfEmpty(Environment.GetEnvironmentVariable("SAVEGAMECLOUD_DATA_DIR"))
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SaveGameCloud");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Utilisé par les tests pour isoler les données.</summary>
    public static void OverrideDataDir(string dir) => _dataDirOverride = dir;

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string TokenDir => Path.Combine(DataDir, "google-token");
    public static string BackupDir => Path.Combine(DataDir, "Backups");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ClientSecretFile => Path.Combine(DataDir, "client_secret.json");

    private const string EmbeddedClientSecretName = "SaveGameCloud.client_secret.json";

    /// <summary>Un identifiant OAuth est-il disponible (fichier ou intégré à l'exe) ?</summary>
    public static bool HasClientSecret => FindClientSecretFile() is not null || HasEmbeddedClientSecret;

    public static bool HasEmbeddedClientSecret =>
        typeof(AppPaths).Assembly.GetManifestResourceInfo(EmbeddedClientSecretName) is not null;

    /// <summary>
    /// Ouvre l'identifiant OAuth. Ordre : fichier à côté de l'exe, fichier importé dans %APPDATA%,
    /// puis identifiant intégré à l'exe. Les fichiers permettent de remplacer l'identifiant intégré.
    /// </summary>
    public static Stream? OpenClientSecret()
    {
        var file = FindClientSecretFile();
        if (file is not null) return File.OpenRead(file);
        return typeof(AppPaths).Assembly.GetManifestResourceStream(EmbeddedClientSecretName);
    }

    private static string? FindClientSecretFile()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "client_secret.json");
        if (File.Exists(besideExe)) return besideExe;
        return File.Exists(ClientSecretFile) ? ClientSecretFile : null;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "jeu" : cleaned;
    }
}
