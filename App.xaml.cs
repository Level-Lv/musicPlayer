using System;
using System.Threading;
using System.Windows;

namespace musicPlayer
{
    /// <summary>
    /// 应用入口 + 单例控制：
    /// 用命名 Mutex 保证同一时刻只有一个播放器实例；
    /// 第二个实例启动时通过命名 EventWaitHandle 通知第一实例“唤醒自己”，然后立即退出。
    /// 前缀用 Local\（会话级），同一 Windows 用户会话内生效，权限要求最低。
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Local\\musicPlayer_SingleInstance";
        private const string ActivateEventName = "Local\\musicPlayer_SingleInstance_Activate";

        private Mutex? instanceMutex;
        private EventWaitHandle? activateEvent;

        /// <summary>
        /// 本次启动是否是"测试运行"（设置了任一 MP_ONLINE_* 自检入口）。
        ///
        /// 测试运行用**独立的单实例名 + 独立 WebView2 数据目录**。原因很实际：
        /// 一旦出现内核挂死的僵尸实例（taskkill /F 都"拒绝访问"），它会一直占着单实例 Mutex 和
        /// Chromium profile，让之后所有测试启动全部无效（环境变量不生效、日志不更新）。
        /// 隔离后测试至少能跑；正常使用时的单实例行为完全不变。
        /// </summary>
        private static bool IsTestRun =>
            Environment.GetEnvironmentVariable("MP_ONLINE_UI_TEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_MANAGER_TEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_WIZARD_TEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_SEARCH_TEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_CAPTCHA_TEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_SELFTEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MP_ONLINE_DIAG") is { Length: > 0 };

        private static string EffectiveMutexName => IsTestRun ? MutexName + "_test" : MutexName;

        private static string EffectiveActivateEventName =>
            IsTestRun ? ActivateEventName + "_test" : ActivateEventName;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常日志：任何未处理异常都落到 %TEMP%\musicplayer_crash.log，便于事后诊断
            //（TODO: S10 收尾时决定是否保留/移到正式日志目录）
            LogCrash("===== 启动 =====");

            // 测试入口互斥：同一个 PowerShell 会话里环境变量会一直留着，
            // 若同时开了多套测试，它们会抢同一个 WebView2 内核（互相顶掉页面、验证窗口卡住），
            // 表现就是程序像"卡死"。这里按优先级只保留一个，其余清掉并记日志。
            EnforceSingleTestEntry();

            // 测试运行使用独立的 WebView2 数据目录（原因见 IsTestRun 注释）
            if (IsTestRun && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MP_WEBVIEW_USERDATA")))
            {
                Environment.SetEnvironmentVariable("MP_WEBVIEW_USERDATA",
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musicplayer_webview_test"));
            }

            DispatcherUnhandledException += (_, args) =>
            {
                LogCrash("UI线程异常: " + args.Exception);
                args.Handled = true; // 开发期先兜住不让程序消失，便于排查
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogCrash("非UI线程异常: " + args.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogCrash("未观察Task异常: " + args.Exception);
                args.SetObserved();
            };

            instanceMutex = new Mutex(initiallyOwned: true, EffectiveMutexName, out bool createdNew);

            if (!createdNew)
            {
                // 已存在实例。但有一种例外：上次实例崩溃，Mutex 被废弃（AbandonedMutex），
                // 此时第二次 WaitOne 能直接接管所有权，可以继续正常启动。
                bool tookOver = false;
                try
                {
                    tookOver = instanceMutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    tookOver = true; // 上个实例异常退出，Mutex 归我们接管
                }

                if (!tookOver)
                {
                    // 已有实例在跑 → 本次启动只负责唤醒它。
                    // 记一笔日志：测试时若前一个实例没关干净，环境变量会对这个"新启动"完全无效，
                    // 表现就是"命令跑了但什么都没发生、日志也不更新"，很容易误判成程序卡死。
                    LogCrash("已有实例在运行，本次启动被忽略（环境变量不会生效）");

                    // 确实有另一个实例在运行 → 唤醒它，然后自己退出
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(EffectiveActivateEventName, out var existing))
                        {
                            existing.Set();
                            existing.Dispose();
                        }
                    }
                    catch
                    {
                        // 通知失败也无所谓——用户再点一次图标即可，绝不能因通知失败而崩
                    }
                    Shutdown();
                    return;
                }
            }

            // 我们是正式实例：创建“唤醒事件”，监听后续第二实例的请求
            activateEvent = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: EffectiveActivateEventName);
            var listener = new Thread(ListenForActivateRequests)
            {
                IsBackground = true, // 不阻塞进程退出
                Name = "SingleInstanceActivateListener"
            };
            listener.SetApartmentState(ApartmentState.STA);
            listener.Start();

