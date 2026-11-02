using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Windows.Devices.Radios;

namespace Pulse;

public enum AdapterKind { WiFi, Ethernet, Bluetooth, Virtual }

public sealed class RawAdapter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public AdapterKind Kind { get; set; }
    public bool AdapterEnabled { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>Lists adapters and switches them. Wi-Fi/Bluetooth use the Windows radio API (instant, no admin);
/// everything else is enabled/disabled through WMI (needs admin).</summary>
public sealed class AdapterService
{
    Radio? _wifi, _bt;
    public event Action? RadiosChanged;

    public async Task InitRadiosAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi && _wifi == null) _wifi = r;
                else if (r.Kind == RadioKind.Bluetooth && _bt == null) _bt = r;
                else continue;
                r.StateChanged += (_, _) => RadiosChanged?.Invoke();
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    static readonly string[] VirtualWords = ["Tunnel", "VPN", "TAP-", "WireGuard", "Virtual", "Hyper-V", "VMware", "VirtualBox", "WARP", "Tailscale", "Miniport"];

    static bool IsWifiName(string s) =>
        s.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || s.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("802.11") || s.Contains("WLAN", StringComparison.OrdinalIgnoreCase);

    public List<RawAdapter> Query()
    {
        var list = new List<RawAdapter>();
        using var s = new ManagementObjectSearcher(
            "SELECT Name,NetConnectionID,GUID,PhysicalAdapter,ConfigManagerErrorCode,PNPDeviceID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
        foreach (ManagementBaseObject o in s.Get())
        {
            string desc = o["Name"] as string ?? "";
            string conn = o["NetConnectionID"] as string ?? desc;
            string pnp = o["PNPDeviceID"] as string ?? "";
            bool physical = o["PhysicalAdapter"] is true;
            // Bluetooth is shown as a radio, not as its network adapter
            if (pnp.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
            if (desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)) continue;
            bool tunnel = pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
                || pnp.StartsWith("VMS", StringComparison.OrdinalIgnoreCase) || pnp.Length == 0
                || VirtualWords.Any(w => desc.Contains(w, StringComparison.OrdinalIgnoreCase));
            var kind = !physical || tunnel ? AdapterKind.Virtual : IsWifiName(desc) || IsWifiName(conn) ? AdapterKind.WiFi : AdapterKind.Ethernet;
            bool enabled = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0) != 22; // 22 = disabled in Device Manager
            list.Add(new RawAdapter
            {
                Id = o["GUID"] as string ?? "", Name = conn, Description = desc, Kind = kind,
                AdapterEnabled = enabled,
                Enabled = enabled && (kind != AdapterKind.WiFi || _wifi == null || _wifi.State == RadioState.On),
            });
        }
        if (_bt != null)
            list.Add(new RawAdapter
            {
                Id = "bluetooth-radio", Name = "Bluetooth", Description = "Bluetooth radio", Kind = AdapterKind.Bluetooth,
                AdapterEnabled = true, Enabled = _bt.State == RadioState.On,
            });
        return list;
    }

    public async Task SetAsync(RawAdapter a, bool on, bool fullyDisable)
    {
        switch (a.Kind)
        {
            case AdapterKind.Bluetooth:
                await SetRadio(_bt!, on);
                break;
            case AdapterKind.WiFi:
                if (on)
                {
                    if (!a.AdapterEnabled) await Wmi(a.Id, true);
                    if (_wifi != null) await SetRadio(_wifi, true);
                }
                else if (!fullyDisable && _wifi != null) await SetRadio(_wifi, false);
                else await Wmi(a.Id, false);
                break;
            default:
                await Wmi(a.Id, on);
                break;
        }
    }

    static async Task SetRadio(Radio r, bool on)
    {
        if (await r.SetStateAsync(on ? RadioState.On : RadioState.Off) != RadioAccessStatus.Allowed)
            throw new InvalidOperationException("Windows denied radio access");
    }

    static Task Wmi(string guid, bool enable) => Task.Run(() =>
    {
        if (!Elevation.IsAdmin) throw new InvalidOperationException("Needs administrator rights");
        using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_NetworkAdapter WHERE GUID='{guid}'");
        foreach (ManagementObject o in s.Get())
        {
            var rc = Convert.ToUInt32(o.InvokeMethod(enable ? "Enable" : "Disable", null));
            if (rc != 0) throw new InvalidOperationException($"Windows returned error {rc}");
            return;
        }
        throw new InvalidOperationException("Adapter not found");
    });
}

