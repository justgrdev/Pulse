using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Pulse;

sealed class Tray : IDisposable
{
    readonly NotifyIcon _ni;
    readonly MainViewModel _vm;
    Icon? _icon;
    bool? _lastOnline;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public Tray(MainViewModel vm, Action show, Action exit)
    {
        _vm = vm;
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
            BackColor = Color.FromArgb(44, 44, 44),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Variable Text", 9.75f),
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };
        menu.Opening += (_, _) => Build(menu, show, exit);
        _ni = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "Pulse" };
        _ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
        vm.StateChanged += Update;
        Update();
    }

    void Build(ContextMenuStrip menu, Action show, Action exit)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"{_vm.StatusTitle} — {_vm.StatusSub}") { Enabled = false });
        var game = new ToolStripMenuItem(_vm.GameButtonText) { ShortcutKeyDisplayString = "Ctrl+Alt+N", Enabled = !_vm.MasterBusy };
        game.Click += async (_, _) => await _vm.ToggleGameMode();
        menu.Items.Add(game);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var a in _vm.Adapters)
        {
            var item = new ToolStripMenuItem(a.Name) { Checked = a.IsOn, Enabled = a.Idle };
            item.Click += (_, _) => a.IsOn = !a.IsOn;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Pulse", null, (_, _) => show());
        menu.Items.Add("Exit", null, (_, _) => exit());
    }

    void Update()
    {
        _ni.Text = $"Pulse — {_vm.StatusTitle}";
        if (_lastOnline == _vm.IsOnline) return;
        _lastOnline = _vm.IsOnline;
        var old = _icon;
        _icon = Make(_vm.IsOnline);
        _ni.Icon = _icon;
        old?.Dispose();
    }

    static Icon Make(bool online)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(online ? Color.FromArgb(96, 205, 255) : Color.FromArgb(255, 125, 135));
        using var fg = new SolidBrush(Color.FromArgb(24, 28, 36));
        g.FillEllipse(bg, 1, 1, 30, 30);
        g.FillPolygon(fg, [new PointF(11, 6.5f), new PointF(6, 13), new PointF(16, 13)]);
        g.FillRectangle(fg, 9.5f, 12, 3, 13);
        g.FillPolygon(fg, [new PointF(21, 25.5f), new PointF(16, 19), new PointF(26, 19)]);
        g.FillRectangle(fg, 19.5f, 7, 3, 13);
        var h = bmp.GetHicon();
        var ic = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return ic;
    }

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
        _icon?.Dispose();
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(44, 44, 44), Hi = Color.FromArgb(66, 66, 66), Bd = Color.FromArgb(72, 72, 72);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Bd;
        public override Color MenuItemBorder => Hi;
        public override Color MenuItemSelected => Hi;
        public override Color MenuItemSelectedGradientBegin => Hi;
        public override Color MenuItemSelectedGradientEnd => Hi;
        public override Color SeparatorDark => Bd;
        public override Color SeparatorLight => Bd;
        public override Color CheckBackground => Hi;
        public override Color CheckSelectedBackground => Hi;
        public override Color CheckPressedBackground => Hi;
    }
}
