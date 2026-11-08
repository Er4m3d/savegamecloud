using System.Text.Json;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SaveGameCloud.Core.Cloud;

/// <summary>
/// Stockage dans Google Drive : « Mon Drive / SaveGameCloud / &lt;Nom du jeu&gt; / … ».
/// Avec la portée drive.file, l'application ne voit que les fichiers qu'elle a elle-même créés.
/// </summary>
public sealed class GoogleDriveStorage(DriveService drive) : ICloudStorage
{
    public const string RootFolderName = "SaveGameCloud";
    internal const string FolderMime = "application/vnd.google-apps.folder";
    internal const string InfoFileName = ".savegamecloud.json";

    private string? _rootId;

    public async Task<ICloudGameFolder?> OpenGameFolderAsync(string gameName, bool createIfMissing, CancellationToken ct)
    {
        var rootId = await GetRootIdAsync(ct).ConfigureAwait(false);
        var folder = await FindChildAsync(rootId, gameName, folderOnly: true, ct).ConfigureAwait(false);
        if (folder is null)
        {
            if (!createIfMissing) return null;
            folder = await CreateFolderAsync(rootId, gameName, ct).ConfigureAwait(false);
        }
        var result = new DriveGameFolder(drive, folder.Id);
        await result.LoadAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> RenameGameFolderAsync(string oldName, string newName, CancellationToken ct)
    {
        var rootId = await GetRootIdAsync(ct).ConfigureAwait(false);
        var folder = await FindChildAsync(rootId, oldName, folderOnly: true, ct).ConfigureAwait(false);
        if (folder is null) return false;
        if (await FindChildAsync(rootId, newName, folderOnly: true, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"Un dossier « {newName} » existe déjà dans Google Drive.");
        await drive.Files.Update(new DriveFile { Name = newName }, folder.Id).ExecuteAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<string> GetRootIdAsync(CancellationToken ct)
    {
        if (_rootId is not null) return _rootId;
        var root = await FindChildAsync("root", RootFolderName, folderOnly: true, ct).ConfigureAwait(false)
                   ?? await CreateFolderAsync("root", RootFolderName, ct).ConfigureAwait(false);
        return _rootId = root.Id;
    }

    private async Task<DriveFile?> FindChildAsync(string parentId, string name, bool folderOnly, CancellationToken ct)
    {
        var request = drive.Files.List();
        request.Q = $"'{parentId}' in parents and name = '{Escape(name)}' and trashed = false"
                    + (folderOnly ? $" and mimeType = '{FolderMime}'" : "");
        request.Spaces = "drive";
        request.Fields = "files(id, name, createdTime)";
        request.OrderBy = "createdTime";
        var list = await request.ExecuteAsync(ct).ConfigureAwait(false);
        return list.Files?.FirstOrDefault();
    }

    private async Task<DriveFile> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var request = drive.Files.Create(new DriveFile { Name = name, MimeType = FolderMime, Parents = [parentId] });
        request.Fields = "id, name";
        return await request.ExecuteAsync(ct).ConfigureAwait(false);
    }

    internal static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private sealed class DriveGameFolder(DriveService drive, string folderId) : ICloudGameFolder
    {
        private readonly Dictionary<string, CloudFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _folderIds = new(StringComparer.OrdinalIgnoreCase) { [""] = folderId };
        private string? _infoFileId;

        public IReadOnlyDictionary<string, CloudFile> Files => _files;
        public CloudSyncInfo? Info { get; private set; }

        public async Task LoadAsync(CancellationToken ct)
        {
            var pending = new Queue<(string Id, string Prefix)>();
            pending.Enqueue((folderId, ""));
            while (pending.Count > 0)
            {
                var (id, prefix) = pending.Dequeue();
                string? pageToken = null;
                do
                {
                    var request = drive.Files.List();
                    request.Q = $"'{id}' in parents and trashed = false";
                    request.Spaces = "drive";
                    request.PageSize = 1000;
                    request.PageToken = pageToken;
                    request.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, size, modifiedTime)";
                    var page = await request.ExecuteAsync(ct).ConfigureAwait(false);
                    foreach (var f in page.Files ?? [])
                    {
                        var rel = prefix + f.Name;
                        if (f.MimeType == FolderMime)
                        {
                            _folderIds.TryAdd(rel, f.Id);
                            pending.Enqueue((f.Id, rel + "/"));
                        }
                        else if (prefix == "" && f.Name == InfoFileName)
                        {
                            _infoFileId = f.Id;
                        }
                        else if (f.Md5Checksum is not null && !_files.ContainsKey(rel))
                        {
                            var modified = f.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
                            _files[rel] = new CloudFile(rel, f.Md5Checksum, f.Size ?? 0, modified, f.Id);
                        }
                    }
                    pageToken = page.NextPageToken;
                } while (pageToken is not null);
            }

            if (_infoFileId is not null)
            {
                try
                {
                    using var ms = new MemoryStream();
                    await DownloadAsync(_infoFileId, ms, ct).ConfigureAwait(false);
                    Info = JsonSerializer.Deserialize<CloudSyncInfo>(ms.ToArray());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Infrastructure.Log.Warn($"Fichier {InfoFileName} illisible : {ex.Message}");
                }
            }
        }

        public Task DownloadFileAsync(CloudFile file, Stream destination, CancellationToken ct) =>
            DownloadAsync(file.Id ?? throw new InvalidOperationException("Identifiant Drive manquant"), destination, ct);

        public async Task UploadFileAsync(string relativePath, Stream content, DateTime modifiedUtc, CancellationToken ct)
        {
            var modified = new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc));
            var name = relativePath.Split('/')[^1];
            DriveFile uploaded;
            if (_files.TryGetValue(relativePath, out var existing) && existing.Id is not null)
            {
                var request = drive.Files.Update(new DriveFile { ModifiedTimeDateTimeOffset = modified }, existing.Id, content, "application/octet-stream");
                request.Fields = "id, md5Checksum, size, modifiedTime";
                uploaded = await RunUploadAsync(request, () => request.ResponseBody, ct).ConfigureAwait(false);
            }
            else
            {
                var parentId = await EnsureFolderAsync(ParentOf(relativePath), ct).ConfigureAwait(false);
                var request = drive.Files.Create(new DriveFile { Name = name, Parents = [parentId], ModifiedTimeDateTimeOffset = modified },
                    content, "application/octet-stream");
                request.Fields = "id, md5Checksum, size, modifiedTime";
                uploaded = await RunUploadAsync(request, () => request.ResponseBody, ct).ConfigureAwait(false);
            }
            _files[relativePath] = new CloudFile(relativePath, uploaded.Md5Checksum ?? "", uploaded.Size ?? 0,
                uploaded.ModifiedTimeDateTimeOffset?.UtcDateTime ?? modifiedUtc, uploaded.Id);
        }

