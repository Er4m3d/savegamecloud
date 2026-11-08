using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud.Tests;

public sealed class SyncEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sgc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCloudStorage _cloud = new();
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public SyncEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Un « PC » simulé : son propre dossier de jeu avec un fichier de config et un dossier de saves.</summary>
    private (GameEntry Game, SyncEngine Engine, string GameDir) Machine(string name)
    {
        var gameDir = Path.Combine(_root, name, "Game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "game.exe"), "fake");
        var game = new GameEntry
        {
            Name = "Mon Jeu",
            ExePath = Path.Combine(gameDir, "game.exe"),
            SyncItems =
            {
                new SyncItem { Kind = SyncItemKind.Directory, Path = PathTokens.GameDir + @"\Saves", CloudName = "Saves" },
                new SyncItem { Kind = SyncItemKind.File, Path = PathTokens.GameDir + @"\config.ini", CloudName = "config.ini" },
            },
        };
        var engine = new SyncEngine(_cloud, new BackupService(Path.Combine(_root, name, "Backups"), 3));
        return (game, engine, gameDir);
    }

    private static void WriteFile(string path, string content, DateTime modifiedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    [Fact]
    public async Task NoCloudData_WhenNothingUploaded()
    {
        var (game, engine, dir) = Machine("A");
        WriteFile(Path.Combine(dir, "Saves", "slot1.sav"), "v1", T0);

        var analysis = await engine.AnalyzeAsync(game, default);

        Assert.Equal(SyncState.NoCloudData, analysis.State);
    }

    [Fact]
    public async Task Upload_CreatesMirror_AndThenInSync()
    {
        var (game, engine, dir) = Machine("A");
        WriteFile(Path.Combine(dir, "Saves", "slot1.sav"), "v1", T0);
        WriteFile(Path.Combine(dir, "Saves", "sub", "slot2.sav"), "v1", T0);
        WriteFile(Path.Combine(dir, "config.ini"), "cfg", T0);

        var result = await engine.UploadAsync(game, null, default);

        Assert.Equal(3, result.Transferred);
        var folder = _cloud.Folders["Mon Jeu"];
        Assert.Equal(["Saves/slot1.sav", "Saves/sub/slot2.sav", "config.ini"], folder.Content.Keys.Order(StringComparer.Ordinal));
        Assert.NotNull(folder.Info);
        Assert.Equal(SyncState.InSync, (await engine.AnalyzeAsync(game, default)).State);

        // Un second envoi sans changement n'envoie rien.
        Assert.True((await engine.UploadAsync(game, null, default)).NothingToDo);
    }

    [Fact]
    public async Task CloudNewer_IsDownloaded_WithBackupAndMirrorDeletion()
    {
        var a = Machine("A");
        WriteFile(Path.Combine(a.GameDir, "Saves", "slot1.sav"), "progression A", T0.AddHours(2));
        await a.Engine.UploadAsync(a.Game, null, default);

        var b = Machine("B");
        WriteFile(Path.Combine(b.GameDir, "Saves", "slot1.sav"), "ancienne progression B", T0);
        WriteFile(Path.Combine(b.GameDir, "Saves", "orphelin.sav"), "à supprimer", T0);

        var analysis = await b.Engine.AnalyzeAsync(b.Game, default);
        Assert.Equal(SyncState.CloudNewer, analysis.State);

        var result = await b.Engine.DownloadAsync(b.Game, null, default);

        var slot = Path.Combine(b.GameDir, "Saves", "slot1.sav");
        Assert.Equal("progression A", File.ReadAllText(slot));
        Assert.Equal(T0.AddHours(2), File.GetLastWriteTimeUtc(slot));
        Assert.False(File.Exists(Path.Combine(b.GameDir, "Saves", "orphelin.sav")));
        Assert.NotNull(result.BackupDir);
        Assert.Equal("ancienne progression B", File.ReadAllText(Path.Combine(result.BackupDir!, "Saves", "slot1.sav")));
        Assert.True(File.Exists(Path.Combine(result.BackupDir!, "Saves", "orphelin.sav")));
        Assert.Equal(SyncState.InSync, (await b.Engine.AnalyzeAsync(b.Game, default)).State);
    }

    [Fact]
    public async Task NewMachine_WithoutLocalFiles_GetsCloudNewer()
    {
        var a = Machine("A");
        WriteFile(Path.Combine(a.GameDir, "Saves", "slot1.sav"), "A", T0);
        WriteFile(Path.Combine(a.GameDir, "config.ini"), "cfg", T0);
        await a.Engine.UploadAsync(a.Game, null, default);

        var b = Machine("B");
        Assert.Equal(SyncState.CloudNewer, (await b.Engine.AnalyzeAsync(b.Game, default)).State);

        var result = await b.Engine.DownloadAsync(b.Game, null, default);
        Assert.Null(result.BackupDir); // rien à sauvegarder localement
        Assert.Equal("A", File.ReadAllText(Path.Combine(b.GameDir, "Saves", "slot1.sav")));
        Assert.Equal("cfg", File.ReadAllText(Path.Combine(b.GameDir, "config.ini")));
    }

    [Fact]
    public async Task LocalNewer_WhenLocalFileModifiedAfterCloud()
    {
        var a = Machine("A");
        WriteFile(Path.Combine(a.GameDir, "Saves", "slot1.sav"), "cloud", T0);
        await a.Engine.UploadAsync(a.Game, null, default);

        var b = Machine("B");
        WriteFile(Path.Combine(b.GameDir, "Saves", "slot1.sav"), "local plus récent", T0.AddMinutes(30));

        var analysis = await b.Engine.AnalyzeAsync(b.Game, default);
        Assert.Equal(SyncState.LocalNewer, analysis.State);
        Assert.Equal(T0.AddMinutes(30), analysis.LocalLatestUtc);
        Assert.Equal(T0, analysis.CloudLatestUtc);
    }

    [Fact]
    public async Task SameTimestamp_DifferentContent_IsTreatedAsConflict()
    {
        var a = Machine("A");
        WriteFile(Path.Combine(a.GameDir, "Saves", "slot1.sav"), "x", T0);
        await a.Engine.UploadAsync(a.Game, null, default);

        var b = Machine("B");
        WriteFile(Path.Combine(b.GameDir, "Saves", "slot1.sav"), "y", T0.AddSeconds(1));

        Assert.Equal(SyncState.LocalNewer, (await b.Engine.AnalyzeAsync(b.Game, default)).State);
    }

    [Fact]
    public async Task Upload_DeletesRemovedFiles_ButKeepsUnmanagedCloudFiles()
    {
        var (game, engine, dir) = Machine("A");
        WriteFile(Path.Combine(dir, "Saves", "slot1.sav"), "1", T0);
        WriteFile(Path.Combine(dir, "Saves", "slot2.sav"), "2", T0);
        await engine.UploadAsync(game, null, default);
        _cloud.Folders["Mon Jeu"].Content["Autre/fichier.txt"] = ("hors config"u8.ToArray(), T0);

        File.Delete(Path.Combine(dir, "Saves", "slot2.sav"));
        var result = await engine.UploadAsync(game, null, default);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(["Autre/fichier.txt", "Saves/slot1.sav"], _cloud.Folders["Mon Jeu"].Content.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Backups_ArePruned()
    {
        var a = Machine("A");
        WriteFile(Path.Combine(a.GameDir, "Saves", "slot1.sav"), "cloud", T0.AddDays(1));
        await a.Engine.UploadAsync(a.Game, null, default);

        var b = Machine("B");
        for (var i = 0; i < 5; i++)
        {
            WriteFile(Path.Combine(b.GameDir, "Saves", "slot1.sav"), "local " + i, T0);
            await b.Engine.DownloadAsync(b.Game, null, default);
        }

        var backups = Directory.GetDirectories(Path.Combine(_root, "B", "Backups", "Mon Jeu"));
        Assert.Equal(3, backups.Length);
    }

    [Fact]
    public void PathTokens_RoundTrip()
    {
        var gameDir = Path.Combine(_root, "Jeux", "Vieux Jeu");
        var inGame = Path.Combine(gameDir, "SAVE", "slot.dat");
        Assert.Equal(@"{GameDir}\SAVE\slot.dat", PathTokens.Tokenize(inGame, gameDir));
        Assert.Equal(inGame, PathTokens.Expand(PathTokens.Tokenize(inGame, gameDir), gameDir));

        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "X");
        Assert.StartsWith("{Documents}", PathTokens.Tokenize(docs, gameDir));
        Assert.Equal(docs, PathTokens.Expand(PathTokens.Tokenize(docs, gameDir), gameDir));

        var other = @"Z:\ailleurs\save.dat";
        Assert.Equal(other, PathTokens.Tokenize(other, gameDir));
    }

    [Fact]
    public void ResolveLocalPath_RejectsPathTraversal()
    {
        var (game, _, _) = Machine("A");
        Assert.Null(LocalScanner.ResolveLocalPath(game, "Saves/../../evil.exe"));
        Assert.Null(LocalScanner.ResolveLocalPath(game, "Inconnu/fichier"));
        Assert.NotNull(LocalScanner.ResolveLocalPath(game, "Saves/a/b.sav"));
    }

    [Fact]
    public void ConfigService_RoundTrip()
    {
        var config = new ConfigService(Path.Combine(_root, "config.json"));
        var game = new GameEntry { Name = "Test", SyncItems = { new SyncItem { Kind = SyncItemKind.Directory, Path = "{Documents}\\X", CloudName = "X" } } };
        config.SaveGame(game);
        config.UpdateGame(game.Id, g => g.LastSyncMessage = "ok");

        var loaded = config.Load().Games.Single();
        Assert.Equal("Test", loaded.Name);
        Assert.Equal("ok", loaded.LastSyncMessage);
        Assert.Equal(SyncItemKind.Directory, loaded.SyncItems[0].Kind);
    }
}
