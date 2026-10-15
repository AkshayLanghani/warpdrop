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

    static async Task<bool> TryUseFiveGhzAsync(NetworkOperatorTetheringAccessPointConfiguration cfg)
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration", "Band"))
                return false;
            if (!await cfg.IsBandSupportedAsync(TetheringWiFiBand.FiveGigahertz)) return false;
            cfg.Band = TetheringWiFiBand.FiveGigahertz;
            return true;
        }
        catch { return false; }
    }

    static NetworkOperatorTetheringManager? CreateManager(out string why)
    {
        why = "Windows Mobile Hotspot needs a connection to share. Connect the laptop to Wi-Fi or Ethernet.";
        var candidates = new List<ConnectionProfile>();
        if (NetworkInformation.GetInternetConnectionProfile() is { } inet) candidates.Add(inet);
        candidates.AddRange(NetworkInformation.GetConnectionProfiles());

        foreach (var p in candidates)
        {
            try
            {
                var cap = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(p);
                if (cap != TetheringCapability.Enabled)
                {
                    why = cap switch
                    {
                        TetheringCapability.DisabledByGroupPolicy => "Mobile Hotspot is disabled by group policy.",
                        TetheringCapability.DisabledByHardwareLimitation => "This Wi-Fi adapter can't host a hotspot.",
                        TetheringCapability.DisabledDueToUnknownCause => "Mobile Hotspot is unavailable (is Wi-Fi turned on?).",
                        _ => "Mobile Hotspot unavailable: " + cap,
                    };
                    continue;
                }
                return NetworkOperatorTetheringManager.CreateFromConnectionProfile(p);
            }
            catch (Exception ex) { why = ex.Message; }
        }
        return null;
    }

    static string Describe(NetworkOperatorTetheringOperationResult r)
    {
        string msg = r.Status switch
        {
            TetheringOperationStatus.WiFiDeviceOff => "Wi-Fi is off. Turn Wi-Fi on and try again.",
            TetheringOperationStatus.MobileBroadbandDeviceOff => "The shared connection is off.",
            TetheringOperationStatus.OperationInProgress => "Windows is still switching the hotspot. Try again in a moment.",
            _ => "Mobile Hotspot couldn't start (" + r.Status + ").",
        };
        return string.IsNullOrWhiteSpace(r.AdditionalErrorMessage) ? msg : msg + " " + r.AdditionalErrorMessage;
    }

    public static async Task<string> WaitForHotspotIpAsync()
    {
        for (int i = 0; i < 40; i++)
        {
            if (FindHotspotIp() is { } ip) return ip;
            await Task.Delay(250);
        }
        return "192.168.137.1"; // Windows ICS default
    }

    static string? FindHotspotIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string s = a.Address.ToString();
                if (s.StartsWith("192.168.137.")) return s;
                if (ni.Description.Contains("Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("169.254.")) return s;
            }
        }
        return null;
    }

    /// <summary>Negotiated PHY rate of the hotspot's virtual adapter, in bit/s (0 if unknown).</summary>
    public static long HotspotLinkBps()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString().StartsWith("192.168.137.")))
                .Select(ni => ni.Speed).FirstOrDefault();
        }
        catch { return 0; }
    }

    /// <summary>
    /// A physical Ethernet link (e.g. a USB-C Ethernet adapter cabled to an iPhone's USB-C
    /// Ethernet adapter). Both sides self-assign 169.254.x.x, so no setup is needed.
    /// </summary>
    public static (string Ip, long Bps)? FindWiredLink()
    {
        string[] skip = { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wsl", "loopback", "bluetooth", "wi-fi", "wireless", "802.11" };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
                || skip.Any(s => ni.Description.Contains(s, StringComparison.OrdinalIgnoreCase)))
                continue;
            var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ip != null) return (ip.Address.ToString(), ni.Speed);
        }
        return null;
    }

    /// <summary>Best IPv4 address on the current LAN, used when the hotspot can't start.</summary>
    public static string? FindLanIp()
    {
        string[] skip = { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wsl", "loopback", "bluetooth" };
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                         && ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                         && !skip.Any(s => ni.Description.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(ni => ni.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString())
            .FirstOrDefault();
    }

}

/// <summary>
/// The laptop's own Wi-Fi client connection. It shares the one radio with the hotspot, so
/// while it is connected the hotspot is pinned to its channel (or time-sliced against it).
/// Disconnecting it gives the WarpDrop network the whole radio and a free channel choice.
/// </summary>
public static class WifiStation
{
    public sealed record Info(string Interface, bool Connected, string? Profile, string? Band);

    public static Info? Query()
    {
        string output = Netsh("wlan show interfaces");
        string? Get(string key) => output.Split('\n')
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).FirstOrDefault();
        string? name = Get("Name");
        if (name == null) return null;
        return new Info(name, Get("State")?.Equals("connected", StringComparison.OrdinalIgnoreCase) == true, Get("Profile"), Get("Band"));
    }

    public static void Disconnect(string iface) => Netsh($"wlan disconnect interface=\"{iface}\"");

    public static void Connect(string iface, string profile) => Netsh($"wlan connect name=\"{profile}\" interface=\"{iface}\"");

    static string Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch { return ""; }
    }
}
