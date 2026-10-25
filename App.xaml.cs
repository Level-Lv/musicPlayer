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

        protected override void OnStartup(StartupEventArgs e)
        {
            instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

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
                    // 确实有另一个实例在运行 → 唤醒它，然后自己退出
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
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
            activateEvent = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: ActivateEventName);
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