        public async Task DeleteFileAsync(string relativePath, CancellationToken ct)
        {
            if (!_files.TryGetValue(relativePath, out var file) || file.Id is null) return;
            // Mise à la corbeille plutôt que suppression définitive : récupérable depuis Drive.
            await drive.Files.Update(new DriveFile { Trashed = true }, file.Id).ExecuteAsync(ct).ConfigureAwait(false);
            _files.Remove(relativePath);
        }

        public async Task WriteInfoAsync(CloudSyncInfo info, CancellationToken ct)
        {
            using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(info, new JsonSerializerOptions { WriteIndented = true }));
            if (_infoFileId is not null)
            {
                var request = drive.Files.Update(new DriveFile(), _infoFileId, content, "application/json");
                await RunUploadAsync(request, () => request.ResponseBody, ct).ConfigureAwait(false);
            }
            else
            {
                var request = drive.Files.Create(new DriveFile { Name = InfoFileName, Parents = [folderId] }, content, "application/json");
                request.Fields = "id";
                _infoFileId = (await RunUploadAsync(request, () => request.ResponseBody, ct).ConfigureAwait(false)).Id;
            }
            Info = info;
        }

        private async Task<string> EnsureFolderAsync(string relativeDir, CancellationToken ct)
        {
            if (_folderIds.TryGetValue(relativeDir, out var id)) return id;
            var parentId = await EnsureFolderAsync(ParentOf(relativeDir), ct).ConfigureAwait(false);
            var request = drive.Files.Create(new DriveFile { Name = relativeDir.Split('/')[^1], MimeType = FolderMime, Parents = [parentId] });
            request.Fields = "id";
            var created = await request.ExecuteAsync(ct).ConfigureAwait(false);
            return _folderIds[relativeDir] = created.Id;
        }

        private async Task DownloadAsync(string fileId, Stream destination, CancellationToken ct)
        {
            var progress = await drive.Files.Get(fileId).DownloadAsync(destination, ct).ConfigureAwait(false);
            if (progress.Status != DownloadStatus.Completed)
                throw new IOException("Échec du téléchargement depuis Google Drive", progress.Exception);
        }

        private static async Task<DriveFile> RunUploadAsync(ResumableUpload upload, Func<DriveFile?> response, CancellationToken ct)
        {
            var progress = await upload.UploadAsync(ct).ConfigureAwait(false);
            if (progress.Status != UploadStatus.Completed)
            {
                if (CloudStorageFullException.IsStorageFull(progress.Exception))
                    throw new CloudStorageFullException(progress.Exception);
                throw new IOException("Échec de l'envoi vers Google Drive", progress.Exception);
            }
            return response() ?? throw new IOException("Réponse vide de Google Drive après l'envoi");
        }

        private static string ParentOf(string relativePath)
        {
            var index = relativePath.LastIndexOf('/');
            return index < 0 ? "" : relativePath[..index];
        }
    }
}
