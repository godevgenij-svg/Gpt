using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using GreyVPN.Models;
using GreyVPN.Services;
using Microsoft.Win32;

namespace GreyVPN;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private CancellationTokenSource? _testCts;

    public ObservableCollection<VpnProfile> Profiles { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var stored = await ProfileStore.LoadAsync();
        foreach (var profile in stored)
            Profiles.Add(profile);

        RefreshStatus("Готово");
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _testCts?.Cancel();
        await ProfileStore.SaveAsync(Profiles);
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "VPN configs|*.ovpn;*.conf;*.txt;*.json;*.yaml;*.yml;*.vpn|All files|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await ImportAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку с VPN-конфигурациями",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        await ImportAsync(ProfileImporter.EnumerateSupportedFiles(dialog.SelectedPath));
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        try
        {
            RefreshStatus("Импорт...");
            var imported = await ProfileImporter.ImportFilesAsync(paths);
            var existing = new HashSet<string>(Profiles.Select(BuildUiIdentity), StringComparer.OrdinalIgnoreCase);
            var added = 0;

            foreach (var profile in imported)
            {
                if (existing.Add(BuildUiIdentity(profile)))
                {
                    Profiles.Add(profile);
                    added++;
                }
            }

            await ProfileStore.SaveAsync(Profiles);
            RefreshStatus($"Импортировано новых: {added}. Всего: {Profiles.Count}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshStatus("Ошибка импорта");
        }
    }

    private async void TestSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfilesGrid.SelectedItems.Cast<VpnProfile>().ToList();
        await TestProfilesAsync(selected);
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        await TestProfilesAsync(Profiles.ToList());
    }

    private async Task TestProfilesAsync(IReadOnlyList<VpnProfile> profiles)
    {
        if (profiles.Count == 0)
            return;

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        try
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                RefreshStatus($"Проверка {i + 1}/{profiles.Count}: {profiles[i].Name}");
                await ProfileTester.TestAsync(profiles[i], ct);
            }

            RefreshStatus($"Проверка завершена: {profiles.Count}");
        }
        catch (OperationCanceledException)
        {
            RefreshStatus("Проверка остановлена");
        }
        finally
        {
            await ProfileStore.SaveAsync(Profiles);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfilesGrid.SelectedItems.Cast<VpnProfile>().ToList();
        foreach (var profile in selected)
            Profiles.Remove(profile);

        await ProfileStore.SaveAsync(Profiles);
        RefreshStatus($"Удалено: {selected.Count}. Осталось: {Profiles.Count}");
    }

    private void RefreshStatus(string text)
    {
        StatusText.Text = text;
    }

    private static string BuildUiIdentity(VpnProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.RawValue) && p.RawValue.Contains("://", StringComparison.Ordinal))
            return p.RawValue.Trim();

        return $"{p.SourcePath}|{p.Type}|{p.Name}";
    }
}
