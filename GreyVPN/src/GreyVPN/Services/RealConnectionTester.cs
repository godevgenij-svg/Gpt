using GreyVPN.Models;

namespace GreyVPN.Services;

public static class RealConnectionTester
{
    public static bool Supports(VpnProfile profile) =>
        OpenVpnRealTester.Supports(profile) || SingBoxConfigBuilder.Supports(profile);

    public static bool IsRealWorking(VpnProfile profile) =>
        profile.RealStatus.Equals("РАБОТАЕТ", StringComparison.OrdinalIgnoreCase);

    public static Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        if (OpenVpnRealTester.Supports(profile))
            return OpenVpnRealTester.TestAsync(profile, ct);

        if (SingBoxConfigBuilder.Supports(profile))
            return RealProxyTester.TestAsync(profile, ct);

        profile.RealStatus = "НЕ ПОДДЕРЖАН";
        profile.RealError = "Для этого протокола real-test ещё не реализован.";
        profile.LastRealTested = DateTimeOffset.Now;
        return Task.CompletedTask;
    }
}
