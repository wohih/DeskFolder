using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace DeskFolder.Services;

/// <summary>
/// 音乐检测与控制服务：检测播放器（酷狗优先 + 通用 SMTC 兜底）、获取当前播放信息、控制播放状态、调度歌词与封面。
/// 所有 Win32 探测在后台线程完成，绝不阻塞 UI。
/// 职责分区：进程/窗口检测(DetectKugou)、SMTC 会话(FindKugouSession/FindActiveMusicSession/HookSessionEvents+事件)、
/// 歌曲信息(UpdateSongInfo/UpdateSongInfoFromSmtc，统一用 SMTC 元数据)、播放状态(CheckPlaybackStatus/ApplyPlaybackState)、
/// 播放控制(PlayPause/NextTrack 等媒体键)、歌词(SearchLyricsAsync/LyricsTickCallback/FindLyricIndex)、封面(FetchAlbumArtAsync)。
/// </summary>
public class MusicService : IDisposable
{
    // Win32 API
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // WinEvent Hook（监听酷狗窗口标题变化；标题是跑马灯，配合 ParseSongTitle 的 EndsWith 校验只在完整瞬间解析）
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // 媒体键常量
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_STOP = 0xB2;

    // 酷狗相关
    private static readonly string[] KUGOU_PROCESS_NAMES = { "KuGou", "KGMusic", "kgmusic" };

    // SMTC（系统媒体传输控制）用于检测实际播放状态
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private GlobalSystemMediaTransportControlsSession? _smtcSession;
    private GlobalSystemMediaTransportControlsSession? _subscribedSession; // 已订阅事件的 SMTC 会话
    private bool _sessionsHooked; // 是否已订阅 manager.SessionsChanged

    private IntPtr _kugouWindowHandle;
    private readonly Timer _updateTimer;
    private readonly Timer _lyricsTimer; // 歌词滚动专用定时器（更高频率）
    private readonly Dispatcher _uiDispatcher;
    private bool _isPlaying;
    private string _currentTitle = "";
    private string _currentArtist = "";
    private string _currentAlbum = "";
    private List<LyricLine> _currentLyrics = new();
    private bool _isDisposed;

    // 播放进度估算
    private DateTime _playStartTime;   // 开始播放的时刻
    private double _playOffset;        // 播放偏移量（秒），用于暂停/恢复
    private int _lastLyricIndex = -1;  // 上次通知的歌词行索引
    private bool _userPaused;          // 用户主动暂停（防止启发式回退覆盖）

    // SMTC 时间轴可靠性检测（BugFix: 歌词卡住不滚动）：酷狗等播放器不上报时间轴，
    // GetTimelineProperties().Position 恒为 0/不前进，播放中累计停滞超阈值则判定不可靠并回退 DateTime 估算
    private double _lastSmtcPosition = -1;  // 上次读到的 SMTC 位置（-1 = 尚未建立基准）
    private DateTime _lastSmtcReadTime;     // 上次读取 SMTC 位置的时刻
    private double _smtcStallSeconds;       // 播放中 SMTC 位置停滞的累计时长（秒）
    private bool _smtcTimelineBroken;       // 已判定 SMTC 时间轴不可靠 → 用时间估算
    private const double SmtcStallThresholdSec = 3.0; // 停滞判定阈值（秒）

    // 避免事件风暴：记录上次触发的状态，只有真正变化才通知
    private string _lastNotifiedTitle = "";
    private string _lastNotifiedArtist = "";
    private BitmapSource? _currentAlbumArt; // 当前歌曲真实专辑封面（Freeze 后可跨线程安全读取）
    private int _albumArtToken; // 封面请求令牌：每次请求递增，异步结果携带旧令牌则丢弃（防换歌竞态，同 SearchLyricsAsync 模式）
    private int _artRefetchGeneration; // 封面防抖重取代际：MediaPropertiesChanged 高频连发时折叠为一次，避免反复解码/刷 UI
    private string _lastLoggedSmtc = ""; // 调试日志去重：上次记录的 SMTC 元数据
    private string _lastLoggedRawTitle = ""; // 调试日志去重：上次记录的完整窗口标题

    // WinEvent Hook（酷狗窗口标题变化监听）
    private WinEventDelegate? _titleChangeProc; // 保持引用防止 GC 回收
    private IntPtr _titleChangeHook = IntPtr.Zero;

    /// <summary>当前歌曲标题</summary>
    public string CurrentTitle => _currentTitle;
    /// <summary>当前艺术家</summary>
    public string CurrentArtist => _currentArtist;
    /// <summary>当前专辑</summary>
    public string CurrentAlbum => _currentAlbum;
    /// <summary>是否正在播放</summary>
    public bool IsPlaying => _isPlaying;
    /// <summary>带时间戳的歌词列表</summary>
    public IReadOnlyList<LyricLine> CurrentLyrics => _currentLyrics;
    /// <>当前歌词行索引（-1 表示无）</summary>
    public int CurrentLyricIndex => _lastLyricIndex;
    /// <summary>酷狗是否在运行</summary>
    public bool IsKugouRunning => _kugouWindowHandle != IntPtr.Zero;
    /// <summary>当前歌曲的真实专辑封面（来自 SMTC thumbnail，可能为 null）</summary>
    public BitmapSource? CurrentAlbumArt => _currentAlbumArt;

