using PodBridge.Core.Bluetooth;
using PodBridge.Core.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace PodBridge.Windows;

/// <summary>
/// WinRT implementation of <see cref="IConnectionMonitor"/> for paired AirPods.
/// Combines a <see cref="DeviceWatcher"/> over paired Bluetooth-Classic
/// association endpoints (pairing/presence) with a held <see cref="BluetoothDevice"/>
/// per matched device (authoritative connect/disconnect via
/// <see cref="BluetoothDevice.ConnectionStatusChanged"/>). No Bluetooth radio →
/// <see cref="ConnectionStatus.BluetoothUnavailable"/>, never a crash.
/// Tier 1: no driver, no admin (<c>asInvoker</c>). API choices per docs/research/connection-detection.md.
/// </summary>
/// <remarks>
/// A held <see cref="BluetoothDevice"/> reference is required or its
/// <see cref="BluetoothDevice.ConnectionStatusChanged"/> event stops firing once
/// the object is garbage-collected (research footgun). Radio enumeration is only
/// valid when the process architecture matches the OS — ship x64/ARM64, never x86.
/// </remarks>
public sealed class WinRtConnectionMonitor : IConnectionMonitor, IDisposable
{
    // System.Devices.Aep.IsConnected carries the initial + live connection state;
    // System.Devices.Aep.DeviceAddress lets callers match a specific device.
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.DeviceAddress",
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, BluetoothDevice> _devices = new(StringComparer.Ordinal);

    private DeviceWatcher? _watcher;
    private bool _radioUnavailable;
    private bool _enumerationCompleted;
    private ConnectionStatus _currentStatus = ConnectionStatus.Unknown;

    /// <inheritdoc />
    public ConnectionStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _currentStatus;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_watcher is not null)
        {
            return;
        }

        if (!await IsBluetoothAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _radioUnavailable = true;
            }

            RecomputeAndRaise();
            return;
        }

        StartWatcher();
    }

    /// <inheritdoc />
    public void Stop()
    {
        DeviceWatcher? watcher;
        List<BluetoothDevice> devices;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            devices = [.. _devices.Values];
            _devices.Clear();
            _enumerationCompleted = false;
        }

        DetachWatcher(watcher);
        foreach (var device in devices)
        {
            ReleaseDevice(device);
        }
    }

    /// <summary>Releases the watcher subscription and all held device references.</summary>
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Windows' WinRT surface has no direct "connect" call for a paired
    /// Bluetooth-Classic device (docs/research/connection-detection.md covers only
    /// status detection). The documented-in-practice way to force the OS to bring a
    /// paired-but-idle link up — the same mechanism Settings' own "Connect" button
    /// relies on — is to query the device's RFCOMM services with
    /// <see cref="BluetoothCacheMode.Uncached"/>: an uncached query runs a live SDP
    /// lookup over the air, which requires (and therefore triggers) the underlying
    /// ACL connection. We discard the query result; only the side effect matters.
    /// </remarks>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        List<BluetoothDevice> devices;
        lock (_gate)
        {
            devices = [.. _devices.Values];
        }

        if (devices.Count == 0)
        {
            return false; // never paired / not yet enumerated — caller falls back to Settings.
        }

        var connected = false;
        foreach (var device in devices)
        {
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                connected = true;
                continue;
            }

            try
            {
                await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed/timed-out SDP query is a normal "still out of range or off"
                // outcome, not a crash — try the next tracked device.
                continue;
            }

            connected |= device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }

        return connected;
    }

    private void StartWatcher()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var watcher = DeviceInformation.CreateWatcher(
            selector,
            RequestedProperties,
            DeviceInformationKind.AssociationEndpoint);

        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved; // must be non-null or the watcher may not start
        watcher.EnumerationCompleted += OnEnumerationCompleted;

        _watcher = watcher;
        watcher.Start();
    }

    private void DetachWatcher(DeviceWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;

        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }

    private async void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            await TryTrackDeviceAsync(info).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A single device callback must never crash the monitor (graceful degradation).
        }
    }

    private async Task TryTrackDeviceAsync(DeviceInformation info)
    {
        if (!AirPodsNameHeuristic.IsMatch(info.Name))
        {
            return;
        }

        var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask().ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        bool tracked;
        lock (_gate)
        {
            // Stop()/Dispose() may have run during the await above; don't resurrect
            // tracking after teardown or the held reference would leak.
            tracked = _watcher is not null && _devices.TryAdd(info.Id, device);
        }

        if (tracked)
        {
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            RecomputeAndRaise();
        }
        else
        {
            device.Dispose();
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        BluetoothDevice? device = null;
        lock (_gate)
        {
            if (_devices.Remove(update.Id, out var removed))
            {
                device = removed;
            }
        }

        if (device is not null)
        {
            ReleaseDevice(device);
        }

        RecomputeAndRaise();
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => RecomputeAndRaise();

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        lock (_gate)
        {
            _enumerationCompleted = true;
        }

        RecomputeAndRaise();
    }

    // The event args is a bare object/IInspectable; the authoritative state is
    // sender.ConnectionStatus, read during the recompute below (research decision).
    private void OnConnectionStatusChanged(BluetoothDevice sender, object args) => RecomputeAndRaise();

    private void ReleaseDevice(BluetoothDevice device)
    {
        device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        device.Dispose();
    }

    private void RecomputeAndRaise()
    {
        ConnectionStatus computed;
        ConnectionStatus previous;
        lock (_gate)
        {
            computed = ComputeStatusLocked();
            previous = _currentStatus;
            _currentStatus = computed;
        }

        if (computed != previous)
        {
            StatusChanged?.Invoke(this, computed);
        }
    }

    private ConnectionStatus ComputeStatusLocked()
    {
        if (_radioUnavailable)
        {
            return ConnectionStatus.BluetoothUnavailable;
        }

        if (_devices.Count == 0)
        {
            return _enumerationCompleted ? ConnectionStatus.NoDevice : ConnectionStatus.Unknown;
        }

        foreach (var device in _devices.Values)
        {
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                return ConnectionStatus.Connected;
            }
        }

        return ConnectionStatus.Disconnected;
    }

    private static async Task<bool> IsBluetoothAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var radio in radios)
            {
                if (radio.Kind == RadioKind.Bluetooth)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Radio enumeration can fault on some hosts; treat as "Bluetooth unavailable", never crash.
        }

        return false;
    }
}
