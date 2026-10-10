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

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket c;
            try { c = await _listener!.AcceptAsync(_cts.Token); }
            catch when (_cts.IsCancellationRequested) { break; }
            catch { continue; }
            try
            {
                c.NoDelay = true;
                c.SendBufferSize = SocketBuffer;
                c.ReceiveBufferSize = SocketBuffer;
            }
            catch { }
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(Socket s)
    {
        using var c = new Conn(s);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Request? req;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    req = await c.ReadHeadAsync(idle.Token);
                }
                if (req == null) return;
                bool keep = await Handle(c, req);
                if (!keep || !req.KeepAlive) return;
            }
        }
        catch { /* client went away */ }
    }

    async Task<bool> Handle(Conn c, Request r)
    {
        string p = r.Path;

        if (p is "/" or "/index.html")
            return await Respond(c, r, 200, "text/html; charset=utf-8", _indexHtml);
        if (p == "/favicon.ico")
            return await Respond(c, r, 204, "text/plain", Array.Empty<byte>());

        if (r.Q("k") != Token)
            return await Respond(c, r, 403, "text/plain", "Forbidden: scan the QR code on your PC again."u8.ToArray());

        if (p == "/api/state" && r.Method == "GET")
        {
            var files = Library.Snapshot().Select(f => new { id = f.Id, name = f.Name, size = f.Size });
            var json = JsonSerializer.SerializeToUtf8Bytes(new { pc = Environment.MachineName, files, rateOut = RateOut, rateIn = RateIn });
            return await Respond(c, r, 200, "application/json", json);
        }

        if (p.StartsWith("/f/") && r.Method is "GET" or "HEAD")
        {
            var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 2 && int.TryParse(seg[1], out var id) && Library.Get(id) is { } f && File.Exists(f.Path))
                return await SendFile(c, r, f);
            return await Respond(c, r, 404, "text/plain", "File no longer shared."u8.ToArray());
        }

        if (p == "/api/bench/down" && r.Method == "GET") return await BenchDown(c, r);
        if (p == "/api/bench/up" && r.Method is "PUT" or "POST") return await BenchUp(c, r);
        if (p == "/api/bench/result" && r.Method == "POST")
        {
            await c.DrainAsync(r.ContentLength);
            static double D(string s) => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            LastBench = new BenchResult(D(r.Q("down1")), D(r.Q("down4")), D(r.Q("up")));
            Diag.Log($"Speed test from phone: down1={LastBench.Down1:0} Mbps down4={LastBench.Down4:0} Mbps up={LastBench.Up:0} Mbps");
            return await Respond(c, r, 200, "application/json", "{}"u8.ToArray());
        }

        if (p == "/api/log" && r.Method == "POST")
        {
            await c.DrainAsync(r.ContentLength);
            string msg = r.Q("msg");
            Diag.Log("phone: " + (msg.Length > 500 ? msg[..500] : msg));
            return await Respond(c, r, 200, "application/json", "{}"u8.ToArray());
        }

        if (p == "/api/up/init" && r.Method == "POST")
            return await UploadInit(c, r);

        if (p.StartsWith("/api/up/"))
        {
            var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, up, {id}, [done|cancel]
            if (seg.Length >= 3 && int.TryParse(seg[2], out var uid))
            {
                if (seg.Length == 3 && r.Method == "PUT") return await UploadChunk(c, r, uid);
                if (seg.Length == 4 && seg[3] == "status" && r.Method == "GET")
                {
                    if (!_uploads.TryGetValue(uid, out var su))
                        return await Respond(c, r, 404, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { received = -1 }));
                    long[][] ranges;
                    lock (su) ranges = su.Ranges.Select(kv => new[] { kv.Key, kv.Value }).ToArray();
                    return await Respond(c, r, 200, "application/json",
                        JsonSerializer.SerializeToUtf8Bytes(new { received = Interlocked.Read(ref su.Contig), ranges }));
                }
                if (seg.Length == 4 && seg[3] == "done" && r.Method == "POST") return await UploadDone(c, r, uid);
                if (seg.Length == 4 && seg[3] == "cancel" && r.Method == "POST") return await UploadCancel(c, r, uid);
            }
        }

        return await Respond(c, r, 404, "text/plain", "Not found"u8.ToArray());
    }

    // ---------- PC -> phone ----------

    async Task<bool> SendFile(Conn c, Request r, SharedFile f)
    {
        using var h = File.OpenHandle(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long size = RandomAccess.GetLength(h);
        long start = 0, end = size - 1;
        int status = 200;
        if (r.Headers.TryGetValue("Range", out var range))
        {
            if (TryParseRange(range, size, out start, out end)) status = 206;
            else
            {
                var bad = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{size}\r\nContent-Length: 0\r\n\r\n";
                await c.SendAllAsync(Encoding.ASCII.GetBytes(bad));
                return true;
            }
        }
        long len = size == 0 ? 0 : end - start + 1;

        var head = new StringBuilder(256)
            .Append("HTTP/1.1 ").Append(status).Append(status == 206 ? " Partial Content" : " OK").Append("\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Content-Length: ").Append(len).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Disposition: ").Append(Disposition(f.Name)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n");
        if (status == 206) head.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(size).Append("\r\n");
        head.Append(r.KeepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");
        await c.SendAllAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (r.Method == "HEAD" || len == 0) return true;

        var t = NewTransfer(f.Name, toPc: false, len);
        // Fresh (not pooled) buffers: a read may still be in flight if the client disconnects mid-send.
        byte[] a = GC.AllocateUninitializedArray<byte>(IoChunk), b = GC.AllocateUninitializedArray<byte>(IoChunk);
        try
        {
            long pos = start, remaining = len;
            int n = await RandomAccess.ReadAsync(h, a.AsMemory(0, (int)Math.Min(IoChunk, remaining)), pos);
            while (true)
            {
                if (n <= 0) throw new IOException("File shrank while sending.");
                pos += n;
                remaining -= n;
                // Read the next block from disk while this one goes out over the network.
                var next = remaining > 0 ? RandomAccess.ReadAsync(h, b.AsMemory(0, (int)Math.Min(IoChunk, remaining)), pos) : default;
                await c.SendAllAsync(a.AsMemory(0, n));
                Interlocked.Add(ref _bytesOut, n);
                Interlocked.Add(ref t.Done, n);
                if (remaining <= 0) break;
                n = await next;
                (a, b) = (b, a);
            }
            Finish(t, ok: true);
            return true;
        }
        catch
        {
            Finish(t, ok: false);
            throw;
        }
    }

    static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || size == 0) return false;
        var spec = header[6..].Split(',')[0].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;
        string s = spec[..dash].Trim(), e = spec[(dash + 1)..].Trim();
        if (s.Length == 0)
        {
            if (!long.TryParse(e, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
            return true;
        }
        if (!long.TryParse(s, out start) || start >= size) return false;
        if (e.Length > 0)
        {
            if (!long.TryParse(e, out end) || end < start) return false;
            end = Math.Min(end, size - 1);
        }
        return true;
    }

    static string Disposition(string name)
    {
        var ascii = new string(name.Select(ch => ch < 32 || ch > 126 || ch is '"' or '\\' ? '_' : ch).ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    // ---------- speed test: pure network, no disk ----------

    async Task<bool> BenchDown(Conn c, Request r)
    {
        long len = long.TryParse(r.Q("bytes"), out var b) ? Math.Clamp(b, 0, 64L << 30) : 256L << 20;
        await c.SendAllAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {len}\r\nCache-Control: no-store\r\n" +
            (r.KeepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n")));
        while (len > 0)
        {
            int n = (int)Math.Min(BenchBlock.Length, len);
            await c.SendAllAsync(BenchBlock.AsMemory(0, n));
            Interlocked.Add(ref _bytesOut, n);
            len -= n;
        }
        return true;
    }

    async Task<bool> BenchUp(Conn c, Request r)
    {
        var buf = GC.AllocateUninitializedArray<byte>(IoChunk);
        long remaining = r.ContentLength;
        while (remaining > 0)
        {
            int n = await c.ReadBodyAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)));
            if (n == 0) throw new IOException("Client disconnected.");
            remaining -= n;
            Interlocked.Add(ref _bytesIn, n);
        }
        return await Respond(c, r, 200, "application/json", "{}"u8.ToArray());
    }

    // ---------- phone -> PC ----------

    async Task<bool> UploadInit(Conn c, Request r)
    {
        await c.DrainAsync(r.ContentLength);
        if (!long.TryParse(r.Q("size"), out var size) || size < 0)
            return await Respond(c, r, 400, "text/plain", "bad size"u8.ToArray());
        string name = SafeName(r.Q("name"));
        Directory.CreateDirectory(ReceiveDir);

        string final, part;
        SafeFileHandle handle;
        lock (_nameLock)
        {
            final = UniquePath(ReceiveDir, name);
            part = final + ".warpdrop-part";
            handle = File.OpenHandle(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                FileOptions.Asynchronous, preallocationSize: size);
        }
        var t = NewTransfer(Path.GetFileName(final), toPc: true, size);
        int id = Interlocked.Increment(ref _nextUpload);
        _uploads[id] = new Upload { Handle = handle, PartPath = part, FinalPath = final, T = t };
        Diag.Log($"Upload #{id} start '{name}' {size / 1e6:0.0} MB");
        return await Respond(c, r, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { id }));
    }

    async Task<bool> UploadChunk(Conn c, Request r, int id)
    {
        if (!_uploads.TryGetValue(id, out var u))
        {
            await Respond(c, r, 404, "text/plain", "unknown upload"u8.ToArray());
            return false; // body not consumed: close the connection
        }
        if (!long.TryParse(r.Q("off"), out var off) || off < 0 || r.ContentLength < 0 || off + r.ContentLength > u.T.Total)
        {
            await Respond(c, r, 400, "text/plain", "bad range"u8.ToArray());
            return false;
        }

        bool verbose = r.ContentLength >= 64L << 20; // single-stream mode; parallel pieces would flood the log
        if (verbose) Diag.Log($"Upload #{id}: receiving {r.ContentLength / 1e6:0.0} MB from offset {off / 1e6:0.0} MB");
        byte[] a = GC.AllocateUninitializedArray<byte>(IoChunk), b = GC.AllocateUninitializedArray<byte>(IoChunk);
        long pos = off, remaining = r.ContentLength, pendingStart = 0, pendingEnd = 0;
        Task? pendingWrite = null;
        // A phone that drops off Wi-Fi never sends a FIN; don't hold the upload hostage forever.
        using var stall = new CancellationTokenSource();
        try
        {
            while (remaining > 0)
            {
                int want = (int)Math.Min(IoChunk, remaining), filled = 0;
                while (filled < want)
                {
                    stall.CancelAfter(BodyStallTimeout);
                    int n = await c.ReadBodyAsync(a.AsMemory(filled, want - filled), stall.Token);
                    if (n == 0) throw new IOException("Phone closed the connection mid-upload.");
                    filled += n;
                    Interlocked.Add(ref _bytesIn, n);
                    Interlocked.Add(ref u.T.Done, n);
                }
                // Write this block to disk while the next one streams in from the network.
                if (pendingWrite != null) { await pendingWrite; AddRange(u, pendingStart, pendingEnd); }
                pendingWrite = RandomAccess.WriteAsync(u.Handle, a.AsMemory(0, filled), pos).AsTask();
                (pendingStart, pendingEnd) = (pos, pos + filled);
                pos += filled;
                remaining -= filled;
                (a, b) = (b, a);
            }
            if (pendingWrite != null) { await pendingWrite; AddRange(u, pendingStart, pendingEnd); }
        }
        catch (Exception ex)
        {
            Diag.Log($"Upload #{id}: stream broke after {(pos - off) / 1e6:0.0} MB ({ex.GetType().Name}: {ex.Message}); resume point {u.Contig / 1e6:0.0} MB");
            throw;
        }
        if (verbose) Diag.Log($"Upload #{id}: received through {pos / 1e6:0.0} MB");
        return await Respond(c, r, 200, "application/json", "{}"u8.ToArray());
    }

    /// <summary>Records a written range. Parallel pieces land out of order; Contig is the gap-free prefix.</summary>
    static void AddRange(Upload u, long start, long end)
    {
        lock (u)
        {
            var r = u.Ranges;
            for (int i = r.Count - 1; i >= 0; i--)
            {
                long s = r.Keys[i], e = r.Values[i];
                if (e < start || s > end) continue; // neither overlapping nor touching
                start = Math.Min(start, s);
                end = Math.Max(end, e);
                r.RemoveAt(i);
            }
            r[start] = end;
            if (r.Keys[0] == 0) Interlocked.Exchange(ref u.Contig, r.Values[0]);
        }
    }

    async Task<bool> UploadDone(Conn c, Request r, int id)
    {
        await c.DrainAsync(r.ContentLength);
        if (_uploads.TryGetValue(id, out var pending) && Interlocked.Read(ref pending.Contig) < pending.T.Total)
        {
            // Never save a truncated file: tell the phone where to resume instead.
            Diag.Log($"Upload #{id}: finish refused, only {pending.Contig / 1e6:0.0} of {pending.T.Total / 1e6:0.0} MB present");
            return await Respond(c, r, 409, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { received = pending.Contig }));
        }
        if (!_uploads.TryRemove(id, out var u))
            return await Respond(c, r, 404, "text/plain", "unknown upload"u8.ToArray());
        u.Handle.Dispose();
        string final;
        lock (_nameLock)
        {
            final = File.Exists(u.FinalPath) ? UniquePath(Path.GetDirectoryName(u.FinalPath)!, Path.GetFileName(u.FinalPath)) : u.FinalPath;
            File.Move(u.PartPath, final);
        }
        u.T.Done = u.T.Total;
        Finish(u.T, ok: true);
        FileReceived?.Invoke(final);
        return await Respond(c, r, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { name = Path.GetFileName(final) }));
    }

    async Task<bool> UploadCancel(Conn c, Request r, int id)
    {
        await c.DrainAsync(r.ContentLength);
        if (_uploads.TryRemove(id, out var u))
        {
            u.Handle.Dispose();
            try { File.Delete(u.PartPath); } catch { }
            Finish(u.T, ok: false);
        }
        return await Respond(c, r, 200, "application/json", "{}"u8.ToArray());
    }

