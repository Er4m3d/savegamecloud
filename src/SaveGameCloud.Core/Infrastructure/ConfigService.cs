using System.Text.Json;
using System.Text.Json.Serialization;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Core.Infrastructure;

/// <summary>
/// Lecture/écriture de config.json. Chaque modification relit le fichier sous verrou inter-processus,
/// car un raccourci « --launch » peut tourner en même temps que la fenêtre principale.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;

    public ConfigService(string? file = null) => _file = file ?? AppPaths.ConfigFile;

    public AppConfig Load()
    {
        using var _ = Lock();
        return LoadUnlocked();
    }

    public void Update(Action<AppConfig> change)
    {
        using var _ = Lock();
        var config = LoadUnlocked();
        change(config);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, _file, overwrite: true);
    }

    public void UpdateGame(string gameId, Action<GameEntry> change) =>
        Update(c =>
        {
            var game = c.Games.FirstOrDefault(g => g.Id == gameId);
            if (game is not null) change(game);
        });

    public void SaveGame(GameEntry game) =>
        Update(c =>
        {
            var index = c.Games.FindIndex(g => g.Id == game.Id);
            if (index >= 0) c.Games[index] = game;
            else c.Games.Add(game);
        });

    public void RemoveGame(string gameId) => Update(c => c.Games.RemoveAll(g => g.Id == gameId));

    private AppConfig LoadUnlocked()
    {
        if (!File.Exists(_file)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_file), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("config.json illisible, une copie est conservée", ex);
            File.Copy(_file, _file + $".corrompu-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return new AppConfig();
        }
    }

    private IDisposable Lock()
    {
        // Hash déterministe (string.GetHashCode est aléatoire d'un processus à l'autre).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_file).ToUpperInvariant())))[..16];
        var name = @"Local\SaveGameCloud_config_" + hash;
        var mutex = new Mutex(false, name);
        try { mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { /* le verrou nous appartient quand même */ }
        return new MutexReleaser(mutex);
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            mutex.Dispose();
        }
    }
}
