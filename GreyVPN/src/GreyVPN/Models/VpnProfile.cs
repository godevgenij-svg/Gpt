using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GreyVPN.Models;

public sealed class VpnProfile : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _type = "Unknown";
    private string _endpoint = string.Empty;
    private string _transport = string.Empty;
    private string _status = "Ожидание";
    private long? _latencyMs;
    private DateTimeOffset? _lastTested;
    private string _error = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string Transport { get => _transport; set => Set(ref _transport, value); }
    public string SourcePath { get; set; } = string.Empty;
    public string RawValue { get; set; } = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }
    public long? LatencyMs { get => _latencyMs; set => Set(ref _latencyMs, value); }
    public DateTimeOffset? LastTested { get => _lastTested; set => Set(ref _lastTested, value); }
    public string Error { get => _error; set => Set(ref _error, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
