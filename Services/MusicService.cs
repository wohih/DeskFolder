using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Windows.Media.Control;

namespace DeskFolder.Services;

/// <summary>
/// 音乐检测与控制服务：检测酷狗音乐、获取当前播放信息、控制播放状态。
/// 所有 Win32 探测工作在后台线程完成，绝不阻塞 UI。
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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // WinEvent Hook（监听窗口标题变化，实现零延迟换歌检测）
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

    // 避免事件风暴：记录上次触发的状态，只有真正变化才通知
    private string _lastNotifiedTitle = "";
    private string _lastNotifiedArtist = "";

    // WinEvent Hook（窗口标题变化监听）
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

    /// <summary>歌曲信息更新事件（由后台线程触发，已 marshal 到 UI 线程）</summary>
    public event EventHandler? SongInfoChanged;
    /// <summary>播放状态变化事件</summary>
    public event EventHandler? PlaybackStateChanged;
    /// <summary>歌词更新事件（歌词数据变化）</summary>
    public event EventHandler? LyricsChanged;
    /// <summary>歌词位置变化事件（高频，用于歌词滚动）</summary>
    public event EventHandler? LyricsPositionChanged;

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

        // 注册窗口标题变化钩子（零延迟检测换歌）
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

        // 移除窗口标题变化钩子
        if (_titleChangeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_titleChangeHook); } catch { }
            _titleChangeHook = IntPtr.Zero;
        }
    }

    /// <summary>窗口标题变化回调（由系统触发，可能在不同线程）</summary>
    private void OnWindowTitleChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd != _kugouWindowHandle || idObject != 0) return;

        // 酷狗窗口标题变了（换歌），立即检测新歌曲信息
        try
        {
            UpdateSongInfo();
        }
        catch (Exception ex)
        {
            LogError("MusicService.OnWindowTitleChanged", ex);
        }
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
        if (_isDisposed || _kugouWindowHandle == IntPtr.Zero) return null;

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
            }
            if (_smtcManager == null) return null;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = _smtcManager.GetSessions(); }
            catch { return null; }

            // 只找酷狗专属会话，不做 GetCurrentSession 回退
            GlobalSystemMediaTransportControlsSession? kugouSession = null;
            foreach (var session in sessions)
            {
                try
                {
                    string? appId = null;
                    try { appId = session.SourceAppUserModelId; } catch { }
                    if (string.IsNullOrEmpty(appId)) continue;

                    string lower = appId.ToLowerInvariant();
                    if (lower.Contains("kugou") || lower.Contains("ku gou") || lower.Contains("kgmusic"))
                    {
                        kugouSession = session;
                        break;
                    }
                }
                catch { }
            }

            // 没找到酷狗会话 → 返回 null，让启发式接管
            if (kugouSession == null) return null;

            var info = kugouSession.GetPlaybackInfo();
            if (info == null) return null;

            var status = info.PlaybackStatus;
            bool actuallyPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            _smtcSession = kugouSession;
            ApplyPlaybackState(actuallyPlaying, trusted: true);
            return actuallyPlaying;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MusicService] CheckPlaybackStatus error: {ex.Message}");
            return null;
        }
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

    /// <summary>获取当前播放位置（秒）。优先使用 SMTC 精确位置，回退到时间估算</summary>
    private double GetPlaybackPosition()
    {
        // 尝试 SMTC 精确位置
        if (_smtcSession != null)
        {
            try
            {
                var timeline = _smtcSession.GetTimelineProperties();
                return timeline.Position.TotalSeconds;
            }
            catch { }
        }

        // 回退：时间估算
        return (DateTime.Now - _playStartTime).TotalSeconds + _playOffset;
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

    /// <summary>检测酷狗音乐窗口（后台线程执行）—— 使用 GetProcessesByName 避免遍历所有进程</summary>
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
                        if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
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
                _lyricsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                PostToUi(() =>
                {
                    SongInfoChanged?.Invoke(this, EventArgs.Empty);
                    PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                    LyricsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return;
        }

        _kugouWindowHandle = found;
    }

    /// <summary>更新歌曲信息（后台线程执行）</summary>
    private void UpdateSongInfo()
    {
        if (_isDisposed) return;

        if (_kugouWindowHandle == IntPtr.Zero)
        {
            if (!string.IsNullOrEmpty(_currentTitle))
            {
                _currentTitle = "";
                _currentArtist = "";
                PostToUi(() => SongInfoChanged?.Invoke(this, EventArgs.Empty));
            }
            return;
        }

        try
        {
            int len = GetWindowTextLength(_kugouWindowHandle);
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                GetWindowText(_kugouWindowHandle, sb, sb.Capacity);
                string windowTitle = sb.ToString();

                if (!string.IsNullOrEmpty(windowTitle))
                {
                    ParseSongTitle(windowTitle);
                }
            }
        }
        catch (Exception ex)
        {
            LogError("MusicService.UpdateSongInfo", ex);
        }
    }

    /// <summary>解析歌曲标题</summary>
    private void ParseSongTitle(string windowTitle)
    {
        string cleaned = windowTitle
            .Replace(" - 酷狗音乐", "")
            .Replace(" [酷狗音乐]", "")
            .Replace("- 酷狗音乐", "")
            .Replace("酷狗音乐 - ", "")
            .Trim();

        int dashIdx = cleaned.IndexOf(" - ");
        string title = "";
        string artist = "";

        if (dashIdx > 0)
        {
            // 酷狗窗口标题格式："Artist - SongTitle - 酷狗音乐"
            // 去掉酷狗后缀后为 "Artist - SongTitle"
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
            // 只有歌曲真正变化时才通知 UI，避免频繁刷新
            if (_lastNotifiedTitle != _currentTitle || _lastNotifiedArtist != _currentArtist)
            {
                _lastNotifiedTitle = _currentTitle;
                _lastNotifiedArtist = _currentArtist;

                // 重置播放进度估算
                _playStartTime = DateTime.Now;
                _playOffset = 0;
                _lastLyricIndex = -1;
                _userPaused = false; // 新歌默认播放中

                PostToUi(() => SongInfoChanged?.Invoke(this, EventArgs.Empty));
                _ = SearchLyricsAsync();
            }
        }
    }

    /// <summary>异步搜索歌词（带缓存+竞态保护，避免重复请求和旧结果覆盖新结果）</summary>
    private string _lastLyricsSearchTitle = "";
    private int _lyricsSearchToken; // 每次搜索递增，用于竞态保护

    private async Task SearchLyricsAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(_currentTitle)) return;

        // 缓存：同一首歌不重复搜索
        if (string.Equals(_lastLyricsSearchTitle, _currentTitle, StringComparison.OrdinalIgnoreCase))
            return;

        _lastLyricsSearchTitle = _currentTitle;
        int myToken = ++_lyricsSearchToken;

        // 立即清空旧歌词，让 UI 显示"加载中"而非旧歌的歌词
        _currentLyrics = new();
        _lastLyricIndex = -1;
        PostToUi(() => LyricsChanged?.Invoke(this, EventArgs.Empty));

        try
        {
            var lyrics = await LyricsService.SearchLyricsAsync(_currentTitle, _currentArtist);

            // 竞态保护：如果在搜索期间又换了歌，丢弃这次结果
            if (myToken != _lyricsSearchToken || _isDisposed) return;

            _currentLyrics = lyrics;
            _lastLyricIndex = -1;
            PostToUi(() => LyricsChanged?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            LogError("MusicService.SearchLyricsAsync", ex);
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
        if (_titleChangeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_titleChangeHook); } catch { }
            _titleChangeHook = IntPtr.Zero;
        }
        _kugouWindowHandle = IntPtr.Zero;
    }
}