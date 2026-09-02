using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FocusListFloat;

internal static class Program
{
    private const string MutexName = "Local\\FocusListFloatingWindow";
    private const string ActivationEventName = "Local\\FocusListFloatingWindow.Activate";

    internal static void Log(string msg)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusList");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "desktop.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log($"UnhandledException: {e.ExceptionObject}");
        Application.ThreadException += (s, e) => Log($"ThreadException: {e.Exception}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Log("Program.Main starting...");
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);

        if (!createdNew)
        {
            Log("Another instance exists. Signaling activationEvent and exiting.");
            activationEvent.Set();
            return;
        }

        Log("Instance acquired mutex. Initializing application.");
        ApplicationConfiguration.Initialize();
        Application.Run(new FocusListForm(activationEvent));
        Log("Application.Run finished.");
    }
}

internal sealed class FocusListForm : Form
{
    private const int HeaderHeight = 38;
    private const int CollapsedHeight = HeaderHeight;
    private const int ResizeGrip = 7;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private readonly EventWaitHandle _activationEvent;
    private readonly System.Windows.Forms.Timer _activationTimer = new() { Interval = 300 };
    private readonly Panel _header = new();
    private readonly Button _pinButton = new();
    private readonly Button _collapseButton = new();
    private readonly Button _closeButton = new();
    private readonly WebView2 _webView = new();
    private readonly string _statePath;
    private Process? _serverProcess;
    private Uri? _serverBaseUri;
    private string? _serverToken;
    private bool _collapsed;
    private bool _closing;
    private int _expandedHeight = 640;

    internal FocusListForm(EventWaitHandle activationEvent)
    {
        _activationEvent = activationEvent;
        Text = "焦点清单";
        Icon = CreateAppIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.White;
        MinimumSize = new Size(320, 420);
        Size = new Size(380, 640);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusList");
        Directory.CreateDirectory(dataDirectory);
        _statePath = Path.Combine(dataDirectory, "window.json");

        ConfigureHeader();
        ConfigureWebView();
        LoadWindowState();
        ApplyWindowState();
        ApplyWindowRegion();

        HandleCreated += (_, _) => Program.Log($"Form.HandleCreated: {Handle}");
        HandleDestroyed += (_, _) => Program.Log($"Form.HandleDestroyed");
        _webView.HandleCreated += (_, _) => Program.Log($"_webView.HandleCreated: {_webView.Handle}");
        _webView.HandleDestroyed += (_, _) => Program.Log($"_webView.HandleDestroyed");

        _activationTimer.Tick += (_, _) => ActivateIfRequested();
        Move += (_, _) => SaveWindowState();
        ResizeEnd += (_, _) => SaveWindowState();
        Resize += (_, _) => ApplyWindowRegion();
        FormClosing += OnFormClosing;
        Shown += (_, _) => _ = InitializeAsync();
    }

    private void ConfigureHeader()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = HeaderHeight;
        _header.BackColor = Color.White;
        _header.Cursor = Cursors.SizeAll;
        _header.MouseDown += BeginNativeDrag;
        _header.Paint += PaintHeader;

        ConfigureHeaderButton(_pinButton, "置顶", "切换置顶或普通窗口层级");
        ConfigureHeaderButton(_collapseButton, "—", "收拢窗口");
        ConfigureHeaderButton(_closeButton, "×", "关闭焦点清单");

        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(254, 226, 226);
        _closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(254, 202, 202);
        _closeButton.MouseEnter += (_, _) =>
        {
            _closeButton.ForeColor = Color.FromArgb(220, 38, 38);
            _closeButton.FlatAppearance.BorderColor = Color.FromArgb(254, 202, 202);
        };
        _closeButton.MouseLeave += (_, _) =>
        {
            _closeButton.ForeColor = Color.FromArgb(100, 116, 139);
            _closeButton.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        };

        _pinButton.Click += (_, _) =>
        {
            TopMost = !TopMost;
            UpdatePinButton();
            SaveWindowState();
        };
        _collapseButton.Click += (_, _) => ToggleCollapsed();
        _closeButton.Click += (_, _) => Close();

