using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    /// </summary>
    public partial class CaptchaWindow : Window
    {
        private const int TimeoutSeconds = 120;

        private readonly HiddenBrowserService _svc = HiddenBrowserService.Instance;
        private readonly string _targetUrl;
        private readonly Func<Task<bool>>? _extraVerify;
        private readonly bool _skipNavigate;
        private readonly DispatcherTimer _pollTimer;

        private int _remaining = TimeoutSeconds;
        private bool _checking;
        private bool _closing;
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

            Loaded += OnLoaded;
            SizeChanged += (_, _) => SafeFitBounds();
            DpiChanged += (_, _) => SafeFitBounds();
            SourceInitialized += (_, _) => WindowEffects.ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 打开验证窗口（模态）并返回是否通过。
        /// 注意：内部会占住隐藏内核（串行闸门），期间不要并发发起别的在线任务。
        /// </summary>
        public static CaptchaResult Run(Window? owner, string targetUrl,
            Func<Task<bool>>? extraVerify = null, bool skipNavigate = false)
        {
            var win = new CaptchaWindow(targetUrl, extraVerify, skipNavigate);
            try
            {
                if (owner != null && owner.IsVisible) win.Owner = owner;
            }
            catch { /* Owner 设置失败不影响功能 */ }

            win.ShowDialog();
            return win.Result;
        }

        #region 生命周期

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _svc.InitAsync();

                // 把同一内核切挂到本窗口的内容区（顶栏/底栏在矩形之外，不会被子窗口遮挡）
                _svc.ShowIn(this, contentHost);
                txtPlaceholder.Visibility = Visibility.Collapsed;

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
                        txtStatus.Text = "页面加载失败，可点「我已完成验证」重试检查";
                    }
                }

                UpdateStatus();
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[验证窗口] 初始化失败: {ex}");
                Result = CaptchaResult.Failed;
                _closing = true;
                MessageBox.Show(this, "验证窗口初始化失败：" + ex.Message, "错误");
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            _pollTimer.Stop();
            _navCts?.Cancel();

            // 必须在窗口销毁前把内核摘回隐藏宿主，否则 controller 的父句柄失效会崩
            try { _svc.HideBack(); }
            catch (Exception ex) { Debug.WriteLine($"[验证窗口] 归还内核失败: {ex.Message}"); }

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

                if (await IsPassedAsync())
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
                if (await IsPassedAsync())
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
