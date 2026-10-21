using System.Windows;

namespace WarpDrop;

public partial class App : Application
{
    /// <summary>--lan: skip the hotspot and serve on the current network.</summary>
    public static bool LanOnly { get; private set; }
    /// <summary>--nofw: don't touch the firewall.</summary>
    public static bool NoFirewall { get; private set; }
    /// <summary>--token=xyz: fixed access token, for scripting/testing.</summary>
    public static string? FixedToken { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--restore-system"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool ok = await SessionGuardian.RestoreAsync();
            MessageBox.Show(ok ? "Original Wi-Fi, power and hotspot settings restored, firewall rule removed."
                               : "Restore was cancelled.", "WarpDrop");
            Shutdown();
            return;
        }
        if (e.Args.Contains("--probe-bands"))
        {
            // Read-only: which hotspot bands/security this PC's Wi-Fi driver can host. Changes nothing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var lines = await Hotspot.ProbeAsync();
            foreach (var line in lines) Diag.Log("probe: " + line);
            MessageBox.Show(string.Join("\n", lines), "WarpDrop · hotspot capabilities");
            Shutdown();
            return;
        }
        LanOnly = e.Args.Contains("--lan");
        NoFirewall = e.Args.Contains("--nofw") || e.Args.Contains("--notune");
        FixedToken = e.Args.FirstOrDefault(a => a.StartsWith("--token="))?["--token=".Length..];
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "WarpDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
