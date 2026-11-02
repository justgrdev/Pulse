using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pulse;

public partial class MainWindow : Window
{
    const int WM_HOTKEY = 0x0312, HotkeyId = 0x504C;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMSBT_MAINWINDOW = 2; // Mica
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly MainViewModel _vm;
    IntPtr _hwnd;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Sample += Graph.Push;
        Graph.UseBits = vm.UseBits;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UseBits)) Graph.UseBits = vm.UseBits;
        };
        StateChanged += (_, _) =>
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1) : new Thickness(0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd)!;
        int dark = 1, noColor = DWMWA_COLOR_NONE, border = 0x3A3A3A, mica = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref noColor, 4); // otherwise Windows tints the title bar with the accent color
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, 4);
        if (DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, 4) == 0)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
        }
        RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'N');
        source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _ = _vm.ToggleGameMode();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Exiting && _vm.Settings.CloseToTray) { e.Cancel = true; Hide(); return; }
        UnregisterHotKey(_hwnd, HotkeyId);
        base.OnClosing(e);
    }

    void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.Relaunch(Environment.GetCommandLineArgs().Skip(1).Append("--wait").ToArray()))
            Application.Current.Dispatcher.BeginInvoke(() => { _vm.Dispose(); Environment.Exit(0); });
    }
}
