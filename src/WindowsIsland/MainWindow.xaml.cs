using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;

namespace WindowsIsland;

/// <summary>
/// 灵动岛主窗口：无边框、透明、置顶、不抢焦点。
///
/// 三层状态：
///   收起（鼠标穿透）→ 悬停展开（可点击）→ 点击钉住（可点击，直到点岛外）
/// 展开时吸附屏幕顶边，宽度随内容自适应。
/// </summary>
public partial class MainWindow : Window
{
    private const double CollapsedWidth = 144d;
    private const double CollapsedHeight = 22d;
    private const double ExpandedHeight = 58d;
    private const double MinExpandedWidth = 396d;

    private const double FloatingTopMargin = 10d;
    private const double AttachedTopMargin = 0d;

    // 窗口鼠标穿透，收不到 WPF 的 MouseEnter，因此自己轮询光标做命中检测。
    private static readonly TimeSpan HoverPollInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(80);
    private const double HoverPadding = 3d;

    private const int HotkeyIdQuit = 0x1001;
    private const int HotkeyIdToggle = 0x1002;
    private const int HotkeyIdMax = 0x1003;

    private const string PlayGlyphPlaying = "\uE769"; // 播放中 → 显示暂停图标
    private const string PlayGlyphPaused = "\uE768";  // 暂停中 → 显示播放图标

    // 开发期自检用：设置 ISLAND_FORCE_EXPANDED=1 可强制保持展开，便于截图/调样式。
    private static readonly bool ForceExpanded =
        Environment.GetEnvironmentVariable("ISLAND_FORCE_EXPANDED") == "1";

    private static readonly bool DebugLogEnabled =
        Environment.GetEnvironmentVariable("ISLAND_LOG") == "1";


    private readonly ForegroundWatcher _watcher = new();
    private readonly MediaWatcher _mediaWatcher;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly string _settingsPath;
    private readonly Settings _settings;
    private readonly SystemStatsSampler _stats = new();
    private readonly NotificationWatcher _notifications;

    private bool _maximized;
    private int _progressTicks;
    private double _progressRatio;
    private double _progressAnchorRatio;
    private DateTime _progressAnchorAt;
    private bool _renderedMaximized;
    private bool _lastMeasuredMaximized;
    private IntPtr _currentMonitor;

    private TrayIcon? _tray;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;

    private bool _expandedByFocus;
    private bool _expandedByHover;
    private bool _pinned;
    private bool _interactive;
    private bool _renderedExpanded;
    private double _renderedWidth;
    private double _renderedHeight;

    private bool _hiddenByUser;
    private bool _fullscreen;
    private bool _windowVisible = true;

    private MediaInfo? _media;
    private List<LrcLine>? _lyrics;
    private string? _lyricsTitle;
    private DateTime? _hoverStartedAt;
    private DateTime? _hoverLeftAt;
    private bool _lastInsideLogged;

    private Native.LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;

    public MainWindow()
    {
        InitializeComponent();

        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowsIsland",
            "island-settings.json");
        _settings = LoadSettings(_settingsPath);

        _notifications = new NotificationWatcher(Dispatcher);

