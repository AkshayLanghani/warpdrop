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

