using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>一次下载任务：结果条目 + 已解析好的地址。</summary>
    public class DownloadItem
    {
        public required OnlineResult Result { get; init; }
        public required string AudioUrl { get; init; }

        /// <summary>流媒体索引（m3u8/mpd/m4s/ts）→ 走 ffmpeg；false → HttpClient 直链。</summary>
        public bool IsStream { get; init; }

        /// <summary>来源页地址（防盗链 Referer 用）。</summary>
        public string? Referer { get; init; }

        /// <summary>downloads 表记录 id。</summary>
        public long DownloadId { get; internal set; }

        /// <summary>这条任务自己的取消源（点行内按钮取消 / 退出程序取消）。</summary>
        internal CancellationTokenSource Cts { get; } = new();

        /// <summary>取消令牌（对外只读）。</summary>
        public CancellationToken Token => Cts.Token;
    }

    /// <summary>403：地址被拒（可能是站点需要登录）。</summary>
    public class DownloadForbiddenException : Exception
    {
        /// <summary>请求时是否带上了站点 Cookie（带 Cookie 还 403 → 多半是防盗链而不是没登录）。</summary>
        public bool HadCookies { get; }

        public DownloadForbiddenException(string url, bool hadCookies)
            : base($"403 禁止访问（{(hadCookies ? "已带登录信息仍被拒，可能是防盗链或地址过期" : "站点可能需要登录")}）：{url}")
            => HadCookies = hadCookies;
    }

    /// <summary>
    /// 在线下载与入库（步骤清单 S9，PRD F6）。
    ///
    /// 结构：
    /// - **队列**：SemaphoreSlim(2) 控并发，每条任务 CancellationTokenSource 可取消；
    ///   状态/进度直接写回 <see cref="OnlineResult"/>（标量绑定，后台线程通知也安全）；
    /// - **直链**：HttpClient 携带内核里该站点的 Cookie + Referer，边下边报进度，
    ///   落临时目录成功后 move 到目标（避免半截文件被曲库扫描看到）；
    /// - **流媒体**：ffmpeg 子进程合并（m3u8 含 AES-128 自动处理，mpd/m4s 转封装 m4a），
    ///   -headers 注入 Cookie/Referer，-progress pipe:1 解析进度；
    /// - **入库**：taglib 写 title/artist/comment → UpsertTrack(source=online) →
    ///   Completed 事件里主界面刷新列表 + Toast"已入库"。
    ///
    /// 事件都在**后台线程**触发，订阅方要自己回 UI 线程。
    /// </summary>
    public static class DownloadService
    {
        // ══════════════════════════════ 事件（后台线程） ══════════════════════════════

        /// <summary>一条下载成功并已入库。参数 = 任务、最终路径。</summary>
        public static event Action<DownloadItem, string>? Completed;

        /// <summary>一条下载失败。参数 = 任务、给用户看的原因。</summary>
        public static event Action<DownloadItem, string>? Failed;

        /// <summary>一条下载被取消（点按钮 / 退出程序）。</summary>
        public static event Action<DownloadItem>? Cancelled;

        // ══════════════════════════════ 队列状态 ══════════════════════════════

        private static readonly SemaphoreSlim Slots = new(2, 2);
        private static readonly object Gate = new();
        private static readonly HashSet<DownloadItem> PendingSet = new();
        private static readonly HashSet<DownloadItem> RunningSet = new();

        /// <summary>正在下载（含 ffmpeg）的任务数。</summary>
        public static int ActiveCount { get; private set; }

        /// <summary>排队还没轮到的任务数。</summary>
        public static int PendingCount { get; private set; }

        /// <summary>有没有没干完的活（退出保护用）。</summary>
        public static bool HasWork => ActiveCount + PendingCount > 0;

        /// <summary>并发峰值（自检断言"并发 2"用）。</summary>
        public static int MaxConcurrentObserved { get; private set; }

        private static HttpClient? _http;

        // ══════════════════════════════ 对外入口 ══════════════════════════════

        /// <summary>
        /// 把一条结果排进下载队列（地址必须已解析好）。
        /// UI 线程调用；重复入队同一条结果会被忽略。
        /// </summary>
        public static DownloadItem? Enqueue(OnlineResult r, string audioUrl, bool isStream, string? referer)
        {
            lock (Gate)
            {
                if (PendingSet.Concat(RunningSet).Any(i => ReferenceEquals(i.Result, r)))
                    return null;
            }

            var item = new DownloadItem { Result = r, AudioUrl = audioUrl, IsStream = isStream, Referer = referer };
            item.DownloadId = Db.InsertDownload(new DownloadRecord
            {
                SiteId = r.SiteId,
                Title = r.Title,
                Artist = r.Artist,
                AudioUrl = audioUrl,
                Status = "pending"
            });

            r.DownloadState = DownloadState.Pending;
            r.DownloadProgress = 0;
            r.DownloadError = "";

            lock (Gate) PendingSet.Add(item);
            _ = Task.Run(() => RunAsync(item));
            return item;
        }

        /// <summary>取消某条结果的任务（排队/下载中都行）。返回是否找到了这条任务。</summary>
        public static bool Cancel(OnlineResult r)
        {
            DownloadItem? item;
            lock (Gate)
                item = PendingSet.Concat(RunningSet).FirstOrDefault(i => ReferenceEquals(i.Result, r));
            if (item == null) return false;
            try { item.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        /// <summary>取消全部（退出程序用）。</summary>
        public static void CancelAll()
        {
            List<DownloadItem> items;
            lock (Gate) items = PendingSet.Concat(RunningSet).ToList();
            foreach (var i in items)
            {
                try { i.Cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // ══════════════════════════════ 执行 ══════════════════════════════

        private static async Task RunAsync(DownloadItem item)
        {
            // 排队中就可能被取消，单独处理：只更新状态并广播，不进下载流程。
            // （之前取消路径依赖下面的统一 catch，一旦 Slots.WaitAsync 之前/之中的
            //   异常没被正确接住，事件就永远不发，等结果的一方会无限等——实测踩过）
            try
            {
                await Slots.WaitAsync(item.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
            {
                FinalizeCancelled(item);
                lock (Gate) { PendingSet.Remove(item); PendingCount = PendingSet.Count; }
                item.Cts.Dispose();
                return;   // 排队中取消：没拿过槽位，无需 Release
            }

            lock (Gate)
            {
                PendingSet.Remove(item);
                RunningSet.Add(item);
                ActiveCount = RunningSet.Count;
                PendingCount = PendingSet.Count;
                if (ActiveCount > MaxConcurrentObserved) MaxConcurrentObserved = ActiveCount;
            }

            item.Result.DownloadState = DownloadState.Running;
            try { Db.UpdateDownload(item.DownloadId, "running"); }
            catch (Exception ex)
            {
                // 状态更新失败不该杀掉整个下载（SQLite 偶发 busy 之类），记日志继续
                App.LogCrash($"[下载] 更新 running 状态失败（忽略）：{ex.Message}");
            }

            // 目标路径只算一次（BuildTargetPath 有"已存在就加 (1)"的去重，
            // 下载完成后重算会得到另一个路径——取消竞态判断必须用同一个）
            string target = BuildTargetPath(item);
            string? finalPath = null;
            try
            {
                string tempDir = EnsureTempDir();
                finalPath = item.IsStream
                    ? await DownloadViaFfmpegAsync(item, target, tempDir)
                    : await DownloadDirectAsync(item, target, tempDir);

                ImportToLibrary(item, finalPath);
                FinalizeDone(item, finalPath);
            }
            // ⚠ .NET 8 的 HttpClient 超时抛的也是 OperationCanceledException，
            // 必须用"用户确实取消了"来区分，否则超时会被记成"用户取消"
            catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
            {
                // 竞态：取消信号到达前文件其实已经落盘（实测本地小文件必现）。
                // 文件已在目标位置就按成功收尾，否则会留下"状态取消但文件存在"的孤儿。
                if (File.Exists(target))
                {
                    ImportToLibrary(item, target);
                    FinalizeDone(item, target);
                }
                else
                {
                    FinalizeCancelled(item);
                }
            }
            catch (OperationCanceledException)
            {
                CleanTemp(item);
                const string reason = "下载超时（10 分钟无进展），稍后重试";
                Db.UpdateDownload(item.DownloadId, "failed", error: reason);
                item.Result.DownloadState = DownloadState.Failed;
                item.Result.DownloadError = reason;
                App.LogCrash($"[下载] 超时「{item.Result.Title}」");
                Failed?.Invoke(item, reason);
            }
            catch (Exception ex)
            {
                CleanTemp(item);
                string reason = ex is DownloadForbiddenException f ? f.Message : Describe(ex);
                Db.UpdateDownload(item.DownloadId, "failed", error: reason);
                item.Result.DownloadState = DownloadState.Failed;
                item.Result.DownloadError = reason;
                App.LogCrash($"[下载] 失败「{item.Result.Title}」{reason}");
                Failed?.Invoke(item, reason);
            }
            finally
            {
                lock (Gate)
                {
                    RunningSet.Remove(item);
                    ActiveCount = RunningSet.Count;
                    PendingCount = PendingSet.Count;
                }
                Slots.Release();
                item.Cts.Dispose();
            }
        }

        /// <summary>下载成功的统一收尾：落库、写状态、广播。</summary>
        private static void FinalizeDone(DownloadItem item, string finalPath)
        {
            long size = 0;
            try { size = new FileInfo(finalPath).Length; } catch { }
            Db.UpdateDownload(item.DownloadId, "done", finalPath, size);
            item.Result.DownloadState = DownloadState.Done;
            App.LogCrash($"[下载] 成功收尾「{item.Result.Title}」→ {finalPath}");
            Completed?.Invoke(item, finalPath);
        }

        /// <summary>用户取消的统一收尾：清临时文件、落库、广播（无论排队中还是下载中）。</summary>
        private static void FinalizeCancelled(DownloadItem item)
        {
            CleanTemp(item);
            try { Db.UpdateDownload(item.DownloadId, "cancelled", error: "用户取消"); }
            catch (Exception ex) { App.LogCrash($"[下载] 取消落库失败（忽略）：{ex.Message}"); }
            item.Result.DownloadState = DownloadState.Cancelled;
            App.LogCrash($"[下载] 取消收尾「{item.Result.Title}」");
            Cancelled?.Invoke(item);
        }

        /// <summary>把常见异常翻译成一句话文案（PRD F6.4 分文案）。</summary>
        private static string Describe(Exception ex) => ex switch
        {
            HttpRequestException { } h when h.StatusCode == System.Net.HttpStatusCode.Forbidden
                => "403 禁止访问：该站点可能需要登录（在站点面板点「登录」）",
            HttpRequestException { } h when h.StatusCode == System.Net.HttpStatusCode.NotFound
                => "404：地址已失效（重新搜索后再试）",
            HttpRequestException h => "网络错误：" + (h.StatusCode?.ToString() ?? h.InnerException?.Message ?? h.Message),
            TimeoutException => "下载超时，稍后重试",
            _ => ex.Message
        };

        /// <summary>这条任务可能留下的临时文件（下载中取消要清掉）。</summary>
        private static void CleanTemp(DownloadItem item)
        {
            try
            {
                string marker = TempPrefix(item.DownloadId);
                foreach (string f in Directory.GetFiles(EnsureTempDir()))
                {
                    if (f.Contains(marker)) try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        // ══════════════════════════════ 直链下载 ══════════════════════════════

        private static async Task<string> DownloadDirectAsync(DownloadItem item, string target, string tempDir)
        {
            _http ??= new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            { Timeout = TimeSpan.FromMinutes(10) };

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.Token);
            linked.CancelAfter(TimeSpan.FromMinutes(10));

            using var req = new HttpRequestMessage(HttpMethod.Get, item.AudioUrl);
            bool hadCookies = await AddSiteHeadersAsync(req, item).ConfigureAwait(false);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new DownloadForbiddenException(item.AudioUrl, hadCookies);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);

            // 扩展名：优先 URL，其次 Content-Type，最后 .mp3
            string ext = ExtFromUrl(item.AudioUrl, fallbackExt: ".mp3");
            if (ext == ".mp3")
                ext = ExtFromContentType(resp.Content.Headers.ContentType?.MediaType) ?? ".mp3";

            string tempPath = Path.Combine(tempDir, TempPrefix(item.DownloadId) + ext);
            await using (var remote = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var local = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long? total = resp.Content.Headers.ContentLength;
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                double lastPct = -5;
                while ((n = await remote.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, n), linked.Token).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                    {
                        double pct = read * 100.0 / total.Value;
                        if (pct - lastPct >= 0.5)
                        {
                            lastPct = pct;
                            item.Result.DownloadProgress = pct;
                        }
                    }
                }
            }

            MoveToTarget(tempPath, target);
            return target;
        }

        /// <summary>往请求里塞该站点的 Cookie（从隐藏内核取）+ Referer。返回是否带上了 Cookie。</summary>
        private static async Task<bool> AddSiteHeadersAsync(HttpRequestMessage req, DownloadItem item)
        {
            bool hadCookies = false;
            if (HiddenBrowserService.Instance.IsInitialized)
            {
                try
                {
                    string origin = new Uri(item.AudioUrl).GetLeftPart(UriPartial.Authority);
                    var cookies = await HiddenBrowserService.Instance.GetCookiesAsync(origin).ConfigureAwait(false);
                    if (cookies is { Count: > 0 })
                    {
                        req.Headers.TryAddWithoutValidation("Cookie",
                            string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
                        hadCookies = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[下载] 取 Cookie 失败（继续无 Cookie 下载）: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(item.Referer))
                req.Headers.Referrer = new Uri(item.Referer);
            return hadCookies;
        }

        // ══════════════════════════════ ffmpeg（HLS/DASH） ══════════════════════════════

        /// <summary>ffmpeg 路径（随构建输出到 Assets\ffmpeg，运行时在 ffmpeg\ 子目录）。</summary>
        internal static string FfmpegPath => ToolPath("ffmpeg.exe");

        /// <summary>ffprobe 路径（自检验证产物有效性用）。</summary>
        public static string FfprobePath => ToolPath("ffprobe.exe");

        private static string ToolPath(string exe)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string packaged = Path.Combine(exeDir, "ffmpeg", exe);
            if (File.Exists(packaged)) return packaged;
            return Path.Combine(exeDir, "Assets", "ffmpeg", exe); // 设计时目录
        }

        /// <summary>
        /// 启动恢复：上次异常退出残留的 running/pending 一律置 failed
        /// （downloads 表设计说明里指定由本服务做这件事）。
        /// </summary>
        public static void ResetInterruptedDownloads()
        {
            foreach (var d in Db.GetActiveDownloads())
            {
                try { Db.UpdateDownload(d.Id, "failed", error: "程序中断"); } catch { }
            }
        }

        /// <summary>清零并发峰值（自检专用）。</summary>
        internal static void ResetForTest() => MaxConcurrentObserved = 0;

        /// <summary>
        /// 流媒体下载：ffmpeg 一口气完成 取密钥 → 合并分片 → 转封装 m4a。
        /// 失败时降级重编一次（有些源 copy 不了）。
        /// </summary>
        private static async Task<string> DownloadViaFfmpegAsync(DownloadItem item, string target, string tempDir)
        {
            string tempOut = Path.Combine(tempDir, TempPrefix(item.DownloadId) + ".m4a");

            string? headers = await BuildFfmpegHeadersAsync(item).ConfigureAwait(false);

            // 第一枪：流拷贝（无损、最快）；失败再降级 AAC 重编
            int code = await RunFfmpegAsync(item, tempOut, headers, remux: true).ConfigureAwait(false);
            if (code != 0)
            {
                App.LogCrash($"[下载] ffmpeg copy 失败（{item.Result.Title}，code={code}），降级重编");
                try { File.Delete(tempOut); } catch { }
                code = await RunFfmpegAsync(item, tempOut, headers, remux: false).ConfigureAwait(false);
            }
            if (code != 0 || !File.Exists(tempOut) || new FileInfo(tempOut).Length < 1024)
                throw new InvalidOperationException("流媒体合并失败（密钥获取失败或地址失效），可稍后重试");

            // 重命名目标：BuildTargetPath 给的是 .m4a，正好
            MoveToTarget(tempOut, target);
            return target;
        }

        private static async Task<string?> BuildFfmpegHeadersAsync(DownloadItem item)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Referer))
                lines.Add("Referer: " + item.Referer.Trim());
            if (HiddenBrowserService.Instance.IsInitialized)
            {
                try
                {
                    string origin = new Uri(item.AudioUrl).GetLeftPart(UriPartial.Authority);
                    var cookies = await HiddenBrowserService.Instance.GetCookiesAsync(origin).ConfigureAwait(false);
                    if (cookies is { Count: > 0 })
                        lines.Add("Cookie: " + string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
                }
                catch { }
            }

            if (lines.Count == 0) return null;
            // ffmpeg 的 -headers 要求每行 \r\n 结尾
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>跑 ffmpeg 子进程，解析 -progress 输出报进度。返回退出码。</summary>
        private static async Task<int> RunFfmpegAsync(DownloadItem item, string outputPath,
            string? headers, bool remux)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,   // -progress pipe:1 走这里
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            // ⚠ 编码参数必须逐个 Add：ArgumentList 不做 shell 拆分，
            // 整串 "-c:a copy" 塞进去 ffmpeg 会当成一个未知选项直接失败（实测踩过）
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            if (headers != null)
            {
                psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(headers);
            }
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(item.AudioUrl);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("ipod"); // m4a 容器
            if (remux)
            {
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("copy");
            }
            else
            {
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("192k");
            }
            psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
            psi.ArgumentList.Add(outputPath);

            var stderrTail = new StringBuilder();
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // 取消 → 直接杀进程（ffmpeg 对 CTRL_C 信号不认 Token）
            using var killOnCancel = item.Token.Register(() => { try { proc.Kill(); } catch { } });

            // stderr 只留尾巴做诊断
            _ = proc.StandardError.ReadToEndAsync().ContinueWith(t =>
            {
                try
                {
                    string s = t.Result ?? "";
                    if (s.Length > 400) s = s[^400..];
                    lock (stderrTail) stderrTail.Append(s);
                }
                catch { }
            }, TaskScheduler.Default);

            var duration = item.Result.Duration;
            var stdout = proc.StandardOutput;
            while (!proc.HasExited)
            {
                string? line = await stdout.ReadLineAsync(item.Token).ConfigureAwait(false);
                if (line == null) break;
                // progress 行：out_time_ms=123456 / progress=continue|end
                if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) && duration > TimeSpan.Zero)
                {
                    if (long.TryParse(line.AsSpan(12).TrimEnd('\r'), out long us) && us > 0)
                    {
                        double pct = Math.Min(99, us / 1000.0 / duration.TotalMilliseconds * 100);
                        item.Result.DownloadProgress = pct;
                    }
                }
            }

            await proc.WaitForExitAsync(item.Token.IsCancellationRequested
                ? CancellationToken.None : item.Token).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                string tail;
                lock (stderrTail) tail = stderrTail.ToString();
                App.LogCrash($"[下载] ffmpeg 退出码 {proc.ExitCode}：{tail}");
            }
            return proc.ExitCode;
        }

        // ══════════════════════════════ 落盘与入库 ══════════════════════════════

        /// <summary>下载目录：设置项 online_download_dir，默认 我的音乐\MusicPlayer\在线下载。</summary>
        public static string GetDownloadDirectory()
        {
            // 自检隔离：测试跑专门的目录，不污染真实下载目录
            string? testDir = Environment.GetEnvironmentVariable("MP_DOWNLOAD_TEST_DIR");
            if (!string.IsNullOrWhiteSpace(testDir)) return testDir;

            string dir = Db.GetSetting("online_download_dir", "");
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;

            string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(myMusic)) myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(myMusic, "MusicPlayer", "在线下载");
        }

        /// <summary>目标路径：{艺人} - {标题}.{ext}；非法字符替换；重名加 (1)(2)…</summary>
        public static string BuildTargetPath(DownloadItem item)
        {
            string ext = item.IsStream
                ? ".m4a"
                : ExtFromUrl(item.AudioUrl, fallbackExt: ".mp3");

            string name = string.IsNullOrWhiteSpace(item.Result.Artist)
                ? item.Result.Title
                : $"{item.Result.Artist} - {item.Result.Title}";
            name = SanitizeFileName(name);
            if (name.Length == 0) name = "在线下载";

            string dir = GetDownloadDirectory();
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, name + ext);
            int n = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, $"{name} ({n++}){ext}");
                if (n > 99) { path = Path.Combine(dir, Guid.NewGuid().ToString("N")[..8] + ext); break; }
            }
            return path;
        }

        /// <summary>文件名里的 \/:*?"<>| 与控制字符全部换成下划线，收尾空白/点去掉。</summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':' };
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString().Trim(' ', '.');
        }

        private static string TempPrefix(long downloadId) => $"mp_dl_{downloadId}_";

        private static string EnsureTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "musicplayer_downloads");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>临时文件挪到目标（目标在 BuildTargetPath 时查过重名；Move 后不可能撞）。</summary>
        private static void MoveToTarget(string tempPath, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(tempPath, target);
        }

        /// <summary>URL 里取扩展名（去查询串）；认不出的音频格式给 fallback。</summary>
        private static string ExtFromUrl(string url, string fallbackExt)
        {
            try
            {
                string path = new Uri(url).AbsolutePath;
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return fallbackExt;

                string known = ".mp3 .m4a .flac .wav .ogg .aac .mp4 .m4s .ts .opus .wma";
                if (known.Contains(ext.ToLowerInvariant())) return ext.ToLowerInvariant();
                return fallbackExt;
            }
            catch
            {
                return fallbackExt;
            }
        }

        private static string? ExtFromContentType(string? mediaType) => mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/flac" => ".flac",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/ogg" or "application/ogg" => ".ogg",
            "audio/aac" => ".aac",
            "audio/opus" => ".opus",
            _ => null
        };

        /// <summary>
        /// 入库（PRD F6.3）：taglib 写标题/艺人/备注（来源站 + 原链接）→
        /// UpsertTrack(source=online) → 调用方（Completed 事件）刷新列表 + Toast。
        /// 写标签失败不致命（有的容器不支持），照常入库。
        /// </summary>
        private static void ImportToLibrary(DownloadItem item, string path)
        {
            TimeSpan duration = item.Result.Duration;
            string format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            try
            {
                var tag = TagLib.File.Create(path);
                tag.Tag.Title = item.Result.Title;
                if (!string.IsNullOrWhiteSpace(item.Result.Artist))
                {
                    tag.Tag.Performers = new[] { item.Result.Artist! };
                    tag.Tag.AlbumArtists = new[] { item.Result.Artist! };
                }
                tag.Tag.Comment = string.IsNullOrWhiteSpace(item.Result.SiteName)
                    ? "在线下载"
                    : $"来源: {item.Result.SiteName}{(string.IsNullOrWhiteSpace(item.Result.DetailUrl) ? "" : " " + item.Result.DetailUrl)}";
                tag.Save();

                var probed = tag.Properties?.Duration ?? TimeSpan.Zero;
                if (probed > TimeSpan.Zero) duration = probed;
            }
            catch (Exception ex)
            {
                App.LogCrash($"[下载] 写标签失败（仍入库）「{item.Result.Title}」: {ex.Message}");
            }

            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(path).ToFileTime(); } catch (IOException) { }

            Db.UpsertTrack(path, item.Result.Title, (long)duration.TotalMilliseconds, format, mtime,
                lastSeen: 0, source: "online", sourceUrl: item.Result.DetailUrl);
        }
    }
}
