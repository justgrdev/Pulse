using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace Pulse;

static class Elevation
{
    public static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool Relaunch(string[] args)
    {
        try
        {
            var a = args.Where(x => x != "--no-elevate").Select(x => x.Contains(' ') ? $"\"{x}\"" : x);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', a),
            });
            return true;
        }
        catch (Win32Exception) { return false; } // UAC prompt cancelled
    }
}

static class Fmt
{
    public static string Rate(double bytesPerSec, bool bits) => bits
        ? Scale(bytesPerSec * 8, ["bps", "Kbps", "Mbps", "Gbps"], 1000)
        : Scale(bytesPerSec, ["B/s", "KB/s", "MB/s", "GB/s"], 1024);

    public static string Size(double bytes) => Scale(bytes, ["B", "KB", "MB", "GB", "TB"], 1024);

    static string Scale(double v, string[] units, double k)
    {
        int i = 0;
        while (v >= k && i < units.Length - 1) { v /= k; i++; }
        string n = i == 0 || v >= 100 ? v.ToString("0", CultureInfo.CurrentCulture)
                 : v >= 10 ? v.ToString("0.0", CultureInfo.CurrentCulture)
                 : v.ToString("0.00", CultureInfo.CurrentCulture);
        return $"{n} {units[i]}";
    }
}

public sealed class AdapterPrefs
{
    public bool InGameMode { get; set; } = true;
    public string WifiMethod { get; set; } = "Radio"; // "Radio" = instant radio switch, "Adapter" = disable the device
}

public sealed class AppSettings
{
    public bool UseBits { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string SortKey { get; set; } = "Total"; // Name, Download, Upload or Total
    public bool SortDescending { get; set; } = true;
    public Dictionary<string, AdapterPrefs> Adapters { get; set; } = new();
    public List<string> OfflineSnapshot { get; set; } = new();

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulse", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { } // losing a settings write isn't worth crashing for
    }

    public AdapterPrefs Prefs(string id, bool defaultInGameMode)
    {
        if (!Adapters.TryGetValue(id, out var p))
            Adapters[id] = p = new AdapterPrefs { InGameMode = defaultInGameMode };
        return p;
    }
}

public sealed class BoolConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return targetType == typeof(Visibility) ? (b ? Visibility.Visible : Visibility.Collapsed) : b;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