public sealed record ProcInfo(string Key, string Display, string? Path);

/// <summary>Per-process network bytes from the kernel ETW provider (admin only).</summary>
public sealed class AppTrafficMonitor : IDisposable
{
    sealed class Counter { public long Down, Up; }

    readonly ConcurrentDictionary<int, Counter> _c = new();
    readonly Dictionary<int, ProcInfo> _names = new();
    TraceEventSession? _s;
    public bool Running { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (!Elevation.IsAdmin) { Error = "Per-app usage needs administrator rights."; return; }
        try { Begin("PulseKernelNet"); }
        catch
        {
            try { Begin(KernelTraceEventParser.KernelSessionName); }
            catch (Exception ex) { Error = "Could not start network tracing: " + ex.Message; }
        }
    }

    void Begin(string name)
    {
        try { using var old = TraceEventSession.GetActiveSession(name); old?.Stop(true); } catch { }
        _s = new TraceEventSession(name) { StopOnDispose = true };
        try
        {
            _s.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var k = _s.Source.Kernel;
            k.TcpIpRecv += d => Add(d.ProcessID, d.size, false, d.saddr);
            k.TcpIpSend += d => Add(d.ProcessID, d.size, true, d.daddr);
            k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false, d.saddr);
            k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, true, d.daddr);
            k.UdpIpRecv += d => Add(d.ProcessID, d.size, false, d.saddr);
            k.UdpIpSend += d => Add(d.ProcessID, d.size, true, d.daddr);
            k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false, d.saddr);
            k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, true, d.daddr);
        }
        catch { _s.Dispose(); _s = null; throw; }
        var src = _s.Source;
        new Thread(() => { try { src.Process(); } catch { } }) { IsBackground = true, Name = "pulse-etw" }.Start();
        Running = true;
    }

    void Add(int pid, int size, bool up, IPAddress remote)
    {
        if (size <= 0 || IPAddress.IsLoopback(remote)) return;
        var c = _c.GetOrAdd(pid, static _ => new Counter());
        Interlocked.Add(ref up ? ref c.Up : ref c.Down, size);
    }

    /// <summary>Bytes seen per process name since the last call.</summary>
    public Dictionary<string, (ProcInfo Info, long Down, long Up)> Drain()
    {
        var r = new Dictionary<string, (ProcInfo, long, long)>();
        foreach (var kv in _c)
        {
            long d = Interlocked.Exchange(ref kv.Value.Down, 0), u = Interlocked.Exchange(ref kv.Value.Up, 0);
            if (d == 0 && u == 0) continue;
            var info = Resolve(kv.Key);
            r[info.Key] = r.TryGetValue(info.Key, out var e) ? (info, e.Item2 + d, e.Item3 + u) : (info, d, u);
        }
        return r;
    }

    ProcInfo Resolve(int pid)
    {
        if (_names.TryGetValue(pid, out var cached)) return cached;
        ProcInfo info;
        if (pid is 0 or 4) info = new("system", "Windows System", null);
        else
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                string display = p.ProcessName;
                if (path != null)
                {
                    try
                    {
                        var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                        if (!string.IsNullOrWhiteSpace(d)) display = d.Trim();
                    }
                    catch { }
                }
                info = new(p.ProcessName.ToLowerInvariant(), display, path);
            }
            catch { info = new("pid" + pid, "Process " + pid + " (closed)", null); }
        }
        if (_names.Count > 5000) _names.Clear();
        return _names[pid] = info;
    }

    public void Dispose()
    {
        Running = false;
        try { _s?.Dispose(); } catch { }
    }
}
