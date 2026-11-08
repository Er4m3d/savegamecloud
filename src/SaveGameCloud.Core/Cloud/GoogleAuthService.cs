using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using SaveGameCloud.Core.Infrastructure;

namespace SaveGameCloud.Core.Cloud;

/// <summary>Connexion au compte Google (OAuth « application de bureau », navigateur + boucle locale).</summary>
public sealed class GoogleAuthService
{
    private static readonly string[] Scopes = [DriveService.Scope.DriveFile];

    private UserCredential? _credential;
    private DriveService? _drive;
    private GoogleDriveStorage? _storage;

    public bool IsSignedIn => _drive is not null;
    public string? UserEmail { get; private set; }
    public string? UserDisplayName { get; private set; }

    public event EventHandler? StateChanged;

    public static bool HasClientSecret => AppPaths.HasClientSecret;

    public static bool HasStoredToken =>
        Directory.Exists(AppPaths.TokenDir) && Directory.EnumerateFiles(AppPaths.TokenDir).Any();

    public GoogleDriveStorage? Storage => _storage;

    /// <summary>Ouvre le navigateur si aucun jeton n'est enregistré, sinon réutilise le jeton.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        GoogleClientSecrets secrets;
        await using (var stream = AppPaths.OpenClientSecret()
                                  ?? throw new FileNotFoundException("Identifiant Google (client_secret.json) introuvable."))
            secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct).ConfigureAwait(false);

        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets, Scopes, "user", ct, new FileDataStore(AppPaths.TokenDir, fullPath: true)).ConfigureAwait(false);

        var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = "SaveGameCloud",
        });

        var about = drive.About.Get();
        about.Fields = "user(emailAddress, displayName)";
        var info = await about.ExecuteAsync(ct).ConfigureAwait(false);

        _drive = drive;
        _storage = new GoogleDriveStorage(drive);
        UserEmail = info.User?.EmailAddress;
        UserDisplayName = info.User?.DisplayName;
        Log.Info($"Connecté à Google en tant que {UserEmail}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reconnexion sans interaction si un jeton est déjà enregistré.</summary>
    public async Task<bool> TrySignInSilentlyAsync(CancellationToken ct)
    {
        if (IsSignedIn) return true;
        if (!HasClientSecret || !HasStoredToken) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await SignInAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reconnexion automatique impossible : {ex.Message}");
            return false;
        }
    }

    public async Task SignOutAsync()
    {
        if (_credential is not null)
        {
            try { await _credential.RevokeTokenAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"Révocation du jeton impossible : {ex.Message}"); }
        }
        _drive?.Dispose();
        _drive = null;
        _storage = null;
        _credential = null;
        UserEmail = null;
        UserDisplayName = null;
        if (Directory.Exists(AppPaths.TokenDir)) Directory.Delete(AppPaths.TokenDir, recursive: true);
        Log.Info("Déconnecté de Google");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
