using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pulse;

public sealed partial class AdapterVM : ObservableObject
{
    readonly MainViewModel _vm;
    bool _syncing, _isOn;

    public AdapterVM(MainViewModel vm, RawAdapter raw, AdapterPrefs prefs)
    {
        _vm = vm; Raw = raw; Prefs = prefs;
        _isOn = raw.Enabled;
    }

    public RawAdapter Raw { get; private set; }
    public AdapterPrefs Prefs { get; }
    public string Id => Raw.Id;
    public AdapterKind Kind => Raw.Kind;
    public string Name => Raw.Name;
    public string Description => Raw.Description;
    public bool IsWiFi => Kind == AdapterKind.WiFi;
    public string Glyph => Kind switch
    {
        AdapterKind.WiFi => "", AdapterKind.Ethernet => "", AdapterKind.Bluetooth => "", _ => "",
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowRates))] bool _connected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Idle))] bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] string? _error;
    [ObservableProperty] bool _settingsOpen;
    [ObservableProperty] string _ratesText = "";

    public bool Idle => !Busy;
    public bool HasError => Error != null;
    public bool ShowRates => IsOn && Connected && Kind != AdapterKind.Bluetooth;
    public string StatusText => !IsOn ? "Off"
        : Kind == AdapterKind.Bluetooth ? "On"
        : Connected ? "Connected"
        : Kind == AdapterKind.Ethernet ? "On · no cable" : "On · not connected";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetOn(value)) return;
            if (_syncing) _vm.RefreshHeader();
            else _ = ApplyAsync(value);
        }
    }

    bool SetOn(bool value)
    {
        if (_isOn == value) return false;
        _isOn = value;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowRates));
        return true;
    }

    public bool InGameMode
    {
        get => Prefs.InGameMode;
        set { Prefs.InGameMode = value; OnPropertyChanged(); _vm.Settings.Save(); _vm.RefreshHeader(); }
    }

    public bool FullyDisable
    {
        get => Prefs.WifiMethod == "Adapter";
        set { Prefs.WifiMethod = value ? "Adapter" : "Radio"; OnPropertyChanged(); _vm.Settings.Save(); }
    }

    public void Sync(RawAdapter raw)
    {
        Raw = raw;
        if (Busy) return;
        _syncing = true; IsOn = raw.Enabled; _syncing = false;
    }

    public void SetRate(double down, double up, bool bits) =>
        RatesText = $"↓ {Fmt.Rate(down, bits)}   ↑ {Fmt.Rate(up, bits)}";

    async Task ApplyAsync(bool on)
    {
        Busy = true; Error = null;
        _vm.RefreshHeader();
        try { await _vm.Svc.SetAsync(Raw, on, Prefs.WifiMethod == "Adapter"); }
        catch (Exception ex)
        {
            Error = ex.Message;
            _syncing = true; IsOn = !on; _syncing = false;
        }
        finally { Busy = false; }
        _vm.RequestRefresh();
    }

    public Task ApplyAndWait(bool on) => SetOn(on) ? ApplyAsync(on) : Task.CompletedTask;
}

public sealed partial class AppUsageVM : ObservableObject
{
    int _idle;
    public AppUsageVM(string key, string name, ImageSource? icon) { Key = key; Name = name; Icon = icon; }
    public string Key { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public double Rate => DownRate + UpRate;

    [ObservableProperty] double _downRate;
    [ObservableProperty] double _upRate;
    [ObservableProperty] double _totalBytes;
    [ObservableProperty] string _downText = "–";
    [ObservableProperty] string _upText = "–";
    [ObservableProperty] string _totalText = "";
    [ObservableProperty] double _share;
    [ObservableProperty] double _dim = 1;

    public void Update(double down, double up, long dBytes, long uBytes, bool bits)
    {
        DownRate = down;
        UpRate = up;
        TotalBytes += dBytes + uBytes;
        DownText = down >= 1 ? Fmt.Rate(down, bits) : "–";
        UpText = up >= 1 ? Fmt.Rate(up, bits) : "–";
        TotalText = Fmt.Size(TotalBytes);
        _idle = Rate > 0 ? 0 : _idle + 1;
        Dim = Rate > 0 ? 1 : _idle > 10 ? 0.45 : 0.75;
    }
}

static class IconCache
{
    static readonly Dictionary<string, ImageSource?> _c = new(StringComparer.OrdinalIgnoreCase);
    static ImageSource? _default;

    public static ImageSource? Get(string? path)
    {
        if (path == null) return _default ??= Load(Environment.ProcessPath);
        if (_c.TryGetValue(path, out var i)) return i;
        return _c[path] = Load(path) ?? (_default ??= Load(Environment.ProcessPath));
    }

