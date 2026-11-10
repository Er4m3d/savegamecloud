using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SaveGameCloud.Core.Infrastructure;
using SaveGameCloud.Views;

namespace SaveGameCloud;

public partial class App : Application
{
    /// <summary>Identité de l'appli pour la barre des tâches Windows.</summary>
    public const string AppUserModelId = "Er4m3d.SaveGameCloud";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Mode raccourci : « SaveGameCloud.exe --launch <id> » synchronise, lance le jeu puis se ferme.
        var launchIndex = Array.FindIndex(e.Args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        var launchId = launchIndex >= 0 && launchIndex + 1 < e.Args.Length ? e.Args[launchIndex + 1] : null;

        // Sans identité explicite, Windows rattache la fenêtre à n'importe quel raccourci qui vise
        // SaveGameCloud.exe (par exemple le raccourci bureau d'un jeu) et en reprend l'icône dans la barre des tâches.
        SetAppUserModelId(launchId is null ? AppUserModelId : $"{AppUserModelId}.Game.{launchId}");

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info($"Démarrage ({string.Join(' ', e.Args)})");

        if (launchId is not null)
        {
            var game = AppServices.Config.Load().Games.FirstOrDefault(g => g.HasId(launchId));
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

    private static void SetAppUserModelId(string id)
    {
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(id);
            if (hr != 0) Log.Warn($"AppUserModelID non défini (0x{hr:X8})");
        }
        catch (Exception ex)
        {
            Log.Warn($"AppUserModelID non défini : {ex.Message}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erreur non gérée", e.Exception);
        MessageBox.Show($"Une erreur inattendue s'est produite :\n{e.Exception.Message}", "SaveGameCloud",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
