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

// System.Drawing 提供 Rectangle（WebView2 的 Bounds 用它），但它也有 Point；
// WPF 的坐标换算要用 System.Windows.Point，这里显式指定，避免 CS0104 歧义
using Point = System.Windows.Point;

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

            // 数据目录：默认 %APPDATA%\musicPlayer\webview2（登录态/Cookie 跨会话保留）；
            // 测试可用 MP_WEBVIEW_USERDATA 指到临时目录，避免与残留实例争同一个 Chromium profile
            // （同一 profile 被占用时，新实例的内核会创建失败，表现为"在线功能整个不可用"）。
            string userDataFolder = Environment.GetEnvironmentVariable("MP_WEBVIEW_USERDATA") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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

            // 内核"未绘制区域"的底色：默认是白色，切挂到可见窗口的瞬间会闪一大片白，
            // 页面要是还没渲染完就一直是白的（看起来像"验证页没加载出来"）。
            // 这里改成与界面一致的深色，视觉上和占位背景无缝衔接。
            try { _controller.DefaultBackgroundColor = Color.FromArgb(255, 11, 11, 18); }
            catch (Exception ex) { Debug.WriteLine($"[内核] 设置默认底色失败: {ex.Message}"); }

            // 拦截弹窗：任何 window.open/新标签一律取消（PRD F4.x"杜绝弹窗"）
            core.NewWindowRequested += (_, e) =>
            {
                // 登录窗口期间允许"弹窗转同页"：不少站点用 window.open 走第三方登录（微信/QQ/邮箱），
                // 一刀切拦掉会让这些站点永远登不上。这里不新开窗口，而是把当前内核导航过去
                // （脚本上下文可能丢失，但登录跳转本身就靠重定向完成，够用）。
                if (AllowNewWindows && !string.IsNullOrWhiteSpace(e.Uri))
                {
                    e.Handled = true;
                    core.Navigate(e.Uri);
                    return;
                }

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

        /// <summary>
        /// 无视"可见中/有任务进行中"强制释放内核。
        /// 用途：验证窗口关闭时若仍有内核调用在飞行中（脚本/导航没返回），
        /// 此时改挂 ParentWindow 会让内核挂死（之后所有调用永远等不到结果）——
        /// 直接整体释放最安全，下次搜索会自动重建（约 1 秒）。
        /// </summary>
        public void ForceDisposeCore() => Dispose();

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

        /// <summary>是否把站点弹窗转成当前页导航（仅登录窗口期间为 true，其余时间一律拦截）。</summary>
        public bool AllowNewWindows { get; set; }

        /// <summary>停止当前一切加载（搜索取消按钮调用）。</summary>
        public void StopLoading()
        {
            if (_initialized) CoreWebView2?.Stop();
        }

        /// <summary>
        /// 清掉某个网址作用域内的全部 Cookie，返回删除条数（S7 删除站点时调用）。
        ///
        /// WebView2 的 Cookie 是按 UserDataFolder 共享的，没有"按站点删除"的现成 API，
        /// 只能用 <c>GetCookies(uri)</c> 取出该 uri 适用的 Cookie（含 Domain 通配）再逐个删。
        /// 注意：这里只清该站点的登录态，其他站点不受影响。
        /// </summary>
        public Task<int> ClearCookiesForAsync(string url, CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 0;

                var cookies = await core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
                int deleted = 0;
                foreach (var cookie in cookies)
                {
                    try
                    {
                        core.CookieManager.DeleteCookie(cookie);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[内核] 删除 Cookie 失败({cookie.Name}): {ex.Message}");
                    }
                }
                return deleted;
            }, ct);

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

        /// <summary>
        /// 把内核切挂到指定窗口的**某个内容区**并显示（验证窗口 S3 / 向导 S5）。
        ///
        /// 为什么要传 contentArea：WebView2 是原生子窗口，会盖住同区域的 WPF 元素（airspace），
        /// 所以宿主窗口只能把"留给网页的矩形"交给内核，顶栏/底栏用 WPF 画在矩形之外。
        /// </summary>
        public void ShowIn(Window host, FrameworkElement contentArea)
        {
            if (!_initialized)
                throw new InvalidOperationException("内核尚未初始化，请先在 UI 线程调用 InitAsync");

            // 顺序很关键：
            // ① 先隐藏（避免旧父窗口的残留画面/尺寸被带过去）
            // ② 改挂父窗口
            // ③ 设定精确矩形
            // ④ 显示 + 通知内核"父窗口变了、位置变了"，让它重新计算并立刻重绘
            //    ——少了 NotifyParentWindowPositionChanged，内核会沿用旧坐标，
            //      表现为内容空白/白底/位置错位，直到下一次尺寸变化才恢复。
            _controller!.IsVisible = false;
            _controller.ParentWindow = new WindowInteropHelper(host).Handle;
            _isVisibleOnHost = true;
            FitBoundsTo(host, contentArea);
            _controller.IsVisible = true;
            NotifyParentWindowPositionChanged();
        }

        /// <summary>
        /// 告诉内核父窗口的位置/尺寸变了（改挂父窗口、拖动、缩放后都要调）。
        /// WebView2 靠这个通知重新计算相对坐标并触发重绘；不调的话切挂后可能一片空白。
        /// </summary>
        public void NotifyParentWindowPositionChanged()
        {
            if (!_initialized) return;
            try { _controller!.NotifyParentWindowPositionChanged(); }
            catch (Exception ex) { Debug.WriteLine($"[内核] 通知父窗口变更失败: {ex.Message}"); }
        }

        /// <summary>让内核精确填满宿主窗口里 contentArea 那块矩形（像素对齐，按 DPI 换算）。</summary>
        public void FitBoundsTo(Window host, FrameworkElement contentArea)
        {
            if (!_initialized || contentArea == null) return;
            _controller!.Bounds = ComputeBounds(host, contentArea);
            // 窗口拖动/缩放/DPI 变化时也通知一次，保证内容跟着布局走（否则会停在旧位置）
            if (_isVisibleOnHost) NotifyParentWindowPositionChanged();
        }

        /// <summary>把 WPF 元素矩形换算成父窗口客户区的物理像素矩形。</summary>
        private static Rectangle ComputeBounds(Window host, FrameworkElement contentArea)
        {
            double dpiX = 1.0, dpiY = 1.0;
            if (PresentationSource.FromVisual(host) is HwndSource src && src.CompositionTarget != null)
            {
                dpiX = src.CompositionTarget.TransformToDevice.M11;
                dpiY = src.CompositionTarget.TransformToDevice.M22;
            }

            Point origin;
            try
            {
                origin = contentArea.TransformToAncestor(host).Transform(new Point(0, 0));
            }
            catch
            {
                origin = new Point(0, 0); // 视觉树还没挂好时的兜底
            }

            int x = (int)Math.Round(origin.X * dpiX);
            int y = (int)Math.Round(origin.Y * dpiY);
            int w = Math.Max(1, (int)Math.Round(contentArea.ActualWidth * dpiX));
            int h = Math.Max(1, (int)Math.Round(contentArea.ActualHeight * dpiY));
            return new Rectangle(x, y, w, h);
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

        #region 人机验证检测（S3 用）

        /// <summary>当前页面是否卡在人机验证上（URL 特征或 DOM 挑战元素，任一命中）。</summary>
        public Task<bool> DetectCaptchaAsync(CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                if (CaptchaDetector.IsCaptchaUrl(core.Source)) return true;
                return await CaptchaDetector.IsChallengeVisibleAsync(core);
            }, ct);

        /// <summary>验证是否已经通过（授权 Cookie 出现，或已回到目标站且无挑战元素）。</summary>
        public Task<bool> IsCaptchaPassedAsync(string targetUrl, CancellationToken ct = default) =>
            RunAsync(core => CaptchaDetector.IsPassedAsync(core, targetUrl), ct);

        /// <summary>诊断快照：当前 URL、URL 判据、DOM 挑战判据（排查"为什么判定通过/未通过"用）。</summary>
        public Task<(string Url, bool UrlLooksCaptcha, bool ChallengeVisible)> GetCaptchaProbeAsync(
            CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                string url = core.Source ?? "";
                bool byUrl = CaptchaDetector.IsCaptchaUrl(url);
                bool visible = await CaptchaDetector.IsChallengeVisibleAsync(core);
                return (url, byUrl, visible);
            }, ct);

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

            SniffEventCount++;
            LastSniffedUrl = url;

            AudioSniffed?.Invoke(this, new AudioSniffedEventArgs
            {
                Url = url,
                ContentType = contentType,
                IsStream = isStream
            });
        }

        /// <summary>累计收到的音频嗅探事件数（诊断/自检用：0 说明内核连音频请求都没发出）。</summary>
        public int SniffEventCount { get; private set; }

        /// <summary>最近一次嗅探到的地址（诊断/自检用）。</summary>
        public string? LastSniffedUrl { get; private set; }

        /// <summary>
        /// **只等不导**：在当前已打开的页面上等待音频请求命中（上限 <paramref name="timeout"/>），
        /// 可选在 1.2 秒后注入 kick 脚本"点一下播放"。
        ///
        /// 导航由调用方先用 <see cref="NavigateAsync"/> 完成——这样能先做 DOM 静态提取，
        /// 只有"必须点了播放才去请求音频"的站点才走到这里。
        ///
        /// ⚠ 两个实测坑：
        /// 1. 隐藏内核里页面是 hidden 状态，Chromium 会把媒体加载往后推（实测 play() 后约 6s 才发请求），
        ///    所以等待期间把 controller 临时置为可见——宿主窗口在屏幕外(-32000)，用户什么都看不到；
        /// 2. kick 脚本必须在 UI 线程执行（见 <see cref="KickAfterDelay"/>）。
        /// </summary>
        public Task<AudioSniffedEventArgs?> WaitForAudioAsync(TimeSpan timeout, string? kickScript = null,
            CancellationToken ct = default) =>
            RunAsync(async core =>
            {
                var tcs = new TaskCompletionSource<AudioSniffedEventArgs?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                AudioSniffedEventArgs? streamFallback = null;

                void OnSniffed(object? s, AudioSniffedEventArgs e)
                {
                    if (e.IsStream) { streamFallback ??= e; return; }   // 先记下，继续等直链
                    tcs.TrySetResult(e);
                }

                AudioSniffed += OnSniffed;
                bool wasVisible = _controller!.IsVisible;
                try
                {
                    _controller.IsVisible = true;    // 解除后台节流（宿主在屏幕外，不可见）

                    if (!string.IsNullOrWhiteSpace(kickScript))
                        KickAfterDelay(core, kickScript, ct, tcs);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(timeout);
                    using (timeoutCts.Token.Register(() =>
                           {
                               if (ct.IsCancellationRequested) { try { core.Stop(); } catch { } }
                               tcs.TrySetResult(null);                 // 超时 = 没等着
                           }))
                    {
                        var hit = await tcs.Task;
                        App.LogCrash(hit == null
                            ? $"[嗅探] 超时未命中（页面 {core.Source}）"
                            : $"[嗅探] 命中 {(hit.IsStream ? "流媒体" : "直链")} {hit.Url}");
                        return hit ?? streamFallback;
                    }
                }
                finally
                {
                    AudioSniffed -= OnSniffed;
                    try { _controller!.IsVisible = wasVisible; } catch { }
                }
            }, ct);

        /// <summary>
        /// 延迟后注入"自动播放"脚本（嗅探辅助，异常一律吞掉）。
        /// ⚠ 必须在 UI 线程调用 WebView2：`Task.Delay(...).ConfigureAwait(false)` 之后
        /// 直接 ExecuteScriptAsync 会抛"线程不对"，脚本静默失败——这个坑实测踩过一次。
        ///
        /// **踢两次**（1.2s / 4.2s）：模块化站点的事件绑定有早有晚，只踢一脚容易扑空；
        /// 命中（tcs 完成）或取消后不再踢——对"点了就播"的站点，多踢会把播放切成暂停。
        /// 每次把脚本返回值写进日志（脚本自带 JSON 诊断：踢到了哪个按钮）。
        /// </summary>
        private static void KickAfterDelay(CoreWebView2 core, string script, CancellationToken ct,
            TaskCompletionSource<AudioSniffedEventArgs?> tcs)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (int delayMs in new[] { 1200, 3000 })
                    {
                        await Task.Delay(delayMs, ct);
                        if (tcs.Task.IsCompleted) return;

                        await dispatcher.BeginInvoke(new Action(async () =>
                        {
                            try
                            {
                                string raw = await core.ExecuteScriptAsync(script);
                                App.LogCrash("[嗅探] kick → " + raw);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[嗅探] 自动播放脚本失败（忽略）: {ex.Message}");
                            }
                        }));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Debug.WriteLine($"[嗅探] kick 循环异常（忽略）: {ex.Message}"); }
            });
        }

        /// <summary>
        /// 防双声断言（S8）：播放前确认隐藏内核仍然静音——页面里被 kick 脚本"点醒"的
        /// &lt;audio&gt; 若没被内核静音，就会和 MediaElement 一起响。
        /// 返回当前是否静音（内核未初始化视为安全）。
        /// </summary>
        public bool EnsureMutedCore()
        {
            var core = CoreWebView2;
            if (!_initialized || core == null) return true;
            try
            {
                if (!core.IsMuted) core.IsMuted = true;
                return core.IsMuted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[内核] 检查静音失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>当前内核是否静音（诊断/自检用；未初始化返回 true）。</summary>
        public bool IsMutedCore => !_initialized || _controller?.CoreWebView2 is not { } core || core.IsMuted;

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
