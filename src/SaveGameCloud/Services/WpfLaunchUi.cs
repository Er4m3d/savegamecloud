using System.Windows;
using SaveGameCloud.Core.Launch;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;
using SaveGameCloud.Views;

namespace SaveGameCloud.Services;

/// <summary>Implémentation WPF des interactions pendant un lancement.</summary>
public sealed class WpfLaunchUi(Window owner, Action<string> status) : ILaunchUi
{
    public void ReportStatus(string message) => OnUi(() => status(message));

    public Task<ConflictChoice> ResolveConflictAsync(GameEntry game, SyncAnalysis analysis) =>
        OnUiAsync(() =>
        {
            Restore();
            var dialog = new ConflictWindow(game, analysis) { Owner = owner };
            dialog.ShowDialog();
            return dialog.Choice;
        });

    public Task<bool> ConfirmAsync(string title, string message) =>
        OnUiAsync(() =>
        {
            Restore();
            return MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        });

    public void ShowError(string message) =>
        OnUi(() =>
        {
            Restore();
            MessageBox.Show(owner, message, "SaveGameCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

    public void GameStarted(GameEntry game) => OnUi(() => owner.WindowState = WindowState.Minimized);

    public void GameExited(GameEntry game) => OnUi(Restore);

    private void Restore()
    {
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();
    }

    private void OnUi(Action action)
    {
        if (owner.Dispatcher.CheckAccess()) action();
        else owner.Dispatcher.Invoke(action);
    }

    private Task<T> OnUiAsync<T>(Func<T> func) =>
        owner.Dispatcher.CheckAccess() ? Task.FromResult(func()) : owner.Dispatcher.InvokeAsync(func).Task;
}
