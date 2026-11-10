using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SaveGameCloud.Core.Cloud;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Launch;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;
using SaveGameCloud.Services;
using SaveGameCloud.Views;

namespace SaveGameCloud;

public partial class MainWindow : Window
{
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        AppServices.Auth.StateChanged += (_, _) => Dispatcher.Invoke(UpdateAccount);
        Loaded += OnLoaded;
        // Un lancement depuis un raccourci peut avoir mis à jour la config entre-temps.
        Activated += (_, _) => { if (!_busy) RefreshGames(); };
    }

    private GameEntry? SelectedGame => (GamesList.SelectedItem as GameItem)?.Game;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshGames();
        UpdateAccount();
        if (!GoogleAuthService.HasClientSecret)
        {
            SetStatus("Configuration Google requise : importez votre fichier client_secret.json (voir README).");
            return;
        }
        if (GoogleAuthService.HasStoredToken)
        {
            SetBusy("Reconnexion à Google…");
            var ok = await AppServices.Auth.TrySignInSilentlyAsync(CancellationToken.None);
            ClearBusy(ok ? "Connecté." : "Reconnexion impossible : cliquez sur « Se connecter avec Google ».");
            if (ok) await SyncLibraryAsync();
        }
        else
        {
            SetStatus("Connectez-vous à votre compte Google pour activer la synchronisation.");
        }
    }

    // ---------- Compte Google ----------

    private void UpdateAccount()
    {
        var auth = AppServices.Auth;
        var hasSecret = GoogleAuthService.HasClientSecret;
        AccountName.Text = auth.IsSignedIn ? auth.UserDisplayName ?? "Compte Google" : hasSecret ? "Non connecté" : "Google non configuré";
        AccountEmail.Text = auth.IsSignedIn ? auth.UserEmail ?? "" : "";
        SignInButton.Visibility = !auth.IsSignedIn && hasSecret ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = auth.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        ImportSecretButton.Visibility = hasSecret ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInInteractiveAsync();

    private async Task<bool> SignInInteractiveAsync()
    {
        if (!GoogleAuthService.HasClientSecret)
        {
            MessageBox.Show(this,
                "Avant la première connexion, importez le fichier client_secret.json de votre projet Google Cloud " +
                "(identifiant OAuth de type « Application de bureau »). Voir le README pour la procédure.",
                "Configuration Google requise", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        SetBusy("Connexion : terminez l'autorisation dans votre navigateur…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await AppServices.Auth.SignInAsync(cts.Token);
            ClearBusy($"Connecté en tant que {AppServices.Auth.UserEmail}.");
            await SyncLibraryAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            ClearBusy("Connexion annulée (délai dépassé).");
        }
        catch (Exception ex)
        {
            Log.Error("Connexion Google impossible", ex);
            ClearBusy("Échec de la connexion.");
            MessageBox.Show(this, $"Connexion à Google impossible :\n{ex.Message}", "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Se déconnecter du compte Google ?\nLes sauvegardes restent dans Google Drive.", "Déconnexion",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy("Déconnexion…");
        await AppServices.Auth.SignOutAsync();
        ClearBusy("Déconnecté.");
    }

    private void ImportSecret_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir le fichier client_secret.json", Filter = "Fichier JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var text = File.ReadAllText(dialog.FileName);
        if (!text.Contains("\"installed\"") || !text.Contains("client_id"))
        {
            MessageBox.Show(this, "Ce fichier ne ressemble pas à un identifiant OAuth « Application de bureau » (section \"installed\" absente).",
                "Fichier invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        File.Copy(dialog.FileName, AppPaths.ClientSecretFile, overwrite: true);
        UpdateAccount();
        SetStatus("Identifiants Google importés. Vous pouvez vous connecter.");
    }

    private async Task<bool> EnsureSignedInAsync()
    {
        if (await AppServices.Auth.TrySignInSilentlyAsync(CancellationToken.None)) return true;
        if (MessageBox.Show(this, "Cette action nécessite d'être connecté à Google Drive. Se connecter maintenant ?", "Connexion requise",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        return await SignInInteractiveAsync();
    }

    // ---------- Bibliothèque partagée entre PC ----------

    /// <summary>
    /// Fusionne la liste des jeux avec celle de Google Drive : les jeux ajoutés sur un autre PC apparaissent ici,
    /// et ceux d'ici sont envoyés. Silencieux si l'utilisateur n'est pas connecté.
    /// </summary>
    private async Task SyncLibraryAsync()
    {
        if (AppServices.Auth.Storage is not { } cloud) return;
        try
        {
            var report = await new Core.Library.LibrarySync(AppServices.Config).SyncAsync(cloud, CancellationToken.None);
            RefreshGames();
            if (!report.HasLocalChanges) return;
            SetStatus(report.Summary);
            if (report.Added.Count > 0)
            {
                var missing = report.Added.Where(a => !a.Installed).Select(a => "  • " + a.Name).ToList();
                MessageBox.Show(this,
                    $"{report.Added.Count} jeu(x) récupéré(s) depuis votre bibliothèque Google Drive, avec leurs fichiers de sauvegarde déjà configurés." +
                    (missing.Count == 0
                        ? ""
                        : "\n\nNon installé(s) sur ce PC :\n" + string.Join("\n", missing) +
                          "\n\nInstallez-les, ou utilisez « Localiser l'exécutable… » s'ils sont installés ailleurs."),
                    "Bibliothèque récupérée", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Synchronisation de la bibliothèque impossible", ex);
            SetStatus($"Bibliothèque cloud non synchronisée : {ex.Message}");
        }
    }

    // ---------- Liste des jeux ----------

    private void RefreshGames(string? selectId = null)
    {
        selectId ??= SelectedGame?.Id;
        var games = AppServices.Config.Load().Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var items = games.Select(g => new GameItem(g)).ToList();
        GamesList.ItemsSource = items;
        EmptyListText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesList.SelectedItem = items.FirstOrDefault(i => i.Game.Id == selectId) ?? items.FirstOrDefault();
        ShowDetails();
    }

    private void GamesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowDetails();

    private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedGame is not null && !_busy) Play_Click(sender, e);
    }

    private void ShowDetails()
    {
        var game = SelectedGame;
        DetailsPanel.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionText.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        if (game is null) return;

        var installed = game.IsInstalled;
        DetailIcon.Source = ShellHelpers.GetIcon(game.ResolvedExePath) ?? GameItem.Placeholder;
        DetailIcon.Opacity = installed ? 1 : 0.5;
        DetailName.Text = game.Name;
        DetailExe.Text = game.ResolvedExePath + (string.IsNullOrWhiteSpace(game.Arguments) ? "" : "  " + game.Arguments);
        NotInstalledBanner.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        NotInstalledText.Text = $"Ce jeu n'est pas installé sur ce PC : l'exécutable est introuvable.\n{game.ResolvedExePath}";
        // Sans le jeu, ni lancement ni synchro : les chemins de sauvegarde dépendent de son dossier.
        PlayButton.IsEnabled = UploadButton.IsEnabled = DownloadButton.IsEnabled = ShortcutButton.IsEnabled = installed;
        DetailSync.Text = GameItem.DescribeSync(game) +
                          (game.LastPlayedUtc is { } played ? $"\nDernière partie : {played.ToLocalTime():g}" : "") +
                          (game.SyncItems.Count > 0 ? $"\nTaille locale : {SizeGuard.Measure(game)}" : "") +
                          $"\nDossier Google Drive : {GoogleDriveStorage.RootFolderName} / {game.Name}";
        DetailSync.Foreground = game.LastSyncFailed ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextBrush");
        var rows = game.SyncItems.Select(i => new SyncItemRow(game, i)).ToList();
        DetailItems.ItemsSource = rows;
        NoItemsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Actions ----------

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir l'exécutable du jeu",
            Filter = "Programmes (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tous les fichiers (*.*)|*.*",
            DereferenceLinks = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var game = new GameEntry { ExePath = dialog.FileName, Name = GuessGameName(dialog.FileName) };
        var editor = new GameEditWindow(game, isNew: true, ExistingNames(exceptId: game.Id)) { Owner = this };
        if (editor.ShowDialog() != true) return;
        AppServices.Config.SaveGame(editor.Result);
        RefreshGames(editor.Result.Id);
        SetStatus($"« {editor.Result.Name} » ajouté.");
        await SyncLibraryAsync();
    }

    /// <summary>Indique où est installé le jeu sur ce PC (ne change que ce PC, pas la bibliothèque).</summary>
    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        var expectedName = Path.GetFileName(game.ResolvedExePath);
        var dialog = new OpenFileDialog
        {
            Title = $"Où est installé « {game.Name} » ? Choisissez {expectedName}",
            Filter = "Programmes (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tous les fichiers (*.*)|*.*",
            FileName = expectedName,
            DereferenceLinks = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        AppServices.Config.UpdateGame(game.Id, g => g.ExePath = dialog.FileName);
        RefreshGames(game.Id);
        SetStatus($"« {game.Name} » : exécutable trouvé.");
    }

    private async void EditGame_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        var editor = new GameEditWindow(game.Clone(), isNew: false, ExistingNames(exceptId: game.Id)) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var updated = editor.Result;

        if (!string.Equals(updated.Name, game.Name, StringComparison.Ordinal))
        {
            // Le nom du jeu est le nom du dossier Drive : on renomme aussi le dossier cloud.
            SetBusy("Renommage du dossier Google Drive…");
            try
            {
                if (await AppServices.GetCloudAsync(CancellationToken.None) is { } cloud)
                    await cloud.RenameGameFolderAsync(game.Name, updated.Name, CancellationToken.None);
                else
                    MessageBox.Show(this, $"Non connecté : pensez à renommer vous-même le dossier « {game.Name} » en « {updated.Name} » dans Google Drive.",
                        "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("Renommage du dossier Drive impossible", ex);
                MessageBox.Show(this, $"Le dossier Google Drive n'a pas pu être renommé :\n{ex.Message}\n\nLe jeu utilisera le dossier « {updated.Name} ».",
                    "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ClearBusy();
        }

        AppServices.Config.SaveGame(updated);
        RefreshGames(updated.Id);
        SetStatus($"« {updated.Name} » enregistré.");
        await SyncLibraryAsync();
    }

    private async void DeleteGame_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        if (MessageBox.Show(this, $"Retirer « {game.Name} » du launcher ?\n\nIl sera aussi retiré de vos autres PC. Aucun fichier n'est supprimé : " +
                                  "ni le jeu, ni les sauvegardes locales, ni le dossier Google Drive.",
                "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        AppServices.Config.RemoveGame(game.Id);
        RefreshGames();
        SetStatus($"« {game.Name} » retiré.");
        await SyncLibraryAsync();
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        try
        {
            var path = ShellHelpers.CreateDesktopShortcut(game);
            SetStatus($"Raccourci créé : {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Création du raccourci impossible", ex);
            MessageBox.Show(this, $"Impossible de créer le raccourci :\n{ex.Message}", "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game || _busy) return;
        if (!game.IsInstalled)
        {
            if (MessageBox.Show(this, $"« {game.Name} » n'est pas installé sur ce PC.\n\nS'il est installé ailleurs, voulez-vous indiquer son exécutable ?",
                    "Jeu non installé", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Locate_Click(sender, e);
            return;
        }
        if (game.SyncItems.Count == 0 &&
            MessageBox.Show(this, "Aucun fichier de sauvegarde n'est configuré pour ce jeu : rien ne sera synchronisé.\n\nLancer quand même ?",
                "SaveGameCloud", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (!AppServices.Auth.IsSignedIn && !GoogleAuthService.HasStoredToken && GoogleAuthService.HasClientSecret)
            await SignInInteractiveAsync();

        SetBusy($"Lancement de {game.Name}…");
        try
        {
            var ui = new WpfLaunchUi(this, SetStatus);
            var outcome = await AppServices.CreateCoordinator().RunAsync(game, ui);
            ClearBusy(outcome switch
            {
                LaunchOutcome.Completed => StatusText.Text,
                LaunchOutcome.Cancelled => "Lancement annulé.",
                _ => "Le lancement n'a pas abouti.",
            });
        }
        catch (Exception ex)
        {
            Log.Error("Erreur pendant le lancement", ex);
            ClearBusy("Erreur pendant le lancement.");
            MessageBox.Show(this, ex.Message, "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshGames(game.Id);
    }

    private async void UploadNow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        if (MessageBox.Show(this, $"Envoyer la sauvegarde locale de « {game.Name} » vers Google Drive ?\nElle remplacera la version du cloud.",
                "Envoyer vers le cloud", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ManualSyncAsync(game, upload: true);
    }

    private async void DownloadNow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game) return;
        if (MessageBox.Show(this, $"Remplacer la sauvegarde locale de « {game.Name} » par celle de Google Drive ?\nUne copie de secours des fichiers locaux sera faite avant.",
                "Récupérer depuis le cloud", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ManualSyncAsync(game, upload: false);
    }

    private async Task ManualSyncAsync(GameEntry game, bool upload)
    {
        if (!await EnsureSignedInAsync()) return;
        using var guard = GameRunGuard.TryAcquire(game.Id);
        if (guard is null)
        {
            MessageBox.Show(this, "Le jeu est en cours d'exécution : attendez sa fermeture.", "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (upload)
        {
            var size = await Task.Run(() => SizeGuard.Measure(game));
            if (SizeGuard.NeedsUploadConfirmation(size, game.LastSyncedBytes) &&
                MessageBox.Show(this, LaunchCoordinator.LargeUploadMessage(game, size), "Sauvegarde volumineuse",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        SetBusy(upload ? "Envoi vers Google Drive…" : "Téléchargement depuis Google Drive…");
        string message;
        var failed = false;
        long? syncedBytes = null;
        try
        {
            var engine = AppServices.CreateEngine()!;
            var progress = new Progress<string>(SetStatus);
            if (upload)
            {
                var r = await engine.UploadAsync(game, progress, CancellationToken.None);
                message = r.NothingToDo ? "Cloud déjà à jour" : $"Envoyé vers le cloud ({r.Transferred} fichier(s), {r.Deleted} supprimé(s))";
            }
            else
            {
                var r = await engine.DownloadAsync(game, progress, CancellationToken.None);
                message = r.NothingToDo ? "Local déjà à jour" : $"Récupéré depuis le cloud ({r.Transferred} fichier(s), {r.Deleted} supprimé(s))";
            }
            syncedBytes = (await Task.Run(() => SizeGuard.Measure(game))).TotalBytes;
        }
        catch (Exception ex)
        {
            Log.Error("Synchronisation manuelle impossible", ex);
            failed = true;
            var error = CloudStorageFullException.IsStorageFull(ex) ? new CloudStorageFullException(ex).Message : ex.Message;
            message = CloudStorageFullException.IsStorageFull(ex) ? "Échec : Google Drive plein" : $"Échec : {ex.Message}";
            MessageBox.Show(this, error, "Synchronisation impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        AppServices.Config.UpdateGame(game.Id, g =>
        {
            g.LastSyncUtc = DateTime.UtcNow;
            g.LastSyncFailed = failed;
            g.LastSyncMessage = message;
            if (syncedBytes is not null) g.LastSyncedBytes = syncedBytes;
        });
        ClearBusy(message + ".");
        RefreshGames(game.Id);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is { } game) ShellHelpers.OpenInExplorer(AppServices.Backups.GetGameBackupDir(game));
    }

    // ---------- Utilitaires ----------

    private IReadOnlyCollection<string> ExistingNames(string exceptId) =>
        AppServices.Config.Load().Games.Where(g => g.Id != exceptId).Select(g => g.Name).ToList();

    private static string GuessGameName(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var candidate = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length > 1) return candidate;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void SetBusy(string message)
    {
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        ActionsPanel.IsEnabled = false;
        GamesList.IsEnabled = false;
        SignInButton.IsEnabled = SignOutButton.IsEnabled = false;
        SetStatus(message);
    }

    private void ClearBusy(string? message = null)
    {
        _busy = false;
        BusyBar.Visibility = Visibility.Hidden;
        ActionsPanel.IsEnabled = true;
        GamesList.IsEnabled = true;
        SignInButton.IsEnabled = SignOutButton.IsEnabled = true;
        if (message is not null) SetStatus(message);
    }
}

public sealed class GameItem(GameEntry game)
{
    public GameEntry Game { get; } = game;
    /// <summary>Icône de l'appli, affichée en grisé pour un jeu non installé.</summary>
    public static readonly ImageSource Placeholder = LoadPlaceholder();

    private readonly bool _installed = game.IsInstalled;

    public string Name => Game.Name;
    public ImageSource? Icon => ShellHelpers.GetIcon(Game.ResolvedExePath) ?? Placeholder;
    public double IconOpacity => _installed ? 1 : 0.45;
    public string Subtitle => _installed ? DescribeSync(Game) : "Non installé sur ce PC";

    public Brush SubtitleBrush => (Brush)Application.Current.FindResource(
        !_installed ? "WarningBrush" : Game.LastSyncFailed ? "DangerBrush" : "SubtleBrush");

    private static ImageSource LoadPlaceholder()
    {
        var image = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app-256.png"));
        image.Freeze();
        return image;
    }

    public static string DescribeSync(GameEntry game) =>
        game.LastSyncUtc is { } when ? $"{game.LastSyncMessage} — {when.ToLocalTime():g}" : "Jamais synchronisé";
}

public sealed class SyncItemRow(GameEntry game, SyncItem item)
{
    public string Glyph => item.Kind == SyncItemKind.Directory ? "📁" : "📄";
    public string CloudName => item.CloudName;
    public string LocalPath => game.ResolveItemPath(item);
    private bool Exists => item.Kind == SyncItemKind.Directory ? Directory.Exists(LocalPath) : File.Exists(LocalPath);
    public Brush PathBrush => (Brush)Application.Current.FindResource(Exists ? "SubtleBrush" : "DangerBrush");
}
