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

        // Proxy engines do not alter the Windows routing table. Test them first in parallel.
        // WG/AWG/OpenVPN install a real Windows tunnel/adapter and therefore must run strictly
        // alone; otherwise one profile can hijack another profile's HTTPS probe.
        var proxyProfiles = profiles.Where(p => !IsSystemTunnelProfile(p)).ToList();
        var tunnelProfiles = profiles.Where(IsSystemTunnelProfile).ToList();

        DiagnosticsService.Log("REAL", $"Real-test started. Profiles={profiles.Count}; Proxy={proxyProfiles.Count}; SystemTunnel={tunnelProfiles.Count}; ProxyConcurrency={RealTestConcurrency}; TunnelConcurrency=1");

        try
        {
            RefreshStatus($"Real-test 0/{profiles.Count}. Сначала proxy: {proxyProfiles.Count}, затем системные туннели: {tunnelProfiles.Count} по одному.");

            using (var proxyGate = new SemaphoreSlim(RealTestConcurrency))
            {
                var tasks = proxyProfiles.Select(async profile =>
                {
                    await proxyGate.WaitAsync(ct);
                    try
                    {
                        await RealConnectionTester.TestAsync(profile, ct);
                    }
                    finally
                    {
                        proxyGate.Release();
                        var done = Interlocked.Increment(ref completed);
                        await Dispatcher.InvokeAsync(() =>
                            RefreshStatus($"Real-test {done}/{profiles.Count}. Proxy-фаза. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}"));
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }

            foreach (var profile in tunnelProfiles)
            {
                ct.ThrowIfCancellationRequested();
                RefreshStatus($"Real-test {completed}/{profiles.Count}. Системный туннель: {profile.Type} / {profile.Name}");
                try
                {
                    await RealConnectionTester.TestAsync(profile, ct);
                }
                finally
                {
                    completed++;
                    await Dispatcher.InvokeAsync(() =>
                        RefreshStatus($"Real-test {completed}/{profiles.Count}. Туннели по одному. Работают: {profiles.Count(RealConnectionTester.IsRealWorking)}"));
                }
            }

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

    private static bool IsSystemTunnelProfile(VpnProfile profile) =>
        profile.Type.Equals("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase) ||
        profile.Type.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase);
}
