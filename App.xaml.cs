using System.Windows;
using System.Windows.Interop;

namespace Pulse;

public partial class App : Application
{
    public static bool Exiting { get; private set; }

    Mutex? _mutex;
    EventWaitHandle? _showEvt;
    MainViewModel? _vm;
    MainWindow? _win;
    Tray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (!Elevation.IsAdmin && !args.Contains("--no-elevate") && Elevation.Relaunch(args))
        {
            Shutdown();
            return;
        }

        _showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Pulse.Show");
        _mutex = new Mutex(true, "Pulse.Single", out bool first);
        for (int i = 0; !first && args.Contains("--wait") && i < 25; i++) // restarting: give the old instance time to exit
        {
            _mutex.Dispose();
            Thread.Sleep(200);
            _mutex = new Mutex(true, "Pulse.Single", out first);
        }
        if (!first) { _showEvt.Set(); Shutdown(); return; }
        new Thread(() => { while (_showEvt.WaitOne()) Dispatcher.BeginInvoke(ShowMain); }) { IsBackground = true }.Start();

        _vm = new MainViewModel();
        _win = new MainWindow(_vm);
        _tray = new Tray(_vm, ShowMain, ExitApp);

        if (args.Contains("--tray")) new WindowInteropHelper(_win).EnsureHandle();
        else _win.Show();

        _ = _vm.StartAsync();
    }

    void ShowMain()
    {
        if (_win == null) return;
        _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
    }

    void ExitApp()
    {
        Exiting = true;
        _tray?.Dispose();
        _vm?.Dispose();
        Shutdown();
    }
}
