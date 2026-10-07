using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WarpDrop;

public sealed class SharedFile
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public string SizeText => Fmt.Bytes(Size);
}

public sealed class Transfer
{
    public int Id;
    public string Name = "";
    public bool ToPc;
    public long Total;
    public long Done;
    public long Started = Environment.TickCount64;
    public long Ended;
    public volatile int State; // 0 = active, 1 = done, 2 = failed
    public double Speed;       // smoothed bytes/s, updated by the rate timer
    internal long LastDone;

    public double Seconds => Math.Max(0.001, ((Ended == 0 ? Environment.TickCount64 : Ended) - Started) / 1000.0);
}

