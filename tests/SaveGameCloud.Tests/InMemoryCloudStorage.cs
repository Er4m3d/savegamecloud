using System.Security.Cryptography;
using System.Text.Json;
using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Library;

namespace SaveGameCloud.Tests;

/// <summary>Faux Google Drive en mémoire, partageable entre plusieurs « PC » simulés.</summary>
public sealed class InMemoryCloudStorage : ICloudStorage
{
    public Dictionary<string, Folder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ICloudGameFolder?> OpenGameFolderAsync(string gameName, bool createIfMissing, CancellationToken ct)
    {
        if (!Folders.TryGetValue(gameName, out var folder))
        {
            if (!createIfMissing) return Task.FromResult<ICloudGameFolder?>(null);
            Folders[gameName] = folder = new Folder();
        }
        return Task.FromResult<ICloudGameFolder?>(folder);
    }

    public Task<bool> RenameGameFolderAsync(string oldName, string newName, CancellationToken ct)
    {
        if (!Folders.Remove(oldName, out var folder)) return Task.FromResult(false);
        Folders[newName] = folder;
        return Task.FromResult(true);
    }

    /// <summary>Bibliothèque stockée en JSON, comme dans Drive (pour tester aussi la sérialisation).</summary>
    public string? LibraryJson { get; set; }
    public int LibraryWrites { get; private set; }

    public Task<LibraryDocument?> ReadLibraryAsync(CancellationToken ct) =>
        Task.FromResult(LibraryJson is null ? null : JsonSerializer.Deserialize<LibraryDocument>(LibraryJson, AppJson.Options));

    public Task WriteLibraryAsync(LibraryDocument document, CancellationToken ct)
    {
        LibraryJson = JsonSerializer.Serialize(document, AppJson.Options);
        LibraryWrites++;
        return Task.CompletedTask;
    }

    public sealed class Folder : ICloudGameFolder
    {
        public Dictionary<string, (byte[] Data, DateTime Modified)> Content { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, CloudFile> Files =>
            Content.ToDictionary(
                kv => kv.Key,
                kv => new CloudFile(kv.Key, Convert.ToHexStringLower(MD5.HashData(kv.Value.Data)), kv.Value.Data.Length, kv.Value.Modified),
                StringComparer.OrdinalIgnoreCase);

        public CloudSyncInfo? Info { get; set; }

        public async Task DownloadFileAsync(CloudFile file, Stream destination, CancellationToken ct) =>
            await destination.WriteAsync(Content[file.RelativePath].Data, ct);

        public async Task UploadFileAsync(string relativePath, Stream content, DateTime modifiedUtc, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Content[relativePath] = (ms.ToArray(), modifiedUtc);
        }

        public Task DeleteFileAsync(string relativePath, CancellationToken ct)
        {
            Content.Remove(relativePath);
            return Task.CompletedTask;
        }

        public Task WriteInfoAsync(CloudSyncInfo info, CancellationToken ct)
        {
            Info = info;
            return Task.CompletedTask;
        }
    }
}
