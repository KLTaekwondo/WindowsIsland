using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsIsland;

/// <summary>当前媒体会话的快照。</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string App,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    BitmapImage? Artwork,
    DateTime SampledAt,
    bool SupportsControls = true,
    System.Windows.Media.Color? Accent = null);

/// <summary>
/// 通过 Windows 的 GSMTC（全局系统媒体传输控件）读取「正在播放」。
/// 任何一步失败都静默降级为 null，界面退回到时钟模式，不让系统调用影响主流程。
/// </summary>
public sealed class MediaWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artworkKey = string.Empty;
    private BitmapImage? _artwork;
    private System.Windows.Media.Color? _accent;
    private bool _refreshing;
    private DateTime _refreshingSince;

    public MediaWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _pollTimer.Tick += (_, _) => Refresh();
    }

    public MediaInfo? Current { get; private set; }

    /// <summary>曲目 / 播放状态变化时触发；null 表示当前没有媒体会话。</summary>
    public event EventHandler<MediaInfo?>? Changed;

    public void Start()
    {
        _pollTimer.Start();
        _ = InitializeAsync();
    }

    /// <summary>立刻刷新一次（按键操作后调用，别等 1.5s 的轮询）。</summary>
    public void RefreshNow() => Refresh();

    public async Task<bool> TogglePlayPauseAsync()
    {
        try
        {
            if (_session is not null)
            {
                var ok = await _session.TryTogglePlayPauseAsync();
                RefreshNow();
                return ok;
            }
        }
        catch
        {
            // 会话在按键瞬间消失：忽略
        }

        return false;
    }

    public async Task<bool> SkipNextAsync()
    {
        try
        {
            if (_session is not null)
            {
                var ok = await _session.TrySkipNextAsync();
                RefreshNow();
                return ok;
            }
        }
        catch
        {
            // 同上
        }

        return false;
    }

    public async Task<bool> SkipPreviousAsync()
    {
        try
        {
            if (_session is not null)
            {
                var ok = await _session.TrySkipPreviousAsync();
                RefreshNow();
                return ok;
            }
        }
        catch
        {
            // 同上
        }

        return false;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _session = null;
        _manager = null;
        Current = null;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _dispatcher.BeginInvoke(new Action(Refresh));
            Refresh();
        }
        catch
        {
            // 系统不支持 GSMTC 或被策略禁用：保持 null，界面用时钟模式。
        }
    }

    private static readonly bool DebugLogEnabled =
        Environment.GetEnvironmentVariable("ISLAND_LOG") == "1";

    private static void DebugLog(string message)
    {
        if (!DebugLogEnabled)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "island-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void Refresh()
    {
        // 看门狗：万一上一次刷新卡死（WinRT 调用不返回），别让轮询永远停摆
        if (_refreshing && (DateTime.UtcNow - _refreshingSince) > TimeSpan.FromSeconds(6))
        {
            DebugLog("refresh watchdog: reset stuck flag");
            _refreshing = false;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_manager is null || _refreshing)
        {
            return;
        }

        _refreshing = true;
        _refreshingSince = DateTime.UtcNow;
        try
        {
            // 不再只取 GetCurrentSession()（那是系统挑的“最后变化”的那条）：
            // 枚举所有会话，优先正在播放的，其次保持当前选中的那条，
            // 这样“网易云在放、浏览器停在暂停态”时也能读到网易云。
            var sessions = _manager.GetSessions();
            var session = SelectSession(sessions);

            if (DebugLogEnabled && sessions.Count > 0)
            {
                DebugLog("sessions=" + string.Join(", ", sessions.Select(s =>
                {
                    try
                    {
                        return $"{s.SourceAppUserModelId}:{s.GetPlaybackInfo().PlaybackStatus}";
                    }
                    catch
                    {
                        return s.SourceAppUserModelId;
                    }
                })) + $" chosen={session?.SourceAppUserModelId ?? "none"}");
            }

            // 选中的会话没在播放（或根本没有会话）时，才考虑窗口标题兜底：
            // 这样「Edge 挂着暂停会话、网易云在放」的场景也能读到网易云。
            var sessionPlaying = false;
            if (session is not null)
            {
                try
                {
                    sessionPlaying = session.GetPlaybackInfo().PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch
                {
                    sessionPlaying = false;
                }
            }

            // 注意：这里曾经有一个“抓窗口标题”的兜底分支，已经移除——
            // 它无法区分“暂停”和“正在播”（标题在暂停后仍然存在），
            // 结果是暂停时会用一个旧歌名顶上来，反而比没有更糟。

            if (session is null)
            {
                _session = null;
                _artwork = null;
                _artworkKey = string.Empty;
                Publish(null);
                return;
            }

            if (!ReferenceEquals(session, _session))
            {
                _session = session;
                _artworkKey = string.Empty;
                _artwork = null;

                // 只用轮询：会话在两个应用之间来回切时会反复订阅同一批事件，
                // 而事件里又调 Refresh，结果是成倍的重绘（进度条一顿一顿的元凶之一）。
                // 1.5s 轮询对“切歌延迟”完全够用。
            }

            var playback = session.GetPlaybackInfo();
            var properties = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();

            var key = $"{properties.Title}\u0001{properties.Artist}\u0001{properties.AlbumTitle}";
            if (!string.Equals(key, _artworkKey, StringComparison.Ordinal))
            {
                _artworkKey = key;

                // 缩略图流有可能永远不返回（网易云这类 Electron 播放器实测会卡住），
                // 卡住会让整个刷新流程停摆，所以给个超时，超时就先不要封面。
                var artworkTask = LoadArtworkAsync(properties);
                var winner = await Task.WhenAny(artworkTask, Task.Delay(1500));

                if (winner == artworkTask)
                {
                    _artwork = await artworkTask;
                }
                else
                {
                    _artwork = null;
                    DebugLog("artwork timeout, continue without cover");
                }

                _accent = ExtractAccent(_artwork);
            }

            DebugLog($"timeline app={session.SourceAppUserModelId} status={playback.PlaybackStatus} pos={timeline.Position} start={timeline.StartTime} end={timeline.EndTime}");

            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;

            // 有的播放器（网易云实测）只上报元数据、不上报时间轴：
            // canSeek=false 且 end=start=0。这种情况直接把时长当 0，
            // 界面就会用“不确定态脉冲”而不是一条永远不动的进度条。
            if (!playback.Controls.IsPlaybackPositionEnabled)
            {
                duration = TimeSpan.Zero;
            }
            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            Publish(new MediaInfo(
                properties.Title ?? string.Empty,
                properties.Artist ?? string.Empty,
                FriendlyName(session.SourceAppUserModelId),
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                position,
                duration,
                _artwork,
                DateTime.UtcNow,
                true,
                _accent));
        }
        catch (Exception ex)
        {
            DebugLog($"media refresh failed {ex.GetType().Name} hresult=0x{ex.HResult:X8} msg={ex.Message}");
            Publish(null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>在多条媒体会话里挑一条：正在播放的优先，其次维持当前选中的，最后取第一条。</summary>
    private GlobalSystemMediaTransportControlsSession? SelectSession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (sessions.Count == 0)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? best = null;
        var bestScore = int.MinValue;
        var currentId = _session?.SourceAppUserModelId;

        foreach (var session in sessions)
        {
            var score = 0;

            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    score += 100;
                }
            }
            catch
            {
                // 会话刚消失
            }

            if (currentId is not null && string.Equals(session.SourceAppUserModelId, currentId, StringComparison.Ordinal))
            {
                score += 10;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = session;
            }
        }

        return best;
    }

    private void Publish(MediaInfo? info)
    {
        var previous = Current;

        // 位置会一直变，但它不是「值得重建界面」的理由：
        // 只有标题/歌手/播放状态/时长/封面变了才通知 UI，位置由 UI 侧自己推算。
        if (previous is not null && info is not null && !IsSignificantChange(previous, info))
        {
            Current = info;
            return;
        }

        if (Equals(previous, info))
        {
            return;
        }

        Current = info;
        Changed?.Invoke(this, info);

        if (DebugLogEnabled)
        {
            DebugLog($"publish title={info?.Title} playing={info?.IsPlaying} controls={info?.SupportsControls}");
        }
    }

    private static bool IsSignificantChange(MediaInfo a, MediaInfo b)
        => !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
           || !string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
           || !string.Equals(a.App, b.App, StringComparison.Ordinal)
           || a.IsPlaying != b.IsPlaying
           || a.Duration != b.Duration
           || !ReferenceEquals(a.Artwork, b.Artwork);

    /// <summary>从封面里取一个“有彩度”的颜色当强调色；取不到就用薄荷绿。</summary>
    private static System.Windows.Media.Color? ExtractAccent(BitmapImage? image)
    {
        if (image is null)
        {
            return null;
        }

        try
        {
            var writeable = new System.Windows.Media.Imaging.WriteableBitmap(image);
            var stride = writeable.PixelWidth * 4;
            var pixels = new byte[stride * writeable.PixelHeight];
            writeable.CopyPixels(pixels, stride, 0);

            long r = 0, g = 0, b = 0, count = 0;

            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                int b0 = pixels[i], g0 = pixels[i + 1], r0 = pixels[i + 2], a0 = pixels[i + 3];
                if (a0 < 128)
                {
                    continue;
                }

                var max = Math.Max(r0, Math.Max(g0, b0));
                var min = Math.Min(r0, Math.Min(g0, b0));
                if (max - min < 25)
                {
                    continue; // 灰白像素没有色彩信息
                }

                r += r0;
                g += g0;
                b += b0;
                count++;
            }

            if (DebugLogEnabled)
            {
                DebugLog($"accent samples={count}");
            }

            if (count < 20)
            {
                return null;
            }

            var color = System.Windows.Media.Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            return Vivid(color);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把颜色提亮到在深色背景上足够醒目。</summary>
    private static System.Windows.Media.Color Vivid(System.Windows.Media.Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        if (max == 0)
        {
            return System.Windows.Media.Color.FromRgb(0x5B, 0xE4, 0x9B);
        }

        var scale = 225d / max;
        var r = (byte)Math.Min(255, color.R * scale);
        var g = (byte)Math.Min(255, color.G * scale);
        var b = (byte)Math.Min(255, color.B * scale);

        return System.Windows.Media.Color.FromRgb(r, g, b);
    }

    private static async Task<BitmapImage?> LoadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        try
        {
            if (properties.Thumbnail is null)
            {
                return null;
            }

            using var stream = await properties.Thumbnail.OpenReadAsync();
            if (stream.Size == 0)
            {
                return null;
            }

            var buffer = new byte[(int)stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(buffer);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = new MemoryStream(buffer);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlyName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return string.Empty;
        }

        var id = appUserModelId;

        // 形如 Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic 的包标识只取可读段。
        var bang = id.IndexOf('!');
        if (bang >= 0 && bang < id.Length - 1)
        {
            id = id[(bang + 1)..];
        }

        var dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1)
        {
            id = id[(dot + 1)..];
        }

        return id.Trim();
    }
}
