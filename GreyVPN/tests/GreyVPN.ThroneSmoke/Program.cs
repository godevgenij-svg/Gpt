using System.Text;
using System.Text.Json;
using GreyVPN.Services;

if (args.Length != 1) throw new ArgumentException("Usage: GreyVPN.ThroneSmoke <ThroneCore.exe>");
var core = Path.GetFullPath(args[0]);
if (!File.Exists(core)) throw new FileNotFoundException("ThroneCore.exe", core);

var privateKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
var publicKey = Convert.ToBase64String(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());

var wg = $"""
[Interface]
PrivateKey = {privateKey}
Address = 10.77.0.2/32
MTU = 1280

[Peer]
PublicKey = {publicKey}
Endpoint = 198.51.100.10:51820
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
""";

var awg = $"""
[Interface]
PrivateKey = {privateKey}
Address = 10.88.0.2/32
Jc = 3
Jmin = 40
Jmax = 70
S1 = 12
S2 = 18
S3 = 20
S4 = 22
H1 = 11111111
H2 = 22222222
H3 = 33333333
H4 = 44444444
I1 = alpha
I2 = beta
I3 = gamma
I4 = delta
I5 = epsilon
HeaderProtectionKey = hp-test
ContentPaddingAddition = 1-8
RekeyAfterTime = 120
RekeyTimeout = 5-10
RejectAfterTime = 180
KeepaliveTimeout = 15
MaxHandshakeAttempts = 8
RandomTrailers = true
DisableCookies = 1

[Peer]
PublicKey = {publicKey}
Endpoint = 198.51.100.11:51820
AllowedIPs = 0.0.0.0/0
""";

Must(!ThroneWireGuardConfigBuilder.LooksLikeAmnezia(wg), "plain WireGuard classification");
Must(ThroneWireGuardConfigBuilder.LooksLikeAmnezia(awg), "AmneziaWG classification");

var wgBuilt = await ThroneWireGuardConfigBuilder.BuildFromTextAsync(wg, 39001, false);
var awgBuilt = await ThroneWireGuardConfigBuilder.BuildFromTextAsync(awg, 39002, false);
Must(!wgBuilt.IsAmnezia, "plain WG builder verdict");
Must(awgBuilt.IsAmnezia, "AWG builder verdict");

using (var wgDoc = JsonDocument.Parse(wgBuilt.Json))
{
    var endpoint = wgDoc.RootElement.GetProperty("endpoints")[0];
    Must(!endpoint.TryGetProperty("amnezia_wg", out _), "plain WG must not contain amnezia_wg");
    Must(endpoint.GetProperty("system").ValueKind == JsonValueKind.False, "headless WG is userspace");
}

using (var awgDoc = JsonDocument.Parse(awgBuilt.Json))
{
    var endpoint = awgDoc.RootElement.GetProperty("endpoints")[0];
    var am = endpoint.GetProperty("amnezia_wg");
    Must(am.GetProperty("jc").GetInt32() == 3, "AWG jc mapping");
    Must(am.GetProperty("s4").GetInt32() == 22, "AWG s4 mapping");
    Must(am.GetProperty("h4").GetString() == "44444444", "AWG h4 mapping");
    Must(am.GetProperty("i5").GetString() == "epsilon", "AWG i5 mapping");
    Must(am.GetProperty("header_protection_key").GetString() == "hp-test", "AWG header protection mapping");
    Must(am.GetProperty("content_padding_addition").GetString() == "1-8", "AWG range mapping");
    Must(am.GetProperty("rekey_after_time").GetInt32() == 120, "AWG numeric range mapping");
    Must(am.GetProperty("random_trailers").GetBoolean(), "AWG bool mapping");
    Must(am.GetProperty("disable_cookies").GetBoolean(), "AWG bool 1 mapping");
}

var wgCheck = await ThroneCoreWireGuardTester.CheckConfigWithCoreAsync(core, wgBuilt.Json);
if (!string.IsNullOrWhiteSpace(wgCheck)) throw new Exception("ThroneCore rejected generated WireGuard config: " + wgCheck);
Console.WriteLine("OK ThroneCore WireGuard CheckConfig over named-pipe IPC");

// Some newer AWG extension fields may be unavailable in older sing-box schemas. The pinned ThroneCore
// must at least accept the common AWG parameters used by existing Amnezia configs.
var awgCommon = $"""
[Interface]
PrivateKey = {privateKey}
Address = 10.89.0.2/32
Jc = 3
Jmin = 40
Jmax = 70
S1 = 12
S2 = 18
H1 = 11111111
H2 = 22222222
H3 = 33333333
H4 = 44444444

[Peer]
PublicKey = {publicKey}
Endpoint = 198.51.100.12:51820
AllowedIPs = 0.0.0.0/0
""";
var awgCommonBuilt = await ThroneWireGuardConfigBuilder.BuildFromTextAsync(awgCommon, 39003, false);
var awgCheck = await ThroneCoreWireGuardTester.CheckConfigWithCoreAsync(core, awgCommonBuilt.Json);
if (!string.IsNullOrWhiteSpace(awgCheck)) throw new Exception("ThroneCore rejected generated AmneziaWG config: " + awgCheck);
Console.WriteLine("OK ThroneCore AmneziaWG CheckConfig over named-pipe IPC");
Console.WriteLine("ALL THRONECORE SMOKE TESTS PASSED");

static void Must(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("OK " + name);
}