            base.OnStartup(e);
            var main = new MainWindow();
            Current.MainWindow = main; // StartupUri 已移除，手动指定主窗口
            main.Show();
        }

        /// <summary>
        /// 测试入口互斥（保留的测试代码，S4 加）：
        /// 同一个 PowerShell 会话里 `$env:XXX` 会一直保留，用户连着测 S3/S4 时很容易两个都在，
        /// 两套测试会抢同一个 WebView2 内核 → 页面互相顶掉、验证窗口卡住不关、主窗口被模态窗锁死。
        /// 这里按优先级只保留一个测试入口，其余清掉并写入 crash 日志，便于事后确认。
        /// </summary>
        private static void EnforceSingleTestEntry()
        {
            // 优先级从高到低（同时开多个时，前面那个生效）
            string[] entries =
            {
                "MP_ONLINE_UI_TEST",       // S6 在线视图界面
                "MP_ONLINE_MANAGER_TEST",  // S7 站点面板
                "MP_ONLINE_WIZARD_TEST",   // S5 添加站点向导
                "MP_ONLINE_SEARCH_TEST",   // S4 搜索引擎
                "MP_ONLINE_CAPTCHA_TEST",  // S3 验证窗口
                "MP_ONLINE_SELFTEST",      // S2 内核自检
                "MP_ONLINE_DIAG"           // 诊断窗口
            };

            string? winner = null;
            foreach (string name in entries)
            {
                string value = Environment.GetEnvironmentVariable(name) ?? "";
                if (value.Length == 0) continue;

                if (winner == null)
                {
                    winner = $"{name}={value}";
                    continue;
                }

                // 已经在跑另一个测试入口 → 清掉这个，避免两套抢内核
                Environment.SetEnvironmentVariable(name, null);
                LogCrash($"测试入口互斥：保留 {winner}，已忽略 {name}={value}");
            }
        }

        /// <summary>
        /// 把诊断文本追加到 %TEMP%\musicplayer_crash.log。
        /// 之所以公开：验证窗口（CaptchaWindow）等"整个程序被模态锁死"的高风险环节
        /// 需要在关键节点留痕，出问题时能直接看出窗口有没有真的显示出来、位置在哪。
        /// </summary>
        public static void LogCrash(string message)
        {
            try
            {
                string file = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "musicplayer_crash.log");
                System.IO.File.AppendAllText(file,
                    $"\r\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
            }
            catch
            {
                // 日志失败不能影响运行
            }
        }

        /// <summary>收到第二实例的唤醒请求：恢复主窗口并置于前台。</summary>
        private void ListenForActivateRequests()
        {
            while (activateEvent!.WaitOne())
            {
                Dispatcher.Invoke(RestoreMainWindow);
            }
        }

        private void RestoreMainWindow()
        {
            var win = Current.MainWindow;
            if (win == null) return;

            if (win.WindowState == WindowState.Minimized)
                win.WindowState = WindowState.Normal; // 最小化先还原
            win.Show();
            win.Activate();
            // Topmost 瞬间置顶再取消：绕过系统的前台权限限制，确保窗口真正被带到最前
            win.Topmost = true;
            win.Topmost = false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            activateEvent?.Dispose();
            try
            {
                instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 线程不拥有 Mutex 时的保护，忽略
            }
            instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
