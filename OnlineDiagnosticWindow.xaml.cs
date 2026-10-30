using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>
    /// 在线功能诊断窗口（保留的测试入口，S2 之后每一步都能用它回归）。
    /// 显示隐藏内核的实时状态（空闲倒计时、进程数、内存），并提供一键自检。
    /// </summary>
    public partial class OnlineDiagnosticWindow : Window
    {
        private readonly DispatcherTimer _refreshTimer;
        private CancellationTokenSource? _selfTestCts;
        private bool _busy;

        public OnlineDiagnosticWindow()
        {
            InitializeComponent();

            // 每秒刷新一次动态指标——用户能亲眼看到倒计时走到 0、内核进程数归零
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (_, _) => RefreshStats();
            _refreshTimer.Start();

            Loaded += (_, _) => RefreshStats();
            Closed += (_, _) =>
            {
                _refreshTimer.Stop();
                _selfTestCts?.Cancel();
            };
        }

        /// <summary>打开诊断窗口（模态，挂在主窗口上）。</summary>
        public static void ShowDialog(Window owner)
        {
            new OnlineDiagnosticWindow { Owner = owner }.ShowDialog();
        }

        #region 实时状态

        private void RefreshStats()
        {
            var svc = HiddenBrowserService.Instance;

            txtRuntime.Text = OnlineEnvironment.WebView2Available
                ? (RuntimeVersionText ?? "可用")
                : "缺失";

            txtFfmpeg.Text = OnlineEnvironment.FfmpegAvailable ? "就绪" : "缺失";

            if (svc.IsInitialized)
            {
                txtKernelState.Text = "运行中";
                txtKernelState.Foreground = System.Windows.Media.Brushes.LightGreen;
                txtIdle.Text = $"{svc.IdleRemainingSeconds} 秒后回收";
                txtIdle.Foreground = System.Windows.Media.Brushes.Gold;
            }
            else
            {
                txtKernelState.Text = "已释放";
                txtKernelState.Foreground = System.Windows.Media.Brushes.Gray;
                txtIdle.Text = "—（无内核）";
                txtIdle.Foreground = System.Windows.Media.Brushes.Gray;
            }

            var (count, mb) = HiddenBrowserService.GetBrowserProcessStats();
            txtKernelProcs.Text = $"{count} 个 · {mb} MB";
            txtKernelProcs.Foreground = count > 0
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Gray;

            txtOwnMemory.Text = $"{HiddenBrowserService.GetOwnWorkingSetMb()} MB";
            txtUserData.Text = HiddenBrowserService.UserDataFolder;
        }

        private string? RuntimeVersionText { get; set; }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            RuntimeVersionText = await OnlineEnvironment.GetWebView2VersionAsync();
            RefreshStats();
        }

        #endregion

        #region 按钮

        private async void btnSelfTest_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            btnSelfTest.IsEnabled = false;
            btnRecycle.IsEnabled = false;
            txtLog.Text = "";

            _selfTestCts = new CancellationTokenSource();
            try
            {
                await OnlineSelfTest.RunCoreSelfTestAsync(AppendLog, _selfTestCts.Token);
            }
            finally
            {
                _busy = false;
                btnSelfTest.IsEnabled = true;
                btnRecycle.IsEnabled = true;
                _selfTestCts.Dispose();
                _selfTestCts = null;
                RefreshStats();
            }
        }

        private void btnRecycle_Click(object sender, RoutedEventArgs e)
        {
            HiddenBrowserService.Instance.ForceRecycle();
            AppendLog("已请求立即释放内核（浏览器进程应在 1~3 秒内退出）");
            RefreshStats();
        }

        private void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            string text =
                $"WebView2: {txtRuntime.Text}\n" +
                $"ffmpeg: {txtFfmpeg.Text}\n" +
                $"内核状态: {txtKernelState.Text}\n" +
                $"空闲: {txtIdle.Text}\n" +
                $"内核进程: {txtKernelProcs.Text}\n" +
                $"主进程内存: {txtOwnMemory.Text}\n" +
                $"用户数据: {txtUserData.Text}\n\n" +
                txtLog.Text;

            try
            {
                Clipboard.SetText(text);
                AppendLog("（结果已复制到剪贴板）");
            }
            catch (Exception ex)
            {
                AppendLog($"（复制失败：{ex.Message}）");
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        #endregion

        /// <summary>追加一行日志（自动滚动到底部）。可能由后台线程回调，统一转到 UI 线程。</summary>
        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(line));
                return;
            }
            txtLog.Text += line + "\n";
            logScroll.ScrollToEnd();
        }
    }
}
