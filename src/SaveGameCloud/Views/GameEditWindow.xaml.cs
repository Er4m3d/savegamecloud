using System.Windows;
using Microsoft.Win32;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud.Views;

public partial class GameEditWindow : Window
{
    private readonly GameEntry _game;
    private readonly IReadOnlyCollection<string> _otherNames;

    public GameEntry Result => _game;

    public GameEditWindow(GameEntry game, bool isNew, IReadOnlyCollection<string> otherNames)
    {
        InitializeComponent();
        _game = game;
        _otherNames = otherNames;
        Title = isNew ? "Ajouter un jeu" : "Modifier le jeu";
        HeaderText.Text = isNew ? "Nouveau jeu" : game.Name;
        NameBox.Text = game.Name;
        ExeBox.Text = game.ResolvedExePath;
        ArgsBox.Text = game.Arguments;
        RefreshItems();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>Dossier du jeu tel que saisi actuellement (sert au jeton {GameDir}).</summary>
    private string? CurrentGameDir =>
        string.IsNullOrWhiteSpace(ExeBox.Text) ? null : Path.GetDirectoryName(ExeBox.Text.Trim());

    private void RefreshItems()
    {
        var preview = _game.Clone();
        preview.ExePath = ExeBox.Text.Trim();
        ItemsList.ItemsSource = _game.SyncItems.Select(i => new SyncItemRow(preview, i)).ToList();
        EmptyText.Visibility = _game.SyncItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var size = SizeGuard.Measure(preview);
        SizeText.Text = _game.SyncItems.Count == 0
            ? ""
            : $"Total à synchroniser : {size}" + (SizeGuard.IsLarge(size)
                ? " ⚠ C'est volumineux pour des sauvegardes : chaque lancement et chaque fermeture du jeu seront plus longs."
                : "");
        SizeText.Foreground = (System.Windows.Media.Brush)FindResource(SizeGuard.IsLarge(size) ? "DangerBrush" : "SubtleBrush");
    }

    private void ExeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded) RefreshItems();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir l'exécutable du jeu",
            Filter = "Programmes (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tous les fichiers (*.*)|*.*",
            InitialDirectory = CurrentGameDir ?? "",
        };
        if (dialog.ShowDialog(this) == true) ExeBox.Text = dialog.FileName;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir les fichiers à synchroniser",
            Multiselect = true,
            InitialDirectory = CurrentGameDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddItem(SyncItemKind.File, file);
        RefreshItems();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir les dossiers à synchroniser",
            Multiselect = true,
            InitialDirectory = CurrentGameDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var folder in dialog.FolderNames) AddItem(SyncItemKind.Directory, folder);
        RefreshItems();
    }

    private void AddItem(SyncItemKind kind, string fullPath)
    {
        var gameDir = CurrentGameDir;
        var tokenized = PathTokens.Tokenize(fullPath, gameDir);
        if (_game.SyncItems.Any(i => string.Equals(i.Path, tokenized, StringComparison.OrdinalIgnoreCase))) return;

        // Un seul avertissement : dossier entier du jeu, sinon élément volumineux.
        var size = SizeGuard.Measure(kind, fullPath);
        var isGameDir = kind == SyncItemKind.Directory && gameDir is not null &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir)), StringComparison.OrdinalIgnoreCase);
        string? warning = null;
        if (isGameDir)
            warning = $"Vous avez choisi le dossier entier du jeu ({size}) : tout le jeu sera envoyé dans Google Drive à chaque fermeture.";
        else if (SizeGuard.IsLarge(size))
            warning = $"« {Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath))} » contient {size}.\n\n" +
                      "C'est beaucoup pour des sauvegardes : tout sera envoyé dans Google Drive, et chaque lancement " +
                      "et chaque fermeture du jeu seront plus longs. Les sauvegardes se trouvent souvent dans un sous-dossier " +
                      "(SAVE, Saves…).";
        if (warning is not null &&
            MessageBox.Show(this, warning + "\n\nAjouter quand même ?", isGameDir ? "Dossier du jeu" : "Élément volumineux",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Nom unique dans le dossier Drive du jeu.
        var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (string.IsNullOrEmpty(baseName)) baseName = "Racine";
        var name = baseName;
        for (var n = 2; _game.SyncItems.Any(i => i.CloudName.Equals(name, StringComparison.OrdinalIgnoreCase)) || name == ".savegamecloud.json"; n++)
        {
            name = kind == SyncItemKind.File
                ? $"{Path.GetFileNameWithoutExtension(baseName)} ({n}){Path.GetExtension(baseName)}"
                : $"{baseName} ({n})";
        }
        _game.SyncItems.Add(new SyncItem { Kind = kind, Path = tokenized, CloudName = name });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ItemsList.SelectedItems.Cast<SyncItemRow>().ToList())
            _game.SyncItems.RemoveAll(i => i.CloudName == row.CloudName);
        RefreshItems();
    }

    private void ItemsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = ItemsList.SelectedItems.Count > 0;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var exe = ExeBox.Text.Trim();
        string? error = null;
        if (name.Length == 0) error = "Indiquez un nom pour le jeu.";
        else if (name.Contains('/') || name.Contains('\\')) error = "Le nom ne peut pas contenir / ou \\.";
        else if (_otherNames.Contains(name, StringComparer.OrdinalIgnoreCase)) error = "Un autre jeu porte déjà ce nom.";
        else if (!File.Exists(exe)) error = "L'exécutable est introuvable.";
        if (error is not null)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_game.SyncItems.Count == 0 &&
            MessageBox.Show(this, "Aucun fichier ou dossier à synchroniser n'a été choisi. Enregistrer quand même ?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // Les chemins d'éléments en {GameDir} suivent automatiquement l'exécutable.
        _game.Name = name;
        _game.ExePath = exe;
        _game.Arguments = ArgsBox.Text.Trim();
        DialogResult = true;
    }
}
