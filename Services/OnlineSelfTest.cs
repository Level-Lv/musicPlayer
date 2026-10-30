using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace musicPlayer.Services
{
    /// <summary>
    /// 在线功能自检（保留，长期可用）。
    ///
    /// 用途：
    /// 1. 每一步开发完成后回归验证基础能力是否还在（导航/JS/拦截/Cookie/进程回收）；
    /// 2. 现场排查"在线搜不到/内核没退出"这类问题——看日志就知道是运行时缺失、导航失败还是回收没生效。
    ///
    /// 入口：设置 → 在线功能诊断 → 运行内核自检（也可由代码直接调用 RunCoreSelfTestAsync）。
    /// </summary>
    public static class OnlineSelfTest
    {
        /// <summary>测试目标页（极轻量、无广告、可离线缓存）。</summary>
        private const string TestUrl = "https://example.com/";

        /// <summary>
        /// 跑一遍内核基础自检，逐行回调日志。
        /// 返回 true 表示全部通过。**不会**主动释放内核——留着让调用方观察空闲回收。
        /// </summary>
        public static async Task<bool> RunCoreSelfTestAsync(Action<string> log,
            CancellationToken ct = default)
        {
            int pass = 0, fail = 0;

            void Ok(string msg) { pass++; log($"  ✓ {msg}"); }
            void Bad(string msg) { fail++; log($"  ✗ {msg}"); }

            log("── 环境 ──");

            string? runtime = await OnlineEnvironment.GetWebView2VersionAsync();
            if (runtime != null) Ok($"WebView2 Runtime {runtime}");
            else Bad("WebView2 Runtime 缺失（在线功能不可用，需安装 Edge WebView2 Runtime）");

            if (OnlineEnvironment.FfmpegAvailable)
            {
                Ok($"ffmpeg {GetFfmpegVersion()}");
            }
            else
            {
                Bad("ffmpeg.exe 缺失（HLS/DASH 下载不可用）");
            }

            if (runtime == null)
            {
                log($"\n结果：{pass} 通过 / {fail} 失败（运行时缺失，后续跳过）");
                return false;
            }

            var svc = HiddenBrowserService.Instance;

            try
            {
                log("\n── 内核 ──");
                var sw = Stopwatch.StartNew();
                await svc.InitAsync();
                sw.Stop();
                Ok($"内核初始化 {sw.ElapsedMilliseconds} ms");

                var (procsAfterInit, memAfterInit) = HiddenBrowserService.GetBrowserProcessStats();
                Ok($"内核进程 {procsAfterInit} 个 / {memAfterInit} MB（属本程序的后代进程）");

                // 导航
                sw.Restart();
                string finalUrl = await svc.NavigateAsync(TestUrl, ct);
                sw.Stop();
                Ok($"导航 {TestUrl} → {finalUrl}（{sw.ElapsedMilliseconds} ms）");

                // JS
                string title = await svc.ExecuteScriptAsync("document.title", ct);
                if (title.Contains("Example", StringComparison.OrdinalIgnoreCase))
                    Ok($"执行 JS 取标题 {title}");
                else
                    Bad($"执行 JS 结果异常：{title}");

                // 弹窗拦截
                svc.ResetBlockedNewWindowCount();
                await svc.ExecuteScriptAsync("window.open('https://example.com/blocked','_blank')", ct);
                if (svc.BlockedNewWindowCount > 0) Ok("window.open 被拦截");
                else Bad("window.open 未被拦截（可能被浏览器自身策略拦下，非致命）");

                // Cookie
                var cookies = await svc.GetCookiesAsync("https://example.com", ct);
                Ok($"Cookie 读取 {cookies.Count} 条");

                log("\n── 内存 ──");
                long ownMb = HiddenBrowserService.GetOwnWorkingSetMb();
                Ok($"主进程 {ownMb} MB，内核 {HiddenBrowserService.GetBrowserProcessStats().TotalMb} MB");

                log($"\n结果：{pass} 通过 / {fail} 失败");
                log("提示：关闭本窗口后不要操作在线功能，等待 60 秒再看上面的「内核进程」，" +
                    "归零即说明空闲回收生效。");
                return fail == 0;
            }
            catch (OperationCanceledException)
            {
                log("\n已取消");
                return false;
            }
            catch (Exception ex)
            {
                fail++;
                log($"\n✗ 自检异常：{ex.GetType().Name} {ex.Message}");
                log($"\n结果：{pass} 通过 / {fail} 失败");
                return false;
            }
        }

        /// <summary>
        /// 取 ffmpeg 版本：ffmpeg.exe 通常不带文件版本资源，得跑一次 `-version` 读首行
        /// （只在诊断时执行一次，开销可忽略）。
        /// </summary>
        private static string GetFfmpegVersion()
        {
            string exe = OnlineEnvironment.FfmpegExePath;
            try
            {
                var psi = new ProcessStartInfo(exe, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "（无法启动）";
                string? first = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);

                if (!string.IsNullOrWhiteSpace(first))
                {
                    // 形如 "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) ..."
                    int idx = first.IndexOf("version", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string ver = first[(idx + 7)..].Trim();
                        int space = ver.IndexOf(' ');
                        if (space > 0) ver = ver[..space];
                        return ver;
                    }
                    return first;
                }
            }
            catch (Exception ex)
            {
                return $"（读取失败：{ex.Message}）";
            }
            return "（版本未知）";
        }
    }
}