    /// <summary>歌曲信息更新事件（由后台线程触发，已 marshal 到 UI 线程）</summary>
    public event EventHandler? SongInfoChanged;
    /// <summary>播放状态变化事件</summary>
    public event EventHandler? PlaybackStateChanged;
    /// <summary>歌词更新事件（歌词数据变化）</summary>
    public event EventHandler? LyricsChanged;
    /// <summary>歌词位置变化事件（高频，用于歌词滚动）</summary>
    public event EventHandler? LyricsPositionChanged;
    /// <summary>专辑封面变化事件（已 marshal 到 UI 线程）</summary>
    public event EventHandler? AlbumArtChanged;

    public MusicService()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _updateTimer = new Timer(UpdateCallback, null, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(3));
        // 歌词滚动：每 500ms 更新一次进度
        _lyricsTimer = new Timer(LyricsTickCallback, null, Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>启动服务</summary>
    public void Start()
    {
        _updateTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(3));

        // 注册酷狗窗口标题变化钩子（跑马灯滚动到完整格式瞬间，由 ParseSongTitle 的 EndsWith 校验解析）
        try
        {
            _titleChangeProc = OnWindowTitleChanged;
            _titleChangeHook = SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _titleChangeProc,
                0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MusicService] SetWinEventHook failed: {ex.Message}");
        }
    }

    /// <summary>停止服务</summary>
    public void Stop()
    {
        _updateTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lyricsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (_titleChangeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_titleChangeHook); } catch { }
            _titleChangeHook = IntPtr.Zero;
        }
    }

    /// <summary>酷狗窗口标题变化回调（跑马灯滚动每帧触发；ParseSongTitle 内部用 EndsWith 校验只在完整瞬间解析）。</summary>
    private void OnWindowTitleChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd != _kugouWindowHandle || idObject != 0) return;
        try { UpdateSongInfo(); }
        catch (Exception ex) { LogError("MusicService.OnWindowTitleChanged", ex); }
    }

    private void UpdateCallback(object? state)
    {
        if (_isDisposed) return;

        try
        {
            DetectKugou();
            UpdateSongInfo();

            // 1) 先走 SMTC 权威检测
            bool? smtcState = CheckPlaybackStatus();

            // 2) SMTC 失败/无结果时，使用启发式 fallback
            if (smtcState == null)
            {
                // 启发式：有歌名且用户没主动暂停 → 认为在播放
                bool heuristic = !string.IsNullOrEmpty(_currentTitle) && !_userPaused;
                ApplyPlaybackState(heuristic, trusted: false);
            }
        }
        catch (Exception ex)
        {
            LogError("MusicService.UpdateCallback", ex);
        }
    }

    /// <summary>
    /// 通过 SMTC 检测实际播放状态（后台线程执行）。
    /// 只信任酷狗专属会话，不用 GetCurrentSession 回退（避免取到浏览器等其他 App 的状态）。
    /// 返回：true=Playing，false=Paused/Stopped，null=未找到酷狗会话/检测失败
    /// </summary>
    private bool? CheckPlaybackStatus()
    {
        if (_isDisposed) return null;

        try
        {
            // 酷狗专属会话优先，找不到则用任意"正在播放"的会话（通用多播放器支持）
            GlobalSystemMediaTransportControlsSession? session = FindActiveMusicSession();

            // 没找到可用会话 → 返回 null，让启发式接管
            if (session == null) return null;

            var info = session.GetPlaybackInfo();
            if (info == null) return null;

            var status = info.PlaybackStatus;
            bool actuallyPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            _smtcSession = session;
            HookSessionEvents(session); // 订阅该会话的媒体/播放/进度事件（事件驱动加速，轮询仍兜底）
            ApplyPlaybackState(actuallyPlaying, trusted: true);
            return actuallyPlaying;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MusicService] CheckPlaybackStatus error: {ex.Message}");
            return null;
        }
    }

    /// <summary>初始化 SMTC Manager 并查找酷狗专属会话（后台线程执行，同步）。</summary>
    private GlobalSystemMediaTransportControlsSession? FindKugouSession()
    {
        try
        {
            if (_smtcManager == null)
            {
                try
                {
                    _smtcManager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                        .AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MusicService] SMTC manager init failed: {ex.Message}");
                    _smtcManager = null;
                }

                // 订阅会话集变化（新播放器上线/下线时重新检测），只订阅一次
                if (_smtcManager != null && !_sessionsHooked)
                {
                    try { _smtcManager.SessionsChanged += OnSessionsChanged; _sessionsHooked = true; } catch { }
                }
            }
            if (_smtcManager == null) return null;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = _smtcManager.GetSessions(); }
            catch { return null; }

            // 只找酷狗专属会话，不做 GetCurrentSession 回退
            foreach (var session in sessions)
            {
                try
                {
                    string? appId = null;
                    try { appId = session.SourceAppUserModelId; } catch { }
                    if (string.IsNullOrEmpty(appId)) continue;

                    string lower = appId.ToLowerInvariant();
                    if (lower.Contains("kugou") || lower.Contains("ku gou") || lower.Contains("kgmusic"))
                        return session;
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>订阅指定 SMTC 会话的事件（媒体信息/播放状态/进度），会话切换时先退订旧会话。</summary>
    private void HookSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_subscribedSession, session)) return;
        if (_subscribedSession != null)
        {
            try
            {
                _subscribedSession.MediaPropertiesChanged -= OnMediaPropsChanged;
                _subscribedSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
        }
        _subscribedSession = session;
        if (_subscribedSession != null)
        {
            try
            {
                _subscribedSession.MediaPropertiesChanged += OnMediaPropsChanged;
                _subscribedSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
            catch { }
        }
        // 会话切换：重置时间轴可靠性检测（新播放器可能正常上报时间轴，如网易云；避免沿用酷狗的"不可靠"判定）
        _lastSmtcPosition = -1;
        _smtcStallSeconds = 0;
        _smtcTimelineBroken = false;
    }

    /// <summary>SMTC 会话集变化（新播放器上线/下线）→ 重新检测。</summary>
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_isDisposed) return;
        try { UpdateCallback(null); } catch { }
    }

    /// <summary>媒体信息变化（换歌）→ 刷新歌曲信息与封面。SMTC 元数据稳定（非窗口标题跑马灯），所有播放器统一走这里；
    /// UpdateSongInfoFromSmtc 内部变化检测保证只在真换歌时刷 UI。</summary>
    private void OnMediaPropsChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_isDisposed) return;
        try { UpdateSongInfoFromSmtc(); } catch { }
        // BugFix(封面滞后)：酷狗先更新 Title/Artist、后更新 Thumbnail，Thumbnail 就绪时本事件会再次触发；
        // 防抖 300ms 补取一次封面。高频连发由代际检查折叠，不会反复重建 UI。
        _ = ScheduleAlbumArtRefetchAsync(300);
    }

    /// <summary>播放状态变化（播放/暂停）→ 立即应用真实状态。
    /// BugFix(反复暂停/继续后歌词漂移)：酷狗此前被整体跳过、只能靠 3s 轮询发现暂停/继续，
    /// 每次暂停检测延迟 δ1（超前）、继续检测延迟 δ2（落后），且酷狗恢复时 SMTC 走
    /// Stopped→Changing→Playing 过渡序列使 δ2 系统性大于 δ1 → 净落后随机游走累加（约 5 次循环落后 3~7s）。
    /// 现改为状态过滤：仅信任 Playing/Paused 确定态立即应用（把暂停/继续检测延迟从 0~3s 降到事件级），
    /// Stopped/Changing/Closed 等过渡态一律忽略——切歌期间插件本就处于 playing 态，
    /// 忽略过渡态后整个 Stopped→Changing→Playing 序列不引起状态翻转，无歌词定时器停顿/重启抖动。
    /// 频率说明：本事件只在播放/暂停/切歌过渡时触发、频率天然低，与当年高频回归的元凶
    /// （Timeline/MediaProperties 事件）不同；3s 轮询仍保留作为兜底。</summary>
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (_isDisposed) return;
        try
        {
            var info = sender.GetPlaybackInfo();
            if (info == null) return;
            var status = info.PlaybackStatus;
            // 只应用确定态；过渡态（Stopped/Changing/Closed 等）忽略，见上方注释
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                ApplyPlaybackState(status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, trusted: true);
            }
        }
        catch { }
    }

    /// <summary>异步获取当前歌曲的真实专辑封面（来自 SMTC thumbnail），取不到则清空回退 K logo。
    /// 竞态保护：_albumArtToken 每次请求递增，取图期间若又发起新请求（换歌/防抖重取），旧结果直接丢弃，
    /// 避免"慢的一首的图覆盖快的一首的图"。</summary>
    private async Task FetchAlbumArtAsync()
    {
        if (_isDisposed) return;
        int myToken = ++_albumArtToken;
        try
        {
            var session = _smtcSession ?? FindKugouSession();
            if (session == null) { if (myToken == _albumArtToken && !_isDisposed) SetAlbumArt(null); return; }

            var props = await session.TryGetMediaPropertiesAsync();
            if (myToken != _albumArtToken || _isDisposed) return; // 取属性期间又换歌 → 丢弃旧结果
            var thumbRef = props?.Thumbnail;
            // thumbRef==null 分支同样要过令牌守卫：极端竞态下旧请求会把新歌封面误清成 null
            if (thumbRef == null) { if (myToken == _albumArtToken && !_isDisposed) SetAlbumArt(null); return; }

            using var raStream = await thumbRef.OpenReadAsync();
            if (myToken != _albumArtToken || _isDisposed) return; // 开流期间又换歌 → 丢弃旧结果
            using var netStream = raStream.AsStreamForRead();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // 立即解码完成后再释放流
            bitmap.StreamSource = netStream;
            bitmap.EndInit();
            bitmap.Freeze(); // 跨线程安全

            if (myToken != _albumArtToken || _isDisposed) return; // 解码期间又换歌 → 丢弃旧结果
            SetAlbumArt(bitmap);
        }
        catch (Exception ex)
        {
            LogError("MusicService.FetchAlbumArtAsync", ex);
        }
    }

    /// <summary>防抖延迟重取封面（BugFix: 封面滞后一首歌）。酷狗换歌时先上报 Title/Artist、后更新 Thumbnail，
    /// 换歌瞬间立即取图会拿到上一首的图，需延迟补取纠正。代际递增折叠高频触发
    /// （MediaPropertiesChanged 可能短时间连发多次），保证只有一次真正解码/通知 UI。</summary>
    private async Task ScheduleAlbumArtRefetchAsync(int delayMs)
    {
        int gen = ++_artRefetchGeneration;
        try
        {
            await Task.Delay(delayMs);
            if (_isDisposed || gen != _artRefetchGeneration) return; // 期间又有更新的重取请求 → 本次作废
            _ = FetchAlbumArtAsync();
        }
        catch { }
    }

    /// <summary>设置专辑封面并通知 UI（引用未变则不重复发事件）。</summary>
    private void SetAlbumArt(BitmapSource? art)
    {
        if (ReferenceEquals(_currentAlbumArt, art)) return;
        _currentAlbumArt = art;
        PostToUi(() => AlbumArtChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// 统一应用播放状态（不会重复发送通知/重启定时器，除非状态真的改变）。
    /// trusted=true 表示状态来自酷狗专属 SMTC 会话（权威），
    /// false 表示启发式猜测（有歌名=播放中）
    /// </summary>
    private void ApplyPlaybackState(bool wantPlaying, bool trusted)
    {
        if (wantPlaying == _isPlaying) return;
        ApplyPlaybackStateCore(wantPlaying);
    }

    /// <summary>重载：允许 forceOverride 忽略"权威保护"，用于用户明确点击按钮的场景</summary>
    private void ApplyPlaybackState(bool wantPlaying, bool trusted, bool forceOverride)
    {
        if (wantPlaying == _isPlaying && !forceOverride) return;
        ApplyPlaybackStateCore(wantPlaying);
    }

    /// <summary>
    /// 实际执行状态切换的内部方法。
    /// 关键：暂停时用 GetPlaybackPosition()（SMTC 优先）获取真实位置写入 _playOffset，
    /// 避免 DateTime.Now 估算时间与真实播放进度之间的累积误差。
    /// </summary>
    private void ApplyPlaybackStateCore(bool wantPlaying)
    {
        // 真正状态切换（或 forceOverride 强制切换）时才改动计时变量，避免重复调用引入漂移
        if (wantPlaying == _isPlaying)
        {
            // 状态没变化但仍要通知 UI 刷新图标（例如按钮点击后 SMTC 纠错时）
            PostToUi(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
            return;
        }

        if (wantPlaying)
        {
            // → 进入播放态：仅重置开始时刻，_playOffset 保留暂停瞬间的真实进度
            _isPlaying = true;
            _playStartTime = DateTime.Now;
            _lyricsTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }
        else
        {
            // → 进入暂停态：用 SMTC 精确位置（或回退估算）捕获当前真实进度
            double realPosition = GetPlaybackPosition();
            if (realPosition < 0) realPosition = 0;
            _playOffset = realPosition;
            _isPlaying = false;
            _lyricsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        PostToUi(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>歌词滚动定时回调：根据播放进度更新当前歌词行</summary>
    private void LyricsTickCallback(object? state)
    {
        if (_isDisposed || !_isPlaying || _currentLyrics.Count == 0) return;

        try
        {
            double elapsed = GetPlaybackPosition();
            int newIndex = FindLyricIndex(elapsed);

            if (newIndex != _lastLyricIndex)
            {
                _lastLyricIndex = newIndex;
                PostToUi(() => LyricsPositionChanged?.Invoke(this, EventArgs.Empty));
            }
        }
        catch (Exception ex)
        {
            LogError("MusicService.LyricsTickCallback", ex);
        }
    }

    /// <summary>获取当前播放位置（秒）。优先使用 SMTC 精确位置；
    /// 若检测到 SMTC 时间轴"不前进"（BugFix: 酷狗不上报时间轴，Position 恒为 0 导致歌词卡住），回退 DateTime 时间估算。</summary>
    private double GetPlaybackPosition()
    {
        // 尝试 SMTC 精确位置（已判定不可靠的会话直接跳过，避免每 500ms 白读一次）
        if (_smtcSession != null && !_smtcTimelineBroken)
        {
            try
            {
                var timeline = _smtcSession.GetTimelineProperties();
                double pos = timeline.Position.TotalSeconds;
                if (IsSmtcTimelineHealthy(pos))
                    return pos;
                // 播放中停滞超阈值 → 判定不可靠，落入下方时间估算
            }
            catch { }
        }

        // 回退：时间估算
        return (DateTime.Now - _playStartTime).TotalSeconds + _playOffset;
    }

    /// <summary>SMTC 时间轴健康检测（BugFix: 歌词卡住不滚动）。播放状态下位置应随墙钟前进：
    /// 位置明显前进/后退（正常播放/seek/换歌）→ 重置停滞计时并信任 SMTC；
    /// 播放中位置累计停滞超过 SmtcStallThresholdSec → 判定该会话时间轴不可靠（置 _smtcTimelineBroken，返回 false）。
    /// 暂停期间歌词定时器停走、本方法不被调用，天然不产生误判；会话切换时在 HookSessionEvents 重置检测状态。</summary>
    private bool IsSmtcTimelineHealthy(double pos)
    {
        var now = DateTime.Now;

        // 首次读取：先信任，建立基准
        if (_lastSmtcPosition < 0)
        {
            _lastSmtcPosition = pos;
            _lastSmtcReadTime = now;
            return true;
        }

        double posDelta = pos - _lastSmtcPosition;
        if (posDelta > 0.2 || posDelta < -0.5)
        {
            // 位置明显前进（正常播放）或回退（用户 seek/换歌）→ 时间轴可信，继续用 SMTC 精确位置
            _lastSmtcPosition = pos;
            _lastSmtcReadTime = now;
            _smtcStallSeconds = 0;
            return true;
        }

        // 位置几乎没动：仅在播放状态下累计停滞时长。
        // 单次增量钳制 ≤1s：长时间暂停期间 _lastSmtcReadTime 不更新（歌词定时器停走），
        // 恢复播放后第一次 tick 会把整个暂停时长一次性累进来 → 瞬间超阈值误判 broken（QA Round1 缺陷）。
        // 正常 tick 间隔 0.5s 不受钳制影响；钳制同时还能免疫定时器调度抖动造成的单次大间隔。
        if (_isPlaying)
            _smtcStallSeconds += Math.Min((now - _lastSmtcReadTime).TotalSeconds, 1.0);
        _lastSmtcReadTime = now;
        _lastSmtcPosition = pos; // 跟随微小漂移，避免误差积累

        if (_smtcStallSeconds >= SmtcStallThresholdSec)
        {
            _smtcTimelineBroken = true;
            double est = (now - _playStartTime).TotalSeconds + _playOffset;
            // 一次性诊断日志（置位后不再读 SMTC，自然去重）：便于实机验证"酷狗时间轴不前进 → 用估算"
            DebugLog($"位置: smtc={pos:F1} 播放中停滞{_smtcStallSeconds:F1}s 不前进 est={est:F1} → 用估算");
            return false;
        }
        return true; // 判定窗口内仍用 SMTC（最多 3s 偏差，随后回退估算）
    }

    /// <summary>根据时间戳找到当前应显示的歌词行索引</summary>
    private int FindLyricIndex(double seconds)
    {
        if (_currentLyrics.Count == 0) return -1;

        // 二分查找：找到最后一个 Time <= seconds 的行
        int lo = 0, hi = _currentLyrics.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_currentLyrics[mid].Time <= seconds)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    /// <summary>检测酷狗音乐窗口（后台线程执行）—— 使用 GetProcessesByName 避免遍历所有进程。
    /// 不检查 IsWindowVisible：酷狗最小化到托盘时主窗口被隐藏，但进程仍在播放、窗口标题仍在更新，
    /// GetWindowText 对隐藏窗口同样有效，因此隐藏窗口也要接受（这是托盘态检测不到的关键修复）。</summary>
    private void DetectKugou()
    {
        if (_isDisposed) return;

        IntPtr found = IntPtr.Zero;

        foreach (var procName in KUGOU_PROCESS_NAMES)
        {
            try
            {
                var processes = Process.GetProcessesByName(procName);
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            found = p.MainWindowHandle;
                            break;
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                    if (found != IntPtr.Zero) break;
                }
            }
            catch { }
            if (found != IntPtr.Zero) break;
        }

        if (found == IntPtr.Zero)
        {
            if (_kugouWindowHandle != IntPtr.Zero)
            {
                _kugouWindowHandle = IntPtr.Zero;
                _isPlaying = false;
                _currentTitle = "";
                _currentArtist = "";
                _currentLyrics = new();
                _lastLyricIndex = -1;
                _currentAlbumArt = null; // 酷狗退出，清空封面回退 K logo
                _lyricsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                PostToUi(() =>
                {
                    SongInfoChanged?.Invoke(this, EventArgs.Empty);
                    PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                    LyricsChanged?.Invoke(this, EventArgs.Empty);
                    AlbumArtChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return;
        }

        _kugouWindowHandle = found;
    }

    /// <summary>更新歌曲信息（后台线程执行）。优先级：酷狗 SMTC 会话（开启"支持系统播放控件"后上报，
    /// 元数据干净、有封面、窗口最小化到托盘也能检测）→ 酷狗窗口标题兜底（未开 SMTC 的老酷狗；标题跑马灯，
    /// ParseSongTitle 用 EndsWith 校验只在完整瞬间解析）→ 其他播放器走通用 SMTC 元数据。</summary>
    private void UpdateSongInfo()
    {
        if (_isDisposed) return;
        // 酷狗开了"支持系统播放控件"后会上报 SMTC，优先走 SMTC（比窗口标题更准，且托盘态可用）
        if (FindKugouSession() != null)
        {
            UpdateSongInfoFromSmtc();
            return;
        }
        if (_kugouWindowHandle != IntPtr.Zero)
        {
            UpdateSongInfoFromWindowTitle(); // 酷狗未开 SMTC：窗口标题兜底
            return;
        }
        UpdateSongInfoFromSmtc(); // 其他播放器：SMTC
    }

    /// <summary>从酷狗窗口标题读取并解析歌曲信息（标题是跑马灯滚动文本，由 ParseSongTitle 校验完整瞬间）。</summary>
    private void UpdateSongInfoFromWindowTitle()
    {
        try
        {
            int len = GetWindowTextLength(_kugouWindowHandle);
            if (len <= 0) return;
            var sb = new StringBuilder(len + 1);
            GetWindowText(_kugouWindowHandle, sb, sb.Capacity);
            string windowTitle = sb.ToString();
            if (!string.IsNullOrEmpty(windowTitle))
                ParseSongTitle(windowTitle);
        }
        catch (Exception ex)
        {
            LogError("MusicService.UpdateSongInfoFromWindowTitle", ex);
        }
    }

    /// <summary>解析酷狗窗口标题。标题是跑马灯循环滚动：只有滚动到"以'酷狗音乐'结尾"的完整格式瞬间
    ///（此时为稳定的"歌手 - 歌名 - 酷狗音乐"）才解析；滚动的其他中间态一律跳过，避免解析错乱 + 高频刷新 UI。</summary>
    private void ParseSongTitle(string windowTitle)
    {
        if (!windowTitle.EndsWith("酷狗音乐")) return; // 跑马灯中间态一律跳过

        if (windowTitle != _lastLoggedRawTitle) // 日志去重
        {
            _lastLoggedRawTitle = windowTitle;
            DebugLog($"标题(完整): [{windowTitle}]");
        }

        string cleaned = windowTitle
            .Replace(" - 酷狗音乐", "")
            .Replace(" [酷狗音乐]", "")
            .Replace("- 酷狗音乐", "")
            .Replace("酷狗音乐 - ", "")
            .Trim();

        // 空闲态（未播放任何歌曲）标题就是"酷狗音乐"本身，不能当成歌名
        if (cleaned == "酷狗音乐") cleaned = "";

        int dashIdx = cleaned.IndexOf(" - ");
        string title = "";
        string artist = "";

        if (dashIdx > 0)
        {
            artist = cleaned.Substring(0, dashIdx).Trim();
            title = cleaned.Substring(dashIdx + 3).Trim();
            int bracketIdx = title.IndexOf('[');
            if (bracketIdx > 0) title = title.Substring(0, bracketIdx).Trim();
            bracketIdx = artist.IndexOf('[');
            if (bracketIdx > 0) artist = artist.Substring(0, bracketIdx).Trim();
        }
        else if (!string.IsNullOrEmpty(cleaned))
        {
            title = cleaned;
        }

        bool titleChanged = _currentTitle != title;
        bool artistChanged = _currentArtist != artist;
        _currentTitle = title;
        _currentArtist = artist;

        if (titleChanged || artistChanged)
        {
            if (_lastNotifiedTitle != _currentTitle || _lastNotifiedArtist != _currentArtist)
            {
                _lastNotifiedTitle = _currentTitle;
                _lastNotifiedArtist = _currentArtist;
                _playStartTime = DateTime.Now;
                _playOffset = 0;
                _lastLyricIndex = -1;
                _userPaused = false;
                PostToUi(() => SongInfoChanged?.Invoke(this, EventArgs.Empty));
                _ = SearchLyricsAsync();
                // 此分支只在酷狗未开 SMTC 时走到，无封面源，不调 FetchAlbumArtAsync（封面保持 K logo 占位）
                DebugLog($"  => 应用歌曲(标题): title=[{_currentTitle}] artist=[{_currentArtist}]");
            }
        }
    }

    /// <summary>通用 SMTC 兜底：酷狗不在时，用任意"正在播放"的播放器会话提供歌曲信息。</summary>
    private void UpdateSongInfoFromSmtc()
    {
        try
        {
            var session = FindActiveMusicSession();
            LogSmtcDiagnostic(session); // 诊断：记录 SMTC 会话情况（去重）
            if (session == null)
            {
                // 没有任何正在播放的播放器 → 清空显示
                if (!string.IsNullOrEmpty(_currentTitle))
                {
                    _currentTitle = "";
                    _currentArtist = "";
                    _currentAlbumArt = null;
                    PostToUi(() =>
                    {
                        SongInfoChanged?.Invoke(this, EventArgs.Empty);
                        AlbumArtChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
                _smtcSession = null;
                return;
            }

            _smtcSession = session;
            HookSessionEvents(session);

            var props = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";

            // 调试日志：记录 SMTC 上报的干净元数据（去重），验证它不含窗口标题的跑马灯滚动
            string smtcKey = title + "|" + artist;
            if (smtcKey != _lastLoggedSmtc)
            {
                _lastLoggedSmtc = smtcKey;
                DebugLog($"SMTC: title=[{title}] artist=[{artist}]");
            }

            if (string.IsNullOrEmpty(title)) return;

            bool changed = _currentTitle != title || _currentArtist != artist;
            _currentTitle = title;
            _currentArtist = artist;

            if (changed && (_lastNotifiedTitle != title || _lastNotifiedArtist != artist))
            {
                _lastNotifiedTitle = title;
                _lastNotifiedArtist = artist;
                _playStartTime = DateTime.Now;
                _playOffset = 0;
                _lastLyricIndex = -1;
                _userPaused = false;
                PostToUi(() => SongInfoChanged?.Invoke(this, EventArgs.Empty));
                _ = SearchLyricsAsync();
                _ = FetchAlbumArtAsync();            // 立即取一次（元数据原子更新的播放器可直接拿到正确封面）
                // BugFix(封面滞后)：酷狗 Thumbnail 晚于 Title/Artist 上报，立即取到的是上一首的图，
                // 900ms 后补取纠正；若期间 Thumbnail 就绪触发了 MediaPropertiesChanged，则由事件防抖重取提前纠正。
                _ = ScheduleAlbumArtRefetchAsync(900);
            }
        }
        catch (Exception ex)
        {
            LogError("MusicService.UpdateSongInfoFromSmtc", ex);
        }
    }

    private string _lastSmtcDiag = "";

    /// <summary>诊断：记录 SMTC 管理器状态与所有会话（appId+播放状态），去重。用于确认播放器是否上报 SMTC。</summary>
    private void LogSmtcDiagnostic(GlobalSystemMediaTransportControlsSession? found)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append($"manager={(_smtcManager != null ? "ok" : "null")} found={(found != null ? "Y" : "N")} sessions=[");
            if (_smtcManager != null)
            {
                foreach (var s in _smtcManager.GetSessions())
                {
                    string? appId = null; try { appId = s.SourceAppUserModelId; } catch { }
                    string st = "?"; try { st = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "?"; } catch { }
                    sb.Append($"{appId}:{st}; ");
                }
            }
            sb.Append(']');
            string diag = sb.ToString();
            if (diag != _lastSmtcDiag) { _lastSmtcDiag = diag; DebugLog("诊断 " + diag); }
        }
        catch { }
    }

    /// <summary>查找当前活跃的音乐会话：酷狗专属会话优先，否则返回第一个"正在播放"的 SMTC 会话。</summary>
    private GlobalSystemMediaTransportControlsSession? FindActiveMusicSession()
    {
        // 1) 酷狗专属会话优先
        var kugou = FindKugouSession();
        if (kugou != null) return kugou;

        try
        {
            if (_smtcManager == null) return null;

            // 2) 系统当前媒体会话（最可能正在播放的；也覆盖 appId 不匹配关键词的酷狗）
            try
            {
                var current = _smtcManager.GetCurrentSession();
                if (current != null) return current;
            }
            catch { }

            // 3) 第一个正在播放的会话（Playing 过滤，避免抓到暂停的浏览器/视频）
            var sessions = _smtcManager.GetSessions();
            foreach (var s in sessions)
            {
                try
                {
                    if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        return s;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>异步搜索歌词（Dictionary 缓存 + LRU + 竞态保护）。</summary>
    // 缓存 key = "title|artist"（忽略大小写），避免同名不同歌手串歌；LRU 上限 50 首；static 以便全局共享
    private static readonly Dictionary<string, List<LyricLine>> _lyricsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> _lyricsLru = new();
    private const int MaxLyricsCache = 50;
    private int _lyricsSearchToken; // 每次搜索递增，用于竞态保护

    private static string LyricsKey(string title, string artist) => $"{title}|{artist}";

    private async Task SearchLyricsAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(_currentTitle)) return;

        string key = LyricsKey(_currentTitle, _currentArtist);
        int myToken = ++_lyricsSearchToken;

        // 命中缓存：直接恢复歌词（含 A→B→A 场景），不发网络请求
        if (_lyricsCache.TryGetValue(key, out var cached))
        {
            TouchLyricsLru(key);
            if (myToken != _lyricsSearchToken || _isDisposed) return;
            _currentLyrics = cached;
            _lastLyricIndex = -1;
            PostToUi(() => LyricsChanged?.Invoke(this, EventArgs.Empty));
            return;
        }

        // 立即清空旧歌词，让 UI 显示"加载中"而非旧歌的歌词
        _currentLyrics = new();
        _lastLyricIndex = -1;
        PostToUi(() => LyricsChanged?.Invoke(this, EventArgs.Empty));

        try
        {
            var lyrics = await LyricsService.SearchLyricsAsync(_currentTitle, _currentArtist);

            // 竞态保护：如果在搜索期间又换了歌，丢弃这次结果
            if (myToken != _lyricsSearchToken || _isDisposed) return;

            // 写入缓存（LRU 淘汰最旧项）
            _lyricsCache[key] = lyrics;
            TouchLyricsLru(key);

            _currentLyrics = lyrics;
            _lastLyricIndex = -1;
            PostToUi(() => LyricsChanged?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            LogError("MusicService.SearchLyricsAsync", ex);
        }
    }

    /// <summary>把 key 移到 LRU 最新位置；超过上限时淘汰最旧项。</summary>
    private static void TouchLyricsLru(string key)
    {
        var node = _lyricsLru.Find(key);
        if (node != null) _lyricsLru.Remove(node);
        _lyricsLru.AddLast(key);
        while (_lyricsLru.Count > MaxLyricsCache)
        {
            var oldest = _lyricsLru.First;
            if (oldest == null) break;
            _lyricsCache.Remove(oldest.Value);
            _lyricsLru.RemoveFirst();
        }
    }

    // ===== 播放控制（用户触发，在 UI 线程执行，不再阻塞） =====

    /// <summary>播放/暂停：发送媒体键，然后立即"乐观地"翻转 IsPlaying 保证UI响应。
    /// 随后 ScheduleQuickCheckAsync 会用 SMTC 的真实结果做权威修正（若有偏差再改回来）</summary>
    public void PlayPause()
    {
        if (_kugouWindowHandle == IntPtr.Zero) return;
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);

        // 记录用户意图：暂停时标记，恢复时清除
        _userPaused = !_userPaused;

        // 乐观切换：发送键后立刻翻图标/歌词定时器
        ApplyPlaybackStateCore(!_isPlaying);

        // 后续 2 秒内用 SMTC 校正 4 次（真实状态为准）
        _ = ScheduleQuickCheckAsync();
    }

    /// <summary>发送播放键后快速拉取状态</summary>
    private async Task ScheduleQuickCheckAsync()
    {
        try
        {
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(i == 0 ? 150 : 500);
                if (_isDisposed) break;
                CheckPlaybackStatus();
            }
        }
        catch { }
    }

    /// <summary>下一曲</summary>
    public void NextTrack()
    {
        if (_kugouWindowHandle == IntPtr.Zero) return;
        SendMediaKey(VK_MEDIA_NEXT_TRACK);
        // 下一曲后歌词进度会在下次 ParseSongTitle 检测到歌曲变化时重置，这里快速拉状态
        _ = ScheduleQuickCheckAsync();
    }

    /// <summary>上一曲</summary>
    public void PrevTrack()
    {
        if (_kugouWindowHandle == IntPtr.Zero) return;
        SendMediaKey(VK_MEDIA_PREV_TRACK);
        _ = ScheduleQuickCheckAsync();
    }

    /// <summary>停止播放</summary>
    public void StopPlayback()
    {
        if (_kugouWindowHandle == IntPtr.Zero) return;
        SendMediaKey(VK_MEDIA_STOP);
    }

    /// <summary>打开酷狗音乐主窗口</summary>
    public void OpenKugou()
    {
        if (_kugouWindowHandle == IntPtr.Zero)
        {
            try
            {
                foreach (var procName in KUGOU_PROCESS_NAMES)
                {
                    var processes = Process.GetProcessesByName(procName);
                    foreach (var p in processes)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(p.MainWindowHandle, 9);
                                SetForegroundWindow(p.MainWindowHandle);
                                return;
                            }
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
            }
            catch { }
            return;
        }

        ShowWindow(_kugouWindowHandle, 9);
        SetForegroundWindow(_kugouWindowHandle);
    }

    /// <summary>发送媒体按键（使用 keybd_event 代替 SendMessage，不阻塞调用线程）</summary>
    private void SendMediaKey(uint keyCode)
    {
        try
        {
            keybd_event(keyCode, 0, 0, UIntPtr.Zero);
            keybd_event(keyCode, 0, 2, UIntPtr.Zero);
        }
        catch { }
    }

    /// <summary>将操作 marshal 到 UI 线程执行（使用 BeginInvoke 避免阻塞）</summary>
    private void PostToUi(Action action)
    {
        if (_isDisposed) return;
        try
        {
            if (_uiDispatcher.CheckAccess())
                action();
            else
                _uiDispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            LogError("MusicService.PostToUi", ex);
        }
    }

    /// <summary>音乐检测调试日志：记录窗口标题原文与解析决策，写入 %AppData%/DeskFolder/music-debug.log，便于定位标题解析/高频刷新问题。</summary>
    private static void DebugLog(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "music-debug.log");
            // 防止无限增长：超过 2MB 则清空重写
            if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                File.WriteAllText(file, "");
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    /// <summary>简单错误日志写入（双路径：exe 目录 + AppData）</summary>
    private static void LogError(string source, Exception ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MusicService.{source}");
            sb.AppendLine($"  {ex.Message}");
            sb.AppendLine($"  {ex.StackTrace}");
            sb.AppendLine();

            // 写入 exe 同级目录
            try
            {
                var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
                             ?? AppDomain.CurrentDomain.BaseDirectory;
                File.AppendAllText(Path.Combine(exeDir, "crash.log"), sb.ToString());
            }
            catch { }

            // 写入 AppData
            try
            {
                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
                Directory.CreateDirectory(appDataDir);
                File.AppendAllText(Path.Combine(appDataDir, "crash.log"), sb.ToString());
            }
            catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _updateTimer.Dispose();
        _lyricsTimer.Dispose();
        HookSessionEvents(null); // 退订 SMTC 会话事件
        if (_smtcManager != null && _sessionsHooked)
        {
            try { _smtcManager.SessionsChanged -= OnSessionsChanged; } catch { }
            _sessionsHooked = false;
        }
        if (_titleChangeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_titleChangeHook); } catch { }
            _titleChangeHook = IntPtr.Zero;
        }
        _kugouWindowHandle = IntPtr.Zero;
    }
}