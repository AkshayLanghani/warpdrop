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

