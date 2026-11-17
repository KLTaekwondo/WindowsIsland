using System.IO;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsIsland;

/// <summary>一条系统通知的快照。</summary>
public sealed record IslandNotification(
    string App,
    string Title,
    string Body,
    DateTimeOffset Time,
    string Aumid,
    byte[]? IconBytes);

/// <summary>
/// 读取 Windows 通知中心里的 toast 通知（UserNotificationListener）。
/// 两个坑：
///   1. 订阅 NotificationChanged 会抛 COMException(0x80070490)，不能因此判定“无权限”，所以退化为轮询；
///   2. 读取调用放在线程池（MTA）上跑，避免 WPF STA 线程的公寓问题。
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private static readonly bool DebugLogEnabled =
        Environment.GetEnvironmentVariable("ISLAND_LOG") == "1";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<string, byte[]?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public NotificationWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += (_, _) => Refresh();
    }

    /// <summary>按时间倒序，最多 8 条。</summary>
    public IReadOnlyList<IslandNotification> Items { get; private set; } = Array.Empty<IslandNotification>();

    /// <summary>true 表示系统没给权限（或被策略禁用）。</summary>
    public bool AccessDenied { get; private set; }

    public event EventHandler? Changed;

    public void Start()
    {
        _pollTimer.Start();
        _ = InitializeAsync();
    }

    public void Dispose() => _pollTimer.Stop();

    private async Task InitializeAsync()
    {
        try
        {
            var (before, access) = await Task.Run(async () =>
            {
                var listener = UserNotificationListener.Current;
                var status = listener.GetAccessStatus();
                var requested = await listener.RequestAccessAsync();
                return (status, requested);
            });

            DebugLog($"notif access before={before} after={access}");

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                AccessDenied = true;
                _pollTimer.Stop();
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            _initialized = true;

            try
            {
                var listener = UserNotificationListener.Current;
                listener.NotificationChanged += (_, _) => _dispatcher.BeginInvoke(new Action(Refresh));
            }
            catch (Exception ex)
            {
                // 订阅事件在这台机器上会抛 0x80070490，不影响轮询
                DebugLog($"notif subscribe failed {ex.GetType().Name} hresult=0x{ex.HResult:X8}");
            }

            Refresh();
        }
        catch (Exception ex)
        {
            DebugLog($"notif init exception {ex.GetType().Name}: {ex.Message}");
            AccessDenied = true;
            _pollTimer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            var models = await Task.Run(async () =>
            {
                var listener = UserNotificationListener.Current;
                var raw = await listener.GetNotificationsAsync(NotificationKinds.Toast);

                var list = new List<(string App, string Title, string Body, DateTimeOffset Time, string Aumid)>();

                foreach (var note in raw.OrderByDescending(n => n.CreationTime).Take(8))
                {
                    var aumid = note.AppInfo?.AppUserModelId ?? string.Empty;
                    var app = note.AppInfo?.DisplayInfo?.DisplayName ?? "通知";

                    string title = string.Empty;
                    string body = string.Empty;

                    try
                    {
                        var binding = note.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                        var texts = binding?.GetTextElements();
                        if (texts is { Count: > 0 })
                        {
                            title = texts[0].Text ?? string.Empty;
                            if (texts.Count > 1)
                            {
                                body = string.Join(" ", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                            }
                        }
                    }
                    catch
                    {
                        // 没有通用模板的通知取不到文本
                    }

                    list.Add((app, title, body, note.CreationTime, aumid));
                }

                return list;
            });

            // 图标逐个拉取（带缓存），失败就留空
            var result = new List<IslandNotification>(models.Count);
            foreach (var model in models)
            {
                var icon = await GetIconAsync(model.Aumid);
                result.Add(new IslandNotification(model.App, model.Title, model.Body, model.Time, model.Aumid, icon));
            }

            Items = result;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DebugLog($"notif refresh exception {ex.GetType().Name} hresult=0x{ex.HResult:X8}");
        }
    }

    private async Task<byte[]?> GetIconAsync(string aumid)
    {
        if (string.IsNullOrEmpty(aumid))
        {
            return null;
        }

        if (_iconCache.TryGetValue(aumid, out var cached))
        {
            return cached;
        }

        byte[]? bytes = null;

        try
        {
            bytes = await Task.Run(async () =>
            {
                var listener = UserNotificationListener.Current;
                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var match = notifications.FirstOrDefault(n =>
                    string.Equals(n.AppInfo?.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase));

                var display = match?.AppInfo?.DisplayInfo;
                if (display is null)
                {
                    return null;
                }

                using var stream = await display.GetLogo(new Size(24, 24)).OpenReadAsync();
                if (stream.Size == 0)
                {
                    return null;
                }

                var buffer = new byte[(int)stream.Size];
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(buffer);
                return buffer;
            });
        }
        catch
        {
            bytes = null;
        }

        _iconCache[aumid] = bytes;
        return bytes;
    }

    private static void DebugLog(string message)
    {
        if (!DebugLogEnabled)
        {
            return;
        }

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
