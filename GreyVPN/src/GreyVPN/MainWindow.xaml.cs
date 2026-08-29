using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Data;
using GreyVPN.Models;
using GreyVPN.Services;

namespace GreyVPN;

public partial class MainWindow : Window
{
    private const int TestConcurrency = 8;
    private const int RealTestConcurrency = 2;
    private CancellationTokenSource? _testCts;
    private ICollectionView? _profilesView;

    public ObservableCollection<VpnProfile> Profiles { get; } = new();

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
        {
            ProfileImporter.RefreshParsedFields(profile);
            Profiles.Add(profile);
        }
        _profilesView = CollectionViewSource.GetDefaultView(Profiles);
        RefreshStatus($"Готово. Профилей: {Profiles.Count}");
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _testCts?.Cancel();
        try { ProfileStore.SaveAsync(Profiles).GetAwaiter().GetResult(); } catch { }
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "VPN configs|*.ovpn;*.conf;*.txt;*.json;*.yaml;*.yml;*.vpn|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку с VPN-конфигурациями",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                ProfileImporter.RefreshParsedFields(profile);
                if (existing.Add(BuildUiIdentity(profile)))
                {
                    Profiles.Add(profile);
                    added++;
                }
            }
            await ProfileStore.SaveAsync(Profiles);
            _profilesView?.Refresh();
            RefreshStatus($"Импортировано новых: {added}. Всего: {Profiles.Count}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshStatus("Ошибка импорта");
        }
    }

    private async void TestSelected_Click(object sender, RoutedEventArgs e) =>
        await TestProfilesAsync(ProfilesGrid.SelectedItems.Cast<VpnProfile>().ToList());

    private async void TestAll_Click(object sender, RoutedEventArgs e) =>
        await TestProfilesAsync(Profiles.ToList());

    private async Task TestProfilesAsync(IReadOnlyList<VpnProfile> profiles)
    {
        if (profiles.Count == 0) return;
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        var completed = 0;
        using var gate = new SemaphoreSlim(TestConcurrency);
        try
        {
            RefreshStatus($"Предтест 0/{profiles.Count}. Параллельно: {TestConcurrency}");
            var tasks = profiles.Select(async profile =>
            {
                await gate.WaitAsync(ct);
                try { await ProfileTester.TestAsync(profile, ct); }
                finally
                {
                    gate.Release();
                    var done = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() => RefreshStatus($"Предтест {done}/{profiles.Count}. Отклик: {profiles.Count(ProfileTester.IsRealTestCandidate)}"));
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            RefreshStatus($"Предтест завершён: {profiles.Count}. Допущены к real-test: {profiles.Count(ProfileTester.IsRealTestCandidate)}");
        }
        catch (OperationCanceledException) { RefreshStatus($"Предтест остановлен: {completed}/{profiles.Count}"); }
        finally
        {
            _profilesView?.Refresh();
            await ProfileStore.SaveAsync(Profiles);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _testCts?.Cancel();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfilesGrid.SelectedItems.Cast<VpnProfile>().ToList();
        foreach (var profile in selected) Profiles.Remove(profile);
        await ProfileStore.SaveAsync(Profiles);
        _profilesView?.Refresh();
        RefreshStatus($"Удалено: {selected.Count}. Осталось: {Profiles.Count}");
    }

    private void ShowResponsive_Click(object sender, RoutedEventArgs e)
    {
        _profilesView ??= CollectionViewSource.GetDefaultView(Profiles);
        _profilesView.Filter = item => item is VpnProfile p && ProfileTester.IsRealTestCandidate(p);
        _profilesView.Refresh();
        RefreshStatus($"Показаны допущенные к real-test: {Profiles.Count(ProfileTester.IsRealTestCandidate)}");
    }

    private void ShowRealWorking_Click(object sender, RoutedEventArgs e)
    {
        _profilesView ??= CollectionViewSource.GetDefaultView(Profiles);
        _profilesView.Filter = item => item is VpnProfile p && RealConnectionTester.IsRealWorking(p);
        _profilesView.Refresh();
        RefreshStatus($"Показаны реально рабочие: {Profiles.Count(RealConnectionTester.IsRealWorking)}");
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _profilesView ??= CollectionViewSource.GetDefaultView(Profiles);
        _profilesView.Filter = null;
        _profilesView.Refresh();
        RefreshStatus($"Показаны все: {Profiles.Count}");
    }

    private void ExportResponsive_Click(object sender, RoutedEventArgs e)
    {
        var responsive = Profiles.Where(ProfileTester.IsRealTestCandidate).ToList();
        if (responsive.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Нет профилей, допущенных к real-test.", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ZIP archive|*.zip",
            FileName = $"GreyVPN_candidates_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ExportResponsiveZip(dialog.FileName, responsive);
            RefreshStatus($"Экспортировано: {responsive.Count}; реально рабочие среди них: {responsive.Count(RealConnectionTester.IsRealWorking)}");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void ExportResponsiveZip(string zipPath, IReadOnlyList<VpnProfile> profiles)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var proxyLinks = new StringBuilder();
        var manifest = new StringBuilder("Name\tType\tEndpoint\tTransport\tPreStatus\tTCP_ms\tICMP_ms\tRealStatus\tExitIP\tReal_ms\tRealError\r\n");
        var usedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var p in profiles)
        {
            manifest.Append(CleanTsv(p.Name)).Append('\t').Append(CleanTsv(p.Type)).Append('\t').Append(CleanTsv(p.Endpoint)).Append('\t').Append(CleanTsv(p.Transport)).Append('\t')
                .Append(CleanTsv(p.Status)).Append('\t').Append(p.TcpConnectMs).Append('\t').Append(p.PingMs).Append('\t')
                .Append(CleanTsv(p.RealStatus)).Append('\t').Append(CleanTsv(p.ExitIp)).Append('\t').Append(p.RealTestMs).Append('\t')
                .Append(CleanTsv(p.RealError)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(p.RawValue) && p.RawValue.Contains("://", StringComparison.Ordinal))
            {
                proxyLinks.AppendLine(p.RawValue.Trim());
                continue;
            }
            if (!string.IsNullOrWhiteSpace(p.SourcePath) && File.Exists(p.SourcePath))
            {
                var folder = SanitizeFileName(p.Type);
                var file = SanitizeFileName(Path.GetFileName(p.SourcePath));
                archive.CreateEntryFromFile(p.SourcePath, MakeUniqueEntry($"{folder}/{file}", usedEntries), CompressionLevel.Optimal);
            }
        }
        WriteTextEntry(archive, "candidates_manifest.tsv", manifest.ToString());
        if (proxyLinks.Length > 0) WriteTextEntry(archive, "proxy_links.txt", proxyLinks.ToString());
    }

    private static string CleanTsv(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string MakeUniqueEntry(string desired, HashSet<string> used)
    {
        if (used.Add(desired)) return desired;
        var directory = Path.GetDirectoryName(desired)?.Replace('\\', '/') ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; ; i++)
        {
            var file = $"{baseName}_{i}{ext}";
            var candidate = string.IsNullOrEmpty(directory) ? file : $"{directory}/{file}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private void RefreshStatus(string text) => StatusText.Text = text;

    private static string BuildUiIdentity(VpnProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.RawValue) && p.RawValue.Contains("://", StringComparison.Ordinal)) return p.RawValue.Trim();
        return $"{p.SourcePath}|{p.Type}|{p.Name}";
    }
}
