using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WarpDrop;

/// <summary>Band is "6 GHz", "5 GHz" or "auto band" when Ok.</summary>
public sealed record HotspotResult(bool Ok, string Message, string Band);

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

    /// <summary>Whether the adapter/driver accepted a 6 GHz (WPA3) hotspot on the last start.</summary>
    public bool SixGhzSupported { get; private set; }

    const string ConfigType = "Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration";

    /// <summary>
    /// Starts the hotspot on the fastest band the adapter accepts. 6 GHz comes first: it is a clean
    /// band with no legacy neighbours, so a hotspot there keeps far more airtime than on crowded 5 GHz.
    /// 6 GHz requires WPA3 (SAE). Falls back to 5 GHz (WPA2), then to whatever Windows picks.
    /// </summary>
    public async Task<HotspotResult> StartAsync(string ssid, string password, bool allowSixGhz)
    {
        try
        {
            _mgr = CreateManager(out var why);
            if (_mgr == null) return new(false, why, "");

            if (_previous == null)
            {
                _previous = _mgr.GetCurrentAccessPointConfiguration();
                _wasOnBefore = _mgr.TetheringOperationalState == TetheringOperationalState.On;
            }

            var ladder = new List<(TetheringWiFiBand Band, TetheringWiFiAuthenticationKind Auth, string Label)>();
            if (allowSixGhz) ladder.Add((TetheringWiFiBand.SixGigahertz, TetheringWiFiAuthenticationKind.Wpa3, "6 GHz"));
            ladder.Add((TetheringWiFiBand.FiveGigahertz, TetheringWiFiAuthenticationKind.Wpa2, "5 GHz"));
            ladder.Add((TetheringWiFiBand.Auto, TetheringWiFiAuthenticationKind.Wpa2, "auto band"));

            // A fresh start lets the driver pick the best channel for the current radio situation.
            if (_mgr.TetheringOperationalState == TetheringOperationalState.On) await _mgr.StopTetheringAsync();

            SixGhzSupported = false;
            NetworkOperatorTetheringOperationResult? last = null;
            foreach (var (band, auth, label) in ladder)
            {
                var cfg = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = ssid, Passphrase = password };
                string? refused = await TryConfigureAsync(cfg, band, auth);
                if (refused != null)
                {
                    Diag.Log($"Hotspot: skipping {label}: {refused}");
                    continue;
                }
                if (band == TetheringWiFiBand.SixGigahertz) SixGhzSupported = true;
                await _mgr.ConfigureAccessPointAsync(cfg);
                last = await _mgr.StartTetheringAsync();
                if (last.Status == TetheringOperationStatus.Success)
                {
                    _started = true;
                    return new(true, label, label);
                }
                Diag.Log($"Hotspot: {label} would not start ({Describe(last)})");
            }
            return new(false, last != null ? Describe(last) : "No usable Wi-Fi band for Mobile Hotspot.", "");
        }
        catch (Exception ex)
        {
            return new(false, "Mobile Hotspot failed: " + ex.Message, "");
        }
    }

    /// <summary>Applies band + security to the config. Returns null if usable, otherwise why not.</summary>
    static async Task<string?> TryConfigureAsync(NetworkOperatorTetheringAccessPointConfiguration cfg,
        TetheringWiFiBand band, TetheringWiFiAuthenticationKind auth)
    {
        if (band == TetheringWiFiBand.Auto) return null;
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(ConfigType, "Band"))
                return "this Windows version can't choose the hotspot band";
            if (auth != TetheringWiFiAuthenticationKind.Wpa2)
            {
                if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(ConfigType, "AuthenticationKind"))
                    return "this Windows version can't set hotspot security";
                if (!await cfg.IsAuthenticationKindSupportedAsync(auth))
                    return $"the Wi-Fi driver doesn't support {auth} hotspots";
                cfg.AuthenticationKind = auth;
            }
            if (!await cfg.IsBandSupportedAsync(band))
                return "the Wi-Fi adapter/driver doesn't support hosting on this band";
            cfg.Band = band;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Read-only report of which hotspot bands and security modes this PC can host.</summary>
    public static async Task<IReadOnlyList<string>> ProbeAsync()
    {
        var lines = new List<string>();
        var wifi = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        lines.Add("Wi-Fi adapter: " + (wifi?.Description ?? "none found"));
        if (CreateManager(out var why) == null) { lines.Add("Mobile Hotspot: unavailable (" + why + ")"); return lines; }
        try
        {
            foreach (var auth in new[] { TetheringWiFiAuthenticationKind.Wpa2, TetheringWiFiAuthenticationKind.Wpa3TransitionMode, TetheringWiFiAuthenticationKind.Wpa3 })
            {
                var cfg = new NetworkOperatorTetheringAccessPointConfiguration();
                lines.Add($"Security {auth}: {(await cfg.IsAuthenticationKindSupportedAsync(auth) ? "supported" : "not supported")}");
            }
            foreach (var band in new[] { TetheringWiFiBand.TwoPointFourGigahertz, TetheringWiFiBand.FiveGigahertz, TetheringWiFiBand.SixGigahertz })
            {
                var cfg = new NetworkOperatorTetheringAccessPointConfiguration();
                if (band == TetheringWiFiBand.SixGigahertz && await cfg.IsAuthenticationKindSupportedAsync(TetheringWiFiAuthenticationKind.Wpa3))
                    cfg.AuthenticationKind = TetheringWiFiAuthenticationKind.Wpa3;
                lines.Add($"Band {band}: {(await cfg.IsBandSupportedAsync(band) ? "supported" : "not supported")}");
            }
        }
        catch (Exception ex) { lines.Add("Probe failed: " + ex.Message); }
        return lines;
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
