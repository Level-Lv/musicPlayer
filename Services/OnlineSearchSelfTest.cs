using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// S4 搜索引擎自检（保留，长期可用）。全部用**本地模拟页**，不依赖外网。
    ///
    /// 覆盖用例：
    /// 1. 正常搜索：模板填 {q} → 抓到 3 条结构化结果（标题含关键词、艺人/时长/封面/绝对链接齐全）；
    /// 2. 空结果页 → EmptyResult；
    /// 3. 选择器配坏 → Broken；
    /// 4. 域名解析不到（.invalid 保留顶级域，离线也能稳定复现）→ NavFailed；
    /// 5. 人机验证中转全链路：模拟挑战页 → 自动弹验证窗口 → 通过 → 续抓成功；
    /// 6. 取消：搜索中途 Cancel → Status = Cancelled；
    /// 7. 纯函数：模板填充 {q}/{p}、时长文本解析。
    ///
    /// 入口：环境变量 MP_ONLINE_SEARCH_TEST=1（全自动，含自动过验证）/ =2（验证环节手动点）。
    /// </summary>
    public static class OnlineSearchSelfTest
    {
        /// <summary>模拟结果页里的曲目后缀（3 条结果）。</summary>
        private static readonly string[] SongSuffix = { "甲", "乙", "丙" };

        /// <summary>与页面 JS 相同的结果项标记（选择器配置的依据）。</summary>
        public const string DemoSelectorsJson =
            @"{""Item"":"".song"",""Title"":"".t"",""Artist"":"".a"",""Duration"":"".d"",""Cover"":""img"",""Link"":""a.dl""}";

        /// <summary>写三个模拟页到临时目录并返回地址。</summary>
        public static (string ResultsUrl, string EmptyUrl, string ChallengeUrl) PrepareDemoPages()
        {
            string results = WritePage("musicplayer_search_demo.html", DemoResultsHtml);
            string empty = WritePage("musicplayer_search_empty.html", DemoEmptyHtml);
            string challenge = WritePage("musicplayer_search_challenge.html", DemoChallengeHtml);
            return (results, empty, challenge);
        }

        private static string WritePage(string fileName, string html)
        {
            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, html, Encoding.UTF8);
            return new Uri(path).AbsoluteUri;
        }

        /// <summary>
        /// 跑一遍搜索自检，逐行回调日志，返回 true 表示全部通过。
        /// </summary>
        /// <param name="autoCaptcha">true：验证环节注入自动通过；false：人工点「模拟完成验证」按钮。</param>
        public static async Task<bool> RunAsync(Action<string> log, bool autoCaptcha,
            CancellationToken ct = default)
        {
            int pass = 0, fail = 0;
            void Ok(string msg) { pass++; log($"  ✓ {msg}"); }
            void Bad(string msg) { fail++; log($"  ✗ {msg}"); }

            var svc = HiddenBrowserService.Instance;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                log("── 环境 ──");
                await svc.InitAsync();
                Ok($"内核初始化（{sw.ElapsedMilliseconds} ms）");

                var (resultsUrl, emptyUrl, challengeUrl) = PrepareDemoPages();
                log($"  模拟结果页 = {resultsUrl}");
                log($"  模拟空页   = {emptyUrl}");
                log($"  模拟挑战页 = {challengeUrl}");

                // ── 用例 7：纯函数（不需要内核）──
                log("\n── 模板与解析 ──");

                var siteTpl = new OnlineSite
                {
                    SearchTemplate = "https://demo.example/search?q={q}&p={p}",
                    PageTemplate = "https://demo.example/search?q={q}&p={p}"
                };
                string u1 = OnlineSearchService.BuildSearchUrl(siteTpl, "夜 曲 A&B", 1);
                string u2 = OnlineSearchService.BuildSearchUrl(siteTpl, "abc", 3);
                if (u1 == "https://demo.example/search?q=%E5%A4%9C%20%E6%9B%B2%20A%26B&p=1")
                    Ok($"模板填充（编码 + 页码）：{u1}");
                else Bad($"模板填充异常：{u1}");
                if (u2.EndsWith("p=3")) Ok($"第 3 页填充：{u2}");
                else Bad($"页码填充异常：{u2}");

                var d1 = OnlineSearchService.ParseDurationText("03:41");
                var d2 = OnlineSearchService.ParseDurationText("1:02:03");
                var d3 = OnlineSearchService.ParseDurationText("3分45秒");
                if (d1 == TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(41)
                    && d2 == new TimeSpan(1, 2, 3)
                    && d3 == TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(45))
                    Ok("时长解析 03:41 / 1:02:03 / 3分45秒");
                else Bad($"时长解析异常：{d1} / {d2} / {d3}");

                // ── 用例 1：正常搜索 ──
                log("\n── 正常搜索 ──");
                sw.Restart();
                var demoSite = MakeSite("S4演示站", resultsUrl + "?q={q}",
                    item: ".song", title: ".t", artist: ".a", duration: ".d",
                    cover: "img", link: "a.dl");
                var outcome = await RunLoggedSearchAsync(demoSite, "夜曲", log, ct);
                if (outcome.Status == OnlineSearchStatus.Ok)
                {
                    Ok($"搜索返回 {outcome.Results.Count} 条（{sw.ElapsedMilliseconds} ms）");
                    var first = outcome.Results[0];
                    if (outcome.Results.Count == 3 && first.Title.Contains("夜曲"))
                        Ok($"标题含关键词：{first.Title}");
                    else Bad($"标题异常：{first.Title}");
                    if (first.Artist == "歌手1") Ok($"艺人：{first.Artist}");
                    else Bad($"艺人异常：{first.Artist}");
                    if (first.Duration == TimeSpan.FromSeconds(221)) Ok("时长 03:41 → 221 秒");
                    else Bad($"时长解析异常：{first.Duration}");
                    if (first.DetailUrl != null && first.DetailUrl.StartsWith("file://")
                        && first.DetailUrl.Contains("detail.html"))
                        Ok($"详情链接已转绝对：{first.DetailUrl}");
                    else Bad($"详情链接异常：{first.DetailUrl}");
                    if (first.CoverUrl != null && first.CoverUrl.StartsWith("data:image"))
                        Ok("封面取到（data URI）");
                    else Bad($"封面异常：{first.CoverUrl}");
                    if (first.SiteName == "S4演示站" && first.CanDownload)
                        Ok("站点归属与可下载标记正确");
                    else Bad($"归属/可下载异常：{first.SiteName} / {first.CanDownload}");
                }
                else Bad($"正常搜索失败：{outcome.Status} {outcome.Message}");

                // ── 用例 2：空结果 ──
                log("\n── 空结果 ──");
                var emptySite = MakeSite("空页站", emptyUrl + "?q={q}",
                    item: ".song", title: ".t");
                outcome = await RunLoggedSearchAsync(emptySite, "不存在的歌", log, ct);
                if (outcome.Status == OnlineSearchStatus.EmptyResult)
                    Ok($"EmptyResult：{outcome.Message}");
                else Bad($"应为 EmptyResult，实际 {outcome.Status}");

                // ── 用例 3：选择器配坏 ──
                log("\n── 失效选择器 ──");
                var brokenSite = MakeSite("坏选择器站", resultsUrl + "?q={q}",
                    item: ".song-doesnotexist", title: ".t");
                outcome = await RunLoggedSearchAsync(brokenSite, "夜曲", log, ct);
                if (outcome.Status == OnlineSearchStatus.Broken)
                    Ok($"Broken：{outcome.Message}");
                else Bad($"应为 Broken，实际 {outcome.Status}");

                // ── 用例 4：域名解析失败 ──
                log("\n── 导航失败 ──");
                var deadSite = MakeSite("解析失败站", "https://musicplayer-selftest.invalid/?q={q}",
                    item: ".song", title: ".t");
                outcome = await RunLoggedSearchAsync(deadSite, "夜曲", log, ct);
                if (outcome.Status == OnlineSearchStatus.NavFailed)
                    Ok($"NavFailed：{outcome.Message}");
                else Bad($"应为 NavFailed，实际 {outcome.Status}");

                // ── 用例 5：人机验证中转全链路 ──
                log("\n── 验证中转接入 ──");
                var challengeSite = MakeSite("挑战站", challengeUrl + "?q={q}",
                    item: ".song", title: ".t");
                if (autoCaptcha)
                {
                    // 自动模式：验证窗口每秒轮询 extraVerify，这里注入"点掉挑战"的逻辑
                    OnlineSearchService.CaptchaVerifyHook = async () =>
                    {
                        await svc.ExecuteScriptAsync(
                            "if (typeof passNow === 'function') passNow();", ct);
                        await Task.CompletedTask;
                        return true;
                    };
                }
                else
                {
                    ShowToastHint?.Invoke("请在弹出的验证窗口里点「模拟完成验证」按钮");
                }
                try
                {
                    sw.Restart();
                    outcome = await RunLoggedSearchAsync(challengeSite, "挑战曲", log, ct);
                    if (outcome.Status == OnlineSearchStatus.Ok && outcome.Results.Count == 3)
                        Ok($"验证通过后续抓成功：{outcome.Results.Count} 条（{sw.ElapsedMilliseconds} ms）");
                    else Bad($"验证流程异常：{outcome.Status} {outcome.Message}");
                }
                finally
                {
                    OnlineSearchService.CaptchaVerifyHook = null;
                }

                // ── 用例 6：取消（等到进入"等待结果"阶段再取消，保证确定性）──
                // 注意：Progress<T> 的回调总是 Post 到 UI 队列，搜索太快时来不及执行；
                // 这里用同步回调的实现，让取消精确发生在 Waiting 上报的那一刻
                log("\n── 取消 ──");
                using (var cts = new CancellationTokenSource())
                {
                    bool cancelFired = false;
                    var progress = new SyncProgress(p =>
                    {
                        log($"    [{p.Phase}] {p.SiteName}: {p.Message}");
                        if (p.Phase == OnlineSearchPhase.Waiting && !cancelFired)
                        {
                            cancelFired = true;
                            cts.Cancel();
                        }
                    });
                    outcome = await OnlineSearchService.SearchAsync(demoSite, "取消曲", progress, cts.Token);
                }
                if (outcome.Status == OnlineSearchStatus.Cancelled)
                    Ok("等待阶段取消 → Cancelled");
                else Bad($"应为 Cancelled，实际 {outcome.Status}");

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
        }

        /// <summary>手动模式时提示用户的回调（MainWindow 注入 Toast）。</summary>
        public static Action<string>? ShowToastHint { get; set; }

        /// <summary>同步回调的进度实现（测试里需要"上报即执行"的确定性时机）。</summary>
        private sealed class SyncProgress : IProgress<OnlineSearchProgress>
        {
            private readonly Action<OnlineSearchProgress> _action;
            public SyncProgress(Action<OnlineSearchProgress> action) => _action = action;
            public void Report(OnlineSearchProgress value) => _action(value);
        }

        /// <summary>构造测试站点对象（搜索引擎不读库，直接用内存对象即可）。</summary>
        private static OnlineSite MakeSite(string name, string template,
            string item, string title, string? artist = null, string? duration = null,
            string? cover = null, string? link = null) =>
            new OnlineSite
            {
                Name = name,
                SearchTemplate = template,
                Selectors = new SiteSelectors
                {
                    Item = item, Title = title, Artist = artist,
                    Duration = duration, Cover = cover, Link = link
                }
            };

        /// <summary>执行一次搜索并把每条进度写进日志。</summary>
        private static Task<OnlineSearchOutcome> RunLoggedSearchAsync(
            OnlineSite site, string keyword, Action<string> log, CancellationToken ct)
        {
            var progress = new Progress<OnlineSearchProgress>(p =>
                log($"    [{p.Phase}] {p.SiteName}: {p.Message}"));
            return OnlineSearchService.SearchAsync(site, keyword, progress, ct);
        }

        // ══════════════════════════ 本地模拟页 ══════════════════════════

        /// <summary>共享的结果项渲染脚本（结果页与挑战页注入同一种 .song 结构）。</summary>
        private const string RenderItemsJs = @"
  var box = document.getElementById('results');
  var q = new URLSearchParams(location.search).get('q') || '';
  ['甲','乙','丙'].forEach(function(suffix, i) {
    var div = document.createElement('div');
    div.className = 'song';
    div.innerHTML = '<h3 class=""t"">' + q + ' - 演示曲目' + suffix + '</h3>'
      + '<span class=""a"">歌手' + (i + 1) + '</span>'
      + '<span class=""d"">03:4' + (i + 1) + '</span>'
      + '<img src=""data:image/gif;base64,R0lGODlhAQABAAAAACw="" alt=""cover"">'
      + '<a class=""dl"" href=""detail.html?song=' + encodeURIComponent(q + suffix) + '"">播放</a>';
    box.appendChild(div);
  });";

        /// <summary>模拟结果页：带页头页尾把 DOM 撑过 Broken 阈值（25 个元素）。</summary>
        private const string DemoResultsHtml = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>S4 搜索演示站</title></head>
