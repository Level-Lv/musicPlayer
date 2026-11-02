using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using musicPlayer.Data;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>一次性登录的结果。</summary>
    public sealed class SiteLoginResult
    {
        /// <summary>是否成功保存了登录态（检测到 Cookie）。</summary>
        public bool Success { get; init; }

        /// <summary>保存到的 Cookie 条数。</summary>
        public int CookieCount { get; init; }

        /// <summary>给用户看的说明。</summary>
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// "完成一次登录"窗口（《在线曲库开发步骤清单》S7）。
    ///
    /// 场景：站点要求登录才能搜索（S4 判为 NeedLogin），用户在这里正常登录一次，
    /// 登录态存在 WebView2 的 UserDataFolder 里，之后所有隐藏搜索都自动带上。
    ///
    /// 机制与 S3 验证窗口完全一致：**同一个**隐藏内核用 ParentWindow 切挂到内容区，
    /// 不新开 WebView2 实例 → Cookie 天然共享；窗口是非模态的，绝不会锁死主界面。
    /// 唯一区别是这里**不自动判通过**：用户自己点"完成登录"，我们只负责数 Cookie。
    /// </summary>
    public partial class SiteLoginWindow : Window
    {
        private readonly HiddenBrowserService _svc = HiddenBrowserService.Instance;
        private readonly OnlineSite _site;
        private readonly string _startUrl;
        private readonly DispatcherTimer _attentionTimer;

        private bool _closing;
        private bool _busy;
        private bool _attentionChecked = false;
        private CancellationTokenSource? _navCts;

        /// <summary>窗口关闭后读取的结果。</summary>
        public SiteLoginResult Result { get; private set; } =
            new SiteLoginResult { Success = false, Message = "已取消" };

        public SiteLoginWindow(OnlineSite site)
        {
            InitializeComponent();

            _site = site;
            _startUrl = BuildStartUrl(site);

            txtTitle.Text = "登录站点";
            txtSubtitle.Text = $"{site.Name} · 在下面的网页里正常登录，登录好后点「完成登录」";

            _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _attentionTimer.Tick += (_, _) => CheckAttention();

            Loaded += OnLoaded;
            SizeChanged += (_, _) => SafeFitBounds();
            DpiChanged += (_, _) => SafeFitBounds();
            SourceInitialized += (_, _) =>
            {
                PlaceOnScreen();
                WindowEffects.ApplyRoundedCorners(this);
            };
        }

        /// <summary>
        /// 打开登录窗口并异步等待结果（**非模态**，不阻塞调用方）。
        /// 站点 Cookie 由本窗口负责写库（TouchOnlineSiteCookie + 状态置 ok）。
        /// </summary>
        public static Task<SiteLoginResult> ShowAsync(Window? owner, OnlineSite site)
        {
            var win = new SiteLoginWindow(site);
            var tcs = new TaskCompletionSource<SiteLoginResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            win.Closed += (_, _) => tcs.TrySetResult(win.Result);

            try
            {
                if (owner != null && owner.IsVisible) win.Owner = owner;
            }
            catch { /* Owner 设置失败不影响功能 */ }

            win.Show();
            win.BringToFront();

            // 硬兜底：用户可能把窗口晾在那儿一整天，超过 10 分钟自动收尾，别让调用方永远等
            var hardStop = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            hardStop.Tick += (_, _) =>
            {
                hardStop.Stop();
                App.LogCrash("[登录窗口] 超时（10 分钟），自动关闭");
                try { if (win.IsVisible) win.Close(); } catch { }
                tcs.TrySetResult(new SiteLoginResult { Success = false, Message = "登录超时" });
            };
            hardStop.Start();
            win.Closed += (_, _) => hardStop.Stop();

            return tcs.Task;
        }

        /// <summary>
        /// 从站点模板推出登录页起点：<c>scheme://host/</c>。
        /// 直接打开搜索页会带着 {q} 占位符，有些站点会报错，所以回退到站点根。
        /// </summary>
        private static string BuildStartUrl(OnlineSite site)
        {
            if (Uri.TryCreate(site.SearchTemplate, UriKind.Absolute, out var uri))
                return uri.GetLeftPart(UriPartial.Authority) + "/";
            return site.SearchTemplate;
        }

        // ══════════════════════════════ 生命周期 ══════════════════════════════

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                App.LogCrash($"[登录窗口] Loaded site={_site.Name} url={_startUrl}");

                _svc.AllowNewWindows = true;          // 登录常需弹窗（第三方登录），转成同页导航
                await _svc.InitAsync();

                _svc.ShowIn(this, contentHost);
                txtPlaceholder.Visibility = Visibility.Collapsed;

                _ = Dispatcher.BeginInvoke(new Action(SafeFitBounds), DispatcherPriority.Loaded);
                await Task.Delay(120);
                SafeFitBounds();
                _attentionTimer.Start();

                // 导航不设短超时：登录页可能很慢，用户看到进度就行
                _navCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                try
                {
                    await _svc.NavigateAsync(_startUrl, _navCts.Token);
                    if (!_closing) txtStatus.Text = "登录完成后点「完成登录」，会保存登录状态";
                }
                catch (OperationCanceledException)
                {
                    if (!_closing) txtStatus.Text = "页面加载较慢，可以继续等待或手动操作";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[登录窗口] 导航失败: {ex.Message}");
                    App.LogCrash("[登录窗口] 导航失败: " + ex.Message);
                    if (!_closing) txtStatus.Text = "页面打开失败（网络问题？），可关闭后重试";
                    SetDot(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[登录窗口] 初始化失败: {ex}");
                App.LogCrash("[登录窗口] 初始化失败: " + ex);

                Result = new SiteLoginResult { Success = false, Message = "窗口初始化失败：" + ex.Message };
                txtPlaceholder.Text = "站点页面初始化失败：" + ex.Message;
                txtPlaceholder.Visibility = Visibility.Visible;
                txtStatus.Text = "初始化失败，请关闭窗口后重试";
                SetDot(false);
                await Task.Delay(1200);
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            _attentionTimer.Stop();
            _navCts?.Cancel();
            _svc.AllowNewWindows = false;

            // 与验证窗口同一套保护：先等在飞的内核调用结束再改挂父窗口，
            // 2 秒还没结束就整体释放内核（下次自动重建），避免内核挂死导致整个程序像卡住。
            try
            {
                for (int i = 0; i < 20 && _svc.ActiveOperationCount > 0; i++)
                    Thread.Sleep(100);

                if (_svc.ActiveOperationCount > 0)
                {
                    App.LogCrash("[登录窗口] 关闭时内核仍有调用在飞，强制释放内核");
                    _svc.ForceDisposeCore();
                }
                else
                {
                    _svc.HideBack();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[登录窗口] 归还内核失败: {ex.Message}"); }

            App.LogCrash($"[登录窗口] 关闭，结果={Result.Success} cookies={Result.CookieCount}");
            base.OnClosing(e);
        }

        private void SafeFitBounds()
        {
            try { _svc.FitBoundsTo(this, contentHost); }
            catch (Exception ex) { Debug.WriteLine($"[登录窗口] 同步内核尺寸失败: {ex.Message}"); }
        }

        // ══════════════════════════════ 完成登录 ══════════════════════════════

        private async void btnDone_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            btnDone.IsEnabled = false;
            txtStatus.Text = "正在检查登录状态…";

            try
            {
                // 看门狗：内核无响应时别让按钮永久变灰
                var task = _svc.GetCookiesAsync(_startUrl, CancellationToken.None);
                var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(6)));
                if (!ReferenceEquals(finished, task))
                {
                    txtStatus.Text = "检查超时（内核无响应），请稍后重试或关闭窗口";
                    SetDot(false);
                    return;
                }

                var cookies = await task;
                var names = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var c in cookies) names.Add(c.Name);

                if (names.Count == 0)
                {
                    // 没有 Cookie 不代表失败（有些站点用 localStorage / 免登录），
                    // 但也可能是用户还没登录，这里不自动关窗，让用户自己判断。
                    txtStatus.Text = "没检测到登录 Cookie（可能还没登录，或该站点不用 Cookie）";
                    SetDot(false);
                    return;
                }

                try
                {
                    Db.TouchOnlineSiteCookie(_site.Id);
                    Db.MarkOnlineSiteOk(_site.Id);          // 登录后大概率能搜了，先解除"需登录"
                    _site.CookieUpdatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                }
                catch (Exception ex)
                {
                    App.LogCrash("[登录窗口] 写库失败: " + ex);
                    txtStatus.Text = "登录状态已拿到，但保存失败：" + ex.Message;
                    SetDot(false);
                    return;
                }

                _site.Status = OnlineSiteStatus.Ok;
                App.LogCrash($"[登录窗口] 登录成功，Cookie {names.Count} 个：" +
                             string.Join(",", names));

                Result = new SiteLoginResult
                {
                    Success = true,
                    CookieCount = names.Count,
                    Message = $"已保存登录状态（{names.Count} 个 Cookie）"
                };
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[登录窗口] 检查登录状态失败: {ex}");
                txtStatus.Text = "检查失败：" + ex.Message;
                SetDot(false);
            }
            finally
            {
                _busy = false;
                if (!_closing) btnDone.IsEnabled = true;
            }
        }

        private void SetDot(bool ok) =>
            dotState.Fill = ok
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x7B, 0xD8, 0x8F))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x65));

        // ══════════════════════════════ 位置/置前/交互 ══════════════════════════════

        private void PlaceOnScreen()
        {
            try
            {
                Rect area = GetWorkArea();
                double w = Math.Min(Width, area.Width * 0.92);
                double h = Math.Min(Height, area.Height * 0.92);
                if (w > 0 && Math.Abs(w - Width) > 1) Width = w;
                if (h > 0 && Math.Abs(h - Height) > 1) Height = h;

                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }
            catch (Exception ex) { Debug.WriteLine($"[登录窗口] 定位失败: {ex.Message}"); }
        }

        private Rect GetWorkArea()
        {
            System.Windows.Forms.Screen screen =
                Owner != null && Owner.IsVisible
                    ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Owner).Handle)
                    : System.Windows.Forms.Screen.PrimaryScreen
                      ?? System.Windows.Forms.Screen.AllScreens[0];

            var wa = screen.WorkingArea;
            var src = PresentationSource.FromVisual(this) as HwndSource;
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (scale <= 0) scale = 1.0;

            return new Rect(wa.Left / scale, wa.Top / scale,
                            Math.Max(1, wa.Width / scale), Math.Max(1, wa.Height / scale));
        }

        public void BringToFront()
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!IsActive)
                {
                    Topmost = true;
                    Topmost = false;
                    Activate();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[登录窗口] 置前失败: {ex.Message}"); }

            App.LogCrash($"[登录窗口] 显示位置=({Left:F0},{Top:F0}) " +
                         $"尺寸={ActualWidth:F0}x{ActualHeight:F0} visible={IsVisible}");
        }

        private void CheckAttention()
        {
            _attentionTimer.Stop();
            if (_attentionChecked || _closing || !IsVisible) return;
            _attentionChecked = true;

            try
            {
                Rect area = GetWorkArea();
                var self = new Rect(Left, Top, ActualWidth, ActualHeight);
                if (!area.IntersectsWith(self) || self.Width < 80 || self.Height < 80)
                {
                    App.LogCrash($"[登录窗口] 疑似在屏幕外（rect={self}），重新定位");
                    PlaceOnScreen();
                }
                if (!IsActive) BringToFront();
            }
            catch (Exception ex) { Debug.WriteLine($"[登录窗口] 自检失败: {ex.Message}"); }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { /* 拖动中断，忽略 */ }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void btnCancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
