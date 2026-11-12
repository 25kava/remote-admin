using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using RemoteAdmin.Shared;

internal static class DesktopScreenSharing
{
    private static readonly SemaphoreSlim exclusive = new(1, 1);
    private static readonly Lazy<Task<Control>> dispatcher = new(CreateDispatcher);

    public static async Task<IScreenShareLease?> OpenIndicatorAsync(string admin, CancellationToken token)
    {
        if (!await exclusive.WaitAsync(0, token)) return null;
        var handedOff = false;
        try
        {
            var ui = await dispatcher.Value.WaitAsync(token);
            var result = new TaskCompletionSource<IScreenShareLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) { result.TrySetResult(null); return; }
                var window = new ScreenShareWindow(admin);
                var lease = new IndicatorLease(window);
                var cancellation = token.Register(() =>
                {
                    try { ui.BeginInvoke(() => window.Close()); }
                    catch (InvalidOperationException) { }
                });
                window.Shown += (_, _) =>
                {
                    if (token.IsCancellationRequested || !IsWindowVisible(window.Handle))
                    { window.Close(); return; }
                    result.TrySetResult(lease);
                };
                window.FormClosed += (_, _) =>
                {
                    cancellation.Dispose();
                    lease.Revoke();
                    result.TrySetResult(null);
                };
                window.Show();
            });
            var indicator = await result.Task;
            handedOff = indicator is not null;
            return indicator;
        }
        finally { if (!handedOff) exclusive.Release(); }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    private static Task<Control> CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var control = new Control();
                _ = control.Handle;
                ready.SetResult(control);
                Application.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Screen sharing indicator" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private sealed class IndicatorLease(ScreenShareWindow window) : IScreenShareLease
    {
        private readonly CancellationTokenSource revoked = new();
        private readonly IntPtr handle = window.Handle;
        private int disposed;
        public CancellationToken Revoked => revoked.Token;
        public bool IsVisible => IsWindowVisible(handle) && !IsIconic(handle);
        public async ValueTask SetControlActiveAsync(bool active)
        {
            var ui = await dispatcher.Value;
            await ui.InvokeAsync(() => { if (!window.IsDisposed) window.SetControlActive(active); });
        }
        public void Revoke()
        {
            try { revoked.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                Revoke();
                // The window may already be closing after a local click. Post through
                // the persistent dispatcher so destroying its handle cannot lose the callback.
                var ui = await dispatcher.Value;
                await ui.InvokeAsync(() => { if (!window.IsDisposed) window.Close(); });
            }
            finally { revoked.Dispose(); exclusive.Release(); }
        }
    }

    internal static bool IsInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
}

internal sealed class ScreenShareWindow : Form
{
    private readonly ToolTip tooltip = new() { InitialDelay = 200, AutoPopDelay = 10000, ShowAlways = true };
    private readonly string admin;
    private readonly ScreenStopButton stop;

    public ScreenShareWindow(string admin)
    {
        SuspendLayout();
        this.admin = admin;
        Text = "Skärmdelning pågår – klicka för att stoppa";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(40, 40);
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MinimumSize = new Size(40, 40);
        MaximumSize = new Size(40, 40);
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(239, 68, 68);
        AccessibleName = "Skärmdelning pågår";
        stop = new ScreenStopButton { Dock = DockStyle.Fill, AccessibleName = "Stoppa skärmdelning",
            AccessibleDescription = $"Din huvudskärm delas med Admin på {admin}. Klicka för att stoppa delningen." };
        stop.Click += (_, _) => Close();
        Controls.Add(stop);
        tooltip.SetToolTip(stop, $"Skärmdelning pågår\nAdmin: {admin}\nKlicka för att stoppa delningen");
        CancelButton = stop;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        FormClosed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= SessionChanged;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            tooltip.Dispose();
        };
        ResumeLayout(true);
    }

    public void SetControlActive(bool active)
    {
        Text = active ? "Fjärrstyrning pågår – klicka för att stoppa" : "Skärmdelning pågår – klicka för att stoppa";
        AccessibleName = active ? "Fjärrstyrning pågår" : "Skärmdelning pågår";
        stop.ControlActive = active;
        stop.AccessibleName = active ? "Stoppa fjärrstyrning och skärmdelning" : "Stoppa skärmdelning";
        tooltip.SetToolTip(stop, $"{(active ? "Fjärrstyrning och skärmdelning pågår" : "Skärmdelning pågår")}\nAdmin: {admin}\nKlicka för att stoppa");
        stop.Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        // A client launched with SW_HIDE must still show the sharing indicator.
        ShowWindow(Handle, 4);
        PlaceIndicator();
        base.OnShown(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var circle = new GraphicsPath();
        circle.AddEllipse(ClientRectangle);
        var previous = Region;
        Region = new Region(circle);
        previous?.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        PlaceIndicator();
    }

    private void PlaceIndicator()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        var margin = (int)Math.Round(16 * DeviceDpi / 96d);
        Location = new Point(area.Right - Width - margin, area.Top + margin);
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        try { BeginInvoke(PlaceIndicator); }
        catch (InvalidOperationException) { }
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            try { BeginInvoke(() => Close()); }
            catch (InvalidOperationException) { }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private sealed class ScreenStopButton : Button
    {
        private bool hover;
        [System.ComponentModel.DefaultValue(false)]
        public bool ControlActive { get; set; }
        public ScreenStopButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(1, 1, Width - 2, Height - 2);
            using var red = new SolidBrush(hover ? Color.FromArgb(220, 38, 38) : Color.FromArgb(239, 68, 68));
            using var ring = new Pen(ControlActive ? Color.Gold : Color.White, Math.Max(1, DeviceDpi / 96f));
            e.Graphics.FillEllipse(red, bounds);
            e.Graphics.DrawEllipse(ring, bounds);
            var size = Math.Max(8, Width / 4);
            e.Graphics.FillRectangle(Brushes.White, (Width - size) / 2f, (Height - size) / 2f, size, size);
            if (Focused) e.Graphics.DrawEllipse(ring, 5, 5, Width - 10, Height - 10);
        }
    }
}