<body>
  <header><div class=""logo"">演示音乐站</div>
    <nav><a>首页</a><a>榜单</a><a>分类</a><a>歌手</a></nav></header>
  <div id=""results""></div>
  <footer><span>关于</span><span>联系</span><span>帮助</span><span>© 演示站</span></footer>
  <script>" + RenderItemsJs + @"</script>
</body></html>";

        /// <summary>模拟空结果页：明确给出"没有找到"提示。</summary>
        private const string DemoEmptyHtml = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>搜索结果</title></head>
<body>
  <header><div class=""logo"">演示音乐站</div></header>
  <div class=""empty""><p>很抱歉，没有找到与关键词相关的内容</p></div>
</body></html>";

        /// <summary>模拟挑战页：过验证（移除挑战元素）后注入结果项，模拟"验证完直接落到结果页"。</summary>
        private const string DemoChallengeHtml = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Just a moment...</title></head>
<body>
  <h2 id=""challenge-running"">正在验证您是否是真人…</h2>
  <p><button id=""btnPass"" onclick=""passNow()"">模拟完成验证</button></p>
  <div id=""results""></div>
  <script>
    function passNow() {
      var el = document.getElementById('challenge-running');
      if (el) el.remove();
      var btn = document.getElementById('btnPass');
      if (btn) btn.remove();
      document.title = '搜索结果';
" + RenderItemsJs + @"
    }
  </script>
</body></html>";
    }
}
