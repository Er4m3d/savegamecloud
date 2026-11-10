using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Library;
using SaveGameCloud.Core.Models;

namespace SaveGameCloud.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sgc-lib-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCloudStorage _cloud = new();
    private static readonly DateTime T0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    public LibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Un « PC » : sa propre config.json.</summary>
    private ConfigService Pc(string name) => new(Path.Combine(_root, name + ".json"));

    private string FakeExe(string pc, string game)
    {
        var path = Path.Combine(_root, pc, game, "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "exe");
        return path;
    }

    private static GameEntry Game(string name, string exe) => new()
    {
        Name = name,
        ExePath = exe,
        SyncItems = { new SyncItem { Kind = SyncItemKind.Directory, Path = PathTokens.GameDir + @"\Save", CloudName = "Save" } },
    };

    [Fact]
    public async Task NewPc_GetsGamesPrefilled_AndKnowsWhichAreNotInstalled()
    {
        var a = Pc("A");
        a.SaveGame(Game("Zeus", FakeExe("A", "Zeus")));
        a.SaveGame(Game("Zoo Tycoon", @"Z:\absent\zoo.exe"));
        await new LibrarySync(a).SyncAsync(_cloud, default);

        var b = Pc("B");
        var report = await new LibrarySync(b).SyncAsync(_cloud, default);

        var games = b.Load().Games;
        Assert.Equal(2, games.Count);
        Assert.Equal(2, report.Added.Count);
        Assert.Equal("Save", games.Single(g => g.Name == "Zeus").SyncItems.Single().CloudName);
        // Le chemin de A existe aussi « sur B » ici (même disque de test) ; celui de Zoo n'existe nulle part.
        Assert.True(games.Single(g => g.Name == "Zeus").IsInstalled);
        Assert.False(games.Single(g => g.Name == "Zoo Tycoon").IsInstalled);
        Assert.Contains(report.Added, x => x is { Name: "Zoo Tycoon", Installed: false });
    }

    [Fact]
    public async Task ExistingGamesWithoutLibrary_ArePushedOnFirstSync()
    {
        var a = Pc("A");
        // Jeu créé avant cette fonctionnalité : pas de date de partage.
        a.Update(c => c.Games.Add(Game("Zeus", FakeExe("A", "Zeus"))));

        await new LibrarySync(a).SyncAsync(_cloud, default);

        Assert.NotNull(_cloud.LibraryJson);
        Assert.Contains("Zeus", _cloud.LibraryJson);
        Assert.NotNull(a.Load().Games.Single().UpdatedUtc);
    }

    [Fact]
    public async Task EditOnOtherPc_IsApplied_ButLocalExeAndSyncStateAreKept()
    {
        var a = Pc("A");
        var game = Game("Zeus", FakeExe("A", "Zeus"));
        a.SaveGame(game);
        await new LibrarySync(a).SyncAsync(_cloud, default);

        var b = Pc("B");
        await new LibrarySync(b).SyncAsync(_cloud, default);
        var exeB = FakeExe("B", "Zeus");
        b.UpdateGame(game.Id, g => { g.ExePath = exeB; g.LastSyncMessage = "état de B"; });

        // A renomme le jeu et ajoute un fichier.
        var edited = a.Load().Games.Single();
        edited.Name = "Zeus & Poseidon";
        edited.SyncItems.Add(new SyncItem { Kind = SyncItemKind.File, Path = PathTokens.GameDir + @"\zeus.ini", CloudName = "zeus.ini" });
        a.SaveGame(edited);
        await new LibrarySync(a).SyncAsync(_cloud, default);

        var report = await new LibrarySync(b).SyncAsync(_cloud, default);

        var onB = b.Load().Games.Single();
        Assert.Equal("Zeus & Poseidon", onB.Name);
        Assert.Equal(2, onB.SyncItems.Count);
        Assert.Equal(exeB, onB.ExePath);
        Assert.Equal("état de B", onB.LastSyncMessage);
        Assert.Single(report.Updated);
    }

    [Fact]
    public async Task RemovalOnOnePc_PropagatesToTheOther()
    {
        var a = Pc("A");
        var game = Game("Zeus", FakeExe("A", "Zeus"));
        a.SaveGame(game);
        await new LibrarySync(a).SyncAsync(_cloud, default);
        var b = Pc("B");
        await new LibrarySync(b).SyncAsync(_cloud, default);

        a.RemoveGame(game.Id);
        await new LibrarySync(a).SyncAsync(_cloud, default);
        var report = await new LibrarySync(b).SyncAsync(_cloud, default);

        Assert.Empty(b.Load().Games);
        Assert.Equal(["Zeus"], report.Removed);
        Assert.DoesNotContain("\"Name\": \"Zeus\"", _cloud.LibraryJson);
    }

    [Fact]
    public async Task SameGameAddedOnBothPcs_IsLinkedNotDuplicated()
    {
        var a = Pc("A");
        a.SaveGame(Game("Zeus", FakeExe("A", "Zeus")));
        await new LibrarySync(a).SyncAsync(_cloud, default);
        var cloudId = a.Load().Games.Single().Id;

        // B avait déjà configuré le jeu avant la bibliothèque, ailleurs sur son disque.
        var b = Pc("B");
        var exeB = FakeExe("B", "Zeus");
        var local = Game("zeus", exeB);
        b.Update(c => c.Games.Add(local));

        var report = await new LibrarySync(b).SyncAsync(_cloud, default);

        var onB = b.Load().Games.Single();
        Assert.Equal(1, report.Linked);
        Assert.Equal(cloudId, onB.Id);
        Assert.Contains(local.Id, onB.AliasIds); // les raccourcis bureau de B restent valides
        Assert.Equal(exeB, onB.ExePath);
        Assert.True(onB.HasId(local.Id));
    }

    [Fact]
    public async Task NothingChanged_DoesNotRewriteCloud()
    {
        var a = Pc("A");
        a.SaveGame(Game("Zeus", FakeExe("A", "Zeus")));
        await new LibrarySync(a).SyncAsync(_cloud, default);
        var writes = _cloud.LibraryWrites;

        var report = await new LibrarySync(a).SyncAsync(_cloud, default);

        Assert.Equal(writes, _cloud.LibraryWrites);
        Assert.False(report.HasLocalChanges);
    }

    [Fact]
    public void MissingExe_AdoptsCloudPathOnceGameIsInstalledThere()
    {
        var exe = FakeExe("B", "Zeus");
        var local = new AppConfig
        {
            Games = { new GameEntry { Id = "g1", Name = "Zeus", ExePath = @"Z:\absent\zeus.exe", UpdatedUtc = T0 } },
        };
        var cloud = new LibraryDocument
        {
            Games = { new LibraryGame { Id = "g1", Name = "Zeus", DefaultExePath = exe, UpdatedUtc = T0 } },
        };

        LibraryMerger.Merge(local, cloud, T0.AddHours(1), "B");

        Assert.Equal(exe, local.Games.Single().ExePath);
    }

    [Fact]
    public void ReAddedAfterRemoval_IsKept()
    {
        var local = new AppConfig { Games = { new GameEntry { Id = "g1", Name = "Zeus", UpdatedUtc = T0.AddDays(2) } } };
        var cloud = new LibraryDocument { Removed = { new RemovedGame { Id = "g1", RemovedUtc = T0.AddDays(1) } } };

        var result = LibraryMerger.Merge(local, cloud, T0.AddDays(3), "A");

        Assert.Single(local.Games);
        Assert.Single(result.Document.Games);
    }

    [Fact]
    public void ProgramFiles_IsTokenizedForPortability()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var exe = Path.Combine(programFiles, "GOG Galaxy", "Games", "Zeus", "Zeus.exe");
        Assert.Equal(@"{ProgramFiles}\GOG Galaxy\Games\Zeus\Zeus.exe", PathTokens.Tokenize(exe, null));
        Assert.Equal(exe, PathTokens.Expand(PathTokens.Tokenize(exe, null), null));
    }
}
