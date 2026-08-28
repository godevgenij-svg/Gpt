using GreyVPN.Models;
using GreyVPN.Services;
using System.Windows;

namespace GreyVPN;

public partial class MainWindow
{
    private async void RealTestSelectedV05_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfilesGrid.SelectedItems.Cast<VpnProfile>()
            .Where(RealConnectionTester.Supports)
            .ToList();
        await RealTestProfilesV05Async(selected);
    }

    private async void RealTestResponsiveV05_Click(object sender, RoutedEventArgs e)
    {
        var candidates = Profiles
            .Where(p => ProfileTester.IsResponsive(p) && RealConnectionTester.Supports(p))
            .ToList();
        await RealTestProfilesV05Async(candidates);
    }

    private async Task RealTestProfilesV05Async(IReadOnlyList<VpnProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            RefreshStatus("Нет поддерживаемых откликнувшихся профилей для real-test.");
            return;
        }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        var completed = 0;
        using var gate = new SemaphoreSlim(RealTestConcurrency);

        try
        {
            RefreshStatus($"Real-test 0/{profiles.Count}. Параллельно: {RealTestConcurrency}; OpenVPN выполняется по одному.");
            var tasks = profiles.Select(async profile =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await RealConnectionTester.TestAsync(profile, ct);
                }
                finally
                {
                    gate.Release();
                    var done = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                        RefreshStatus($"Real-test {done}/{profiles.Count}. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}"));
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            RefreshStatus($"Real-test завершён: {profiles.Count}. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}");
        }
        catch (OperationCanceledException)
        {
            RefreshStatus($"Real-test остановлен: {completed}/{profiles.Count}");
        }
        finally
        {
            _profilesView?.Refresh();
            await ProfileStore.SaveAsync(Profiles);
        }
    }
}
