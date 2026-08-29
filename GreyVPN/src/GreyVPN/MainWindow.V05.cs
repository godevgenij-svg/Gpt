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
            .Where(p => ProfileTester.IsRealTestCandidate(p) && RealConnectionTester.Supports(p))
            .ToList();
        await RealTestProfilesV05Async(candidates);
    }

    private async Task RealTestProfilesV05Async(IReadOnlyList<VpnProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            RefreshStatus("Нет поддерживаемых профилей, прошедших предтест достаточно для real-test.");
            return;
        }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        var completed = 0;
        var outcome = "completed";
        using var gate = new SemaphoreSlim(RealTestConcurrency);

        DiagnosticsService.Log("REAL", $"Real-test started. Profiles={profiles.Count}; Concurrency={RealTestConcurrency}");

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
            DiagnosticsService.Log("REAL", $"Real-test completed. Profiles={profiles.Count}; Working={profiles.Count(RealConnectionTester.IsRealWorking)}");
            RefreshStatus($"Real-test завершён: {profiles.Count}. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}");
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            DiagnosticsService.Log("REAL", $"Real-test cancelled. Completed={completed}/{profiles.Count}");
            RefreshStatus($"Real-test остановлен: {completed}/{profiles.Count}");
        }
        catch (Exception ex)
        {
            outcome = "failed";
            DiagnosticsService.Log("REAL", $"Real-test unhandled failure: {ex.GetType().Name}: {ex.Message}");
            RefreshStatus($"Real-test аварийно остановлен: {completed}/{profiles.Count}");
        }
        finally
        {
            _profilesView?.Refresh();
            try { await ProfileStore.SaveAsync(Profiles); }
            catch (Exception ex) { DiagnosticsService.Log("STORE", $"Save after real-test failed: {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                var reportPath = await DiagnosticsService.CreateChatGptReportAsync(Profiles, $"real-test-{outcome}");
                RefreshStatus($"Real-test {completed}/{profiles.Count}. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}. Отчёт: {reportPath}");
            }
            catch (Exception ex)
            {
                DiagnosticsService.Log("REPORT", $"Automatic report failed: {ex.GetType().Name}: {ex.Message}");
                RefreshStatus($"Real-test {completed}/{profiles.Count}. Отчёт создать не удалось.");
            }
        }
    }
}
