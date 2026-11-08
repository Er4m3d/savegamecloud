using Google.Apis.Auth.OAuth2;
using SaveGameCloud.Core.Infrastructure;

namespace SaveGameCloud.Tests;

public sealed class ClientSecretTests
{
    [Fact]
    public async Task EmbeddedClientSecret_IsAValidDesktopClient_WhenPresent()
    {
        // Le fichier n'est pas versionné : sur une machine sans lui, rien à vérifier.
        if (!AppPaths.HasEmbeddedClientSecret) return;

        await using var stream = AppPaths.OpenClientSecret();
        Assert.NotNull(stream);
        var secrets = (await GoogleClientSecrets.FromStreamAsync(stream!)).Secrets;
        Assert.EndsWith(".apps.googleusercontent.com", secrets.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(secrets.ClientSecret));
    }
}
