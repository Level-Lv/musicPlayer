using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// 设置项自检（步骤清单 S10）。纯逻辑：不碰网络、不碰 WebView2 内核、不碰 UI。
    ///
    /// 覆盖：
    /// 1. 默认下载目录的取值（我的音乐\MusicPlayer\在线下载）；
    /// 2. online_download_dir：未设置回落默认 / 设置后生效且自动建目录 /
    ///    目录不可用（指向一个文件）时回落默认且不抛异常 / 清空后回落默认；
    /// 3. online_idle_recycle：写 0/1 读回一致（隐藏内核空闲计时每秒现读该值）；
    /// 4. 现场还原：测试前后保留用户原设置，绝不留脏数据。
    ///
    /// 界面（设置窗口的「在线下载」卡片、按钮交互）无自动化入口，人工按验收清单点一遍。
    /// </summary>
    public static class SettingsSelfTest
    {
        private static string TestDir => Path.Combine(Path.GetTempPath(), "musicplayer_settingstest");

        /// <summary>跑自检，逐行回调日志。返回 true = 全部通过。</summary>
        public static Task<bool> RunAsync(Action<string> log, CancellationToken ct = default)
        {
            int pass = 0, fail = 0;
            void Ok(string msg) { pass++; log($"  ✓ {msg}"); }
            void Bad(string msg) { fail++; log($"  ✗ {msg}"); }
            void Check(string name, bool ok, string detail = "")
            {
                if (ok) Ok(name + (detail.Length > 0 ? $"：{detail}" : ""));
                else Bad(name + (detail.Length > 0 ? $"（实际：{detail}）" : ""));
            }

            // ══ 现场保存：测试可能改用户真实的设置，结束前必须还原 ══
            string savedDownloadDir = Db.GetSetting("online_download_dir", "");
            string savedIdleRecycle = Db.GetSetting("online_idle_recycle", "");
            log($"  原设置：online_download_dir=\"{savedDownloadDir}\" online_idle_recycle=\"{savedIdleRecycle}\"");

            try
            {
                log("── 默认下载目录 ──");

                string expectedDefault = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    "MusicPlayer", "在线下载");
                Check("默认目录 = 我的音乐\\MusicPlayer\\在线下载",
                    DownloadService.GetDefaultDownloadDirectory() == expectedDefault,
                    DownloadService.GetDefaultDownloadDirectory());

                // 模拟"清空设置"状态（这正是恢复默认按钮写入的值）
                Db.SetSetting("online_download_dir", "");
                Check("未设置时回落默认目录",
                    DownloadService.GetDownloadDirectory() == expectedDefault,
                    DownloadService.GetDownloadDirectory());

                log("── online_download_dir ──");

                // 设置一个不存在的目录 → 返回它并自动创建（U 盘场景：盘在、目录没建）
                string customDir = Path.Combine(TestDir, "自定义下载", "子目录");
                if (Directory.Exists(TestDir)) Directory.Delete(TestDir, recursive: true);
                Db.SetSetting("online_download_dir", customDir);
                Check("设置后立即生效", DownloadService.GetDownloadDirectory() == customDir,
                    DownloadService.GetDownloadDirectory());
                Check("目录不存在时自动创建", Directory.Exists(customDir));

                // 目录指向一个"文件"（创建必失败）→ 回落默认且不抛异常
                string blockedPath = Path.Combine(TestDir, "这是个文件不是目录");
                File.WriteAllText(blockedPath, "x");
                Db.SetSetting("online_download_dir", blockedPath);
                Check("目录不可用（是文件）时回落默认且不抛异常",
                    DownloadService.GetDownloadDirectory() == expectedDefault,
                    DownloadService.GetDownloadDirectory());

                // BuildTargetPath 走的也是同一目录解析：目标应落在默认目录而不是坏路径
                var item = new DownloadItem
                {
                    Result = new OnlineResult { Title = "设置自检曲", Artist = "自检艺人", CanDownload = true },
                    AudioUrl = "https://x.example/a.mp3",
                    IsStream = false
                };
                string target = DownloadService.BuildTargetPath(item);
                Check("落盘路径跟随目录解析（坏设置不产生坏路径）",
                    target.StartsWith(expectedDefault, StringComparison.OrdinalIgnoreCase),
                    target);

                // 清空（恢复默认按钮的行为）
                Db.SetSetting("online_download_dir", "");
                Check("清空后回落默认目录",
                    DownloadService.GetDownloadDirectory() == expectedDefault,
                    DownloadService.GetDownloadDirectory());

                if (Directory.Exists(TestDir)) Directory.Delete(TestDir, recursive: true);

                log("── online_idle_recycle ──");

                Db.SetSetting("online_idle_recycle", "0");
                Check("关闭回收（0）读回一致", Db.GetSetting("online_idle_recycle", "1") == "0");
                Db.SetSetting("online_idle_recycle", "1");
                Check("开启回收（1）读回一致", Db.GetSetting("online_idle_recycle", "1") == "1");

                log($"\n结果：{pass} 通过 / {fail} 失败");
                return Task.FromResult(fail == 0);
            }
            catch (Exception ex)
            {
                fail++;
                log($"\n✗ 自检异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                log($"\n结果：{pass} 通过 / {fail} 失败");
                return Task.FromResult(false);
            }
            finally
            {
                // ══ 现场还原（异常路径也必须还原）══
                try
                {
                    Db.SetSetting("online_download_dir", savedDownloadDir);
                    if (savedIdleRecycle.Length > 0)
                        Db.SetSetting("online_idle_recycle", savedIdleRecycle);
                    else
                        Db.SetSetting("online_idle_recycle", "1"); // 无原值时写默认开启
                }
                catch { /* 还原失败不能让进程崩 */ }
            }
        }
    }
}
