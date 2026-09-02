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
    private const int CompactWidth = 208;
    private const int CompactHeight = 64;
    private const int ResizeGrip = 7;
    private const int WmNcHitTest = 0x0084;
    private const int WmSysCommand = 0x0112;
    private const int ScDragMove = 0xF012;
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
    private readonly WebView2 _webView = new();
    private readonly Panel _compactPanel = new();
    private readonly Button _compactTopmostButton = new();
    private readonly Button _compactExpandButton = new();
    private readonly Button _compactCloseButton = new();
    private readonly string _statePath;
    private Process? _serverProcess;
    private Uri? _serverBaseUri;
    private string? _serverToken;
    private bool _collapsed;
    private bool _closing;
    private int _expandedWidth = 380;
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
        BackColor = Color.FromArgb(238, 244, 253);
        MinimumSize = new Size(320, 420);
        Size = new Size(380, 640);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusList");
        Directory.CreateDirectory(dataDirectory);
        _statePath = Path.Combine(dataDirectory, "window.json");

        ConfigureWebView();
        ConfigureCompactPanel();
        LoadWindowState();
        ApplyWindowState();
        ApplyWindowRegion();

        HandleCreated += (_, _) =>
        {
            Program.Log($"Form.HandleCreated: {Handle}");
            ApplyWindowRegion();
        };
        HandleDestroyed += (_, _) => Program.Log($"Form.HandleDestroyed");
        _webView.HandleCreated += (_, _) => Program.Log($"_webView.HandleCreated: {_webView.Handle}");
        _webView.HandleDestroyed += (_, _) => Program.Log($"_webView.HandleDestroyed");

        _activationTimer.Tick += (_, _) => ActivateIfRequested();
        Move += (_, _) => SaveWindowState();
        ResizeEnd += (_, _) => SaveWindowState();
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized)
            {
                ApplyWindowRegion();
                _webView.Invalidate();
            }
        };
        FormClosing += OnFormClosing;
        Shown += (_, _) => _ = InitializeAsync();
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

    private void ApplyWindowRegion()
    {
        if (WindowState == FormWindowState.Minimized || ClientSize.Width < 2 || ClientSize.Height < 2) return;
        EnableNativeRoundedCorners();
        var previous = Region;
        // Let DWM perform the anti-aliased outer rounding. A Win32 Region is
        // hard-edged and leaves light square wedges at the four corners.
        Region = null;
        previous?.Dispose();
    }

    private void EnableNativeRoundedCorners()
    {
        if (!IsHandleCreated) return;
        try
        {
            var preference = 2; // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
            var borderColor = ColorTranslator.ToWin32(BackColor);
            NativeMethods.DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));
        }
        catch { }
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
        _webView.Dock = DockStyle.Fill;
        _webView.Location = Point.Empty;
        _webView.Size = ClientSize;
        _webView.DefaultBackgroundColor = Color.FromArgb(238, 244, 253);
        Controls.Add(_webView);
    }

    private void ConfigureCompactPanel()
    {
        _compactPanel.Dock = DockStyle.Fill;
        _compactPanel.BackColor = Color.FromArgb(238, 244, 253);
        _compactPanel.Visible = false;
        _compactPanel.Cursor = Cursors.Hand;
        _compactPanel.TabStop = true;
        _compactPanel.Paint += (_, eventArgs) => PaintCompactPanel(eventArgs.Graphics);
        _compactPanel.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left) ToggleCollapsed();
        };

        ConfigureCompactButton(_compactTopmostButton, "compactTopmostButton", "切换置顶", new Point(82, 14), CompactButtonKind.Topmost);
        _compactTopmostButton.Click += (_, _) =>
        {
            TopMost = !TopMost;
            SaveWindowState();
            _compactTopmostButton.Invalidate();
            PublishWindowState();
        };

        ConfigureCompactButton(_compactExpandButton, "compactExpandButton", "展开任务清单", new Point(124, 14), CompactButtonKind.Expand);
        _compactExpandButton.Click += (_, _) => ToggleCollapsed();

        ConfigureCompactButton(_compactCloseButton, "compactCloseButton", "关闭焦点清单", new Point(166, 14), CompactButtonKind.Close);
        _compactCloseButton.Click += (_, _) => Close();

        _compactPanel.Controls.Add(_compactTopmostButton);
        _compactPanel.Controls.Add(_compactExpandButton);
        _compactPanel.Controls.Add(_compactCloseButton);
        Controls.Add(_compactPanel);
        _compactPanel.BringToFront();
    }

    private enum CompactButtonKind
    {
        Topmost,
        Expand,
        Close,
    }

    private void ConfigureCompactButton(Button button, string name, string accessibleName, Point location, CompactButtonKind kind)
    {
        button.Name = name;
        button.AccessibleName = accessibleName;
        button.Text = string.Empty;
        button.TabStop = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Color.FromArgb(238, 244, 253);
        button.Size = new Size(36, 36);
        button.Location = location;
        button.Padding = Padding.Empty;
        button.Cursor = Cursors.Hand;
        using var buttonPath = CreateRoundedRectangle(new Rectangle(0, 0, 36, 36), 14);
        button.Region = new Region(buttonPath);
        button.Paint += (_, eventArgs) => PaintCompactButton(eventArgs.Graphics, kind, button.ClientSize);
    }

    private void PaintCompactButton(Graphics graphics, CompactButtonKind kind, Size size)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(size.Width - 1, 1), Math.Max(size.Height - 1, 1));
        var active = kind is CompactButtonKind.Topmost or CompactButtonKind.Expand;
        using var fill = new SolidBrush(active ? Color.FromArgb(239, 246, 255) : Color.FromArgb(248, 251, 255));
        using var border = new Pen(active ? Color.FromArgb(191, 219, 254) : Color.FromArgb(203, 213, 225), 1f);
        using var path = CreateRoundedRectangle(bounds, 14);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var iconPen = new Pen(active ? Color.FromArgb(37, 99, 235) : Color.FromArgb(100, 116, 139), 1.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        if (kind == CompactButtonKind.Topmost)
        {
            graphics.DrawLine(iconPen, new Point(18, 27), new Point(18, 9));
            graphics.DrawLine(iconPen, new Point(12, 15), new Point(18, 9));
            graphics.DrawLine(iconPen, new Point(24, 15), new Point(18, 9));
            graphics.DrawLine(iconPen, new Point(11, 27), new Point(25, 27));
        }
        else if (kind == CompactButtonKind.Expand)
        {
            graphics.DrawLine(iconPen, new Point(11, 25), new Point(25, 11));
            graphics.DrawLine(iconPen, new Point(17, 11), new Point(25, 11));
            graphics.DrawLine(iconPen, new Point(25, 11), new Point(25, 19));
        }
        else
        {
            graphics.DrawLine(iconPen, new Point(11, 11), new Point(25, 25));
            graphics.DrawLine(iconPen, new Point(25, 11), new Point(11, 25));
        }
    }

    private void PaintCompactPanel(Graphics graphics)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var dragBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var dragPath = CreateRoundedRectangle(new Rectangle(16, 31, 24, 3), 2);
        graphics.FillPath(dragBrush, dragPath);
    }

    private void ApplyCompactVisualState()
    {
        _webView.Visible = !_collapsed;
        _compactPanel.Visible = _collapsed;
        if (_collapsed)
        {
            _compactPanel.BringToFront();
            return;
        }

        _webView.BringToFront();
        _webView.Invalidate();
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

    private void ConfigureBrowserSecurity(CoreWebView2 browser)
    {
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.Settings.IsZoomControlEnabled = false;
        browser.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
        browser.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
        browser.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
        browser.WebMessageReceived += HandleBrowserMessage;
        browser.NavigationCompleted += (_, _) => PublishWindowState();
    }

    private void HandleBrowserMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "window-action") return;
            if (!root.TryGetProperty("action", out var action)) return;
            var actionName = action.GetString();
            var screenX = root.TryGetProperty("screenX", out var x) ? x.GetInt32() : 0;
            var screenY = root.TryGetProperty("screenY", out var y) ? y.GetInt32() : 0;

            BeginInvoke(() =>
            {
                switch (actionName)
                {
                    case "topmost":
                        TopMost = !TopMost;
                        SaveWindowState();
                        PublishWindowState();
                        break;
                    case "collapse":
                        ToggleCollapsed();
                        break;
                    case "close":
                        Close();
                        break;
                    case "drag":
                        BeginNativeDragFromWeb(screenX, screenY);
                        break;
                }
            });
        }
        catch
        {
            // Ignore malformed browser messages; task access must stay available.
        }
    }

    private void PublishWindowState()
    {
        if (_webView.CoreWebView2 is null || _closing || IsDisposed) return;
        var state = JsonSerializer.Serialize(new { type = "window-state", topmost = TopMost, collapsed = _collapsed });
        _webView.CoreWebView2.PostWebMessageAsJson(state);
    }

    private void BeginNativeDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WmSysCommand, (IntPtr)ScDragMove, IntPtr.Zero);
        SaveWindowState();
    }

    private void BeginNativeDragFromWeb(int screenX = 0, int screenY = 0)
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WmSysCommand, (IntPtr)ScDragMove, IntPtr.Zero);
        SaveWindowState();
    }

    private void ToggleCollapsed()
    {
        SuspendLayout();
        if (_collapsed)
        {
            _collapsed = false;
            MinimumSize = new Size(320, 420);
            Size = new Size(Math.Max(_expandedWidth, 320), Math.Max(_expandedHeight, 420));
            ApplyWindowRegion();
            ApplyCompactVisualState();
        }
        else
        {
            _expandedWidth = Math.Max(Width, 320);
            _expandedHeight = Math.Max(Height, 420);
            _collapsed = true;
            // Hide WebView2 before resizing so its compositor never renders into
            // the tiny thumbnail surface.
            _webView.Visible = false;
            _compactPanel.Visible = true;
            MinimumSize = new Size(CompactWidth, CompactHeight);
            Size = new Size(CompactWidth, CompactHeight);
            ApplyWindowRegion();
            _compactPanel.BringToFront();
        }

        ResumeLayout(true);
        ApplyWindowRegion();
        SaveWindowState();
        PublishWindowState();
    }

    private void ApplyWindowState()
    {
        if (_collapsed)
        {
            MinimumSize = new Size(CompactWidth, CompactHeight);
            Size = new Size(CompactWidth, CompactHeight);
        }
        PublishWindowState();
        Location = ClampToVisibleScreen(Location, Size);
        ApplyCompactVisualState();
    }

    private void ActivateIfRequested()
    {
        if (!_activationEvent.WaitOne(0)) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
        _webView.Invalidate();
        PublishWindowState();
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
            _expandedWidth = root.TryGetProperty("expandedWidth", out var expandedWidthElement)
                ? Math.Max(320, expandedWidthElement.GetInt32())
                : Math.Max(320, width);
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
        if (_closing) return;
        try
        {
            if (!_collapsed)
            {
                _expandedWidth = Math.Max(Width, 320);
                _expandedHeight = Math.Max(Height, 420);
            }
            var payload = JsonSerializer.Serialize(new
            {
                x = Left,
                y = Top,
                width = _collapsed ? _expandedWidth : Width,
                height = _collapsed ? _expandedHeight : Height,
                expandedWidth = _expandedWidth,
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

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