    static ImageSource? Load(string? path)
    {
        try
        {
            using var ic = System.Drawing.Icon.ExtractAssociatedIcon(path!);
            if (ic == null) return null;
            var bs = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch { return null; }
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly AppTrafficMonitor _etw = new();
    readonly SemaphoreSlim _signal = new(0, 1);
    readonly Dictionary<string, AppUsageVM> _appMap = new();
    readonly CancellationTokenSource _cts = new();
    Dictionary<string, NicStat>? _prev;
    double _sessDown, _sessUp;

    public AppSettings Settings { get; } = AppSettings.Load();
    public AdapterService Svc { get; } = new();
    public ObservableCollection<AdapterVM> Adapters { get; } = new();
    public ObservableCollection<AdapterVM> VirtualAdapters { get; } = new();
    public ObservableCollection<AppUsageVM> Apps { get; } = new();
    public ListCollectionView AppsView { get; }
    public IEnumerable<AdapterVM> AllAdapters => Adapters.Concat(VirtualAdapters);

    public event Action<double, double>? Sample;
    public event Action? StateChanged;

    public bool NotAdmin => !Elevation.IsAdmin;

    [ObservableProperty] string _downText = "0 B/s";
    [ObservableProperty] string _upText = "0 B/s";
    [ObservableProperty] string _sessionText = "";
    [ObservableProperty] string _statusTitle = "Checking…";
    [ObservableProperty] string _statusSub = "";
    [ObservableProperty] bool _isOnline;
    [ObservableProperty] bool _anyOn = true;
    [ObservableProperty] bool _masterBusy;
    [ObservableProperty] string _gameButtonText = "Go offline";
    [ObservableProperty] bool _showVirtual;
    [ObservableProperty] bool _hasVirtual;
    [ObservableProperty] string _virtualLabel = "";
    [ObservableProperty] bool _showAppsEmpty = true;
    [ObservableProperty] string _appsEmptyText = "";

    public bool UseBits
    {
        get => Settings.UseBits;
        set
        {
            if (Settings.UseBits == value) return;
            Settings.UseBits = value; Settings.Save();
            OnPropertyChanged(); OnPropertyChanged(nameof(UseBytes));
        }
    }
    public bool UseBytes { get => !UseBits; set { if (value) UseBits = false; } }

    public MainViewModel()
    {
        AppsView = new ListCollectionView(Apps) { IsLiveSorting = true };
        ApplySort();
    }

    public string NameMark => SortMark("Name");
    public string DownMark => SortMark("Download");
    public string UpMark => SortMark("Upload");
    public string TotalMark => SortMark("Total");

    string SortMark(string key) => Settings.SortKey != key ? "" : Settings.SortDescending ? " ↓" : " ↑";

    [RelayCommand]
    void SortBy(string key)
    {
        if (Settings.SortKey == key) Settings.SortDescending = !Settings.SortDescending;
        else { Settings.SortKey = key; Settings.SortDescending = key != "Name"; }
        Settings.Save();
        ApplySort();
    }

    void ApplySort()
    {
        string prop = Settings.SortKey switch
        {
            "Name" => nameof(AppUsageVM.Name),
            "Download" => nameof(AppUsageVM.DownRate),
            "Upload" => nameof(AppUsageVM.UpRate),
            _ => nameof(AppUsageVM.TotalBytes),
        };
        AppsView.SortDescriptions.Clear();
        AppsView.LiveSortingProperties.Clear();
        AppsView.SortDescriptions.Add(new SortDescription(prop, Settings.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        if (prop != nameof(AppUsageVM.Name)) AppsView.SortDescriptions.Add(new SortDescription(nameof(AppUsageVM.Name), ListSortDirection.Ascending));
        AppsView.LiveSortingProperties.Add(prop);
        foreach (var m in new[] { nameof(NameMark), nameof(DownMark), nameof(UpMark), nameof(TotalMark) }) OnPropertyChanged(m);
    }

    public async Task StartAsync()
    {
        await Svc.InitRadiosAsync();
        Svc.RadiosChanged += RequestRefresh;
        _etw.Start();
        AppsEmptyText = _etw.Running ? "Waiting for network activity…" : _etw.Error ?? "Per-app usage is unavailable.";
        _ = PollAdaptersLoop();
        _ = SampleLoop();
    }

    public void RequestRefresh() { try { _signal.Release(); } catch (SemaphoreFullException) { } }

    async Task PollAdaptersLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Reconcile(await Task.Run(Svc.Query)); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            await _signal.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    static int Order(AdapterVM a) => a.Kind switch { AdapterKind.WiFi => 0, AdapterKind.Ethernet => 1, AdapterKind.Bluetooth => 2, _ => 3 };

    void Reconcile(List<RawAdapter> raws)
    {
        var all = AllAdapters.ToList();
        foreach (var r in raws)
        {
            var vm = all.FirstOrDefault(a => a.Id == r.Id);
            if (vm != null) { vm.Sync(r); continue; }
            vm = new AdapterVM(this, r, Settings.Prefs(r.Id, r.Kind != AdapterKind.Virtual));
            var list = r.Kind == AdapterKind.Virtual ? VirtualAdapters : Adapters;
            list.Insert(list.TakeWhile(a => (Order(a), a.Name).CompareTo((Order(vm), vm.Name)) <= 0).Count(), vm);
            Settings.Save();
        }
        foreach (var gone in all.Where(a => raws.All(r => r.Id != a.Id)).ToList())
        {
            Adapters.Remove(gone); VirtualAdapters.Remove(gone);
        }
        HasVirtual = VirtualAdapters.Count > 0;
        VirtualLabel = $"Virtual & VPN adapters ({VirtualAdapters.Count})";
        RefreshHeader();
    }

    record NicStat(long Rx, long Tx, bool Up);

    static Dictionary<string, NicStat> SampleNics()
    {
        var d = new Dictionary<string, NicStat>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var s = n.GetIPStatistics();
                d[n.Name] = new NicStat(s.BytesReceived, s.BytesSent, n.OperationalStatus == OperationalStatus.Up);
            }
            catch { }
        }
        return d;
    }

    async Task SampleLoop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_cts.Token))
        {
            try
            {
                var (nics, apps) = await Task.Run(() => (SampleNics(), _etw.Running ? _etw.Drain() : []));
                double now = sw.Elapsed.TotalSeconds, dt = now - last;
                last = now;
                if (dt < 0.2) continue;
                ApplyNics(nics, dt);
                ApplyApps(apps, dt);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    void ApplyNics(Dictionary<string, NicStat> nics, double dt)
    {
        double td = 0, tu = 0;
        foreach (var a in AllAdapters)
        {
            if (a.Kind == AdapterKind.Bluetooth) { a.Connected = a.IsOn; continue; }
            if (nics.TryGetValue(a.Raw.Name, out var cur))
            {
                a.Connected = cur.Up && a.IsOn;
                double d = 0, u = 0;
                if (_prev != null && _prev.TryGetValue(a.Raw.Name, out var p))
                {
                    d = Math.Max(0, (cur.Rx - p.Rx) / dt);
                    u = Math.Max(0, (cur.Tx - p.Tx) / dt);
                }
                a.SetRate(d, u, UseBits);
                if (a.Kind is AdapterKind.WiFi or AdapterKind.Ethernet && a.Connected) { td += d; tu += u; }
            }
            else a.Connected = false;
        }
        _prev = nics;
        _sessDown += td * dt; _sessUp += tu * dt;
        DownText = Fmt.Rate(td, UseBits);
        UpText = Fmt.Rate(tu, UseBits);
        SessionText = $"This session   ↓ {Fmt.Size(_sessDown)}   ↑ {Fmt.Size(_sessUp)}";
        Sample?.Invoke(td, tu);
        RefreshHeader();
    }

    void ApplyApps(Dictionary<string, (ProcInfo Info, long Down, long Up)> deltas, double dt)
    {
        foreach (var (key, v) in deltas)
        {
            if (_appMap.ContainsKey(key)) continue;
            var vm = new AppUsageVM(key, v.Info.Display, IconCache.Get(v.Info.Path));
            _appMap[key] = vm; Apps.Add(vm);
        }
        double max = 0;
        foreach (var vm in _appMap.Values)
        {
            deltas.TryGetValue(vm.Key, out var d);
            vm.Update(d.Down / dt, d.Up / dt, d.Down, d.Up, UseBits);
            max = Math.Max(max, vm.Rate);
        }
        foreach (var vm in _appMap.Values) vm.Share = max > 0 ? vm.Rate / max : 0;
        ShowAppsEmpty = Apps.Count == 0;
    }

    [RelayCommand]
    void ResetApps()
    {
        _appMap.Clear(); Apps.Clear(); ShowAppsEmpty = true;
        _sessDown = _sessUp = 0;
    }

    public void RefreshHeader()
    {
        var up = AllAdapters.Where(a => a.IsOn && a.Connected && a.Kind is AdapterKind.WiFi or AdapterKind.Ethernet).ToList();
        IsOnline = up.Count > 0;
        StatusTitle = IsOnline ? "Online" : "Offline";
        StatusSub = IsOnline ? "Connected via " + string.Join(" + ", up.Select(a => a.Name))
            : AllAdapters.Any(a => a.IsOn && a.Kind != AdapterKind.Virtual) ? "No active connection"
            : "Everything is switched off";
        AnyOn = Included().Any(a => a.IsOn);
        GameButtonText = AnyOn ? "Go offline" : "Go online";
        StateChanged?.Invoke();
    }

    List<AdapterVM> Included() => AllAdapters.Where(a => a.Prefs.InGameMode).ToList();

    [RelayCommand]
    public async Task ToggleGameMode()
    {
        if (MasterBusy) return;
        MasterBusy = true;
        try
        {
            var inc = Included();
            if (inc.Any(a => a.IsOn))
            {
                var on = inc.Where(a => a.IsOn).ToList();
                Settings.OfflineSnapshot = on.Select(a => a.Id).ToList();
                Settings.Save();
                await Task.WhenAll(on.Select(a => a.ApplyAndWait(false)));
            }
            else
            {
                var snap = Settings.OfflineSnapshot;
                var targets = snap.Count > 0 ? inc.Where(a => snap.Contains(a.Id)).ToList() : inc;
                if (targets.Count == 0) targets = inc;
                Settings.OfflineSnapshot = new();
                Settings.Save();
                await Task.WhenAll(targets.Select(a => a.ApplyAndWait(true)));
            }
        }
        finally { MasterBusy = false; RefreshHeader(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _etw.Dispose();
        Settings.Save();
    }
}
