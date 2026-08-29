using GreyVPN.Models;

namespace GreyVPN.Services;

public static class RealConnectionTester
{
    public static bool Supports(VpnProfile profile) =>
        ThroneCoreWireGuardTester.Supports(profile) ||
        OpenVpnRealTester.Supports(profile) ||
        XrayRealTester.Supports(profile) ||
        SingBoxConfigBuilder.Supports(profile);

    public static bool IsRealWorking(VpnProfile profile) =>
        profile.RealStatus.Equals("РАБОТАЕТ", StringComparison.OrdinalIgnoreCase);

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        DiagnosticsService.Log("REAL", "Profile real-test start", profile);
        try
        {
            // v0.8: standard WireGuard and AmneziaWG use ThroneCore's current sing-box fork.
            // The core is started headlessly with only a loopback mixed proxy; no system TUN/routes/DNS are changed.
            if (ThroneCoreWireGuardTester.Supports(profile))
            {
                await ThroneCoreWireGuardTester.TestAsync(profile, ct);
                return;
            }

            if (OpenVpnRealTester.Supports(profile))
            {
                await OpenVpnRealTester.TestAsync(profile, ct);
                return;
            }

            if (XrayRealTester.Supports(profile))
            {
                await XrayRealTester.TestAsync(profile, ct);
                return;
            }

            if (SingBoxConfigBuilder.Supports(profile))
            {
                await RealProxyTester.TestAsync(profile, ct);
                return;
            }

            profile.RealStatus = "НЕ ПОДДЕРЖАН";
            profile.RealError = "Для этого протокола real-test ещё не реализован.";
            profile.LastRealTested = DateTimeOffset.Now;
        }
        finally
        {
            DiagnosticsService.Log(
                "REAL",
                $"Profile real-test end. Status={profile.RealStatus}; ExitIP={profile.ExitIp}; RealMs={profile.RealTestMs}; Error={profile.RealError}",
                profile);
        }
    }
}
