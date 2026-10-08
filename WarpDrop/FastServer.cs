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

/// <summary>Files the PC is offering to the phone. Thread-safe.</summary>
public sealed class Library
{
    readonly object _lock = new();
    readonly List<SharedFile> _files = new();
    int _nextId;

    public SharedFile? Add(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        lock (_lock)
        {
            var f = new SharedFile { Id = ++_nextId, Name = info.Name, Path = info.FullName, Size = info.Length };
            _files.Add(f);
            return f;
        }
    }

    public void Remove(int id) { lock (_lock) _files.RemoveAll(f => f.Id == id); }
    public void Clear() { lock (_lock) _files.Clear(); }
    public SharedFile? Get(int id) { lock (_lock) return _files.Find(f => f.Id == id); }
    public SharedFile[] Snapshot() { lock (_lock) return _files.ToArray(); }
}

/// <summary>
/// Minimal HTTP/1.1 server built directly on sockets. No framework overhead:
/// double-buffered 2 MB disk reads overlap with socket sends, large kernel socket
/// buffers keep the Wi-Fi link saturated, and uploads arrive as parallel chunks
/// written straight to their offsets in a preallocated file.
/// </summary>
public sealed class FastServer : IDisposable
{
    const int IoChunk = 2 * 1024 * 1024;
    const int SocketBuffer = 8 * 1024 * 1024; // > bandwidth-delay product of gigabit Wi-Fi even with 50 ms latency spikes
    static readonly byte[] BenchBlock = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4 * 1024 * 1024);

    Socket? _listener;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<int, Transfer> _transfers = new();
    readonly ConcurrentDictionary<int, Upload> _uploads = new();
    readonly object _nameLock = new();
    readonly byte[] _indexHtml;
    readonly Timer _rateTimer;
    readonly Stopwatch _rateClock = Stopwatch.StartNew();
    int _nextTransfer, _nextUpload;
    long _bytesOut, _bytesIn, _lastOut, _lastIn;
    double _lastTick;

    public int Port { get; private set; }
    public string Token { get; } = App.FixedToken ?? Settings.RandomString(10);
    public Library Library { get; } = new();
    public string ReceiveDir { get; set; }
    public double RateOut { get; private set; } // PC -> phone, bytes/s
    public double RateIn { get; private set; }  // phone -> PC, bytes/s
    public event Action<string>? FileReceived;
    /// <summary>Latest speed test reported by the phone, in Mbit/s.</summary>
    public BenchResult? LastBench { get; private set; }

    public sealed record BenchResult(double Down1, double Down4, double Up);

    sealed class Upload
    {
        public required SafeFileHandle Handle;
        public required string PartPath;
        public required string FinalPath;
        public required Transfer T;
        public long Contig; // bytes on disk from offset 0 with no gaps: where a resumed upload continues
        public readonly SortedList<long, long> Ranges = new(); // written [start, end) ranges, merged
    }

    static readonly TimeSpan BodyStallTimeout = TimeSpan.FromSeconds(60);

    public FastServer(string receiveDir)
    {
        ReceiveDir = receiveDir;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _indexHtml = ms.ToArray();
        _rateTimer = new Timer(_ => UpdateRates(), null, 500, 500);
    }

    public void Start(params int[] ports)
    {
        foreach (var p in ports)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.ExclusiveAddressUse = true;
                s.ReceiveBufferSize = SocketBuffer; // inherited by accepted sockets, sets the TCP window scale
                s.SendBufferSize = SocketBuffer;
                s.Bind(new IPEndPoint(IPAddress.Any, p));
                s.Listen(512);
                _listener = s;
                Port = ((IPEndPoint)s.LocalEndPoint!).Port;
                break;
            }
            catch (SocketException) { s.Dispose(); }
        }
        if (_listener == null) throw new InvalidOperationException("Could not open a listening port.");
        _ = AcceptLoop();
    }

    public IReadOnlyList<Transfer> TransfersSnapshot() =>
        _transfers.Values.OrderByDescending(t => t.Id).ToList();

