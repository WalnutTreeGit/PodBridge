using PodBridge.Core.Models;

namespace PodBridge.Core.Bluetooth;

/// <summary>
/// Detects whether a paired AirPods device is present and connected on the host,
/// and raises live connect/disconnect transitions. OS-free abstraction (Tier 1:
/// no driver, no admin); implemented on Windows via WinRT in
/// <c>WinRtConnectionMonitor</c>. Consumers must handle
/// <see cref="ConnectionStatus.BluetoothUnavailable"/> gracefully (no radio).
/// </summary>
public interface IConnectionMonitor
{
    /// <summary>The most recently observed aggregate connection status.</summary>
    ConnectionStatus CurrentStatus { get; }

    /// <summary>Raised when <see cref="CurrentStatus"/> changes; the argument is the new status.</summary>
    event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>
    /// Begins monitoring and determines the initial status. Idempotent: a second
    /// call while already running is a no-op.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring and releases any OS resources held.</summary>
    void Stop();

    /// <summary>
    /// Attempts to bring an already-paired AirPods device's connection up, without
    /// sending the user to Windows Bluetooth settings. Windows exposes no direct
    /// "connect" call for a paired Bluetooth-Classic device; implementations use
    /// whatever OS-level trick forces the link up (see <c>WinRtConnectionMonitor</c>).
    /// Returns <see langword="true"/> if a tracked device ends up connected,
    /// <see langword="false"/> if nothing is tracked yet (never paired) or the
    /// attempt did not bring the link up (out of range, radio off, etc.) — never
    /// throws; callers should treat a <see langword="false"/> result as a normal,
    /// user-actionable outcome, not an error.
    /// </summary>
    Task<bool> ReconnectAsync(CancellationToken cancellationToken = default);
}
