using System.Windows;
using SaveGameCloud.Core.Launch;
using SaveGameCloud.Core.Models;
using SaveGameCloud.Core.Sync;

namespace SaveGameCloud.Views;

public partial class ConflictWindow : Window
{
    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

    public ConflictWindow(GameEntry game, SyncAnalysis analysis)
    {
        InitializeComponent();
        HeaderText.Text = game.Name;
        LocalDate.Text = Format(analysis.LocalLatestUtc);
        LocalDetails.Text = $"{analysis.LocalFileCount} fichier(s)";
        CloudDate.Text = Format(analysis.CloudLatestUtc);
        CloudDetails.Text = $"{analysis.CloudFileCount} fichier(s)" +
                            (analysis.CloudInfo is { } info ? $"\nEnvoyée depuis {info.Machine} le {info.UploadedUtc.ToLocalTime():g}" : "");
        if (analysis.DifferentFileCount > 0) LocalDetails.Text += $" — {analysis.DifferentFileCount} différent(s)";
    }

    private static string Format(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss") : "Aucun fichier";

    private void UseCloud_Click(object sender, RoutedEventArgs e) => Close(ConflictChoice.UseCloud);
    private void UseLocal_Click(object sender, RoutedEventArgs e) => Close(ConflictChoice.UseLocal);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(ConflictChoice.Cancel);

    private void Close(ConflictChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ConflictChoice.Cancel;
    }
}
