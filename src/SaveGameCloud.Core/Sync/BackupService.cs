using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Sync;

/// <summary>Copie de secours des fichiers locaux avant qu'un téléchargement ne les remplace.</summary>
public sealed class BackupService(string backupRoot, int maxBackupsPerGame)
{
    public string GetGameBackupDir(GameEntry game) => Path.Combine(backupRoot, AppPaths.SafeFileName(game.Name));

    /// <returns>Le dossier de la sauvegarde de secours, ou null s'il n'y avait rien à sauvegarder.</returns>
    public string? Backup(GameEntry game, IReadOnlyCollection<LocalFile> files)
    {
        if (files.Count == 0) return null;
        var gameDir = GetGameBackupDir(game);
        var target = Path.Combine(gameDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        var suffix = 1;
        while (Directory.Exists(target)) target = Path.Combine(gameDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{++suffix}");

        foreach (var file in files)
        {
            var dest = Path.Combine(target, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file.FullPath, dest, overwrite: true);
        }
        Log.Info($"Sauvegarde de secours de « {game.Name} » : {files.Count} fichier(s) dans {target}");
        Prune(gameDir);
        return target;
    }

    private void Prune(string gameDir)
    {
        var old = new DirectoryInfo(gameDir).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(Math.Max(1, maxBackupsPerGame));
        foreach (var dir in old)
        {
            try { dir.Delete(recursive: true); }
            catch (Exception ex) { Log.Warn($"Impossible de supprimer l'ancienne sauvegarde {dir.FullName} : {ex.Message}"); }
        }
    }
}
