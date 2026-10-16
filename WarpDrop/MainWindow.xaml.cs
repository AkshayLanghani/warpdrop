using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WarpDrop;

public partial class MainWindow : Window
{
    static readonly Brush Green = Frozen("#34D399"), Amber = Frozen("#F59E0B"), Red = Frozen("#F87171");
    static readonly Brush Accent = new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString("#4F8CFF"), (Color)ColorConverter.ConvertFromString("#22D3EE"), 0);

    readonly Settings _settings = Settings.Load();
    readonly FastServer _server;
    readonly Hotspot _hotspot = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly ObservableCollection<SharedFile> _files = new();
    readonly ObservableCollection<TransferRow> _rows = new();
    bool _closing, _cleaned, _restarting;
    double _peak;
    int _ticks;

    public MainWindow()
    {
        InitializeComponent();
        _server = new FastServer(_settings.ReceiveDir);
        // Leftovers from uploads that never finished in an earlier run (nothing can be in flight yet).
        try
        {
            foreach (var part in Directory.EnumerateFiles(_settings.ReceiveDir, "*.warpdrop-part"))
                try { File.Delete(part); Diag.Log("Removed unfinished upload " + Path.GetFileName(part)); } catch { }
        }
        catch { }
        FilesList.ItemsSource = _files;
        ActivityList.ItemsSource = _rows;
        ReceiveDirText.Text = _settings.ReceiveDir;
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) => Startup();
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")))
            AddPath(arg);
    }

    void Startup()
    {
        _server.Start(80, 8080, 8888, 5080, 0);
        _timer.Start();

        if (App.LanOnly)
        {
            // Test/fallback mode: serve on whatever network the laptop is already on.
            NetSwitch.Visibility = Visibility.Collapsed;
            _wifiIp = Hotspot.FindLanIp() ?? "127.0.0.1";
            Step1Label.Text = "STEP 1 · SAME WI-FI";
            WifiQrPlaceholder.Text = "LAN mode: put the iPhone on the same Wi-Fi as this PC.";
            ShowUrl(_wifiIp);
            SetStatus("LAN mode · " + _wifiIp, Amber);
            return;
        }
        ShowOff();
    }

    // ---------- the master switch ----------
    // OFF: WarpDrop leaves the laptop's Wi-Fi, internet and hotspot completely alone.
    // ON:  boost Windows (guardian), disconnect the laptop's Wi-Fi so the radio is dedicated,
    //      and create the separate WarpDrop network. OFF again restores everything.

    readonly SessionGuardian _guardian = new();
    WifiStation.Info? _station; // the laptop Wi-Fi connection we took down, to restore later
    bool _on, _busy, _dedicated;

    async void OnNetSwitch(object sender, RoutedEventArgs e)
    {
        if (_busy) { NetSwitch.IsChecked = _on; return; }
        _busy = true;
        NetSwitch.IsEnabled = false;
        try
        {
            if (NetSwitch.IsChecked == true) await TurnOnAsync();
            else await TurnOffAsync();
        }
        finally
        {
            _busy = false;
            NetSwitch.IsEnabled = true;
            NetSwitch.IsChecked = _on;
            SwitchLabel.Text = _on ? "WarpDrop network ON" : "WarpDrop network OFF";
        }
    }

    async Task TurnOnAsync()
    {
        SwitchLabel.Text = "Turning on…";
        Diag.Log("==== Switch ON");
        _hotspot.CaptureOriginal();
        var st = await Task.Run(WifiStation.Query);
        _station = st is { Connected: true, Profile: not null } ? st : null;
        Diag.Log($"Laptop Wi-Fi before: connected={st?.Connected} profile={st?.Profile} band={st?.Band}");

        bool boosted = false;
        if (!App.NoFirewall)
        {
            SetStatus("Boosting Windows for max throughput: approve the admin prompt…", Amber);
            var g = await _guardian.StartAsync(_station);
            boosted = g.Ok;
            TuneText.Text = g.Ok
                ? $"✓ Max-performance mode on ({g.Changes.Count} setting{(g.Changes.Count == 1 ? "" : "s")} boosted), restored when you switch off"
                : "⚠ " + g.Error;
            TuneText.ToolTip = g.Changes.Count > 0 ? string.Join("\n", g.Changes) : null;
            TuneText.Foreground = g.Ok ? Green : Amber;
            Diag.Log($"Boost: ok={g.Ok} adapterRestart={g.AdapterRestarted} error={g.Error} changes=[{string.Join("; ", g.Changes)}]");
        }

        // Dedicate the radio: take the laptop off its own Wi-Fi and wait until it's really off,
        // so the WarpDrop network picks its own channel instead of inheriting one.
        SetStatus("Dedicating the Wi-Fi radio to WarpDrop…", Amber);
        for (int i = 0; i < 12; i++)
        {
            st = await Task.Run(WifiStation.Query);
            if (st is not { Connected: true }) break;
            if (st.Profile != null) _station ??= st;
            await Task.Run(() => WifiStation.Disconnect(st.Interface));
            await Task.Delay(500);
        }
        await Task.Delay(1000);
        st = await Task.Run(WifiStation.Query);
        Diag.Log($"Laptop Wi-Fi after disconnect: connected={st?.Connected} profile={st?.Profile}");

        SetStatus("Creating the WarpDrop network…", Amber);
        var res = await _hotspot.StartAsync(_settings.Ssid, _settings.Password);
        Diag.Log($"Hotspot start (dedicated, auto-config paused={boosted}): ok={res.Ok} {res.Message}");
        bool dedicated = true;
        if (!res.Ok && boosted && await _guardian.ResumeAutoConfigAsync())
        {
            // Some drivers need Windows Wi-Fi auto-config running to host; keep the radio dedicated anyway.
            await Task.Delay(2000);
            res = await _hotspot.StartAsync(_settings.Ssid, _settings.Password);
            Diag.Log($"Hotspot start (dedicated, auto-config on): ok={res.Ok} {res.Message}");
        }
        if (!res.Ok && _station != null)
        {
            // Windows refused to host without the laptop's own connection: share the radio instead.
            await ReconnectStationAsync();
            res = await _hotspot.StartAsync(_settings.Ssid, _settings.Password);
            dedicated = false;
            Diag.Log($"Hotspot start (shared radio): ok={res.Ok} {res.Message}");
        }
        if (!res.Ok)
        {
            await TurnOffAsync();
            SetStatus("Couldn't create the network", Red);
            WifiQrPlaceholder.Text = res.Message;
            return;
        }

        _on = true;
        _dedicated = dedicated;
        PerfBoost.Enable();
        _wifiIp = await Hotspot.WaitForHotspotIpAsync();
        Diag.Log($"Network up: ip={_wifiIp} dedicated={dedicated} link={Hotspot.HotspotLinkBps() / 1_000_000} Mbps");
        WifiQr.Source = Qr.Render(Qr.WifiPayload(_settings.Ssid, _settings.Password));
        WifiQrPlaceholder.Visibility = Visibility.Collapsed;
        SsidText.Text = "Network: " + _settings.Ssid;
        PassText.Text = "Password: " + _settings.Password;
        if (!_showWired) ShowUrl(_wifiIp);
        SetStatus(dedicated
            ? $"ON · {res.Message} · dedicated radio · laptop internet off"
            : $"ON · {res.Message} · shared radio (Windows needed the laptop's Wi-Fi)", dedicated ? Green : Amber);
        _ = CheckBandAsync(res.FiveGhz);
    }

    async Task TurnOffAsync()
    {
        SwitchLabel.Text = "Turning off…";
        Diag.Log($"==== Switch OFF (peak this session {_peak / 1e6:0.0} MB/s)");
        SetStatus("Shutting down the WarpDrop network…", Amber);
        _on = false;
        _dedicated = false;
        PerfBoost.Disable();
        try { await _hotspot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        SetStatus("Restoring your Wi-Fi settings and reconnecting…", Amber);
        await _guardian.StopAsync();   // reverts every boosted setting and reconnects the laptop's Wi-Fi
        await ReconnectStationAsync(); // covers the no-admin case; no-op if already connected
        await _hotspot.RestoreOriginalAsync();
        ShowOff();
    }

    void ShowOff()
    {
        Step1Label.Text = "STEP 1 · JOIN WI-FI";
        WifiQr.Source = null;
        WifiQrPlaceholder.Text = "Flip the switch (top right) to create the WarpDrop network.";
        WifiQrPlaceholder.Visibility = Visibility.Visible;
        SsidText.Text = PassText.Text = LinkText.Text = "";
        BandWarning.Visibility = Visibility.Collapsed;
        if (!_showWired) ClearUrl();
        SetStatus("OFF · your laptop's Wi-Fi and internet are untouched", (Brush)FindResource("Mute"));
    }

    async Task ReconnectStationAsync()
    {
        if (_station is not { Profile: { } profile } st) return;
        _station = null;
        if ((await Task.Run(WifiStation.Query))?.Connected == true) return;
        await Task.Run(() => WifiStation.Connect(st.Interface, profile));
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000);
            if ((await Task.Run(WifiStation.Query))?.Connected == true) return;
        }
    }

    /// <summary>Windows may auto-reconnect the laptop's Wi-Fi; while ON, take it down again.</summary>
    async Task EnforceDedicatedAsync()
    {
        var st = await Task.Run(WifiStation.Query);
        if (!_dedicated || _busy || st is not { Connected: true }) return;
        Diag.Log($"Watchdog: laptop Wi-Fi rejoined '{st.Profile}', disconnecting again");
        if (st.Profile != null) _station ??= st;
        await Task.Run(() => WifiStation.Disconnect(st.Interface));
    }

