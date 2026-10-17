using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WarpDrop;

/// <summary>
/// While the WarpDrop network is ON, an elevated PowerShell "guardian" holds Windows in
/// max-throughput mode (Wi-Fi driver, power, hotspot timeout). When the switch goes OFF, or if
/// WarpDrop dies, the guardian puts every original value back and reconnects the laptop's Wi-Fi.
/// One UAC prompt per ON session; nothing stays changed afterwards.
/// </summary>
public sealed class SessionGuardian
{
    public sealed record Result(bool Ok, bool AdapterRestarted, IReadOnlyList<string> Changes, string? Error);

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpDrop", "session");
    static string LogFile => Path.Combine(Dir, "session.log");
    static string StopFile => Path.Combine(Dir, "session.stop");

    Process? _proc;

