using System.Windows;
using System.Windows.Threading;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Views;

namespace SaveGameCloud;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info($"Démarrage ({string.Join(' ', e.Args)})");

        // Mode raccourci : « SaveGameCloud.exe --launch <id> » synchronise, lance le jeu puis se ferme.
        var launchIndex = Array.FindIndex(e.Args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        if (launchIndex >= 0 && launchIndex + 1 < e.Args.Length)
        {
            var game = AppServices.Config.Load().Games.FirstOrDefault(g => g.HasId(e.Args[launchIndex + 1]));
            if (game is null)
            {
                MessageBox.Show("Ce jeu n'existe plus dans SaveGameCloud.", "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            MainWindow = new LaunchWindow(game);
        }
        else
        {
            MainWindow = new MainWindow();
        }
        MainWindow.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erreur non gérée", e.Exception);
        MessageBox.Show($"Une erreur inattendue s'est produite :\n{e.Exception.Message}", "SaveGameCloud",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