        _mediaWatcher = new MediaWatcher(Dispatcher);

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HoverPollInterval
        };
        _hoverTimer.Tick += (_, _) => PollHover();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ---------------- 设置 ----------------

    private sealed class Settings
    {
        public bool HoverEnabled { get; set; } = true;
        public bool AlwaysOnTop { get; set; } = true;
        public bool GlassStyle { get; set; }

        /// <summary>Shake + glow when a notification arrives.</summary>
        public bool NotificationAlert { get; set; } = true;

        /// <summary>Start with Windows (per-user Run key).</summary>
        public bool AutoStart { get; set; }

        /// <summary>Theme accent colour, "#RRGGBB".</summary>
        public string AccentColor { get; set; } = "#5BE49B";
        public string LyricsFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Lyrics");

        public List<ShortcutItem> Shortcuts { get; set; } = DefaultShortcuts();

        private static List<ShortcutItem> DefaultShortcuts() => new()
        {
            new ShortcutItem { Name = "文件资源管理器", Target = "explorer.exe" },
            new ShortcutItem { Name = "计算器", Target = "calc.exe" },
            new ShortcutItem { Name = "记事本", Target = "notepad.exe" },
            new ShortcutItem { Name = "任务管理器", Target = "taskmgr.exe" },
            new ShortcutItem { Name = "系统设置", Target = "ms-settings:" },
            new ShortcutItem { Name = "截图", Target = "ms-screenclip:" },
            new ShortcutItem { Name = "终端", Target = "wt.exe" },
            new ShortcutItem { Name = "剪贴板设置", Target = "ms-settings:clipboard" }
        };
    }

    /// <summary>快捷启动项。Target 可以是 exe 路径、可执行名或协议（ms-settings: 之类）。</summary>
    private sealed class ShortcutItem
    {
        public string Name { get; set; } = string.Empty;

        public string Target { get; set; } = string.Empty;
    }

    private static Settings LoadSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
            }
        }
        catch
        {
            // 设置文件坏了就用默认值
        }

        return new Settings();
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不下来就算了
        }
    }

    // ---------------- 窗口初始化 ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // 不进 Alt+Tab、不抢焦点；收起态先设成鼠标穿透。
        var exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(
            _hwnd,
            Native.GWL_EXSTYLE,
            exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);

        Topmost = _settings.AlwaysOnTop;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        Native.RegisterHotKey(_hwnd, HotkeyIdQuit, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x51 /* Q */);
        Native.RegisterHotKey(_hwnd, HotkeyIdToggle, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x49 /* I */);
        Native.RegisterHotKey(_hwnd, HotkeyIdMax, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x4D /* M */);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnActiveMonitor();
        UpdateClock();
        _clockTimer.Start();

        _watcher.DesktopFocusChanged += OnDesktopFocusChanged;
        _watcher.FullscreenChanged += OnFullscreenChanged;
        _watcher.Start();

        // 先订阅再启动：Start() 内部的首次刷新是同步完成的，
        // 晚订阅会错过那一次 Publish，而后续轮询因“内容未变”不会再发事件。
        _mediaWatcher.Changed += OnMediaChanged;

        if (Environment.GetEnvironmentVariable("ISLAND_NO_MEDIA") != "1")
        {
            _mediaWatcher.Start();
        }

        _progressTimer.Start();

        if (Environment.GetEnvironmentVariable("ISLAND_NO_TRAY") != "1")
        {
            _tray = new TrayIcon
            {
                ExitRequested = Close,
                RestartRequested = Restart,
                HoverToggled = enabled =>
                {
                    _settings.HoverEnabled = enabled;
                    SaveSettings();
                    if (!enabled)
                    {
                        _expandedByHover = false;
                        RefreshState(animate: true);
                    }
                },
                AutoStartToggled = enabled =>
                {
                    _settings.AutoStart = enabled;
                    AutoStart.Apply(enabled);
                    SaveSettings();
                },
                GlassToggled = enabled =>
                {
                    _settings.GlassStyle = enabled;
                    SaveSettings();
                    ApplyStyle();
                },
                AlwaysOnTopToggled = enabled =>
                {
                    _settings.AlwaysOnTop = enabled;
                    SaveSettings();
                    Topmost = enabled;
                },
                VisibleToggled = visible =>
                {
                    _hiddenByUser = !visible;
                    UpdateWindowVisibility();
                }
            };
            _tray.SetAutoStartChecked(_settings.AutoStart);
            _tray.SetGlassChecked(_settings.GlassStyle);
            _tray.SetHoverChecked(_settings.HoverEnabled);
            _tray.SetAlwaysOnTopChecked(_settings.AlwaysOnTop);
        }

        _expandedByFocus = _watcher.IsDesktopFocused;
        ApplyMode(animate: false);
        RefreshState(animate: false);
        _notifications.Changed += OnNotificationsChanged;
        _notifications.Start();
        RenderNotifications();
        UpdateMaxPanel();
        AutoStart.Sync(_settings.AutoStart);
        ApplyStyle();
        _themeAccent = ParseColor(_settings.AccentColor) ?? _themeAccent;
        ApplyAccent(_themeAccent, save: false);
        BuildStatsGauges();
        SwitchPage(IslandPage.Notifications);
        RenderShortcuts();

        // 调试用：ISLAND_PAGE=settings 时自动展开并切到设置页，省去脚本点页签
        if (string.Equals(Environment.GetEnvironmentVariable("ISLAND_PAGE"), "settings", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_maximized)
                {
                    ToggleMax();
                }

                SwitchPage(IslandPage.Settings);
            }), DispatcherPriority.Background);
        }


        if (DebugLogEnabled)
        {
            DebugLog($"loaded hoverEnabled={_settings.HoverEnabled} alwaysOnTop={_settings.AlwaysOnTop} forceExpanded={ForceExpanded}");
        }

        _hoverTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        _hoverTimer.Stop();
        _progressTimer.Stop();

        RemoveMouseHook();

        _watcher.DesktopFocusChanged -= OnDesktopFocusChanged;
        _watcher.FullscreenChanged -= OnFullscreenChanged;
        _watcher.Dispose();

        _mediaWatcher.Changed -= OnMediaChanged;
        _mediaWatcher.Dispose();

        _tray?.Dispose();
        _tray = null;

        if (_hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, HotkeyIdQuit);
        }

        _hwndSource?.RemoveHook(WndProc);
    }

    private void PositionOnPrimaryScreen()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - Width) / 2d);
        Top = workArea.Top;
    }

    /// <summary>把岛放到「鼠标所在那块显示器」的顶部；多屏时不固定在主屏。</summary>
    private void PositionOnActiveMonitor()
    {
        if (!Native.GetCursorPos(out var cursor))
        {
            PositionOnPrimaryScreen();
            return;
        }

        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };

        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info))
        {
            PositionOnPrimaryScreen();
            return;
        }

        _currentMonitor = monitor;

        // 缩放比直接用「窗口物理宽度 / 窗口 DIP 宽度」反算，避免依赖 DPI 感知模式。
        var scale = GetWindowScale();
        var workWidth = (info.rcWork.Right - info.rcWork.Left) / scale;
        Left = (info.rcWork.Left / scale) + ((workWidth - Width) / 2d);
        Top = info.rcWork.Top / scale;

        if (DebugLogEnabled)
        {
            Native.GetWindowRect(_hwnd, out var rect);
            DebugLog($"position scale={scale:F3} work=({info.rcWork.Left},{info.rcWork.Top})-({info.rcWork.Right},{info.rcWork.Bottom}) left={Left:F1} top={Top:F1} winRect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
        }
    }

    /// <summary>物理像素 / DIP：用窗口自身尺寸反算，比 DPI API 稳妥。</summary>
    private double GetWindowScale()
    {
        if (Width > 0 && Native.GetWindowRect(_hwnd, out var rect) && rect.Width > 0)
        {
            var scale = rect.Width / Width;
            if (scale > 0.1d)
            {
                return scale;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return dpi > 0.1d ? dpi : 1d;
    }

    private void Restart()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 启动失败就保持当前实例
        }

        Close();
    }

    // ---------------- 状态来源 ----------------

    private void OnDesktopFocusChanged(object? sender, bool desktopFocused)
    {
        _expandedByFocus = desktopFocused;
        RefreshState(animate: true);
    }

    private void OnFullscreenChanged(object? sender, bool fullscreen)
    {
        _fullscreen = fullscreen;
        UpdateWindowVisibility();
    }

    private void OnMediaChanged(object? sender, MediaInfo? media)
    {
        _media = media;
        ApplyMode(animate: true);
        RefreshState(animate: true);
        UpdateProgress();
    }

    // ---------------- 内容 ----------------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        TimeSecondsText.Text = now.ToString(":ss", CultureInfo.InvariantCulture);
        CollapsedTimeText.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

        var remaining = DateTime.Today.AddDays(1) - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        DayRemainingText.Text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分"
            : $"{remaining.Minutes} 分";

        if (_maximized)
        {
            UpdateMaxPanel();
        }

        // 每分钟检查一次鼠标是否换到了别的显示器
        if (now.Second == 0 && Native.GetCursorPos(out var cursorPoint))
        {
            var monitor = Native.MonitorFromPoint(cursorPoint, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && monitor != _currentMonitor)
            {
                PositionOnActiveMonitor();
            }
        }
    }

    // ---------------- 二级面板内容 ----------------
    // ---------------- 大面板：时钟 + 通知 ----------------

    private void OnNotificationsChanged(object? sender, EventArgs e)
    {
        RenderNotifications();

        var count = _notifications.Items.Count;

        // 新通知变多时提醒一次（首轮初始化不触发）
        if (count > _lastNotificationCount && _lastNotificationCount > 0 && _notifications.Items.Count > 0)
        {
            PlayNotificationAlert();
        }

        _lastNotificationCount = count;
    }

    private void UpdateMaxPanel()
    {
        var now = DateTime.Now;
        MaxTimeText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        MaxSecondsText.Text = now.ToString(":ss", CultureInfo.InvariantCulture);
        MaxDateText.Text = now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

        var snapshot = _stats.Sample();
        var batteryPercent = snapshot.BatteryPercent ?? 0d;
        var batteryLow = snapshot.BatteryPercent is { } level && level <= 20 && !snapshot.OnAcPower;

        SetGauge(_cpuGauge, snapshot.CpuPercent, CpuBrush);
        SetGauge(_memoryGauge, snapshot.MemoryPercent, MemoryBrush);
        SetGauge(_batteryGauge, batteryPercent, batteryLow ? BatteryLowBrush : BatteryBrush);

        try
        {
            // 用「专用工作集」，与任务管理器「内存」列一致
            var bytes = ProcessMemory.PrivateWorkingSetBytes();

            if (bytes > 0)
            {
                AppMemoryText.Text = $"本程序 {bytes / 1024 / 1024} MB";

            }
        }
        catch
        {
            // 读不到就算了
        }

        // 设置页打开时每 5 秒同步一次音量/亮度（用户可能在别处改了）
        if (_page == IslandPage.Settings && ++_controlTicks % 5 == 0)
        {
            _ = ProbeDevicesAsync();
        }
    }

    private int _controlTicks;
    private static void TrimCache<TKey>(Dictionary<TKey, BitmapSource?> cache, int max)
        where TKey : notnull
    {
        if (cache.Count <= max)
        {
            return;
        }

        foreach (var key in cache.Keys.Take(cache.Count - (max / 2)).ToList())
        {
            cache.Remove(key);
        }
    }

    private readonly Dictionary<string, BitmapSource?> _iconImages = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush RowTitleBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    private static readonly Brush RowBodyBrush = new SolidColorBrush(Color.FromArgb(138, 255, 255, 255));
    private static readonly Brush RowTimeBrush = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));
    private static readonly Brush RowHoverBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));

    private void RenderNotifications()
    {
        var items = _notifications.Items;

        // 内容没变就不重建视觉树。
        // 之前每 3 秒轮询都会 Clear + 重建所有行（Border/Grid/TextBlock/Image 全新建），
        // 是常驻内存与 GC 压力的主要来源之一。
        var signature = BuildNotificationSignature(items);
        if (string.Equals(signature, _lastNotificationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotificationSignature = signature;

        NotificationList.Children.Clear();

        if (items.Count == 0)
        {
            // 空状态：中央大字，而不是角落里的一行小字
            var denied = _notifications.AccessDenied;

            NotificationList.Children.Add(new Border
            {
                Height = 108,
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = denied ? "未授权读取通知" : "暂无通知",
                            FontSize = 17,
                            FontWeight = FontWeights.Medium,
                            Foreground = new SolidColorBrush(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF)),
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = denied ? "请在系统设置中允许通知访问" : "有新消息时会出现在这里",
                            FontSize = 9.5,
                            Margin = new Thickness(0, 6, 0, 0),
                            Foreground = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                }
            });

            NotificationCountText.Text = string.Empty;
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            NotificationList.Children.Add(BuildNotificationRow(items[i], i));
        }

        NotificationCountText.Text = $"{items.Count} 条";

        if (DebugLogEnabled)
        {
            DebugLog($"notifications count={items.Count} denied={_notifications.AccessDenied} icons=[{string.Join(",", items.Select(x => x.IconBytes?.Length ?? 0))}] scrollable={NotificationScroll.ScrollableHeight:F0}");
        }
    }

    private static string BuildNotificationSignature(IReadOnlyList<IslandNotification> items)
    {
        var builder = new System.Text.StringBuilder(64);
        builder.Append(items.Count).Append('|');

        foreach (var item in items)
        {
            builder.Append(item.App).Append('\u0001')
                   .Append(item.Title).Append('\u0001')
                   .Append((int)item.Time.ToUnixTimeSeconds() / 60)   // 只精确到分钟，避免每轮翻转
                   .Append('\u0002');
        }

        return builder.ToString();
    }

    private string? _lastNotificationSignature;

    /// <summary>通知行交给代码生成：条目数量可变，天然可滚动。</summary>
    private Border BuildNotificationRow(IslandNotification item, int index)
    {
        // iOS/macOS 通知卡：大圆角图标 + 应用名/时间一行 + 标题 + 正文
        var row = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand
        };

        row.MouseEnter += (_, _) =>
        {
            row.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            row.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        };

        row.MouseLeave += (_, _) =>
        {
            row.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            row.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        };

        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ActivateNotification(index);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 图标：30px 圆角方块（比原来大一圈）
        var iconBox = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var icon = GetIconImage(item);
        if (icon is not null)
        {
            iconBox.Child = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4), Source = icon };
        }
        else
        {
            iconBox.Child = new TextBlock
            {
                Text = "\uE7F4",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        // 第一行：应用名（左）+ 时间（右）
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var appName = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.App) ? "通知" : item.App,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(appName, 0);
        header.Children.Add(appName);

        var time = new TextBlock
        {
            Text = Relative(item.Time),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(time, 1);
        header.Children.Add(time);

        content.Children.Add(header);

        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Title) ? item.App : item.Title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });

        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            content.Children.Add(new TextBlock
            {
                Text = item.Body,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        row.Child = grid;
        return row;
    }

    /// <summary>列表内滚轮滚动；滚到头就让滚轮继续冒泡（外层用它收起大面板）。</summary>
    private void OnNotificationWheel(object sender, MouseWheelEventArgs e)
    {
        if (_page != IslandPage.Notifications)
        {
            // 非通知页没有可滚内容：让滚轮冒泡到外层（收起大面板）
            return;
        }

        var canScroll = e.Delta < 0
            ? NotificationScroll.VerticalOffset < NotificationScroll.ScrollableHeight
            : NotificationScroll.VerticalOffset > 0;

        if (DebugLogEnabled)
        {
            DebugLog($"wheel delta={e.Delta} offset={NotificationScroll.VerticalOffset:F0} scrollable={NotificationScroll.ScrollableHeight:F0} canScroll={canScroll}");
        }

        if (!canScroll)
        {
            // 已经滚到头：再往同方向滚一次就收起大面板
            ToggleMax();
            e.Handled = true;
            return;
        }

        NotificationScroll.ScrollToVerticalOffset(NotificationScroll.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private BitmapSource? GetIconImage(IslandNotification item)
    {
        if (_iconImages.TryGetValue(item.Aumid, out var cached))
        {
            return cached;
        }

        var bitmap = CreateIconFromBytes(item.IconBytes) ?? CreateIconFromProcess(item);

        if (DebugLogEnabled)
        {
            DebugLog($"icon aumid={item.Aumid} app={item.App} bytes={item.IconBytes?.Length ?? 0} ok={bitmap is not null}");
        }

        _iconImages[item.Aumid] = bitmap;
        TrimCache(_iconImages, 32);

        return bitmap;
    }

    private static BitmapImage? CreateIconFromBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 32;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>未打包应用的 logo 拿不到，退而用进程 exe 自带的图标。</summary>
    private static BitmapSource? CreateIconFromProcess(IslandNotification item)
    {
        var path = ToastActivator.ResolveExecutablePath(item.Aumid, item.App);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            if (ExtractIconEx(path, 0, out var large, out var small, 1) == 0)
            {
                return null;
            }

            var handle = small != IntPtr.Zero ? small : large;
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));

                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    /// <summary>点某条通知 = 点它的气泡。</summary>
    private void ActivateNotification(int index)
    {
        if (index < 0 || index >= _notifications.Items.Count)
        {
            return;
        }

        var item = _notifications.Items[index];

        // 优先用 toast 的真实激活参数：wpndatabase.db 里存着完整的 launch
        // （实测 QQ 是 ntqq-notification://... 的 protocol 类型），执行它即可进到正确会话
        var usedLaunch = false;
        try
        {
            usedLaunch = ToastLaunchStore.TryActivate(item);
        }
        catch
        {
            usedLaunch = false;
        }

        // 拿不到激活参数时退回「把应用窗口叫到前台」
        var ok = usedLaunch || ToastActivator.TryClick(item.Aumid, item.App);

        if (DebugLogEnabled)
        {
            DebugLog($"toast click app={item.App} title={item.Title} viaLaunch={usedLaunch} activated={ok}");
        }
    }

    /// <summary>点通知卡空白处：打开系统通知中心。</summary>
    private void OnNotificationCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShellAction.OpenNotificationCenter();

        if (DebugLogEnabled)
        {
            DebugLog("notification card click -> open Windows notification center");
        }
    }
    // ---------------- 外观：纯黑 / 液态玻璃 ----------------

    /// <summary>
    /// 液态玻璃不是真的折射（分层窗拿不到 DWM 模糊），而是靠五层叠加出来的：
    /// 半透明底 → 顶部高光带 → 底部内阴影 → 边缘亮圈 → 内容染色。
    /// </summary>
    private void ApplyStyle()
    {
        var glass = _settings.GlassStyle;

        Island.Background = glass ? BuildGlassBackground() : BuildSolidBackground();
        GlassTopSpecular.Opacity = glass ? 1d : 0d;
        GlassBottomShade.Opacity = glass ? 1d : 0d;
        GlassTint.Opacity = glass ? 1d : 0d;

        Island.BorderBrush = glass ? BuildGlassBorder() : BuildSolidBorder();

        if (DebugLogEnabled)
        {
            DebugLog($"style glass={glass}");
        }
    }

    private static Brush BuildSolidBackground()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x24, 0x2C), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x14, 0x18), 0.6));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0B, 0x0B, 0x0E), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>玻璃底：alpha ≈ 78%，让桌面隐约透上来。</summary>
    private Brush BuildGlassBackground()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };

        // Blend the theme accent into the base colour, otherwise the glass reads as gray.
        Color Blend(double r, double g, double b, double amount)
        {
            byte Mix(double baseValue, byte accentValue)
                => (byte)Math.Clamp(Math.Round((baseValue * (1 - amount)) + (accentValue * amount)), 0, 255);

            return Color.FromRgb(Mix(r, _themeAccent.R), Mix(g, _themeAccent.G), Mix(b, _themeAccent.B));
        }

        var top = Blend(0x2A, 0x2C, 0x34, 0.16);
        var mid = Blend(0x16, 0x17, 0x1D, 0.13);
        var bottom = Blend(0x0C, 0x0D, 0x11, 0.10);

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD2, top.R, top.G, top.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCE, mid.R, mid.G, mid.B), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCA, bottom.R, bottom.G, bottom.B), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush BuildSolidBorder() => BuildBorderGradient(new[]
    {
        (0d, Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
        (0.5, Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
        (1d, Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF))
    });

    /// <summary>玻璃的边缘要更亮一些，像一圈被照亮的内壁。</summary>
    private static Brush BuildGlassBorder() => BuildBorderGradient(new[]
    {
        (0d, Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
        (0.45, Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)),
        (1d, Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
    });

    private static Brush BuildBorderGradient((double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };

        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    // ---------------- 环形指标（CPU / 内存 / 电量） ----------------

    private static readonly Color CpuColor = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color MemoryColor = Color.FromRgb(0xB3, 0x88, 0xFF);
    private static readonly Color BatteryColor = Color.FromRgb(0x57, 0xE3, 0x9B);
    private static readonly Color BatteryLowColor = Color.FromRgb(0xEF, 0x53, 0x50);

    /// <summary>环形指标：虚线阵列与画笔都复用，避免每秒产生新对象。</summary>
    private sealed record Gauge(Ellipse Arc, TextBlock Value, DoubleCollection Dash, double Units);

    private static readonly Brush CpuBrush = MakeBrush(CpuColor);
    private static readonly Brush MemoryBrush = MakeBrush(MemoryColor);
    private static readonly Brush BatteryBrush = MakeBrush(BatteryColor);
    private static readonly Brush BatteryLowBrush = MakeBrush(BatteryLowColor);

    private Gauge? _cpuGauge;
    private Gauge? _memoryGauge;
    private Gauge? _batteryGauge;

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildStatsGauges()
    {
        StatsHost.Children.Clear();

        _cpuGauge = AddGauge("CPU", CpuColor);
        _memoryGauge = AddGauge("内存", MemoryColor);
        _batteryGauge = AddGauge("电量", BatteryColor);

    }

    private Gauge AddGauge(string label, Color color)
    {
        const double size = 42d;
        const double thickness = 3d;

        var host = new StackPanel
        {
            Margin = new Thickness(0, 0, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };

        var track = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Stroke = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
        };
        grid.Children.Add(track);

        // 虚线阵列只建一次，之后原地改值，避免每秒产生新的集合对象
        var units = (2d * Math.PI * ((size - thickness) / 2d)) / thickness;
        var dash = new DoubleCollection { 0.001, units };

        var arc = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Stroke = new SolidColorBrush(color),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = dash,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90)
        };
        grid.Children.Add(arc);

        var valueText = new TextBlock
        {
            Text = "—",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(valueText);

        host.Children.Add(grid);
        host.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        StatsHost.Children.Add(host);
        return new Gauge(arc, valueText, dash, units);
    }

    /// <summary>把百分比画成圆环：原地改虚线阵列，不产生新对象。</summary>
    private static void SetGauge(Gauge? gauge, double percent, Brush brush)
    {
        if (gauge is null)
        {
            return;
        }

        var ratio = Math.Clamp(percent / 100d, 0d, 1d);
        var filled = ratio * gauge.Units;

        gauge.Dash[0] = Math.Max(filled, 0.001);
        gauge.Dash[1] = Math.Max(gauge.Units - filled, 0.001);
        gauge.Arc.Stroke = brush;
        gauge.Value.Text = percent <= 0 ? "—" : $"{(int)Math.Round(percent)}%";
    }

    // ---------------- 分页：通知 / 快捷 ----------------

    private void OnNotificationsTabClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchPage(IslandPage.Notifications);
    }

    private void OnShortcutsTabClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchPage(IslandPage.Shortcuts);
    }

    private enum IslandPage
    {
        Notifications,
        Shortcuts,
        Settings
    }

    private IslandPage _page;

    private void OnSettingsTabClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchPage(IslandPage.Settings);
    }

    private void SwitchPage(IslandPage page)
    {
        _page = page;

        NotificationsPage.Visibility = page == IslandPage.Notifications ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = page == IslandPage.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == IslandPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        var active = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
        var activeText = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255));
        var inactiveText = new SolidColorBrush(Color.FromArgb(122, 255, 255, 255));

        TabNotifications.Background = page == IslandPage.Notifications ? active : Brushes.Transparent;
        TabShortcuts.Background = page == IslandPage.Shortcuts ? active : Brushes.Transparent;
        TabSettings.Background = page == IslandPage.Settings ? active : Brushes.Transparent;

        TabNotificationsText.Foreground = page == IslandPage.Notifications ? activeText : inactiveText;
        TabShortcutsText.Foreground = page == IslandPage.Shortcuts ? activeText : inactiveText;
        TabSettingsText.Foreground = page == IslandPage.Settings ? activeText : inactiveText;

        if (page == IslandPage.Settings)
        {
            if (_volumeRow is null && _brightnessRow is null)
            {
                BuildSettingsControls();
            }
            else
            {
                _ = ProbeDevicesAsync();
            }
        }

        if (DebugLogEnabled)
        {
            DebugLog($"page {page}");
        }
    }

    /// <summary>诊断：把三个页签的实际屏幕位置打出来，便于脚本点击时对准。</summary>
    private void LogTabRects()
    {
        if (!DebugLogEnabled)
        {
            return;
        }

        foreach (var (name, element) in new (string, FrameworkElement)[]
                 {
                     ("notif", TabNotifications),
                     ("short", TabShortcuts),
                     ("set", TabSettings)
                 })
        {
            try
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                DebugLog($"tab {name} rect=({topLeft.X:F0},{topLeft.Y:F0}) size={element.ActualWidth:F0}x{element.ActualHeight:F0}");
            }
            catch
            {
                // 未布局时忽略
            }
        }
    }


    // ---------------- 设置页：音量 / 亮度 ----------------

    private sealed class SliderRow
    {
        public required Grid Host { get; init; }

        public required Border Fill { get; init; }

        public Border? Knob { get; init; }

        public required TextBlock Value { get; init; }

        public required TextBlock Icon { get; init; }

        public required Grid Track { get; init; }

        public double KnobSize { get; init; } = 14d;

        public Action<double>? Apply { get; set; }

        public Action<double>? Commit { get; set; }

        public double Ratio { get; set; }
    }

    private SliderRow? _volumeRow;
    private SliderRow? _brightnessRow;
    private TextBlock? _volumeIcon;
    private bool _audioReady;
    private bool _brightnessReady;
    private bool _probing;
    private DateTime _probeBlockedUntil;


    // ---------------- 主题色 ----------------

    /// <summary>可选的强调色（默认薄荷绿在最前）。</summary>
    private static readonly (string Name, Color Color)[] AccentPalette =
    {
        ("薄荷", Color.FromRgb(0x5B, 0xE4, 0x9B)),
        ("天蓝", Color.FromRgb(0x4F, 0xC3, 0xF7)),
        ("紫罗兰", Color.FromRgb(0xB3, 0x88, 0xFF)),
        ("暖橙", Color.FromRgb(0xFF, 0xB7, 0x4D)),
        ("玫瑰", Color.FromRgb(0xFF, 0x8A, 0x9B)),
        ("香槟", Color.FromRgb(0xE1, 0xC8, 0x9A))
    };

    private Color _themeAccent = Color.FromRgb(0x5B, 0xE4, 0x9B);
    private readonly List<Border> _swatches = new();
    private Border? _customSwatch;

    private static Brush BuildAccentBrush(Color color)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };

        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Lighten(color, 0.35), 1));
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color color, double amount)
    {
        byte Mix(byte value) => (byte)Math.Min(255, value + ((255 - value) * amount));

        return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        try
        {
            var text = hex.Trim().TrimStart('#');

            if (text.Length == 6)
            {
                return Color.FromRgb(
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }
        }
        catch
        {
            // ignore malformed value
        }

        return null;
    }

    /// <summary>Applies the accent colour everywhere it is used.</summary>
    private void ApplyAccent(Color color, bool save)
    {
        _themeAccent = color;

        var gradient = BuildAccentBrush(color);
        var solid = new SolidColorBrush(color);
        solid.Freeze();

        if (_settings.GlassStyle)
        {
            Island.Background = BuildGlassBackground();
        }

        foreach (var row in _sliderRows)
        {
            row.Fill.Background = gradient;
        }

        Bar0.Fill = solid;
        Bar1.Fill = solid;
        Bar2.Fill = solid;

        ProgressFill.Background = solid;
        // 玻璃环境色：从上到下渐变，比原来的 8% 平铺明显得多
        var tint = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(0x4A, color.R, color.G, color.B), 0));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, color.R, color.G, color.B), 0.55));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, color.R, color.G, color.B), 1));
        tint.Freeze();
        GlassTint.Fill = tint;

        var matchedPreset = false;

        foreach (var swatch in _swatches)
        {
            var isCurrent = swatch.Tag is Color c && c == color;
            matchedPreset |= isCurrent;
            swatch.BorderThickness = new Thickness(isCurrent ? 2 : 1);
            swatch.BorderBrush = new SolidColorBrush(
                isCurrent ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        }

        // The custom chip shows the active colour and gets the selected ring
        // whenever the accent is not one of the presets.
        if (_customSwatch is not null)
        {
            _customSwatch.Background = matchedPreset
                ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(color);

            _customSwatch.BorderThickness = new Thickness(matchedPreset ? 1 : 2);
            _customSwatch.BorderBrush = new SolidColorBrush(
                matchedPreset ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }

        if (save)
        {
            _settings.AccentColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SaveSettings();
        }

        if (DebugLogEnabled)
        {
            DebugLog($"accent applied #{color.R:X2}{color.G:X2}{color.B:X2} save={save}");
        }
    }

    /// <summary>Builds the colour swatch row inside the settings page.</summary>
    private void AddAccentSwatches()
    {
        _swatches.Clear();

        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };

        foreach (var (name, color) in AccentPalette)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 9, 0),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Tag = color,
                ToolTip = name
            };

            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ApplyAccent(color, save: true);
            };

            _swatches.Add(swatch);
            host.Children.Add(swatch);
        }

        // Trailing "+" opens the custom HSV picker.
        var custom = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = "自定义颜色",
            Child = new TextBlock
            {
                Text = "+",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        custom.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;

            var picker = new ColorPickerWindow(_themeAccent);

            if (picker.ShowDialog() == true)
            {
                ApplyAccent(picker.SelectedColor, save: true);
            }
        };

        _customSwatch = custom;
        host.Children.Add(custom);
        SettingsHost.Children.Add(host);
    }

    // ---------------- 通知提醒（抖动 + 描边闪光） ----------------

    private int _lastNotificationCount;

    /// <summary>PC 没有振动马达，所以用「岛体左右抖动 + 描边闪一下」代替。</summary>
    private void PlayNotificationAlert()
    {
        if (!_settings.NotificationAlert)
        {
            return;
        }

        try
        {
            var shake = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(460),
                FillBehavior = FillBehavior.Stop
            };

            var offsets = new[] { 0d, 7d, -6d, 5d, -4d, 3d, -2d, 0d };
            var step = 460d / offsets.Length;

            for (var i = 0; i < offsets.Length; i++)
            {
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(step * (i + 1)))));
            }

            IslandShake.BeginAnimation(TranslateTransform.XProperty, shake);

            AlertGlow.BorderBrush = new SolidColorBrush(_themeAccent);
            AlertGlow.BeginAnimation(
                OpacityProperty,
                new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(900),
                    KeyFrames =
                    {
                        new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))),
                        new LinearDoubleKeyFrame(0d, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)))
                    },
                    FillBehavior = FillBehavior.Stop
                });
        }
        catch
        {
            // 动画失败不影响主流程
        }
    }


    private void BuildSettingsControls()
    {
        SettingsHost.Children.Clear();
        _sliderRows.Clear();
        _dragRow = null;

        // 关键：这里不碰任何硬件接口。
        // DDC/CI 读亮度可能阻塞几百毫秒，放在 UI 线程上就会把整块面板卡住。

        _volumeRow = AddSliderRow(
            "\uE767",                 // 扬声器图标
            "音量",
            apply: value => AudioController.TrySetVolume(value),
            muteToggle: () =>
            {
                AudioController.TryToggleMute(out var nowMuted);
                return nowMuted;
            });

        _volumeIcon = _volumeRow.Icon;

        _brightnessRow = AddSliderRow(
            "\uE706",                 // 亮度图标
            "亮度",
            apply: null,              // 拖动期间不动硬件：每次设置都要起一个 PowerShell 进程
            muteToggle: null,
            onCommit: value =>
            {
                var percent = (int)Math.Round(value * 100);
                BrightnessWriter.Request(percent);
            });

        AddAccentSwatches();
        ApplyAccent(_themeAccent, save: false);
        _ = ProbeDevicesAsync();
    }

    /// <summary>后台探测音量/亮度设备，完成后回 UI 线程填值并决定哪些行可用。</summary>
    private async Task ProbeDevicesAsync()
    {
        // 拖动期间不要探测：会把用户刚拖到的位置覆盖回系统旧值
        if (_probing || _dragRow is not null || DateTime.UtcNow < _probeBlockedUntil)
        {
            return;
        }

        _probing = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var audioOk = AudioController.TryGet(out var volume, out var muted);
                var brightOk = BrightnessController.TryGetPercent(out var brightValue);
                return (audioOk, volume, muted, brightOk, brightValue: (double)brightValue);
            });

            await Dispatcher.InvokeAsync(() =>
            {
                // 一旦成功过就记住“支持”，偶发失败不再把行灰掉
                if (result.audioOk)
                {
                    _audioReady = true;
                }

                if (result.brightOk)
                {
                    _brightnessReady = true;
                }

                if (_volumeRow is not null)
                {
                    _volumeRow.Host.IsEnabled = result.audioOk;
                    _volumeRow.Host.Opacity = result.audioOk ? 1d : 0.4d;

                    if (result.audioOk)
                    {
                        SetSlider(_volumeRow, result.volume);
                    }
                }

                if (_volumeIcon is not null)
                {
                    _volumeIcon.Text = result.muted ? "\uE74F" : "\uE767";
                }

                if (_brightnessRow is not null)
                {
                    _brightnessRow.Host.IsEnabled = result.brightOk;
                    _brightnessRow.Host.Opacity = result.brightOk ? 1d : 0.4d;

                    if (result.brightOk)
                    {
                        SetSlider(_brightnessRow, result.brightValue / 100d);
                    }
                }

                if (DebugLogEnabled)
                {
                    DebugLog($"probe done audio={result.audioOk} vol={result.volume:F2} brightness={result.brightOk} value={result.brightValue:F0}");

                    foreach (var row in _sliderRows)
                    {
                        try
                        {
                            var origin = row.Track.PointToScreen(new Point(0, 0));
                            DebugLog($"slider track at=({origin.X:F0},{origin.Y:F0}) size={row.Track.ActualWidth:F0}x{row.Track.ActualHeight:F0}");
                        }
                        catch
                        {
                            // 未布局
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            if (DebugLogEnabled)
            {
                DebugLog($"probe failed {ex}");
            }
        }
        finally
        {
            _probing = false;
        }
    }


    /// <summary>一行：图标 + 名称 + 滑杆 + 百分比。滑杆用列宽权重实现，避免逐像素算位置。</summary>
    private SliderRow AddSliderRow(string glyph, string label, Action<double>? apply, Func<bool>? muteToggle, Action<double>? onCommit = null)
    {
        // 控制中心风格：一条粗圆角条，图标与数值都嵌在条内，填充从左往右推进
        var bar = new Grid
        {
            Height = 34,
            Margin = new Thickness(0, 0, 0, 8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var track = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
        };

        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        fillBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x4F, 0xD9, 0x92), 0));
        fillBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x7B, 0xE8, 0xB4), 1));
        fillBrush.Freeze();

        var fill = new Border
        {
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = fillBrush
        };

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF))
        };

        var valueText = new TextBlock
        {
            Text = "-",
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 13, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF))
        };

        bar.Children.Add(track);
        bar.Children.Add(fill);
        bar.Children.Add(icon);
        bar.Children.Add(valueText);

        var row = new SliderRow
        {
            Host = bar,
            Fill = fill,
            Knob = null,
            Value = valueText,
            Icon = icon,
            Track = bar,
            KnobSize = 0d,
            Apply = apply,
            Commit = onCommit
        };

        _sliderRows.Add(row);
        bar.SizeChanged += (_, _) => LayoutSlider(row);

        // 点击/按下即定位（无状态，不依赖鼠标捕获）
        bar.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            BeginSliderDrag(row);
        };

        bar.MouseLeftButtonUp += (_, _) => EndSliderDrag();

        // 滚轮微调
        bar.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            var step = e.Delta > 0 ? 0.05 : -0.05;
            var next = Math.Clamp(row.Ratio + step, 0d, 1d);
            SetSlider(row, next);
            row.Apply?.Invoke(next);
            row.Commit?.Invoke(next);
        };

        if (muteToggle is not null)
        {
            icon.Cursor = Cursors.Hand;
            icon.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                var muted = muteToggle();
                if (_volumeIcon is not null)
                {
                    _volumeIcon.Text = muted ? "\uE74F" : "\uE767";
                }
            };
        }

        SettingsHost.Children.Add(bar);
        return row;
    }

    /// <summary>填充宽度 = 比例 × 条宽（DIP）。无滑块，几何更简单。</summary>
    private static void LayoutSlider(SliderRow row)
    {
        var width = row.Track.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var fillWidth = row.Ratio * width;

        // 比例接近 0 时必须完全不显示：填充条没有单独高度，会撑满整条，
        // 留 1px 就会变成一根竖直的绿线（之前就是这个原因）。
        row.Fill.Visibility = fillWidth >= 1d ? Visibility.Visible : Visibility.Collapsed;
        row.Fill.Width = Math.Max(fillWidth, 0d);
    }
    private static void SetSlider(SliderRow row, double ratio)
    {
        row.Ratio = Math.Clamp(ratio, 0d, 1d);
        LayoutSlider(row);
        row.Value.Text = $"{(int)Math.Round(row.Ratio * 100)}%";
    }

    private void RefreshAudioAndBrightness()
    {
        if (_audioReady && _volumeRow is not null && AudioController.TryGet(out var volume, out var muted))
        {
            SetSlider(_volumeRow, volume);

            if (_volumeIcon is not null)
            {
                _volumeIcon.Text = muted ? "\uE74F" : "\uE767";
            }
        }

        if (_brightnessReady && _brightnessRow is not null
            && BrightnessController.TryGetPercent(out var brightness))
        {
            SetSlider(_brightnessRow, brightness / 100d);
        }
    }

    private void RenderShortcuts()
    {
        ShortcutGrid.Children.Clear();

        foreach (var shortcut in _settings.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut.Target))
            {
                continue;
            }

            ShortcutGrid.Children.Add(BuildShortcutTile(shortcut));
        }

        ShortcutGrid.Children.Add(BuildAddTile());

        ShortcutHeaderText.Text = _settings.Shortcuts.Count == 0
            ? "快捷启动 · 点 + 添加"
            : $"快捷启动 · {_settings.Shortcuts.Count} 个（右键可移除）";

        if (DebugLogEnabled)
        {
            DebugLog($"shortcuts rendered tiles={ShortcutGrid.Children.Count} items={_settings.Shortcuts.Count}");
        }
    }

    /// <summary>「+」磁贴：打开文件选择器，选程序/快捷方式后加入。</summary>
    private FrameworkElement BuildAddTile()
    {
        var tile = new Border
        {
            Width = 92,
            Height = 74,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            AddShortcutsFromDialog();
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = "\uE710", // +
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "添加",
            FontSize = 9.5,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        tile.Child = stack;
        return tile;
    }

    private void AddShortcutsFromDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加快捷方式",
            Filter = "程序与快捷方式|*.exe;*.lnk;*.bat;*.cmd;*.url;*.ps1|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var added = 0;

        foreach (var path in dialog.FileNames)
        {
            if (_settings.Shortcuts.Any(s => string.Equals(s.Target, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _settings.Shortcuts.Add(new ShortcutItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Target = path
            });

            added++;
        }

        if (added > 0)
        {
            SaveSettings();
            _shortcutIconCache.Clear();
            RenderShortcuts();
        }

        if (DebugLogEnabled)
        {
            DebugLog($"shortcuts added={added} total={_settings.Shortcuts.Count}");
        }
    }

    private ContextMenu BuildTileMenu(ShortcutItem shortcut)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = $"移除「{shortcut.Name}」" };

        remove.Click += (_, _) =>
        {
            _settings.Shortcuts.Remove(shortcut);
            SaveSettings();
            _shortcutIconCache.Clear();
            RenderShortcuts();
        };

        menu.Items.Add(remove);
        return menu;
    }

    private FrameworkElement BuildShortcutTile(ShortcutItem shortcut)
    {
        var tile = new Border
        {
            Width = 92,
            Height = 78,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(2),
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ContextMenu = BuildTileMenu(shortcut)
        };

        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            LaunchShortcut(shortcut);
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var icon = GetShortcutIcon(shortcut);

        // 图标底板：圆角方块 + 用图标自身主色做的淡色渐变，像 iOS 图标那种“有底色”的观感
        var plate = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var accent = icon is null ? null : AverageColor(icon);

        if (DebugLogEnabled)
        {
            DebugLog($"tile name={shortcut.Name} icon={(icon is null ? "none" : "yes")} accent={(accent is null ? "none" : $"#{accent.Value.R:X2}{accent.Value.G:X2}{accent.Value.B:X2}")}");
        }

        if (accent is { } color)
        {
            var background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            background.GradientStops.Add(new GradientStop(Color.FromArgb(0x4D, color.R, color.G, color.B), 0));
            background.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, color.R, color.G, color.B), 1));
            background.Freeze();

            plate.Background = background;
            plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, color.R, color.G, color.B));
        }
        else
        {
            plate.Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
            plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 255, 255, 255));
        }

        if (icon is not null)
        {
            plate.Child = new Image { Source = icon, Width = 22, Height = 22, Stretch = Stretch.Uniform };
        }
        else
        {
            plate.Child = new TextBlock
            {
                Text = "\uE71D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        stack.Children.Add(plate);

        stack.Children.Add(new TextBlock
        {
            Text = shortcut.Name,
            FontSize = 9.5,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 84
        });

        tile.Child = stack;
        return tile;
    }

    /// <summary>取图标的主色（忽略大面积透明与纯灰像素），给底板染色。</summary>
    private static Color? AverageColor(BitmapSource source)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            long red = 0, green = 0, blue = 0, count = 0;

            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

                if (a < 100)
                {
                    continue;
                }

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max - min < 20)
                {
                    continue; // 灰色图标不染色
                }

                red += r;
                green += g;
                blue += b;
                count++;
            }

            if (count < 30)
            {
                return null;
            }

            var average = Color.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));

            // 提亮到在深色底上够醒目
            var peak = Math.Max(average.R, Math.Max(average.G, average.B));
            if (peak == 0)
            {
                return null;
            }

            var scale = 225d / peak;
            return Color.FromRgb(
                (byte)Math.Min(255, average.R * scale),
                (byte)Math.Min(255, average.G * scale),
                (byte)Math.Min(255, average.B * scale));
        }
        catch
        {
            return null;
        }
    }

    private void LaunchShortcut(ShortcutItem shortcut)
    {
        var ok = false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = shortcut.Target,
                UseShellExecute = true
            });

            ok = true;
        }
        catch
        {
            // 目标不存在或没有权限
        }

        if (DebugLogEnabled)
        {
            DebugLog($"shortcut launch name={shortcut.Name} target={shortcut.Target} ok={ok}");
        }
    }

    private static readonly Dictionary<string, BitmapSource?> _shortcutIconCache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private static BitmapSource? GetShortcutIcon(ShortcutItem shortcut)
    {
        try
        {
            var target = Environment.ExpandEnvironmentVariables(shortcut.Target);

            if (_shortcutIconCache.TryGetValue(target, out var cachedIcon))
            {
                return cachedIcon;
            }

            var path = Path.IsPathRooted(target) ? target : FindInPath(target);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            // 用 Shell 的文件图标：.lnk / .url / .exe 都能拿到正确的图标
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(24, 24));

                source.Freeze();
                _shortcutIconCache[path] = source;
                TrimCache(_shortcutIconCache, 64);
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInPath(string exeName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 路径里有非法字符，跳过
            }
        }

        return null;
    }


    private Storyboard? _equalizerStoryboard;

    /// <summary>三根柱用不同周期跳动，看起来像真的在跟着音乐起伏。</summary>
    private void SetEqualizerRunning(bool running)
    {
        if (running)
        {
            if (_equalizerStoryboard is not null)
            {
                return;
            }

            var durations = new[] { 520d, 700d, 610d };
            var low = new[] { 3d, 4d, 3.5d };
            var high = new[] { 10d, 12d, 9d };
            var bars = new[] { Bar0, Bar1, Bar2 };

            var storyboard = new Storyboard();

            for (var i = 0; i < bars.Length; i++)
            {
                var animation = new DoubleAnimation(low[i], high[i], TimeSpan.FromMilliseconds(durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                Storyboard.SetTarget(animation, bars[i]);
                Storyboard.SetTargetProperty(animation, new PropertyPath(FrameworkElement.HeightProperty));
                storyboard.Children.Add(animation);
            }

            storyboard.Begin();
            if (DebugLogEnabled)
            {
                DebugLog("equalizer started");
            }
            _equalizerStoryboard = storyboard;
            return;
        }

        _equalizerStoryboard?.Stop();
        _equalizerStoryboard = null;

        // 暂停时三根柱压平成一样的短柱（“平着”的视觉提示）
        Bar0.Height = 3;
        Bar1.Height = 3;
        Bar2.Height = 3;
    }

    private static string Relative(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;

        if (span < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        return span < TimeSpan.FromDays(1)
            ? $"{(int)span.TotalHours} 小时前"
            : $"{(int)span.TotalDays} 天前";
    }
    private void ApplyMode(bool animate)
    {
        var media = _media;
        var showMedia = media is not null;

        if (showMedia)
        {
            TrackTitle.Text = string.IsNullOrWhiteSpace(media!.Title) ? "正在播放" : media.Title;

            var subtitle = !string.IsNullOrWhiteSpace(media.Artist)
                ? media.Artist
                : (string.IsNullOrWhiteSpace(media.App) ? string.Empty : media.App);
            if (!media.IsPlaying)
            {
                subtitle = string.IsNullOrWhiteSpace(subtitle) ? "已暂停" : subtitle + " · 已暂停";
            }

            TrackSubtitle.Text = subtitle;
            PlayGlyph.Text = media.IsPlaying ? PlayGlyphPlaying : PlayGlyphPaused;

            // 用封面主色做强调色（取不到就用默认薄荷绿）
            var accent = media.Accent ?? _themeAccent;
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            ProgressFill.Background = accentBrush;
            GlassTint.Fill = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));

            Bar0.Fill = accentBrush;
            Bar1.Fill = accentBrush;
            Bar2.Fill = accentBrush;

            // 窗口标题兜底来的信息没有控制能力：把三个按钮藏起来，别给假按钮
            var controls = media.SupportsControls ? Visibility.Visible : Visibility.Collapsed;
            if (DebugLogEnabled)
            {
                DebugLog($"card glyph={PlayGlyph.Text} controls={media.SupportsControls} eq={EqualizerBars.Visibility} op={EqualizerBars.Opacity:F2}");
            }
            PrevButton.Visibility = controls;
            PlayButton.Visibility = controls;
            NextButton.Visibility = controls;

            // 歌名变了才重新找歌词文件
            if (!string.Equals(_lyricsTitle, media.Title, StringComparison.Ordinal))
            {
                _lyricsTitle = media.Title;
                _lyrics = LrcLyrics.TryLoad(media.Title, _settings.LyricsFolder);

                if (DebugLogEnabled)
                {
                    DebugLog($"lyrics title='{media.Title}' lines={_lyrics?.Count ?? 0}");
                }
            }

            ArtworkBrush.ImageSource = media.Artwork;
            ArtworkShape.Opacity = media.Artwork is null ? 0d : 1d;

            if (DebugLogEnabled)
            {
                DebugLog($"media playing={media.IsPlaying} title='{media.Title}' accent=#{accent.R:X2}{accent.G:X2}{accent.B:X2} artwork={(media.Artwork is null ? "none" : "yes")}");
            }
        }

        Fade(MaxPanel, _maximized ? 1d : 0d, animate);

        Fade(ClockPanel, !_maximized && !showMedia ? 1d : 0d, animate);
        Fade(MediaPanel, !_maximized && showMedia ? 1d : 0d, animate);
        UpdateProgressVisibility(animate);

        // 收起态：有媒体就显示三根柱；播放时跳动，暂停时三根一起压平。
        // 必须用 Visibility 而不是 Opacity——透明元素仍占布局宽度，会把时间挤得不居中。
        var playing = showMedia && media!.IsPlaying;

        EqualizerBars.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
        EqualizerBars.Opacity = showMedia ? (playing ? 1d : 0.45d) : 0d;

        SetEqualizerRunning(playing);
    }

    /// <summary>进度线在收起态也保留（更淡），像胶囊底部的一点“活着”的提示。</summary>
    private void UpdateProgressVisibility(bool animate)
    {
        // 4px：之前 2px 在高分屏上几乎看不见
        const double thickness = 4d;
        ProgressFill.Height = thickness;
        ProgressTrack.Height = thickness;
        ProgressFill.CornerRadius = new CornerRadius(thickness / 2d);
        ProgressTrack.CornerRadius = new CornerRadius(thickness / 2d);

        // 来源不报时长就不画进度线（脉冲动画已按反馈移除）
        var hasProgress = _media is { Duration.TotalSeconds: > 0 } && !_maximized;
        var alpha = hasProgress ? (_renderedExpanded ? 1d : 0.85d) : 0d;
        Fade(ProgressFill, alpha, animate);
        Fade(ProgressTrack, alpha * 0.8d, animate);
    }

    private void UpdateProgress()
    {
        var media = _mediaWatcher.Current ?? _media;
        if (media is null)
        {
            _progressRatio = 0d;
            return;
        }

        if (media.Duration <= TimeSpan.Zero)
        {
            // 来源不报时长：不画进度线
            _progressRatio = 0d;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = 0d;
            return;
        }

        var sampled = Math.Clamp(media.Position.TotalSeconds / media.Duration.TotalSeconds, 0d, 1d);

        // Anchor-based extrapolation: only reset the anchor when the reported
        // position actually changes. Edge only reports once, so extrapolating
        // from every sample timestamp would freeze after a couple of seconds.
        if (Math.Abs(sampled - _progressAnchorRatio) > 0.0001 || _progressAnchorAt == default)
        {
            _progressAnchorRatio = sampled;
            _progressAnchorAt = media.SampledAt;
        }

        var predicted = sampled;

        if (media.IsPlaying)
        {
            var age = (DateTime.UtcNow - _progressAnchorAt).TotalSeconds;
            if (age < 0)
            {
                age = 0;
            }

            predicted = _progressAnchorRatio + (age / media.Duration.TotalSeconds);
        }

        // 浏览器这类来源的位置上报很粗（1~2 秒一跳，偶尔还会重复旧值），
        // 直接跟随会出现「前进一点又退回一点」。所以：
        //   小步进 → 只允许前进（单调）
        //   大跳变（拖动进度条 / 换集）→ 直接跟随
        if (predicted < _progressRatio - 0.08 || predicted > _progressRatio + 0.20)
        {
            _progressRatio = predicted;
        }
        else
        {
            _progressRatio = Math.Max(_progressRatio, predicted);
        }

        _progressRatio = Math.Clamp(_progressRatio, 0d, 1d);

        // 线性动画覆盖一个刷新周期，避免每 500ms 重启动画造成的台阶感
        ProgressScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_progressRatio, TimeSpan.FromMilliseconds(650))
            {
                FillBehavior = FillBehavior.HoldEnd
            });

        if (DebugLogEnabled && (++_progressTicks % 4 == 0))
        {
            DebugLog($"progress sampled={sampled:P1} shown={_progressRatio:P1} dur={media.Duration:mm\\:ss} pos={media.Position:mm\\:ss} playing={media.IsPlaying}");
        }
        // 有歌词就用当前行替换副标题（没歌词时保留歌名/歌手）
        if (_lyrics is { Count: > 0 } lyrics)
        {
            var line = LrcLyrics.LineAt(lyrics, media.Position);
            if (!string.IsNullOrWhiteSpace(line))
            {
                TrackSubtitle.Text = line;
            }
        }
    }

    private static void Fade(UIElement element, double target, bool animate)
    {
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = target;
            return;
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    // ---------------- 交互：播放控制 / 钉住 ----------------

    private void OnIslandMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndSliderDrag();
    }

    private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 大面板时点岛体：先退回小卡片
        if (_maximized)
        {
            _maximized = false;
            ApplyMode(animate: true);
            RefreshState(animate: true);
            return;
        }

        // 点在岛体空白处：切换“钉住”
        _pinned = !_pinned;
        RefreshState(animate: true);
    }

    /// <summary>滚轮在岛上来回切换：小卡片 ↔ 二级大面板。</summary>
    private void OnIslandWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_renderedExpanded)
        {
            return;
        }

        e.Handled = true;
        _maximized = !_maximized;

        if (_maximized)
        {
            _pinned = true;
            RenderNotifications();
            UpdateMaxPanel();
        }

        ApplyMode(animate: true);
        RefreshState(animate: true);
    }

    /// <summary>点在大面板的空白处不收起面板（只有点岛外才收起）。</summary>

    private void OnMaxPanelMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;


    // ---------------- 快捷键 ----------------

    /// <summary>Ctrl+Alt+I：展开/收起（钉住）。</summary>
    private void TogglePinned()
    {
        if (_renderedExpanded)
        {
            _pinned = false;
            _expandedByHover = false;
            _maximized = false;
        }
        else
        {
            _pinned = true;
        }

        ApplyMode(animate: true);
        RefreshState(animate: true);
    }

    /// <summary>Ctrl+Alt+M：打开/关闭二级大面板。</summary>
    private void ToggleMax()
    {
        _maximized = !_maximized;

        if (_maximized)
        {
            _pinned = true;
            RenderNotifications();
            UpdateMaxPanel();
        }

        ApplyMode(animate: true);
        RefreshState(animate: true);
    }

    private async void OnPlayPauseClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LogControl("playpause", await _mediaWatcher.TogglePlayPauseAsync());
    }

    private async void OnPreviousClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LogControl("previous", await _mediaWatcher.SkipPreviousAsync());
    }

    private async void OnNextClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LogControl("next", await _mediaWatcher.SkipNextAsync());
    }

    private static void LogControl(string action, bool ok)
    {
        if (DebugLogEnabled)
        {
            DebugLog($"control {action} ok={ok}");
        }
    }

    // ---------------- 悬停 / 命中 ----------------

    private void PollHover()
    {
        if (!_settings.HoverEnabled || !_windowVisible)
        {
            return;
        }

        var inside = IsCursorOverIsland(HoverPadding);

        if (DebugLogEnabled && inside != _lastInsideLogged)
        {
            Native.GetCursorPos(out var pt);
            TryGetIslandRect(out var rectLeft, out var rectTop, out var rectWidth, out var rectHeight);
            DebugLog($"hover inside={inside} cursor=({pt.X},{pt.Y}) rect=({rectLeft:F0},{rectTop:F0},{rectWidth:F0}x{rectHeight:F0})");
            _lastInsideLogged = inside;
        }

        if (inside)
        {
            _hoverLeftAt = null;

            if (!_expandedByHover)
            {
                _hoverStartedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _hoverStartedAt.Value >= HoverDwell)
                {
                    _hoverStartedAt = null;
                    _expandedByHover = true;
                    RefreshState(animate: true);
                }
            }

            return;
        }

        _hoverStartedAt = null;

        if (_expandedByHover && !_pinned)
        {
            _hoverLeftAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _hoverLeftAt.Value >= HoverGrace)
            {
                _hoverLeftAt = null;
                _expandedByHover = false;
                RefreshState(animate: true);
            }
        }
    }

    private bool TryGetIslandRect(out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0d;

        if (_hwnd == IntPtr.Zero || ActualWidth <= 0d)
        {
            return false;
        }

        if (!Native.GetWindowRect(_hwnd, out var windowRect) || windowRect.Width <= 0)
        {
            return false;
        }

        // 物理像素 / DIP，得到当前显示器的缩放比。
        var scale = windowRect.Width / ActualWidth;
        width = Island.ActualWidth * scale;
        height = Island.ActualHeight * scale;
        left = ((windowRect.Left + windowRect.Right) / 2d) - (width / 2d);
        top = windowRect.Top + (Island.Margin.Top * scale);
        return true;
    }

    private bool IsCursorOverIsland(double padding)
    {
        if (!Native.GetCursorPos(out var cursor) || !TryGetIslandRect(out var left, out var top, out var width, out var height))
        {
            return false;
        }

        return cursor.X >= left - padding
            && cursor.X <= left + width + padding
            && cursor.Y >= top - padding
            && cursor.Y <= top + height + padding;
    }

    private bool IsScreenPointInsideIsland(int x, int y)
    {
        if (!TryGetIslandRect(out var left, out var top, out var width, out var height))
        {
            return false;
        }

        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    /// <summary>切换鼠标穿透：收起时穿透（点击落到桌面），展开时可点。</summary>
    private void SetInteractive(bool interactive)
    {
        if (_interactive == interactive || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _interactive = interactive;

        var exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        var updated = interactive
            ? exStyle & ~Native.WS_EX_TRANSPARENT
            : exStyle | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, updated);

        if (interactive)
        {
            InstallMouseHook();
        }
        else
        {
            RemoveMouseHook();
        }
    }

    // ---------------- 岛外点击 → 解除钉住 ----------------

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        _mouseProc = OnLowLevelMouse;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();

            if (message == Native.WM_LBUTTONDOWN)
            {
                var down = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

                if (!IsScreenPointInsideIsland(down.pt.X, down.pt.Y)
                    && (_pinned || _expandedByHover || _maximized))
                {
                    _pinned = false;
                    _expandedByHover = false;
                    _maximized = false;
                    ApplyMode(animate: true);
                    RefreshState(animate: true);
                }
            }
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ---------------- 滑杆拖动（鼠标钩子驱动） ----------------

    private readonly List<SliderRow> _sliderRows = new();
    private SliderRow? _dragRow;
    private int _dragLastX;
    private int _dragLastY;
    private DateTime _dragLastMoveAt;
    private DispatcherTimer? _dragTimer;

    /// <summary>
    /// 按下滑杆后起一个 16ms 定时器，轮询光标位置与左键状态来拖动。
    /// 不用 CaptureMouse（NOACTIVATE 窗口会静默失败），也不依赖鼠标钩子收事件。
    /// </summary>
    private void BeginSliderDrag(SliderRow row)
    {
        if (DebugLogEnabled) { DebugLog($"drag begin ratio={row.Ratio:F2}"); }
        _dragRow = row;
        _dragLastMoveAt = DateTime.UtcNow;

        if (Native.GetCursorPos(out var startCursor))
        {
            _dragLastX = startCursor.X;
            _dragLastY = startCursor.Y;
        }

        UpdateSliderDragFromCursor();

        if (_dragTimer is null)
        {
            _dragTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _dragTimer.Tick += (_, _) => TickSliderDrag();
        }

        _dragTimer.Start();
    }

    private void TickSliderDrag()
    {
        if (_dragRow is null)
        {
            _dragTimer?.Stop();
            return;
        }

        // 松手由 WPF 的 MouseLeftButtonUp 收尾。
        // 不能用 GetAsyncKeyState 判断：鼠标事件合成出来的按下不会更新异步键状态。
        UpdateSliderDragFromCursor();
    }

    private bool TryBeginSliderDragAt(int screenX, int screenY)
    {
        foreach (var row in _sliderRows)
        {
            try
            {
                // 命中范围也用屏幕像素计算，和拖动映射保持同一套单位
                var topLeft = row.Track.PointToScreen(new Point(0, 0));
                var bottomRight = row.Track.PointToScreen(new Point(row.Track.ActualWidth, row.Track.ActualHeight));

                if (bottomRight.X - topLeft.X <= 1)
                {
                    continue;
                }

                if (screenX < topLeft.X || screenX > bottomRight.X
                    || screenY < topLeft.Y - 8 || screenY > bottomRight.Y + 8)
                {
                    continue;
                }

                BeginSliderDrag(row);
                return true;
            }
            catch
            {
                // 未布局
            }
        }

        return false;
    }

    /// <summary>用 PointToScreen 量出轨道的屏幕像素范围，规避 DIP/像素混用。</summary>
    private void ApplySliderFromScreenX(SliderRow row, int screenX)
    {
        var (left, travel, knobPx) = MeasureTrack(row);
        var ratio = Math.Clamp((screenX - left - (knobPx / 2d)) / travel, 0d, 1d);

        SetSlider(row, ratio);
        row.Apply?.Invoke(ratio);
        row.Commit?.Invoke(ratio);
    }

    private static (double Left, double Travel, double KnobPx) MeasureTrack(SliderRow row)
    {
        var left = row.Track.PointToScreen(new Point(0, 0)).X;
        var right = row.Track.PointToScreen(new Point(row.Track.ActualWidth, 0)).X;

        var dipWidth = Math.Max(row.Track.ActualWidth, 1d);
        var scale = (right - left) / dipWidth;
        var knobPx = row.KnobSize * scale;   // 无滑块时为 0

        if (DebugLogEnabled)
        {
            DebugLog($"slider px left={left:F1} right={right:F1} knobPx={knobPx:F1} dipWidth={row.Track.ActualWidth:F1}");
        }

        return (left, Math.Max(right - left - knobPx, 1d), knobPx);
    }

    private void UpdateSliderDragFromCursor()
    {
        if (_dragRow is null || !Native.GetCursorPos(out var cursor))
        {
            return;
        }

        try
        {
            // 关键：全部用屏幕物理像素，避免 DIP 与像素混用造成的“越靠右越偏”
            var (left, travel, knobPx) = MeasureTrack(_dragRow);
            var ratio = Math.Clamp((cursor.X - left - (knobPx / 2d)) / travel, 0d, 1d);

            SetSlider(_dragRow, ratio);
            _dragRow.Apply?.Invoke(ratio);
        }
        catch
        {
            // 布局正在变化时忽略这一帧
        }
    }

    private void EndSliderDrag()
    {
        if (DebugLogEnabled) { DebugLog("drag end"); }
        _dragTimer?.Stop();

        var row = _dragRow;
        _dragRow = null;

        _probeBlockedUntil = DateTime.UtcNow.AddMilliseconds(1200);
        row?.Commit?.Invoke(row.Ratio);
    }

    // ---------------- 尺寸 / 显隐 ----------------

    private double MeasureExpandedWidth()
    {
        var panel = _media is not null ? (FrameworkElement)MediaPanel : ClockPanel;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(MinExpandedWidth, Math.Ceiling(panel.DesiredSize.Width));
    }

    private double MeasureMaxWidth()
    {
        MaxPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(MinExpandedWidth, Math.Ceiling(MaxPanel.DesiredSize.Width));
    }

    private double MeasureMaxHeight()
    {
        MaxPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(ExpandedHeight, Math.Ceiling(MaxPanel.DesiredSize.Height));
    }

    private void RefreshState(bool animate)
    {
        var expanded = ForceExpanded || _expandedByFocus || _expandedByHover || _pinned || _maximized;

        if (DebugLogEnabled)
        {
            DebugLog($"RefreshState expanded={expanded} focus={_expandedByFocus} hover={_expandedByHover} pinned={_pinned} max={_maximized} fg={Native.GetClassNameSafe(Native.GetForegroundWindow())}");
        }

        var width = _maximized ? MeasureMaxWidth() : expanded ? MeasureExpandedWidth() : CollapsedWidth;
        var height = _maximized ? MeasureMaxHeight() : expanded ? ExpandedHeight : CollapsedHeight;

        if (DebugLogEnabled && _maximized != _lastMeasuredMaximized)
        {
            _lastMeasuredMaximized = _maximized;
            MeasureFrames(_maximized ? "to-max" : "to-small");
        }

        // 展开/收起的同时切换鼠标穿透
        SetInteractive(expanded);

        if (_renderedExpanded == expanded
            && Math.Abs(_renderedWidth - width) < 0.5d
            && Math.Abs(_renderedHeight - height) < 0.5d)
        {
            return;
        }

        _renderedExpanded = expanded;
        _renderedWidth = width;
        _renderedHeight = height;

        var leavingMax = _renderedMaximized && !_maximized;
        _renderedMaximized = _maximized;

        ApplySize(expanded, width, height, animate, leavingMax);
        UpdateProgressVisibility(animate);
        // 钉住指示线只在“小卡片”里出现；大面板上它会横贯成一条白线
        Fade(PinnedLine, _pinned && !_maximized ? 0.9d : 0d, animate);
        LogGeometrySoon();
    }


    /// <summary>统计一段动画期间实际渲染了多少帧（仅在 ISLAND_LOG=1 时）。</summary>
    private void MeasureFrames(string tag)
    {
        if (!DebugLogEnabled)
        {
            return;
        }

        var frames = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, EventArgs e) => frames++;

        CompositionTarget.Rendering += OnRendering;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CompositionTarget.Rendering -= OnRendering;
            DebugLog($"frames {tag}={frames} in {watch.ElapsedMilliseconds}ms shadow={(Island.Effect is null ? "off" : "on")}");
        };
        timer.Start();
    }

    /// <summary>动画结束后把真实几何打进日志（仅在 ISLAND_LOG=1 时）。</summary>
    private void LogGeometrySoon()
{
        if (!DebugLogEnabled)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ClockPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            MediaPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            DebugLog(
                $"geometry w={Island.ActualWidth:F1} h={Island.ActualHeight:F1} marginTop={Island.Margin.Top:F1} interactive={_interactive} " +
                $"pinnedLine={PinnedLine.Opacity:F2} collapsedText={CollapsedPanel.Opacity:F2} progress={ProgressFill.ActualWidth:F0}x{ProgressFill.ActualHeight:F0}a{ProgressFill.Opacity:F2} " +
                $"clock={ClockPanel.DesiredSize.Width:F0}x{ClockPanel.DesiredSize.Height:F0} media={MediaPanel.DesiredSize.Width:F0}x{MediaPanel.DesiredSize.Height:F0}");
        };
        timer.Start();
    }

    private void ApplySize(bool expanded, double width, double height, bool animate, bool leavingMax = false)
    {
        var targetMargin = new Thickness(0, expanded ? AttachedTopMargin : FloatingTopMargin, 0, 0);
        var duration = TimeSpan.FromMilliseconds(expanded ? 380 : 220);


        // 收起态显示时间，展开态淡出
        Fade(CollapsedPanel, expanded ? 0d : 1d, animate);

        // 透明不等于不可点：不参与当前布局的层必须显式关掉命中测试，
        // 否则大面板会挡住小卡片的按钮（反之亦然）。
        MaxPanel.IsHitTestVisible = _maximized;
        ContentRoot.IsHitTestVisible = !_maximized;
        CollapsedPanel.IsHitTestVisible = !expanded;

        if (DebugLogEnabled && _maximized)
        {
            LogTabRects();
        }

        // 吸顶时去掉描边：Border 的描边画在边界内侧，1 DIP 在 150% 缩放下占 2 物理像素，
        // 半透明描边会在屏幕最上方留出一条浅色“缝”，看起来像没贴到底。
        Island.BorderThickness = expanded ? new Thickness(0) : new Thickness(1);

        if (!animate)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.BeginAnimation(MarginProperty, null);
            Island.Width = width;
            Island.Height = height;
            Island.Margin = targetMargin;

            ContentRoot.BeginAnimation(OpacityProperty, null);
            ContentRoot.Opacity = expanded ? 1d : 0d;
            ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ContentScale.ScaleX = ContentScale.ScaleY = expanded ? 1d : 0.93d;
            return;
        }

        IEasingFunction sizeEase = expanded
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };

        Island.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(width, duration) { EasingFunction = sizeEase, FillBehavior = FillBehavior.HoldEnd });
        Island.BeginAnimation(
            HeightProperty,
            new DoubleAnimation(height, duration) { EasingFunction = sizeEase, FillBehavior = FillBehavior.HoldEnd });

        Island.BeginAnimation(
            MarginProperty,
            new ThicknessAnimation(targetMargin, duration) { EasingFunction = sizeEase, FillBehavior = FillBehavior.HoldEnd });

        var fadeDelay = expanded ? TimeSpan.FromMilliseconds(110) : TimeSpan.Zero;
        var fadeDuration = expanded ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromMilliseconds(120);

        ContentRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(expanded ? 1d : 0d, fadeDuration)
            {
                BeginTime = fadeDelay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });

        var scaleTo = expanded ? 1d : 0.93d;
        var scaleAnim = new DoubleAnimation(scaleTo, duration)
        {
            BeginTime = fadeDelay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void UpdateWindowVisibility()
    {
        var shouldShow = !_hiddenByUser && !_fullscreen;
        if (shouldShow == _windowVisible || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _windowVisible = shouldShow;
        Native.ShowWindow(_hwnd, shouldShow ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
    }

    // ---------------- 窗口消息 ----------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyIdQuit:
                handled = true;
                Close();
                return IntPtr.Zero;

            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyIdToggle:
                handled = true;
                TogglePinned();
                return IntPtr.Zero;

            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyIdMax:
                handled = true;
                ToggleMax();
                return IntPtr.Zero;

            case Native.WM_NCHITTEST when _interactive:
            {
                // 窗口比岛大（为了投影），命中落在岛外就交给下层窗口。
                var raw = lParam.ToInt64();
                var x = (short)(raw & 0xFFFF);
                var y = (short)((raw >> 16) & 0xFFFF);
                handled = true;
                return IsScreenPointInsideIsland(x, y)
                    ? new IntPtr(Native.HTCLIENT)
                    : new IntPtr(Native.HTTRANSPARENT);
            }
        }

        return IntPtr.Zero;
    }

    private static void DebugLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "island-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}