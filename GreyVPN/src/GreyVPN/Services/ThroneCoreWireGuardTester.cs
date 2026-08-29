using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using GreyVPN.Models;

namespace GreyVPN.Services;

public static class ThroneCoreWireGuardTester
{
    private static readonly Uri[] HttpsProbes =
    {
        new("https://1.1.1.1/cdn-cgi/trace"),
        new("https://1.0.0.1/cdn-cgi/trace")
    };

    public static bool Supports(VpnProfile profile) => ThroneWireGuardConfigBuilder.Supports(profile);

    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "engines", "throne", "ThroneCore.exe");

    public static async Task TestAsync(VpnProfile profile, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var engineLog = new StringBuilder();
        profile.ExitIp = string.Empty;
        profile.RealError = string.Empty;
        profile.LastRealTested = DateTimeOffset.Now;

        try
        {
            if (!File.Exists(EnginePath))
            {
                profile.RealStatus = "ENGINE ERROR";
                profile.RealError = "ThroneCore.exe не найден в engines\\throne.";
                return;
            }

            var mixedPort = GetFreeTcpPort();
            ThroneWireGuardConfig built;
            try
            {
                built = await ThroneWireGuardConfigBuilder.BuildAsync(profile, mixedPort, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                profile.RealStatus = "DNS ERROR";
                profile.RealError = $"Не удалось разрешить WireGuard endpoint: {ex.Message}";
                return;
            }
            catch (InvalidDataException ex)
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = ex.Message;
                return;
            }

            profile.Type = built.IsAmnezia ? "AmneziaWG" : "WireGuard";
            engineLog.AppendLine($"Mode: headless mixed proxy (no system TUN)");
            engineLog.AppendLine($"Profile type after source inspection: {profile.Type}");
            engineLog.AppendLine($"Resolved endpoint: {built.ResolvedEndpoint}");
            engineLog.AppendLine($"Local mixed proxy: 127.0.0.1:{mixedPort}");

            await using var core = await ThroneCoreSession.StartAsync(EnginePath, ct).ConfigureAwait(false);

            var checkError = await core.CheckConfigAsync(built.Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(checkError))
            {
                profile.RealStatus = "CONFIG ERROR";
                profile.RealError = ShortError(checkError);
                engineLog.AppendLine("CheckConfig: " + checkError);
                return;
            }
            engineLog.AppendLine("CheckConfig: OK");

            var startError = await core.StartConfigAsync(built.Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(startError))
            {
                profile.RealStatus = ClassifyCoreStartError(startError);
                profile.RealError = ShortError(startError);
                engineLog.AppendLine("Start: " + startError);
                return;
            }
            engineLog.AppendLine("Start: OK");

            try
            {
                var probe = await ProbeThroughMixedAsync(mixedPort, ct).ConfigureAwait(false);
                engineLog.AppendLine(probe.Log);
                if (probe.Success)
                {
                    profile.RealStatus = "РАБОТАЕТ";
                    profile.ExitIp = probe.ExitIp;
                    profile.RealError = string.Empty;
                }
                else
                {
                    profile.RealStatus = probe.TimedOut ? "TIMEOUT" : "NO INTERNET";
                    profile.RealError = ShortError(probe.Error);
                }
            }
            finally
            {
                try
                {
                    var stopError = await core.StopConfigAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(stopError)) engineLog.AppendLine("Stop: " + stopError);
                    else engineLog.AppendLine("Stop: OK");
                }
                catch (Exception ex)
                {
                    engineLog.AppendLine($"Stop exception: {ex.GetType().Name}: {ex.Message}");
                }
            }

            engineLog.AppendLine();
            engineLog.AppendLine("ThroneCore stdout/stderr:");
            engineLog.AppendLine(core.GetLog());
        }
        catch (OperationCanceledException)
        {
            profile.RealStatus = "ОТМЕНЕНО";
            profile.RealError = "Real-test отменён.";
            throw;
        }
        catch (TimeoutException ex)
        {
            profile.RealStatus = "TIMEOUT";
            profile.RealError = ShortError(ex.Message);
            engineLog.AppendLine($"Timeout: {ex.Message}");
        }
        catch (Exception ex)
        {
            profile.RealStatus = "ENGINE ERROR";
            profile.RealError = ShortError($"{ex.GetType().Name}: {ex.Message}");
            engineLog.AppendLine($"Unhandled: {ex}");
        }
        finally
        {
            sw.Stop();
            profile.RealTestMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            profile.LastRealTested = DateTimeOffset.Now;
            DiagnosticsService.WriteEngineLog(profile, "ThroneCore", engineLog.ToString(), "wireguard-real-test");
        }
    }

    public static async Task<string> CheckConfigWithCoreAsync(string corePath, string configJson, CancellationToken ct = default)
    {
        await using var core = await ThroneCoreSession.StartAsync(corePath, ct).ConfigureAwait(false);
        return await core.CheckConfigAsync(configJson, ct).ConfigureAwait(false);
    }

    private static async Task<ProbeResult> ProbeThroughMixedAsync(int port, CancellationToken ct)
    {
        var log = new StringBuilder();
        var errors = new List<string>();
        var sawTimeout = false;

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            // The probe sends no credentials or user data. Certificate validation is disabled only
            // because the probe deliberately addresses Cloudflare by literal IP to avoid DNS hiding a good tunnel.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        foreach (var uri in HttpsProbes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                request.Headers.UserAgent.ParseAdd("GreyVPN/0.8-real-test");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                log.AppendLine($"HTTPS probe {uri.Host}: HTTP {(int)response.StatusCode}; bytes={Encoding.UTF8.GetByteCount(body)}");
                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 500)
                {
                    var exitIp = ParseCloudflareTraceIp(body);
                    if (!string.IsNullOrWhiteSpace(exitIp)) log.AppendLine($"Exit IP: {exitIp}");
                    return new ProbeResult(true, false, exitIp, string.Empty, log.ToString());
                }
                errors.Add($"{uri.Host}: HTTP {(int)response.StatusCode}");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                sawTimeout = true;
                errors.Add($"{uri.Host}: timeout ({ex.Message})");
                log.AppendLine($"HTTPS probe {uri.Host}: TIMEOUT");
            }
            catch (Exception ex)
            {
                errors.Add($"{uri.Host}: {ex.GetType().Name}: {ex.Message}");
                log.AppendLine($"HTTPS probe {uri.Host}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var error = errors.Count == 0 ? "HTTPS probe не дал результата." : string.Join(" | ", errors);
        return new ProbeResult(false, sawTimeout, string.Empty, error, log.ToString());
    }

    private static string ParseCloudflareTraceIp(string body)
    {
        foreach (var line in body.Replace("\r", string.Empty).Split('\n'))
        {
            if (!line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[3..].Trim();
            if (IPAddress.TryParse(value, out _)) return value;
        }
        return string.Empty;
    }

    private static string ClassifyCoreStartError(string error)
    {
        if (error.Contains("dns", StringComparison.OrdinalIgnoreCase) || error.Contains("lookup", StringComparison.OrdinalIgnoreCase)) return "DNS ERROR";
        if (error.Contains("invalid", StringComparison.OrdinalIgnoreCase) || error.Contains("parse", StringComparison.OrdinalIgnoreCase) || error.Contains("unknown field", StringComparison.OrdinalIgnoreCase)) return "CONFIG ERROR";
        return "CONNECT ERROR";
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string ShortError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 700 ? oneLine : oneLine[..700] + "…";
    }

    private sealed record ProbeResult(bool Success, bool TimedOut, string ExitIp, string Error, string Log);

    private sealed class ThroneCoreSession : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Process _process;
        private readonly StringBuilder _log = new();
        private readonly object _logSync = new();
        private uint _nextRequestId = 1;

        private ThroneCoreSession(NamedPipeServerStream pipe, Process process)
        {
            _pipe = pipe;
            _process = process;
        }

        public static async Task<ThroneCoreSession> StartAsync(string corePath, CancellationToken ct)
        {
            if (!File.Exists(corePath)) throw new FileNotFoundException("ThroneCore.exe not found", corePath);

            var pipeName = "GreyVPN-ThroneCore-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024);

            var psi = new ProcessStartInfo
            {
                FileName = corePath,
                WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["THRONE_CORE_SOCKET"] = @"\\.\pipe\" + pipeName;
            psi.Environment["THRONE_CORE_DEBUG"] = "false";

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                pipe.Dispose();
                throw new InvalidOperationException("Не удалось запустить ThroneCore.exe.");
            }

            var session = new ThroneCoreSession(pipe, process);
            process.OutputDataReceived += (_, e) => session.AppendLog("OUT", e.Data);
            process.ErrorDataReceived += (_, e) => session.AppendLog("ERR", e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("ThroneCore не подключился к локальному IPC за 10 секунд.");
                }

                if (process.HasExited)
                    throw new InvalidOperationException($"ThroneCore завершился до подключения IPC, exit={process.ExitCode}.");
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<string> CheckConfigAsync(string json, CancellationToken ct)
        {
            var response = await CallAsync("CheckConfig", MiniProto.EncodeStringField(1, json), ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return MiniProto.ReadStringField(response, 1);
        }

        public async Task<string> StartConfigAsync(string json, CancellationToken ct)
        {
            var response = await CallAsync("Start", MiniProto.EncodeStringField(1, json), ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return MiniProto.ReadStringField(response, 1);
        }

        public async Task<string> StopConfigAsync(CancellationToken ct)
        {
            var response = await CallAsync("Stop", Array.Empty<byte>(), ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            return MiniProto.ReadStringField(response, 1);
        }

        public string GetLog()
        {
            lock (_logSync) return _log.ToString();
        }

        private void AppendLog(string stream, string? line)
        {
            if (line is null) return;
            lock (_logSync) _log.Append(DateTimeOffset.Now.ToString("O")).Append(" | ").Append(stream).Append(" | ").AppendLine(line);
        }

        private async Task<byte[]> CallAsync(string method, byte[] payload, CancellationToken ct, TimeSpan timeoutValue)
        {
            if (!_pipe.IsConnected) throw new IOException("ThroneCore IPC disconnected.");
            var id = _nextRequestId++;
            var methodBytes = Encoding.UTF8.GetBytes(method);
            if (methodBytes.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(method));

            var frame = new byte[4 + 2 + methodBytes.Length + 4 + payload.Length];
            var offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset, 4), id); offset += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(offset, 2), (ushort)methodBytes.Length); offset += 2;
            methodBytes.CopyTo(frame.AsSpan(offset)); offset += methodBytes.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset, 4), (uint)payload.Length); offset += 4;
            payload.CopyTo(frame.AsSpan(offset));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutValue);
            try
            {
                await _pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

                var header = new byte[9];
                await _pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var responseId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                var status = header[4];
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5, 4));
                if (responseId != id) throw new InvalidDataException($"ThroneCore IPC response id mismatch: expected {id}, got {responseId}.");
                if (length > 32 * 1024 * 1024) throw new InvalidDataException("ThroneCore IPC response is unexpectedly large.");
                var data = new byte[length];
                if (data.Length > 0) await _pipe.ReadExactlyAsync(data, timeout.Token).ConfigureAwait(false);
                if (status != 0)
                {
                    var coreMessage = data.Length == 0 ? "unknown core error" : Encoding.UTF8.GetString(data);
                    throw new InvalidOperationException($"ThroneCore {method} IPC error: {coreMessage}");
                }
                return data;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"ThroneCore IPC method {method} exceeded {timeoutValue.TotalSeconds:0} seconds.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _pipe.Dispose(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            catch { }
            _process.Dispose();
        }
    }

    private static class MiniProto
    {
        public static byte[] EncodeStringField(int fieldNumber, string value)
        {
            using var ms = new MemoryStream();
            WriteVarint(ms, (ulong)((fieldNumber << 3) | 2));
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteVarint(ms, (ulong)bytes.Length);
            ms.Write(bytes);
            return ms.ToArray();
        }

        public static string ReadStringField(ReadOnlySpan<byte> data, int wantedField)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var tag = ReadVarint(data, ref offset);
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                if (wire == 2)
                {
                    var length = checked((int)ReadVarint(data, ref offset));
                    if (length < 0 || offset + length > data.Length) throw new InvalidDataException("Malformed protobuf length.");
                    if (field == wantedField) return Encoding.UTF8.GetString(data.Slice(offset, length));
                    offset += length;
                    continue;
                }
                SkipField(data, ref offset, wire);
            }
            return string.Empty;
        }

        private static void SkipField(ReadOnlySpan<byte> data, ref int offset, int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(data, ref offset); break;
                case 1: offset = checked(offset + 8); break;
                case 2:
                    var len = checked((int)ReadVarint(data, ref offset));
                    offset = checked(offset + len);
                    break;
                case 5: offset = checked(offset + 4); break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
            if (offset > data.Length) throw new InvalidDataException("Malformed protobuf field.");
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (offset >= data.Length) throw new InvalidDataException("Truncated protobuf varint.");
                var b = data[offset++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("Invalid protobuf varint.");
        }

        private static void WriteVarint(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }
    }
}
