using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>验证中转结果。</summary>
    public enum CaptchaResult
    {
        /// <summary>用户完成验证，可以继续原任务。</summary>
        Passed,
        /// <summary>用户主动取消（关窗 / 点"取消本次搜索"）。</summary>
        Cancelled,
        /// <summary>超过 120 秒没有通过。</summary>
        Timeout,
        /// <summary>窗口/内核初始化失败。</summary>
        Failed
    }

    /// <summary>
    /// 人机验证中转窗口（《在线曲库开发步骤清单》S3）。
    ///
    /// 机制：把**同一个**隐藏内核用 ParentWindow 切挂到本窗口的内容区，
    /// 用户看到的就是那个真实网页，自己完成滑块/点选/Turnstile；
    /// 程序每秒轮询"是否已通过"，通过后自动摘走内核并关闭窗口，调用方继续原来的搜索。
    ///
    /// 全程只有一个 WebView2 实例 → Cookie 天然共享，后续搜索不会再被同一站点挑战。
    /// 这里**不做任何绕过**：识别 + 把真实页面交给用户，仅此而已。
    ///
    /// ⚠ 窗口是**非模态**的。之前用 ShowDialog 出过一次严重事故：窗口因为定位/置前失败
    /// 没出现在屏幕上，但模态关系已经把主窗口禁用了 —— 用户看到的就是"程序卡死，
    /// 但有声音、只能任务管理器结束"。非模态后最坏情况只是窗口没被看见，主窗口照常可用。
    /// </summary>
    public partial class CaptchaWindow : Window
    {
        private const int TimeoutSeconds = 120;

        private readonly HiddenBrowserService _svc = HiddenBrowserService.Instance;
        private readonly string _targetUrl;
        private readonly Func<Task<bool>>? _extraVerify;
        private readonly bool _skipNavigate;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _attentionTimer;   // 1.5 秒后自检"窗口真的看得见吗"

        private int _remaining = TimeoutSeconds;
        private bool _checking;
        private bool _closing;
        private bool _attentionChecked;
        private CancellationTokenSource? _navCts;

        /// <summary>本窗口的结果（窗口关闭后读取）。</summary>
        public CaptchaResult Result { get; private set; } = CaptchaResult.Cancelled;

        /// <param name="targetUrl">要打开的页面（通常是那次被拦下的搜索 URL）。</param>
        /// <param name="extraVerify">
        /// 额外的"通过"判据（S4 会传"按选择器能抓到结果"）。返回 true 即认为已通过。
        /// </param>
        /// <param name="skipNavigate">页面已经停在该 URL 时设 true，只切挂不重新导航。</param>
        public CaptchaWindow(string targetUrl, Func<Task<bool>>? extraVerify = null, bool skipNavigate = false)
        {
            InitializeComponent();

            _targetUrl = targetUrl;
            _extraVerify = extraVerify;
            _skipNavigate = skipNavigate;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += async (_, _) => await PollOnceAsync();

            _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _attentionTimer.Tick += (_, _) => CheckAttention();

            Loaded += OnLoaded;
            SizeChanged += (_, _) => SafeFitBounds();
            DpiChanged += (_, _) => SafeFitBounds();
            SourceInitialized += (_, _) =>
            {
                PlaceOnScreen();                       // 先算好位置，别依赖 CenterOwner 的默认行为
                WindowEffects.ApplyRoundedCorners(this);
            };
        }

        /// <summary>
        /// 打开验证窗口并异步等待结果。**非模态**：不阻塞调用方的 UI 线程，
        /// 即使窗口没能显示到前台，主界面仍然可用（用户可关掉它重试，而不是整程序卡死）。
        /// </summary>
        public static Task<CaptchaResult> ShowAsync(Window? owner, string targetUrl,
            Func<Task<bool>>? extraVerify = null, bool skipNavigate = false)
        {
            var win = new CaptchaWindow(targetUrl, extraVerify, skipNavigate);
            var tcs = new TaskCompletionSource<CaptchaResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            win.Closed += (_, _) => tcs.TrySetResult(win.Result);

            try
            {
                if (owner != null && owner.IsVisible) win.Owner = owner;
            }
            catch { /* Owner 设置失败不影响功能 */ }

            win.Show();
            win.BringToFront();

            // 硬兜底：窗口若因任何原因既不关闭也不超时（例如内核卡住导致轮询全空转），
            // 到点强制收尾，绝不让调用方（搜索流程）永远挂着。
            var hardStop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TimeoutSeconds + 15) };
            hardStop.Tick += (_, _) =>
            {
                hardStop.Stop();
                App.LogCrash("[验证窗口] 硬超时，强制关闭");
                try { if (win.IsVisible) win.Close(); } catch { }
                tcs.TrySetResult(CaptchaResult.Timeout);
            };
            hardStop.Start();
            win.Closed += (_, _) => hardStop.Stop();

            return tcs.Task;
        }

        #region 显示位置与置前（防"窗口看不见但卡住界面"）

        /// <summary>
        /// 显式把窗口放到"当前屏幕工作区中央"（Owner 在哪个屏就放哪个屏）。
        /// 不用 WindowStartupLocation=CenterOwner：它在 Owner 最小化/跨屏/DPI 混合时
        /// 可能算出屏幕外的坐标，结果就是"窗口存在但用户看不到"。
        /// </summary>
        private void PlaceOnScreen()
        {
            try
            {
                Rect area = GetWorkArea();

                // 窗口比屏幕还大时先缩到工作区的 92%，避免标题栏被挤出屏幕
                double w = Math.Min(Width, area.Width * 0.92);
                double h = Math.Min(Height, area.Height * 0.92);
                if (w > 0 && Math.Abs(w - Width) > 1) Width = w;
                if (h > 0 && Math.Abs(h - Height) > 1) Height = h;

                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[验证窗口] 定位失败: {ex.Message}");
            }
        }

        /// <summary>取目标屏幕的工作区（DIP 单位；多屏按 Owner 所在屏）。</summary>
        private Rect GetWorkArea()
        {
            System.Windows.Forms.Screen screen;
            if (Owner != null && Owner.IsVisible)
            {
                screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Owner).Handle);
            }
            else
            {
                screen = System.Windows.Forms.Screen.PrimaryScreen
                         ?? System.Windows.Forms.Screen.AllScreens[0];
            }

            var wa = screen.WorkingArea;                              // 物理像素
            var src = PresentationSource.FromVisual(this) as HwndSource;
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (scale <= 0) scale = 1.0;

            return new Rect(wa.Left / scale, wa.Top / scale,
                            Math.Max(1, wa.Width / scale), Math.Max(1, wa.Height / scale));
        }

        /// <summary>把窗口提到前台：最小化先还原、激活；仍不在前台则 Topmost 闪一下。</summary>
        public void BringToFront()
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!IsActive)
                {
                    // 绕过系统"前台权限"限制的常用手法：瞬时置顶再取消
                    Topmost = true;
                    Topmost = false;
                    Activate();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[验证窗口] 置前失败: {ex.Message}"); }

            App.LogCrash($"[验证窗口] 显示位置=({Left:F0},{Top:F0}) 尺寸={ActualWidth:F0}x{ActualHeight:F0} " +
                         $"visible={IsVisible} active={IsActive} skipNavigate={_skipNavigate}");
        }

        /// <summary>
        /// 1.5 秒后自检一次：窗口还在但不激活 / 跑到屏幕外 → 纠正一次并记日志。
        /// 这是给"看不见的窗口"上的第二道保险（第一道是非模态，主界面不会再被锁）。
        /// </summary>
        private void CheckAttention()
        {
            _attentionTimer.Stop();
            if (_attentionChecked || _closing || !IsVisible) return;
            _attentionChecked = true;

            try
            {
                Rect area = GetWorkArea();
                var self = new Rect(Left, Top, ActualWidth, ActualHeight);
                bool offScreen = !area.IntersectsWith(self) || self.Width < 80 || self.Height < 80;

                if (offScreen)
                {
                    App.LogCrash($"[验证窗口] 疑似在屏幕外（rect={self}），重新定位");
                    PlaceOnScreen();
                }

                if (!IsActive)
                {
                    App.LogCrash("[验证窗口] 1.5 秒后仍未激活，重新置前");
                    BringToFront();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[验证窗口] 自检失败: {ex.Message}");
            }
        }

        #endregion

        #region 生命周期

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                App.LogCrash("[验证窗口] Loaded，开始初始化内核并切挂");

                await _svc.InitAsync();

                // 把同一内核切挂到本窗口的内容区（顶栏/底栏在矩形之外，不会被子窗口遮挡）
                _svc.ShowIn(this, contentHost);
                txtPlaceholder.Visibility = Visibility.Collapsed;
                txtPlaceholder.Text = "";

                // 布局刚完成时内容区的实际尺寸可能还差一两帧，补一次精确对齐
                _ = Dispatcher.BeginInvoke(new Action(SafeFitBounds), DispatcherPriority.Loaded);
                await Task.Delay(120);
                SafeFitBounds();

                App.LogCrash("[验证窗口] 内核已切挂到窗口内容区");

                // 轮询先开起来（超时计时真实可信），导航在后台继续
                UpdateStatus();
                _pollTimer.Start();
                _attentionTimer.Start();

                if (!_skipNavigate)
                {
                    // 挑战页常常长时间不触发 NavigationCompleted（页面自己在轮询），
                    // 所以这里给一个宽松超时：导航"没完成"不代表失败，继续让用户操作即可。
                    _navCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    try
                    {
                        await _svc.NavigateAsync(_targetUrl, _navCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("[验证窗口] 导航等待超时，继续等待用户操作");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[验证窗口] 导航失败: {ex.Message}");
                        App.LogCrash("[验证窗口] 导航失败: " + ex.Message);
                        if (!_closing) txtStatus.Text = "页面加载失败，可点「我已完成验证」重试检查";
                    }
                }
            }
            catch (Exception ex)
            {
                // 这里绝不能用 MessageBox：那是又一个会挡在前面的模态窗，
                // 一旦它跑到后台，用户就彻底点不到任何东西了。
                Debug.WriteLine($"[验证窗口] 初始化失败: {ex}");
                App.LogCrash("[验证窗口] 初始化失败: " + ex);

                Result = CaptchaResult.Failed;
                txtPlaceholder.Text = "验证页面初始化失败：" + ex.Message;
                txtPlaceholder.Visibility = Visibility.Visible;
                txtStatus.Text = "初始化失败，可关闭窗口后重试";
                dotState.Fill = System.Windows.Media.Brushes.OrangeRed;

                // 让失败状态停留 1.2 秒再自动关窗，避免用户还没看清就消失
                await Task.Delay(1200);
                Finish(CaptchaResult.Failed);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            _pollTimer.Stop();
            _attentionTimer.Stop();
            _navCts?.Cancel();

            // 必须在窗口销毁前把内核摘回隐藏宿主，否则 controller 的父句柄失效会崩。
            // 但若此刻仍有内核调用在飞行中（例如刚点「我已完成验证」、脚本还没返回就关窗），
            // 直接改挂 ParentWindow 会让内核挂死 → 之后所有在线操作永远等不到结果、程序像卡死。
            // 处理：先等它最多 2 秒；还不结束就整体释放内核（下次搜索自动重建）。
            try
            {
                for (int i = 0; i < 20 && _svc.ActiveOperationCount > 0; i++)
                {
                    System.Threading.Thread.Sleep(100);
                }

                if (_svc.ActiveOperationCount > 0)
                {
                    Debug.WriteLine("[验证窗口] 关闭时仍有内核调用未结束，改用力释放内核");
                    App.LogCrash("[验证窗口] 关闭时内核仍有调用在飞，强制释放内核");
                    _svc.ForceDisposeCore();
                }
                else
                {
                    _svc.HideBack();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[验证窗口] 归还内核失败: {ex.Message}"); }

            App.LogCrash($"[验证窗口] 关闭，结果={Result}");
            base.OnClosing(e);
        }

        private void SafeFitBounds()
        {
            try { _svc.FitBoundsTo(this, contentHost); }
            catch (Exception ex) { Debug.WriteLine($"[验证窗口] 同步内核尺寸失败: {ex.Message}"); }
        }

        #endregion

        #region 通过检测

        private async Task PollOnceAsync()
        {
            if (_checking || _closing) return;
            _checking = true;
            try
            {
                _remaining--;
                UpdateStatus();

                // 看门狗：单轮检测最多等 4 秒。
                // 若内核/串行闸门卡住（页面僵死、脚本调用不返回），不能让 _checking 永久为 true——
                // 否则后续每秒的轮询全部变成空转，窗口就再也不会自动关闭了。
                var check = IsPassedAsync();
                var finished = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(4)));
                if (!ReferenceEquals(finished, check))
                {
                    Debug.WriteLine("[验证窗口] 本轮检测 4 秒未返回（内核疑似无响应），跳过本轮");
                    if (!_closing) txtStatus.Text = "内核响应较慢，正在重试…";
                    return;
                }

                if (await check)
                {
                    Finish(CaptchaResult.Passed);
                    return;
                }

                if (_remaining <= 0)
                {
                    Finish(CaptchaResult.Timeout);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[验证窗口] 轮询异常: {ex.Message}");
            }
            finally
            {
                _checking = false;
            }
        }

        /// <summary>是否已通过：优先用调用方给的精确判据，再退回通用判据。</summary>
        private async Task<bool> IsPassedAsync()
        {
            if (_extraVerify != null)
            {
                try
                {
                    if (await _extraVerify()) return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[验证窗口] 自定义判据失败: {ex.Message}");
                }
            }
            return await _svc.IsCaptchaPassedAsync(_targetUrl);
        }

        private void Finish(CaptchaResult result)
        {
            if (_closing) return;
            Result = result;
            Close();
        }

        private void UpdateStatus()
        {
            if (_closing) return;

            bool expired = _remaining <= 0;
            txtStatus.Text = expired
                ? "验证超时，正在关闭…"
                : $"等待验证结果…  剩余 {Math.Max(0, _remaining)} 秒";
        }

        #endregion

        #region 交互

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { /* 拖动中断，忽略 */ }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Finish(CaptchaResult.Cancelled);
                e.Handled = true;
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Finish(CaptchaResult.Cancelled);

        private void btnCancel_Click(object sender, RoutedEventArgs e) => Finish(CaptchaResult.Cancelled);

        /// <summary>手动兜底：有些站点无法自动判断，让用户自己说"我过了"。</summary>
        private async void btnManualPass_Click(object sender, RoutedEventArgs e)
        {
            btnManualPass.IsEnabled = false;
            txtStatus.Text = "正在检查验证结果…";
            try
            {
                // 同样加看门狗：检测卡住时不能让按钮永久禁用
                var check = IsPassedAsync();
                var finished = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(5)));
                if (!ReferenceEquals(finished, check))
                {
                    txtStatus.Text = "检查超时（内核无响应），可关闭窗口后重新搜索";
                    return;
                }

                if (await check)
                {
                    Finish(CaptchaResult.Passed);
                    return;
                }
                txtStatus.Text = "还没检测到通过，请确认页面上的验证已经完成";
                dotState.Fill = System.Windows.Media.Brushes.OrangeRed;
            }
            catch (Exception ex)
            {
                txtStatus.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                btnManualPass.IsEnabled = true;
            }
        }

        #endregion
    }
}
