using System.Windows;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Services;

namespace SaveGameCloud.Views;

/// <summary>Petite fenêtre utilisée quand le jeu est lancé depuis un raccourci (--launch).</summary>
public partial class LaunchWindow : Window
{
    private readonly GameEntry _game;
    private bool _running;

    public LaunchWindow(GameEntry game)
    {
        InitializeComponent();
        _game = game;
        Title = $"{game.Name} — SaveGameCloud";
        GameName.Text = game.Name;
        GameIcon.Source = ShellHelpers.GetIcon(game.ResolvedExePath);
        // Lancé depuis le raccourci d'un jeu : la barre des tâches montre l'icône du jeu.
        if (GameIcon.Source is not null) Icon = GameIcon.Source;
        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            // Fermer la fenêtre pendant la partie empêcherait l'envoi de la sauvegarde.
            if (_running)
            {
                e.Cancel = true;
                MessageBox.Show(this, "La sauvegarde sera envoyée vers Google Drive à la fermeture du jeu. Cette fenêtre se fermera ensuite toute seule.",
                    "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _running = true;
        try
        {
            var ui = new WpfLaunchUi(this, s => StatusText.Text = s);
            await AppServices.CreateCoordinator().RunAsync(_game, ui);
        }
        catch (Exception ex)
        {
            Log.Error("Erreur pendant le lancement par raccourci", ex);
            MessageBox.Show(this, ex.Message, "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
        }
        Close();
    }
}
