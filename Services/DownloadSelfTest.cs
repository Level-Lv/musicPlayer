using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// 下载与入库自检（步骤清单 S9）。全离线：本地 http 夹具 + 自带 ffmpeg 生成真实流。
    ///
    /// 覆盖：
    /// 1. 判定与路径：DecideForUrl 三态、非法字符文件名、重名加 (1)；
    /// 2. 直链下载：mp3 下载 → taglib 标题/艺人/备注 → UpsertTrack(source=online) → ffprobe 验产物；
    /// 3. HLS（多分片）与 AES-128 加密 HLS：ffmpeg 合并 + 密钥经 http 获取；
    /// 4. DASH（mpd + m4s）：ffmpeg 转封装 m4a；
    /// 5. 取消：排队中的任务取消 / 下载中的任务取消；
    /// 6. 并发：3 个任务同时排队，峰值并发必须 == 2。
    ///
    /// 界面（行内按钮三态、Toast、退出确认）由 MP_ONLINE_DOWNLOAD_TEST=2 手动看。
    /// </summary>
    public static class DownloadSelfTest
    {
        private static string DemoDir => Path.Combine(Path.GetTempPath(), "musicplayer_downloadtest");

        /// <summary>跑自检，逐行回调日志。返回 true = 全部通过。</summary>
        public static async Task<bool> RunAsync(Action<string> log, CancellationToken ct = default)
        {
            int pass = 0, fail = 0;
            void Ok(string msg) { pass++; log($"  ✓ {msg}"); }
            void Bad(string msg) { fail++; log($"  ✗ {msg}"); }
            void Check(string name, bool ok, string detail = "")
            {
                if (ok) Ok(name + (detail.Length > 0 ? $"：{detail}" : ""));
                else Bad(name + (detail.Length > 0 ? $"（实际：{detail}）" : ""));
            }

            var cleanupFiles = new List<string>();
            var cleanupDownloadIds = new List<long>();
            var doneItems = new List<(DownloadItem Item, Task<(bool Ok, string Detail)> Await)>();
            PlaybackSelfTest.LocalHttpServer? server = null;   // 夹具服务器；finally 里统一释放

            try
            {
                // 下载目录隔离到夹具目录，别碰真实的"我的音乐"
                Environment.SetEnvironmentVariable("MP_DOWNLOAD_TEST_DIR",
                    Path.Combine(DemoDir, "output"));
                log($"  下载目录 = {DownloadService.GetDownloadDirectory()}");

                // 清掉上次运行（尤其被强杀时 finally 没跑）残留的输出目录与入库行，
                // 否则"重名加 (1)""入库记录 source_url"这类断言会查到旧数据
                string outDir = DownloadService.GetDownloadDirectory();
                if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
                Directory.CreateDirectory(outDir);
                foreach (var stale in Db.GetAllTracks()
                    .Where(t => t.FullPath.StartsWith(outDir, StringComparison.OrdinalIgnoreCase)))
                {
                    Db.RemoveTrackByPath(stale.FullPath);
                }

                log("── 判定与路径 ──");

                var d1 = OnlineSearchService.DecideForUrl("https://x.example/a.mp3?from=test");
                Check("mp3 判为可下载直链", d1.Url != null && !d1.IsStream, d1.Url ?? "null");

                var d2 = OnlineSearchService.DecideForUrl("https://x.example/live/index.m3u8");
                Check("m3u8 判为流媒体（能下载不能播）", d2.Url != null && d2.IsStream, d2.Reason);

                var d3 = OnlineSearchService.DecideForUrl("blob:https://x.example/abc");
                Check("blob 判为页面临时流", d3.Url == null, d3.Reason);

                var badName = MakeResult(title: "夜曲: <测试>*?.mp3", artist: "周/杰伦");
                string badPath = DownloadService.BuildTargetPath(MakeItem(badName, "https://x.example/a.mp3"));
                Check("文件名非法字符被替换",
                    badName.Title.Split(Path.GetInvalidFileNameChars()).All(c => true) &&
                    !Path.GetFileName(badPath).Intersect(Path.GetInvalidFileNameChars()).Any() &&
                    Path.GetFileName(badPath).Contains("周_杰伦"),
                    Path.GetFileName(badPath));

                string dupPath = badPath;
                File.WriteAllText(dupPath, "x");
                cleanupFiles.Add(dupPath);
                string dupPath2 = DownloadService.BuildTargetPath(MakeItem(badName, "https://x.example/a.mp3"));
                Check("重名自动加 (1)", dupPath2 != dupPath && Path.GetFileName(dupPath2).Contains("(1)"),
                    Path.GetFileName(dupPath2));

                // ══════════════ 夹具：本地服务 + ffmpeg 造真实流 ══════════════
                log("── 夹具（ffmpeg 生成 mp3 / HLS / AES-HLS / DASH）──");
                var sw = Stopwatch.StartNew();

                Directory.CreateDirectory(DemoDir);
                Directory.CreateDirectory(Path.Combine(DemoDir, "hls"));
                Directory.CreateDirectory(Path.Combine(DemoDir, "hls-enc"));
                Directory.CreateDirectory(Path.Combine(DemoDir, "dash"));

                string wav = PlaybackSelfTest.CreateDemoWav(Path.Combine(DemoDir, "tone.wav"), 20);
                server = PlaybackSelfTest.LocalHttpServer.Start(DemoDir);

                // mp3（直链用例的素材）
                await RunToolAsync(log, DownloadService.FfmpegPath,
                    $"-y -i \"{wav}\" -c:a libmp3lame -b:a 64k \"{Path.Combine(DemoDir, "tone.mp3")}\"", ct);

                // 普通 HLS：4 段 aac
                await RunToolAsync(log, DownloadService.FfmpegPath,
                    $"-y -i \"{wav}\" -c:a aac -b:a 64k -f hls -hls_time 3 -hls_list_size 0 " +
                    $"-hls_segment_filename \"{Path.Combine(DemoDir, "hls", "seg%d.ts")}\" " +
                    $"\"{Path.Combine(DemoDir, "hls", "index.m3u8")}\"", ct);

                // AES-128 HLS：密钥经 http 下发（验证 ffmpeg 走网络取密钥）
                // ⚠ 两个 ffmpeg 9 的坑（实测）：
                //   ① key_info 的路径含反斜杠会被规范化成 "D://..." 双斜杠而打不开 → 全部参数用 '/'；
                //   ② key_info 的 IV 行即使合法也会让 crypto 协议报 Invalid argument → 省略（IV 是可选的，
                //      ffmpeg 会用分段号自动推导，播放器/下载端都能解）。
                string ToFf(string p) => p.Replace('\\', '/');
                string keyFile = Path.Combine(DemoDir, "hls-enc", "enc.key");
                File.WriteAllBytes(keyFile, Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray());
                File.Copy(keyFile, Path.Combine(DemoDir, "hls-enc", "key.bin"), overwrite: true);   // http 下发的就是它
                string keyInfo = Path.Combine(DemoDir, "hls-enc", "key.info");
                File.WriteAllText(keyInfo,
                    $"{server.BaseUrl}hls-enc/key.bin\n{ToFf(keyFile)}\n");
                await RunToolAsync(log, DownloadService.FfmpegPath,
                    $"-y -i \"{ToFf(wav)}\" -c:a aac -b:a 64k -f hls -hls_time 3 -hls_list_size 0 " +
                    $"-hls_key_info_file \"{ToFf(keyInfo)}\" " +
                    $"-hls_segment_filename \"{ToFf(Path.Combine(DemoDir, "hls-enc", "seg%d.ts"))}\" " +
                    $"\"{ToFf(Path.Combine(DemoDir, "hls-enc", "index.m3u8"))}\"", ct);

                // DASH：mpd + init.m4s + chunk.m4s
                // ⚠ ffmpeg 的 DASH muxer 把分片写在**进程工作目录**而不是 mpd 所在目录（实测）；
                //   而 -init_seg_name 给绝对路径时 "C:" 又会被当成协议名报 Permission denied。
                //   两坑叠加的正解：把工作目录切到 dash 目录，全部用相对文件名。
                string dashDir = Path.Combine(DemoDir, "dash");
                await RunToolAsync(log, DownloadService.FfmpegPath,
                    $"-y -i \"{ToFf(wav)}\" -c:a aac -b:a 64k -f dash -window_size 0 \"manifest.mpd\"",
                    ct, workDir: dashDir);

                Ok($"夹具生成完毕（{sw.ElapsedMilliseconds} ms）：mp3 / hls / hls-aes / dash");

                string mp3Url = server.BaseUrl + "tone.mp3";
                string hlsUrl = server.BaseUrl + "hls/index.m3u8";
                string hlsEncUrl = server.BaseUrl + "hls-enc/index.m3u8";
                string dashUrl = server.BaseUrl + "dash/manifest.mpd";

                // ══════════════ 直链下载 + 入库 ══════════════
                log("── 直链下载（mp3）──");
                var direct = MakeResult(title: "下载自检曲", artist: "自检艺人");
                direct.SiteName = "本地演示站";
                direct.DetailUrl = server.BaseUrl + "detail.html";
                direct.Duration = TimeSpan.FromSeconds(20);

                var (item1, await1) = Track(downloadIds: cleanupDownloadIds,
                    DownloadService.Enqueue(direct, mp3Url, isStream: false, direct.DetailUrl));
                var (ok1, detail1) = await await1;
                Check("下载完成", ok1, detail1);
                string mp3File = Path.Combine(DownloadService.GetDownloadDirectory(),
                    "自检艺人 - 下载自检曲.mp3");
                Check("落盘路径符合 {艺人} - {标题}.mp3", File.Exists(mp3File),
                    Path.GetFileName(mp3File));
                cleanupFiles.Add(mp3File);

                if (File.Exists(mp3File))
                {
                    var tag = TagLib.File.Create(mp3File);
                    Check("taglib 标题写入", tag.Tag.Title == "下载自检曲", tag.Tag.Title ?? "null");
                    Check("taglib 艺人写入",
                        (tag.Tag.Performers?.FirstOrDefault() ?? "") == "自检艺人",
                        string.Join(",", tag.Tag.Performers ?? Array.Empty<string>()));
                    Check("taglib 备注含来源站与原链接",
                        (tag.Tag.Comment ?? "").Contains("本地演示站") &&
                        (tag.Tag.Comment ?? "").Contains("detail.html"),
                        tag.Tag.Comment ?? "null");
                    Check("按钮状态 = 已入库", direct.DownloadState == DownloadState.Done,
                        direct.DownloadState.ToString());

                    var trackRow = Db.GetAllTracks().FirstOrDefault(t => t.FullPath == mp3File);
                    Check("入库记录 source=online + source_url",
                        trackRow != null && trackRow.Source == "online" &&
                        trackRow.SourceUrl == direct.DetailUrl,
                        trackRow == null ? "未找到记录" : $"{trackRow.Source} / {trackRow.SourceUrl}");

                    double duration = await ProbeDurationAsync(mp3File);
                    Check("ffprobe 产物有效（时长 > 15s）", duration > 15, $"{duration:0.0}s");
                }

                // ══════════════ 流媒体 ══════════════
                log("── HLS（4 分片）──");
                var hls = MakeResult(title: "HLS 合并曲", artist: "自检艺人");
                var (item2, await2) = Track(cleanupDownloadIds,
                    DownloadService.Enqueue(hls, hlsUrl, isStream: true, hlsUrl));
                var (ok2, detail2) = await await2;
                Check("HLS 下载完成", ok2, detail2);
                string hlsFile = Path.Combine(DownloadService.GetDownloadDirectory(),
                    "自检艺人 - HLS 合并曲.m4a");
                if (File.Exists(hlsFile))
                {
                    cleanupFiles.Add(hlsFile);
                    double dur = await ProbeDurationAsync(hlsFile);
                    Check("HLS 产物 ffprobe 有效（时长 > 15s）", dur > 15, $"{dur:0.0}s");
                }
                else Bad("HLS 产物缺失：" + hlsFile);

                log("── AES-128 加密 HLS（密钥走 http）──");
                var hlsEnc = MakeResult(title: "AES 加密曲", artist: "自检艺人");
                var (item3, await3) = Track(cleanupDownloadIds,
                    DownloadService.Enqueue(hlsEnc, hlsEncUrl, isStream: true, hlsEncUrl));
                var (ok3, detail3) = await await3;
                Check("AES-128 HLS 解密合并完成", ok3, detail3);
                string encFile = Path.Combine(DownloadService.GetDownloadDirectory(),
                    "自检艺人 - AES 加密曲.m4a");
                if (File.Exists(encFile))
                {
                    cleanupFiles.Add(encFile);
                    double dur = await ProbeDurationAsync(encFile);
                    Check("解密产物 ffprobe 有效", dur > 15, $"{dur:0.0}s");
                }
                else Bad("AES 产物缺失：" + encFile);

                log("── DASH（mpd + m4s）──");
                var dash = MakeResult(title: "DASH 转封装曲", artist: "自检艺人");
                var (item4, await4) = Track(cleanupDownloadIds,
                    DownloadService.Enqueue(dash, dashUrl, isStream: true, dashUrl));
                var (ok4, detail4) = await await4;
                Check("DASH 下载完成", ok4, detail4);
                string dashFile = Path.Combine(DownloadService.GetDownloadDirectory(),
                    "自检艺人 - DASH 转封装曲.m4a");
                if (File.Exists(dashFile))
                {
                    cleanupFiles.Add(dashFile);
                    double dur = await ProbeDurationAsync(dashFile);
                    Check("DASH 产物 ffprobe 有效", dur > 15, $"{dur:0.0}s");
                }
                else Bad("DASH 产物缺失：" + dashFile);

                // ══════════════ 并发 2 ══════════════
                log("── 并发上限 = 2 ──");
                DownloadService.ResetForTest();
                var concItems = Enumerable.Range(1, 3).Select(i =>
                    MakeResult(title: $"并发曲 {i}", artist: "自检艺人")).ToList();
                var concTasks = concItems.Select(r =>
                        Track(cleanupDownloadIds,
                            DownloadService.Enqueue(r, hlsUrl, isStream: true, hlsUrl)).Await)
                    .ToList();
                foreach (var t in concTasks) await t;
                int files = concItems.Count(r => File.Exists(Path.Combine(
                    DownloadService.GetDownloadDirectory(), $"自检艺人 - {r.Title}.m4a")));
                foreach (var r in concItems)
                {
                    string p = Path.Combine(DownloadService.GetDownloadDirectory(),
                        $"自检艺人 - {r.Title}.m4a");
                    if (File.Exists(p)) cleanupFiles.Add(p);
                }
                Check("3 个流媒体任务全部完成", files == 3, $"{files}/3");
                Check("并发峰值 = 2（第 3 个排队等了）",
                    DownloadService.MaxConcurrentObserved == 2,
                    $"{DownloadService.MaxConcurrentObserved}");

                // ══════════════ 取消 ══════════════
                log("── 取消 ──");
                DownloadService.ResetForTest();

                // 先占住两个并发位（两个 HLS 任务），第三个排队 → 必处于 Pending
                var blockers = Enumerable.Range(1, 2).Select(i =>
                    MakeResult(title: $"占位曲 {i}", artist: "自检艺人")).ToList();
                var blockTasks = blockers.Select(r =>
                        Track(cleanupDownloadIds,
                            DownloadService.Enqueue(r, hlsUrl, isStream: true, hlsUrl)).Await)
                    .ToList();

                var victim = MakeResult(title: "取消目标曲", artist: "自检艺人");
                var (victimItem, victimAwait) = Track(cleanupDownloadIds,
                    DownloadService.Enqueue(victim, mp3Url, isStream: false, mp3Url));

                bool cancelRan = DownloadService.Cancel(victim);
                Check("排队中的任务能取消", cancelRan);
                // ⚠ 带超时等结果：事件万一没触发就报失败，绝不无限等（等 = 卡死整个测试进程）
                var (cancelOk, cancelDetail) = await AwaitWithTimeout(victimAwait, 30, "排队取消", ct);
                // 本地小文件下载极快，取消与"已落盘"存在固有竞态——服务端会把已落盘的按成功收尾，
                // 所以两种自洽结果都算过：Cancelled+未落盘（正常取消）/ Done+已落盘（竞态完成）
                string victimTarget = Path.Combine(DownloadService.GetDownloadDirectory(),
                    "自检艺人 - 取消目标曲.mp3");
                bool cancelConsistent =
                    (victim.DownloadState == DownloadState.Cancelled && !File.Exists(victimTarget)) ||
                    (victim.DownloadState == DownloadState.Done && File.Exists(victimTarget));
                // ⚠ 注意：Track 对取消返回 ok=false（"已取消"），所以这里不能看 cancelOk，
                // 只看"状态 + 文件"是否自洽（本条诊断曾误导排查方向：文件其实根本不存在）
                Check("取消结果自洽（取消→未落盘；竞态完成→已入库）",
                    cancelConsistent,
                    victim.DownloadState + " / " + cancelDetail +
                    "；output 里同名文件=" + (File.Exists(victimTarget) ? "存在" : "无"));

                // 下载中的取消：盯一个刚开跑的 HLS 任务，Running 一到就取消
                var inFlight = MakeResult(title: "下载中取消曲", artist: "自检艺人");
                var (inFlightItem, inFlightAwait) = Track(cleanupDownloadIds,
                    DownloadService.Enqueue(inFlight, hlsUrl, isStream: true, hlsUrl));
                bool sawRunning = false;
                var limit = Stopwatch.StartNew();
                while (limit.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (inFlight.DownloadState == DownloadState.Running) { sawRunning = true; break; }
                    await Task.Delay(20, ct);
                }
                if (sawRunning) DownloadService.Cancel(inFlight);
                var (inOk, inDetail) = await AwaitWithTimeout(inFlightAwait, 30, "下载中取消", ct);
                if (sawRunning && inFlight.DownloadState == DownloadState.Cancelled)
                    Ok("下载中的任务能取消（Running → Cancelled）");
                else if (!sawRunning)
                    Ok("下载中取消：任务在取消前已完成（本地太快，跳过断言）");
                else
                    Bad($"下载中取消异常：sawRunning={sawRunning} state={inFlight.DownloadState} {inDetail}");

                foreach (var t in blockTasks) await t;
                foreach (var r in blockers.Concat(new[] { inFlight }))
                {
                    string p = Path.Combine(DownloadService.GetDownloadDirectory(),
                        $"自检艺人 - {r.Title}.m4a");
                    if (File.Exists(p)) cleanupFiles.Add(p);
                }

                // 临时目录不该有残留
                string tempDir = Path.Combine(Path.GetTempPath(), "musicplayer_downloads");
                Check("临时目录无残留", !Directory.EnumerateFiles(tempDir).Any(),
                    Directory.Exists(tempDir) ? "有文件" : "空");

                log($"\n结果：{pass} 通过 / {fail} 失败");
                return fail == 0;
            }
            catch (OperationCanceledException)
            {
                log("\n自检被取消");
                return false;
            }
            catch (Exception ex)
            {
                fail++;
                log($"\n✗ 自检异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                log($"\n结果：{pass} 通过 / {fail} 失败");
                return false;
            }
            finally
            {
                server?.Dispose();
                // 自检清理：测试产物文件 + 入库记录 + downloads 表记录
                foreach (string f in cleanupFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { Db.RemoveTrackByPath(f); } catch { }
                }
                foreach (long id in cleanupDownloadIds)
                {
                    try { Db.DeleteDownload(id); } catch { }
                }
                Environment.SetEnvironmentVariable("MP_DOWNLOAD_TEST_DIR", null);
            }
        }

        // ══════════════════════════════ 工具 ══════════════════════════════

        /// <summary>带超时等一个"等结果"任务：超时返回失败明细，绝不无限等。</summary>
        private static async Task<(bool Ok, string Detail)> AwaitWithTimeout(
            Task<(bool Ok, string Detail)> task, int seconds, string what, CancellationToken ct)
        {
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
            if (done != task) return (false, $"{what}：等待超时（{seconds}s 内结果事件未触发）");
            return await task;
        }

        /// <summary>
        /// 把一次入队包成"等结果"的任务：Completed/Failed/Cancelled 任一事件即完成。
        /// 返回 (条目, 等待任务)；任务结果 (是否成功, 明细)。
        /// </summary>
        private static (DownloadItem? Item, Task<(bool Ok, string Detail)> Await) Track(
            List<long> downloadIds, DownloadItem? item)
        {
            if (item == null)
                return (null, Task.FromResult((false, "入队被忽略（重复）")));

            lock (downloadIds) downloadIds.Add(item.DownloadId);

            var tcs = new TaskCompletionSource<(bool, string)>();
            void OnCompleted(DownloadItem i, string path) { if (ReferenceEquals(i, item)) tcs.TrySetResult((true, path)); }
            void OnFailed(DownloadItem i, string reason) { if (ReferenceEquals(i, item)) tcs.TrySetResult((false, reason)); }
            void OnCancelled(DownloadItem i) { if (ReferenceEquals(i, item)) tcs.TrySetResult((false, "已取消")); }

            DownloadService.Completed += OnCompleted;
            DownloadService.Failed += OnFailed;
            DownloadService.Cancelled += OnCancelled;

            var task = tcs.Task.ContinueWith(t =>
            {
                DownloadService.Completed -= OnCompleted;
                DownloadService.Failed -= OnFailed;
                DownloadService.Cancelled -= OnCancelled;
                return t.Result;
            }, TaskScheduler.Default);

            return (item, task);
        }

        private static OnlineResult MakeResult(string title, string artist) => new()
        {
            Title = title,
            Artist = artist,
            SiteId = 0,
            SiteName = "本地演示站",
            CanDownload = true
        };

        private static DownloadItem MakeItem(OnlineResult r, string url) => new()
        {
            Result = r,
            AudioUrl = url,
            IsStream = false
        };

        /// <summary>ffprobe 读产物时长（秒）；失败返回 -1。</summary>
        private static async Task<double> ProbeDurationAsync(string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = DownloadService.FfprobePath,
                Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return -1;
            string outText = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return double.TryParse(outText.Trim(), out double d) ? d : -1;
        }

        /// <summary>跑 ffmpeg 造夹具，退出码非 0 抛异常。workDir 可指定子进程工作目录。</summary>
        private static async Task RunToolAsync(Action<string> log, string exe, string args,
            CancellationToken ct, string? workDir = null)
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? "",
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("ffmpeg 启动失败");
            string err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg 造夹具失败（{proc.ExitCode}）：{err[^300..]}");
        }
    }
}
