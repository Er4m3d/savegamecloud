using Google;
using Google.Apis.Requests;
using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud.Tests;

public sealed class SizeGuardTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    [InlineData(10 * Mb, 50, false)]
    [InlineData(200 * Mb, 1, true)]
    [InlineData(1 * Mb, 2000, true)]
    public void IsLarge_UsesBytesOrFileCount(long bytes, int files, bool expected) =>
        Assert.Equal(expected, SizeGuard.IsLarge(new SizeInfo(files, bytes)));

    [Fact]
    public void UploadConfirmation_OnlyWhenLargeAndNewOrDoubled()
    {
        var big = new SizeInfo(10, 300 * Mb);
        Assert.False(SizeGuard.NeedsUploadConfirmation(new SizeInfo(10, 5 * Mb), null)); // petite : jamais
        Assert.True(SizeGuard.NeedsUploadConfirmation(big, null));                     // grosse, jamais synchronisée
        Assert.True(SizeGuard.NeedsUploadConfirmation(big, 100 * Mb));                 // a triplé
        Assert.False(SizeGuard.NeedsUploadConfirmation(big, 250 * Mb));                // déjà acceptée à cette taille
    }

    [Fact]
    public void Measure_CountsFilesRecursively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sgc-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllBytes(Path.Combine(dir, "a.sav"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "sub", "b.sav"), new byte[50]);

            Assert.Equal(new SizeInfo(2, 150), SizeGuard.Measure(SyncItemKind.Directory, dir));
            Assert.Equal(new SizeInfo(1, 100), SizeGuard.Measure(SyncItemKind.File, Path.Combine(dir, "a.sav")));
            Assert.Equal(default, SizeGuard.Measure(SyncItemKind.Directory, Path.Combine(dir, "absent")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Format_IsHumanReadable()
    {
        Assert.Equal("512 octets", SizeGuard.Format(512));
        Assert.Contains("Mo", SizeGuard.Format(300 * Mb));
    }

    [Fact]
    public void StorageFull_IsDetected_EvenWhenWrapped()
    {
        var api = new GoogleApiException("drive", "quota")
        {
            Error = new RequestError { Errors = [new SingleError { Reason = "storageQuotaExceeded" }] },
        };
        Assert.True(CloudStorageFullException.IsStorageFull(new IOException("envoi", api)));
        Assert.False(CloudStorageFullException.IsStorageFull(new IOException("réseau")));
        Assert.False(CloudStorageFullException.IsStorageFull(new GoogleApiException("drive", "autre")
        {
            Error = new RequestError { Errors = [new SingleError { Reason = "rateLimitExceeded" }] },
        }));
    }
}