        _header.Controls.Add(_pinButton);
        _header.Controls.Add(_collapseButton);
        _header.Controls.Add(_closeButton);
        _header.Resize += (_, _) => LayoutHeader();
        Controls.Add(_header);
        LayoutHeader();
        UpdatePinButton();
    }

    private static void ConfigureHeaderButton(Button button, string text, string accessibleName)
    {
        button.Text = text;
        button.AccessibleName = accessibleName;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 232, 240);
        button.BackColor = Color.FromArgb(255, 255, 255);
        button.ForeColor = Color.FromArgb(100, 116, 139);
        button.Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        button.UseVisualStyleBackColor = false;
    }

    private void LayoutHeader()
    {
        const int buttonSize = 28;
        const int buttonY = 5;
        const int buttonGap = 4;
        const int rightInset = 8;
        const int pinWidth = 42;
        _closeButton.SetBounds(_header.ClientSize.Width - buttonSize - rightInset, buttonY, buttonSize, buttonSize);
        _collapseButton.SetBounds(_closeButton.Left - buttonSize - buttonGap, buttonY, buttonSize, buttonSize);
        _pinButton.SetBounds(_collapseButton.Left - pinWidth - buttonGap, buttonY, pinWidth, buttonSize);
        ApplyRoundedRegion(_pinButton, 10);
        ApplyRoundedRegion(_collapseButton, 10);
        ApplyRoundedRegion(_closeButton, 10);
    }

    private void PaintHeader(object? sender, PaintEventArgs eventArgs)
    {
        using var pen = new Pen(Color.FromArgb(231, 235, 242), 1);
        eventArgs.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var outerPath = CreateRoundedRectangle(new Rectangle(0, 0, 32, 32), 10);
        using var outerBrush = new SolidBrush(Color.FromArgb(232, 240, 255));
        graphics.FillPath(outerBrush, outerPath);
        using var innerPath = CreateRoundedRectangle(new Rectangle(8, 7, 16, 18), 5);
        using var innerBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        graphics.FillPath(innerBrush, innerPath);
        using var checkPen = new Pen(Color.White, 2.4f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        graphics.DrawLines(checkPen, [new Point(12, 16), new Point(14, 18), new Point(20, 12)]);

        var handle = bitmap.GetHicon();
        try
        {
            using var nativeIcon = Icon.FromHandle(handle);
            return (Icon)nativeIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static void ApplyRoundedRegion(Control control, int radius)
    {
        using var path = CreateRoundedRectangle(control.ClientRectangle, radius);
        control.Region = new Region(path);
    }

    private void ApplyWindowRegion()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var path = CreateRoundedRectangle(ClientRectangle, 22);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ConfigureWebView()
    {
        // Keep the web surface below the native title area. Dock=Fill caused it to
        // occupy the entire form and visually run underneath the header controls.
        _webView.Dock = DockStyle.None;
        _webView.Location = new Point(0, HeaderHeight);
        _webView.Size = new Size(ClientSize.Width, Math.Max(0, ClientSize.Height - HeaderHeight));
        _webView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _webView.DefaultBackgroundColor = Color.White;
        Controls.Add(_webView);
        _webView.BringToFront();
        _header.BringToFront();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var root = ResolvePluginRoot();
            _serverToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var port = await StartServerAsync(root, _serverToken);
            if (_closing || IsDisposed) return;
            _serverBaseUri = new Uri($"http://127.0.0.1:{port}/");

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusList",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            ConfigureBrowserSecurity(_webView.CoreWebView2);
            _webView.Source = new Uri($"{_serverBaseUri}?token={Uri.EscapeDataString(_serverToken)}");

            _activationTimer.Start();
        }
        catch (Exception error)
        {
            Program.Log($"InitializeAsync error: {error}");
            Text = "焦点清单 · 启动失败";
            MessageBox.Show(
                this,
                $"焦点清单无法启动：\n{error.Message}",
                "焦点清单",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string ResolvePluginRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "scripts", "focus-list-server.mjs")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("无法从桌面程序位置找到 focus-list 插件根目录。");
    }

    private async Task<int> StartServerAsync(string root, string token)
    {
        var script = Path.Combine(root, "scripts", "focus-list-server.mjs");
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        _serverProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地任务服务。");
        var lineTask = _serverProcess.StandardOutput.ReadLineAsync();
        var line = await lineTask.WaitAsync(TimeSpan.FromSeconds(12));
        if (string.IsNullOrWhiteSpace(line))
        {
            var details = await _serverProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"本地任务服务没有返回端口。{details}".Trim());
        }

        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("port", out var portElement) || !portElement.TryGetInt32(out var port))
        {
            throw new InvalidOperationException("本地任务服务返回了无效端口。");
        }
        return port;
    }

    private static void ConfigureBrowserSecurity(CoreWebView2 browser)
    {
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.Settings.IsZoomControlEnabled = false;
        browser.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
        browser.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
        browser.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
    }

    private void BeginNativeDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        SaveWindowState();
    }

    private void ToggleCollapsed()
    {
        if (_collapsed)
        {
            _collapsed = false;
            Height = Math.Max(_expandedHeight, 420);
            MinimumSize = new Size(320, 420);
            _collapseButton.Text = "—";
            _collapseButton.AccessibleName = "收拢窗口";
        }
        else
        {
            _expandedHeight = Math.Max(Height, 420);
            _collapsed = true;
            MinimumSize = new Size(260, CollapsedHeight);
            Height = CollapsedHeight;
            _collapseButton.Text = "□";
            _collapseButton.AccessibleName = "展开窗口";
        }
        SaveWindowState();
    }

    private void ApplyWindowState()
    {
        UpdatePinButton();
        if (_collapsed)
        {
            MinimumSize = new Size(260, CollapsedHeight);
            Height = CollapsedHeight;
            _collapseButton.Text = "□";
            _collapseButton.AccessibleName = "展开窗口";
        }
        Location = ClampToVisibleScreen(Location, Size);
    }

    private void UpdatePinButton()
    {
        _pinButton.Text = TopMost ? "置顶✓" : "置顶";
        _pinButton.BackColor = TopMost ? Color.FromArgb(239, 246, 255) : Color.FromArgb(255, 255, 255);
        _pinButton.ForeColor = TopMost ? Color.FromArgb(37, 99, 235) : Color.FromArgb(100, 116, 139);
        _pinButton.FlatAppearance.BorderColor = TopMost ? Color.FromArgb(191, 219, 254) : Color.FromArgb(226, 232, 240);
        _pinButton.Font = new Font("Microsoft YaHei UI", 8f, TopMost ? FontStyle.Bold : FontStyle.Regular);
    }

    private void ActivateIfRequested()
    {
        if (!_activationEvent.WaitOne(0)) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
    }

    private void LoadWindowState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                Location = new Point(workArea.Right - Width - 24, workArea.Top + 84);
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_statePath));
            var root = document.RootElement;
            var width = root.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : Width;
            var height = root.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : Height;
            var x = root.TryGetProperty("x", out var xElement) ? xElement.GetInt32() : Left;
            var y = root.TryGetProperty("y", out var yElement) ? yElement.GetInt32() : Top;
            TopMost = !root.TryGetProperty("topMost", out var topMostElement) || topMostElement.GetBoolean();
            _collapsed = root.TryGetProperty("collapsed", out var collapsedElement) && collapsedElement.GetBoolean();
            _expandedHeight = root.TryGetProperty("expandedHeight", out var expandedHeightElement)
                ? Math.Max(420, expandedHeightElement.GetInt32())
                : Math.Max(420, height);
            Size = new Size(Math.Clamp(width, 320, 900), Math.Clamp(height, 420, 1200));
            Location = ClampToVisibleScreen(new Point(x, y), Size);
        }
        catch
        {
            var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(workArea.Right - Width - 24, workArea.Top + 84);
        }
    }

    private void SaveWindowState()
    {
        if (_closing || WindowState == FormWindowState.Minimized) return;
        try
        {
            if (!_collapsed) _expandedHeight = Math.Max(Height, 420);
            var payload = JsonSerializer.Serialize(new
            {
                x = Left,
                y = Top,
                width = Width,
                height = _collapsed ? _expandedHeight : Height,
                expandedHeight = _expandedHeight,
                topMost = TopMost,
                collapsed = _collapsed,
            });
            File.WriteAllText(_statePath, payload);
        }
        catch
        {
            // Window state persistence must never prevent task access.
        }
    }

    private static Point ClampToVisibleScreen(Point location, Size size)
    {
        var candidate = new Rectangle(location, size);
        var screen = Screen.AllScreens.FirstOrDefault(item => item.WorkingArea.IntersectsWith(candidate))
            ?? Screen.PrimaryScreen;
        var workArea = screen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var x = Math.Clamp(location.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        var y = Math.Clamp(location.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return new Point(x, y);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest && !_collapsed)
        {
            base.WndProc(ref message);
            var packed = message.LParam.ToInt64();
            var screenPoint = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            var point = PointToClient(screenPoint);
            var left = point.X <= ResizeGrip;
            var right = point.X >= ClientSize.Width - ResizeGrip;
            var top = point.Y <= ResizeGrip;
            var bottom = point.Y >= ClientSize.Height - ResizeGrip;
            if (left && top) message.Result = (IntPtr)HtTopLeft;
            else if (right && top) message.Result = (IntPtr)HtTopRight;
            else if (left && bottom) message.Result = (IntPtr)HtBottomLeft;
            else if (right && bottom) message.Result = (IntPtr)HtBottomRight;
            else if (left) message.Result = (IntPtr)HtLeft;
            else if (right) message.Result = (IntPtr)HtRight;
            else if (top) message.Result = (IntPtr)HtTop;
            else if (bottom) message.Result = (IntPtr)HtBottom;
            return;
        }
        base.WndProc(ref message);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        Program.Log($"OnFormClosing called: CloseReason={eventArgs.CloseReason}, Cancel={eventArgs.Cancel}");
        SaveWindowState();
        _closing = true;
        _activationTimer.Stop();
        try
        {
            if (_serverProcess is { HasExited: false }) _serverProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // The child may already have exited through its parent monitor.
        }
        _serverProcess?.Dispose();
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
