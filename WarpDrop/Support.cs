using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace WarpDrop;

/// <summary>Plain-text diagnostics log: %APPDATA%\WarpDrop\diag.log (last ~2 MB kept).</summary>
public static class Diag
{
    static readonly object Lock = new();
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpDrop", "diag.log");

    public static void Log(string message)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }

    public static string Rate(double bytesPerSec) => $"{bytesPerSec / 1_000_000:0.0} MB/s";

    public static string Eta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 359999) return "--";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// <summary>Persisted so the iPhone remembers the network and auto-joins next time.</summary>
public sealed class Settings
{
    public string Ssid { get; set; } = "";
    public string Password { get; set; } = "";
    public string ReceiveDir { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpDrop", "settings.json");

    public static string RandomString(int len) =>
        RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", len);

    public static Settings Load()
    {
        Settings? s = null;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)); } catch { }
        s ??= new Settings();
        bool dirty = false;
        if (string.IsNullOrWhiteSpace(s.Ssid)) { s.Ssid = "WarpDrop-" + RandomString(4).ToUpperInvariant(); dirty = true; }
        if (s.Password.Length < 8) { s.Password = RandomString(12); dirty = true; }
        if (string.IsNullOrWhiteSpace(s.ReceiveDir))
        {
            s.ReceiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WarpDrop");
            dirty = true;
        }
        if (dirty) s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

public static class Qr
{
    public static BitmapSource Render(string text, int scale = 8)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix; // includes the quiet zone
        int n = m.Count, px = n * scale;
        var pixels = new byte[px * px];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                byte v = m[y][x] ? (byte)0 : (byte)255;
                for (int dy = 0; dy < scale; dy++)
                    pixels.AsSpan((y * scale + dy) * px + x * scale, scale).Fill(v);
            }
        var bmp = BitmapSource.Create(px, px, 96, 96, PixelFormats.Gray8, null, pixels, px);
        bmp.Freeze();
        return bmp;
    }

    public static string WifiPayload(string ssid, string password) =>
        new PayloadGenerator.WiFi(ssid, password, PayloadGenerator.WiFi.Authentication.WPA).ToString();
}

