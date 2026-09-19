using PodBridge.Core.Bluetooth;
using PodBridge.Core.Models;

namespace PodBridge.Core.Tests.Bluetooth;

/// <summary>
/// Device-independent stand-in for a real connection source (the WinRt adapter),
/// mapping simulated connect / disconnect / radio signals onto
/// <see cref="ConnectionStatus"/> and raising <see cref="StatusChanged"/> only on
/// an actual change — the same contract <c>WinRtConnectionMonitor</c> honours.
/// Lets the Tier-1 test gate run with no physical AirPods.
/// </summary>
internal sealed class FakeConnectionMonitor : IConnectionMonitor
{
    public ConnectionStatus CurrentStatus { get; private set; } = ConnectionStatus.Unknown;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public bool IsStarted { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        return Task.CompletedTask;
    }

    public void Stop() => IsStarted = false;

    /// <summary>
    /// Test seam for <see cref="ReconnectAsync"/>: what the next call should report,
    /// and whether it should also simulate the device coming online (mirroring
    /// <c>WinRtConnectionMonitor</c> firing <c>StatusChanged</c> as a side effect of a
    /// successful reconnect).
    /// </summary>
    public bool NextReconnectResult { get; set; }

    public int ReconnectCallCount { get; private set; }

    public Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ReconnectCallCount++;
        if (NextReconnectResult)
        {
            Apply(ConnectionStatus.Connected);
        }

        return Task.FromResult(NextReconnectResult);
    }

    /// <summary>Simulate a paired device becoming connected.</summary>
    public void SimulateConnected() => Apply(ConnectionStatus.Connected);

    /// <summary>Simulate a paired device disconnecting.</summary>
    public void SimulateDisconnected() => Apply(ConnectionStatus.Disconnected);

    /// <summary>Simulate no paired AirPods device being present.</summary>
    public void SimulateNoDevice() => Apply(ConnectionStatus.NoDevice);

    /// <summary>Simulate the Bluetooth radio being absent/unavailable.</summary>
    public void SimulateBluetoothUnavailable() => Apply(ConnectionStatus.BluetoothUnavailable);

    private void Apply(ConnectionStatus status)
    {
        if (CurrentStatus == status)
        {
            return;
        }

        CurrentStatus = status;
        StatusChanged?.Invoke(this, status);
    }
}
