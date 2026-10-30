using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>音频嗅探命中事件（《在线曲库 PRD》F5.1/F5.2，S8 播放与 S9 下载订阅）。</summary>
    public class AudioSniffedEventArgs : EventArgs
    {
        /// <summary>音频/流媒体资源地址。</summary>
        public string Url { get; init; } = "";

        /// <summary>响应 Content-Type（可能为空：仅靠 URL 扩展名命中时）。</summary>
        public string? ContentType { get; init; }

        /// <summary>是否为流媒体（m3u8/mpd/m4s，需 ffmpeg 处理，直链除外）。</summary>
        public bool IsStream { get; init; }
    }

    /// <summary>导航失败异常（区分超时/断网/HTTP 错误，供 S4 失败分类）。</summary>
    public class OnlineNavigationException : Exception
    {
        public CoreWebView2WebErrorStatus Status { get; }
        public OnlineNavigationException(CoreWebView2WebErrorStatus status)
            : base($"导航失败: {status}") => Status = status;
    }

    /// <summary>
    /// 隐藏 WebView2 内核服务（《在线曲库开发步骤清单》S2）。
    ///
    /// 设计要点：
    /// - 全局单例、单实例复用：整个在线功能共用一个浏览器内核，不随站点数量增长；
    /// - 默认不可见（IsVisible=false），ParentWindow 挂主窗口——因此页面上永远看不到它；
    /// - 人机验证/向导等需要"可见"的场景调用 ShowIn(窗口)/HideBack()，把同一内核临时切挂过去
    ///   （S3/S5），Cookie 与会话天然共享；
    /// - 弹窗（NewWindowRequested）与下载（DownloadStarting）一律取消；
    /// - 音频嗅探：WebResourceRequested（URL 扩展名）+ WebResourceResponseReceived（Content-Type）双通道；
    /// - 空闲 60 秒自动释放内核（可经 settings.online_idle_recycle 关闭）；
    /// - 所有操作必须发生在 UI 线程（WebView2 的 STA 限制），对外用 RunAsync 串行化，避免任务互相踩踏。
    /// </summary>
    public sealed class HiddenBrowserService : IDisposable
    {
        public static HiddenBrowserService Instance { get; } = new();

        private CoreWebView2Environment? _environment;
        private CoreWebView2Controller? _controller;
        private HwndSource? _hostSource;             // 自带的屏幕外隐藏宿主窗口
        private IntPtr _hostHwnd;                    // 隐藏宿主句柄（HideBack 的归宿）
        private bool _initialized;
        private bool _isVisibleOnHost;               // 当前是否切挂到了可见窗口（S3/S5）
        private int _activeOps;                      // 进行中的任务数（>0 时不做空闲回收）
        private DispatcherTimer? _idleTimer;
        private int _idleRemaining;                  // 距回收还剩多少秒（仅诊断显示用）

        /// <summary>内核任务串行闸门：同一时刻只跑一个"导航+抓取"流程。</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>音频嗅探命中（UI 线程触发）。</summary>
        public event EventHandler<AudioSniffedEventArgs>? AudioSniffed;

        /// <summary>被拦截的弹窗次数（测试与诊断用）。</summary>
        public int BlockedNewWindowCount { get; private set; }

        /// <summary>清零弹窗拦截计数（诊断/测试用）。</summary>
        public void ResetBlockedNewWindowCount() => BlockedNewWindowCount = 0;

        public bool IsInitialized => _initialized;
        public bool IsVisibleOnHost => _isVisibleOnHost;

        #region 初始化与释放

        /// <summary>
        /// 初始化隐藏内核。可重复调用（已初始化则直接返回）。
        /// 宿主是**服务自建的屏幕外隐藏窗口**（不依赖 MainWindow 的句柄与生命周期）：
        /// WS_EX_TOOLWINDOW 不进任务栏，WS_EX_NOACTIVATE 不会抢焦点，位置在屏幕外永远不可见。
        /// userDataFolder 固定在 %APPDATA%\musicPlayer\webview2——登录态/Cookie 跨会话保留。
        /// </summary>
        public async Task InitAsync()
        {
            if (_initialized) return;

            if (_hostSource == null)
            {
                var p = new HwndSourceParameters("MusicPlayerHiddenBrowserHost")
                {
                    PositionX = -32000,
                    PositionY = -32000,
                    Width = 800,
                    Height = 600,
                    WindowStyle = 0,                              // WS_OVERLAPPED
                    ExtendedWindowStyle = 0x08000000 | 0x00000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                };
                _hostSource = new HwndSource(p);
            }
            _hostHwnd = _hostSource.Handle;

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "musicPlayer", "webview2");
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_hostHwnd);

            var core = _controller.CoreWebView2;

            // 隐藏内核自我约束：静音、禁脚本弹窗/宿主对象，UA 不伪装（PRD 红线）
            core.IsMuted = true;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            _controller.IsVisible = false;

            // 拦截弹窗：任何 window.open/新标签一律取消（PRD F4.x"杜绝弹窗"）
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                BlockedNewWindowCount++;
            };

            // 拦截下载：下载走 HttpClient/ffmpeg（S9），不经过内核
            core.DownloadStarting += (_, e) => e.Handled = true;

            // 音频嗅探通道 1：URL 扩展名（请求发出瞬间即可命中）
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            // 音频嗅探通道 2：响应 Content-Type（B 站 m4s 等无扩展名场景）
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;

            _initialized = true;
            StartIdleTimer();
        }

        /// <summary>
        /// 释放内核（退出程序或空闲回收时调用）。宿主窗口保留，下次免重建。
        ///
        /// ⚠ 关键：**必须把 _environment 一起置空**。
        /// WebView2 的浏览器进程（msedgewebview2.exe）由 Environment 与 Controller 共同持有，
        /// 只 Close() 掉 Controller 而留着 Environment，进程会一直活着（常驻约 100MB）。
        /// environment 本身重建只要几十毫秒，用户数据目录还在，登录态不受影响。
        /// </summary>
        public void Dispose()
        {
            _idleTimer?.Stop();
            _idleTimer = null;

            try { _controller?.Close(); }
            catch (Exception ex) { Debug.WriteLine($"关闭内核失败: {ex.Message}"); }
            _controller = null;

            // 释放环境对象引用 → 浏览器进程随之退出（这是空闲回收真正生效的关键一步）
            _environment = null;

            _initialized = false;
            _isVisibleOnHost = false;

            // 内核进程退出后把工作集还给系统：任务管理器里的内存立刻降下来。
            // 只在回收路径调用（含退出程序），不会在正常使用时抖动。
            ProcessStats.TrimWorkingSet();
        }

        /// <summary>空闲回收：Controller 与 Environment 一起释放，浏览器进程才会真正退出。</summary>
        private void RecycleCore()
        {
            if (!_initialized || _isVisibleOnHost || _activeOps > 0) return;
            Dispose();
        }

        /// <summary>强制立即回收内核（诊断窗口的"立即释放内核"按钮用）。</summary>
        public void ForceRecycle() => RecycleCore();

        private void StartIdleTimer()
        {
            if (_idleTimer != null) return;

            // 秒级倒计时（诊断窗口能直接看到"还有 N 秒回收"）；settings.online_idle_recycle = 0 可关闭
            _idleRemaining = IdleRecycleSeconds;
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _idleTimer.Tick += (_, _) => OnIdleTick();
            _idleTimer.Start();
        }

        private void OnIdleTick()
        {
            if (!_initialized) { _idleRemaining = IdleRecycleSeconds; return; }
            if (_isVisibleOnHost || _activeOps > 0) { _idleRemaining = IdleRecycleSeconds; return; }

            bool enabled = Db.GetSetting("online_idle_recycle", "1") == "1";
            if (!enabled) { _idleRemaining = IdleRecycleSeconds; return; }

            _idleRemaining--;
            if (_idleRemaining <= 0)
            {
                Debug.WriteLine($"[在线内核] 空闲 {IdleRecycleSeconds}s，回收内核");
                Dispose();
                _idleRemaining = IdleRecycleSeconds;
            }
        }

        #endregion

        #region 核心操作（串行化）

        /// <summary>
        /// 在内核上执行一段异步工作，全程串行（同一时刻只有一个任务占用内核）。
        /// work 收到的 CoreWebView2 保证已初始化。
        /// </summary>
        public async Task<T> RunAsync<T>(Func<CoreWebView2, Task<T>> work, CancellationToken ct = default)
        {
            await EnsureInitializedAsync();
            await _gate.WaitAsync(ct);

            Interlocked.Increment(ref _activeOps);
            try
            {
                ResetIdleTimer();
                var core = CoreWebView2 ?? throw new InvalidOperationException("内核未就绪");
                return await work(core);
            }
            finally
            {
                Interlocked.Decrement(ref _activeOps);
                _gate.Release();
                ResetIdleTimer();
            }
        }

        /// <summary>
        /// 导航到指定 URL 并等待加载完成，返回最终 URL（可能经历跳转）。
        /// 失败抛 OnlineNavigationException；取消抛 OperationCanceledException。
        /// 超时由调用方（S4 用 10s）通过 CancellationToken 控制。
        /// </summary>
        public Task<string> NavigateAsync(string url, CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) =>
                    tcs.TrySetResult(e);

                core.NavigationCompleted += OnCompleted;
                try
                {
                    using (ct.Register(() =>
                    {
                        core.Stop();               // 立即停掉加载（取消时省资源）
                        tcs.TrySetCanceled(ct);
                    }))
                    {
                        core.Navigate(url);
                        var args = await tcs.Task;
                        if (!args.IsSuccess)
                            throw new OnlineNavigationException(args.WebErrorStatus);
                    }
                }
                finally
                {
                    core.NavigationCompleted -= OnCompleted;
                }
                return core.Source;
            }, ct);

        /// <summary>
        /// 在当前页面执行 JS，返回**原始 JSON 字符串**（未反序列化——字符串结果带引号，对象为 JSON 文本）。
        /// </summary>
        public Task<string> ExecuteScriptAsync(string js, CancellationToken ct = default) =>
            RunAsync(async core => await core.ExecuteScriptAsync(js), ct);

        /// <summary>读取指定 URL 域下的全部 Cookie（导出给 HttpClient/ffmpeg 下载用）。</summary>
        public Task<List<(string Name, string Value)>> GetCookiesAsync(string url, CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                var cookies = await core.CookieManager.GetCookiesAsync(url);
                var list = new List<(string, string)>(cookies.Count);
                foreach (var c in cookies) list.Add((c.Name, c.Value));
                return list;
            }, ct);

        /// <summary>停止当前一切加载（搜索取消按钮调用）。</summary>
        public void StopLoading()
        {
            if (_initialized) CoreWebView2?.Stop();
        }

        #endregion

        #region 可见切换（人机验证 S3 / 添加向导 S5）

        /// <summary>
        /// 把内核临时切挂到指定窗口并显示（ParentWindow 运行时改挂，仍是同一实例、Cookie 共享）。
        /// 用于人机验证窗口（S3）与添加向导预览（S5）——全程唯一可见内核的场景。
        /// </summary>
        public void ShowIn(Window host)
        {
            if (!_initialized)
                throw new InvalidOperationException("内核尚未初始化，请先在 UI 线程调用 InitAsync");

            _controller!.ParentWindow = new WindowInteropHelper(host).Handle;
            _controller.IsVisible = true;
            _isVisibleOnHost = true;
            FitBoundsTo(host);
        }

        /// <summary>内核切回主窗口并隐藏（验证完成/向导关闭时调用）。</summary>
        public void HideBack()
        {
            if (!_initialized) return;
            _controller!.ParentWindow = _hostHwnd;
            _controller.IsVisible = false;
            _isVisibleOnHost = false;
        }

        /// <summary>让内核填满宿主窗口客户区（宿主 SizeChanged 时也要调一次）。</summary>
        public void FitBoundsTo(Window host)
        {
            if (!_initialized) return;

            var source = PresentationSource.FromVisual(host) as HwndSource;
            double dpiX = source?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

            int w = Math.Max(1, (int)(host.ActualWidth * dpiX));
            int h = Math.Max(1, (int)(host.ActualHeight * dpiY));
            _controller!.Bounds = new Rectangle(0, 0, w, h);
        }

        #endregion

        #region 音频嗅探

        /// <summary>URL 扩展名判定（请求侧，无需等响应）。</summary>
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".aac", ".opus",   // 直链
            ".m4s", ".ts", ".mp4",                                      // DASH/TS 分片
            ".m3u8", ".mpd"                                             // 流媒体索引
        };

        private static readonly HashSet<string> StreamExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".m3u8", ".mpd", ".m4s", ".ts"
        };

        /// <summary>URL 是否疑似音频/流媒体资源。</summary>
        public static bool IsAudioResource(string url, out bool isStream)
        {
            isStream = false;
            try
            {
                string path = new Uri(url).AbsolutePath;
                int dot = path.LastIndexOf('.');
                if (dot < 0) return false;
                string ext = path[dot..];
                if (!AudioExtensions.Contains(ext)) return false;
                isStream = StreamExtensions.Contains(ext);
                return true;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            // 只读监听，不修改请求；URL 扩展名命中即上报（Content-Type 可能还没有）
            if (IsAudioResource(e.Request.Uri, out bool isStream))
            {
                RaiseAudioSniffed(e.Request.Uri, contentType: null, isStream);
            }
        }

        private void OnWebResourceResponseReceived(object? sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            // 响应侧兜底：B 站的 .m4s 之类可能没有扩展名，靠 Content-Type 识别
            string? contentType = null;
            foreach (var header in e.Response.Headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = header.Value;
                    break;
                }
            }

            if (string.IsNullOrEmpty(contentType)) return;
            contentType = contentType.Split(';')[0].Trim(); // 去掉 charset 等参数

            bool isAudio = contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            // video/* 只在 DASH 分片场景上报（m4s 没扩展名时靠它命中），普通 .mp4 视频页会有海量噪音，
            // 所以这里限定：Content-Type 命中 且 URL 已被判为音频资源（扩展名命中）时补充信息；
            // 纯 video/* 且无扩展名的场景目前只服务 B 站音轨，交给 URL 侧兜底即可。
            if (IsAudioResource(e.Request.Uri, out bool isStream) || isAudio)
            {
                RaiseAudioSniffed(e.Request.Uri, contentType, isStream);
            }
        }

        private void RaiseAudioSniffed(string url, string? contentType, bool isStream)
        {
            if (_isVisibleOnHost) return; // 验证/向导模式下不嗅探（用户手动浏览页面，噪音大）
            AudioSniffed?.Invoke(this, new AudioSniffedEventArgs
            {
                Url = url,
                ContentType = contentType,
                IsStream = isStream
            });
        }

        #endregion

        private CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;
            await InitAsync();
        }

        private void ResetIdleTimer()
        {
            // 有操作就把倒计时拨回满值（计时器本身不动，避免频繁 Stop/Start）
            _idleRemaining = IdleRecycleSeconds;
        }

        /// <summary>空闲回收间隔（秒）：连续这么多秒没有任何内核操作才释放。</summary>
        private const int IdleRecycleSeconds = 60;

        #region 诊断信息（设置页"在线功能诊断"用）

        /// <summary>进行中的内核任务数。</summary>
        public int ActiveOperationCount => _activeOps;

        /// <summary>距空闲自动回收还剩多少秒（未初始化时返回满值）。</summary>
        public int IdleRemainingSeconds => _initialized ? _idleRemaining : IdleRecycleSeconds;

        /// <summary>当前用户数据目录（Cookie/登录态所在）。</summary>
        public static string UserDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "musicPlayer", "webview2");

        /// <summary>
        /// 统计属于本程序的 msedgewebview2.exe 进程数与内存占用（MB）。
        /// 系统里还有 Windows Widgets 等自己的内核，所以按**父子进程链**判断归属：只统计能追溯到本进程的那些。
        /// </summary>
        public static (int Count, long TotalMb) GetBrowserProcessStats()
            => ProcessStats.CountDescendants("msedgewebview2");

        /// <summary>当前进程内存占用（MB），任务管理器口径的近似值。</summary>
        public static long GetOwnWorkingSetMb() => ProcessStats.CurrentWorkingSetMb;

        #endregion
    }
}
