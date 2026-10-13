using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WarpDrop;

public sealed record HotspotResult(bool Ok, string Message, bool FiveGhz);

/// <summary>
/// Drives Windows Mobile Hotspot through the WinRT tethering API. The laptop becomes
/// the access point, so the iPhone talks to it directly with no router in between.
/// </summary>
public sealed class Hotspot
{
    NetworkOperatorTetheringManager? _mgr;
    NetworkOperatorTetheringAccessPointConfiguration? _previous;
    bool _wasOnBefore, _started;

    public bool IsOn
    {
        get { try { return _mgr?.TetheringOperationalState == TetheringOperationalState.On; } catch { return false; } }
    }

    public int ClientCount
    {
        get { try { return (int)(_mgr?.ClientCount ?? 0); } catch { return 0; } }
    }

    public async Task<HotspotResult> StartAsync(string ssid, string password)
    {
        try
        {
            _mgr = CreateManager(out var why);
            if (_mgr == null) return new(false, why, false);

            if (_previous == null)
            {
                _previous = _mgr.GetCurrentAccessPointConfiguration();
                _wasOnBefore = _mgr.TetheringOperationalState == TetheringOperationalState.On;
            }

            var cfg = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = ssid, Passphrase = password };
            // 5 GHz is the single biggest speed lever: typically 4-10x the throughput of 2.4 GHz.
            bool five = await TryUseFiveGhzAsync(cfg);

            // A fresh start lets the driver pick the best channel for the current radio situation.
            if (_mgr.TetheringOperationalState == TetheringOperationalState.On) await _mgr.StopTetheringAsync();
            await _mgr.ConfigureAccessPointAsync(cfg);
            var r = await _mgr.StartTetheringAsync();
            if (r.Status != TetheringOperationStatus.Success && five)
            {
                cfg.Band = TetheringWiFiBand.Auto;
                five = false;
                await _mgr.ConfigureAccessPointAsync(cfg);
                r = await _mgr.StartTetheringAsync();
            }
            if (r.Status != TetheringOperationStatus.Success) return new(false, Describe(r), false);

            _started = true;
            return new(true, five ? "5 GHz" : "auto band", five);
        }
        catch (Exception ex)
        {
            return new(false, "Mobile Hotspot failed: " + ex.Message, false);
        }
    }

    /// <summary>Windows switches the hotspot off when idle; turn it back on.</summary>
    public async Task RestartAsync()
    {
        if (_mgr == null || !_started) return;
        try { await _mgr.StartTetheringAsync(); } catch { }
    }

    /// <summary>Remembers the user's own hotspot setup before WarpDrop touches anything.</summary>
    public void CaptureOriginal()
    {
        if (_previous != null) return;
        try
        {
            if (CreateManager(out _) is not { } m) return;
            _previous = m.GetCurrentAccessPointConfiguration();
            _wasOnBefore = m.TetheringOperationalState == TetheringOperationalState.On;
        }
        catch { }
    }

    public async Task StopAsync()
    {
        if (_mgr == null || !_started) return;
        _started = false;
        try { await _mgr.StopTetheringAsync(); } catch { }
    }

    /// <summary>Puts back the user's own hotspot name/password/band, and turns it on if it was on.</summary>
    public async Task RestoreOriginalAsync()
    {
        if (_previous == null) return;
        try
        {
            if (CreateManager(out _) is { } m)
            {
                await m.ConfigureAccessPointAsync(_previous);
                if (_wasOnBefore) await m.StartTetheringAsync();
            }
        }
        catch { }
        _previous = null;
        _wasOnBefore = false;
    }

