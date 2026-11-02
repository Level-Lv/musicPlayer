using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>单站搜索的最终结局（成功带结果；失败带分类与说明）。</summary>
    public class OnlineSearchOutcome
    {
        /// <summary>搜索的站点。</summary>
        public OnlineSite Site { get; init; } = new OnlineSite();

        /// <summary>结局分类（见 <see cref="OnlineSearchStatus"/>）。</summary>
        public OnlineSearchStatus Status { get; init; }

        /// <summary>结构化结果（仅 Status = Ok 时有内容）。</summary>
        public List<OnlineResult> Results { get; init; } = new();

        /// <summary>给 Toast / 日志用的说明文字。</summary>
        public string Message { get; init; } = "";
    }

    /// <summary>搜索结局分类（《在线曲库 PRD》F4.4，UI 据此给不同提示）。</summary>
    public enum OnlineSearchStatus
    {
        Ok,           // 搜到了
        Timeout,      // 页面迟迟加载不完（导航超时）
        NavFailed,    // 导航失败（断网 / 域名解析不到 / HTTP 错误）
        EmptyResult,  // 页面正常但确实没有结果（或页面几乎空白）
        NeedCaptcha,  // 被人机验证拦下（S3 窗口介入后可重试）
        NeedLogin,    // 需要登录态（Cookie 过期或首次要求登录）
        Broken,       // 选择器抓不到东西——站点结构变了，需要重新点选
        Cancelled     // 用户取消
    }

    public enum OnlineSearchPhase { Searching, Waiting, Completed, Failed }

    /// <summary>搜索过程进度上报（Searching/Waiting/Completed/Failed）。</summary>
    public class OnlineSearchProgress
    {
        public OnlineSearchPhase Phase { get; init; }
        public string SiteName { get; init; } = "";
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// 在线搜索引擎（《在线曲库开发步骤清单》S4）。
    ///
    /// 职责：关键词 + 站点配置 → 结构化结果列表。
    /// 流程：模板填充 {q}/{p} → 隐藏内核导航 → 等待策略（轮询抓取，上限 10s）
    ///       → 按选择器抓取 DOM → 结果归一化 → 失败分类。
    ///
    /// 设计要点：
    /// - **不持有内核闸门**：导航、抓取都通过 HiddenBrowserService 的 RunAsync 逐次短占内核，
    ///   中途可以让位给人机验证窗口（S3）的导航，避免自锁；
    /// - **站点级串行**：一个内核 = 一个标签页，两个搜索并发导航会互相顶掉页面、结果串站，
    ///   所以多站模式按站点顺序执行（清单原文"并发 2"在单标签页下不成立，特此修正）；
    /// - **验证中转接入**：检出挑战 → 弹 CaptchaWindow（页面已在验证页，直接切挂）→
    ///   通过后先在当前页续抓（过验证常直接落到结果页），抓不到才重新导航一次，只重试一次；
    /// - 全程 CancellationToken 贯穿，取消时立即停止加载并返回 Cancelled。
    /// </summary>
    public static class OnlineSearchService
    {
        private static readonly HiddenBrowserService Svc = HiddenBrowserService.Instance;

        /// <summary>导航超时（页面加载不完就放弃，归为 Timeout）。</summary>
        private static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(15);

        /// <summary>等待结果出现的上限（清单规定 10s：DOMContentLoaded 后轮询抓取）。</summary>
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        /// <summary>等待轮询间隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// 判定"结构失效（Broken）"的 DOM 规模阈值：结果项一个都没抓到、页面也没有
        /// "没有找到"之类的提示、但 DOM 元素多到说明页面是正常渲染的——大概率是选择器过期。
        /// 启发式必然有模糊地带，S7 的自愈流程（EmptyResult 且页面有内容 → 置 broken）会再兜一道。
        /// </summary>
        private const int BrokenElementThreshold = 25;

        /// <summary>
        /// 验证窗口的"通过"判据替换钩子（**仅测试用**）：
        /// 默认 null → CaptchaWindow 用通用判据 + "当前页能抓到结果"。
        /// S4 自检的自动模式会注入"移除模拟挑战元素"的逻辑，实现全自动回归。
        /// </summary>
        public static Func<Task<bool>>? CaptchaVerifyHook { get; set; }

        // ══════════════════════════════ 对外入口 ══════════════════════════════

        /// <summary>
        /// 单站搜索。失败时返回带分类的 Outcome，不抛异常
        /// （用户取消以 Status = Cancelled 表示，便于调用方统一处理）。
        /// </summary>
        /// <param name="site">站点配置（含搜索模板与选择器）。</param>
        /// <param name="keyword">搜索关键词。</param>
        /// <param name="progress">进度上报（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <param name="page">页码（1 起；模板无 {p} 时忽略）。</param>
        public static async Task<OnlineSearchOutcome> SearchAsync(OnlineSite site, string keyword,
            IProgress<OnlineSearchProgress>? progress = null,
            CancellationToken ct = default, int page = 1)
        {
            Report(progress, OnlineSearchPhase.Searching, site.Name, "正在打开搜索页…");

            // ① 模板填充（{q} URL 编码，{p} 页码）
            string url = BuildSearchUrl(site, keyword, page);
            if (url.Length == 0)
            {
                return Fail(site, OnlineSearchStatus.Broken, "搜索模板没有 {q} 占位符");
            }

            try
            {
            // 站点配置完整性前置校验（缺结果项/标题选择器 = 无法搜索，直接判失效）
            if (!site.Selectors.IsUsable)
            {
                return Fail(site, OnlineSearchStatus.Broken, "站点配置不完整：缺少结果项或标题选择器");
            }

            // ② 导航 + ③ 等待 + ④ 抓取 + ⑤ 分类
            var outcome = await SearchOnceAsync(site, url, keyword, progress, ct,
                allowCaptchaPrompt: true);

                Report(progress,
                    outcome.Status == OnlineSearchStatus.Ok
                        ? OnlineSearchPhase.Completed : OnlineSearchPhase.Failed,
                    site.Name, Describe(outcome));
                return outcome;
            }
            catch (OperationCanceledException)
            {
                Svc.StopLoading();
                Report(progress, OnlineSearchPhase.Failed, site.Name, "已取消");
                return Fail(site, OnlineSearchStatus.Cancelled, "已取消");
            }
            catch (Exception ex)
            {
                // 兜底：任何意外异常都归为 NavFailed（带原始信息），不让一次站点故障炸掉整个搜索
                Report(progress, OnlineSearchPhase.Failed, site.Name, ex.Message);
                return Fail(site, OnlineSearchStatus.NavFailed, $"搜索出错：{ex.Message}");
            }
        }

        /// <summary>
        /// 多站搜索：按站点顺序执行（单标签页约束，见类注释），逐站上报进度、逐站给 Outcome。
        /// 单站失败不影响其余站点。
        /// </summary>
        public static async Task<List<OnlineSearchOutcome>> SearchAllAsync(
            IReadOnlyList<OnlineSite> sites, string keyword,
            IProgress<OnlineSearchProgress>? progress = null,
            CancellationToken ct = default, int page = 1)
        {
            var outcomes = new List<OnlineSearchOutcome>(sites.Count);
            foreach (var site in sites)
            {
                ct.ThrowIfCancellationRequested();
                outcomes.Add(await SearchAsync(site, keyword, progress, ct, page));
            }
            return outcomes;
        }

        // ══════════════════════════════ 单次搜索流程 ══════════════════════════════

        /// <summary>导航 → 等待 → 抓取 → 分类 的一次完整执行。</summary>
        /// <param name="allowCaptchaPrompt">是否允许弹人机验证窗口（重试时为 false，防死循环）。</param>
        private static async Task<OnlineSearchOutcome> SearchOnceAsync(
            OnlineSite site, string url, string keyword,
            IProgress<OnlineSearchProgress>? progress, CancellationToken ct,
            bool allowCaptchaPrompt)
        {
            // ② 导航（15s 超时；失败分类见 NavFailed / Timeout / Cancelled）
            Report(progress, OnlineSearchPhase.Searching, site.Name, "正在加载搜索结果…");
            string finalUrl;
            using (var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                navCts.CancelAfter(NavTimeout);
                try
                {
                    finalUrl = await Svc.NavigateAsync(url, navCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 是我们自己的 15s 超时，不是用户取消
                    Svc.StopLoading();
                    return Fail(site, OnlineSearchStatus.Timeout, "页面加载超时");
                }
                catch (OnlineNavigationException ex)
                {
                    return Fail(site, OnlineSearchStatus.NavFailed, $"导航失败：{ex.Status}");
                }
            }

            // ③④ 等待 + 抓取
            Report(progress, OnlineSearchPhase.Waiting, site.Name, "正在等待结果…");
            var scrape = await WaitAndExtractAsync(site, keyword, ct);

            // ⑤ 分类
            if (scrape.Items.Count > 0)
            {
                return Ok(site, scrape.Items);
            }

            // 选择器本身报错（CSS 语法非法等）→ 直接判失效
            if (scrape.SelectorError != null)
            {
                return Fail(site, OnlineSearchStatus.Broken, $"选择器无效：{scrape.SelectorError}");
            }

            // 被人机验证拦下 → 弹 S3 窗口让用户自己过，通过后续抓
            if (await Svc.DetectCaptchaAsync(ct))
            {
                if (!allowCaptchaPrompt)
                {
                    return Fail(site, OnlineSearchStatus.NeedCaptcha, "人机验证未通过");
                }

                Report(progress, OnlineSearchPhase.Searching, site.Name,
                    "该站点需要人工验证，请在弹出的窗口里完成…");
                bool passed = await ShowCaptchaWindowAsync(finalUrl);
                if (!passed)
                {
                    return Fail(site, OnlineSearchStatus.NeedCaptcha, "验证未完成，本次搜索已取消");
                }

                // 验证通过后先在当前页续抓（很多站点过验证后直接落到结果页）；
                // 抓不到再重新导航一次（allowCaptchaPrompt=false 防二次挑战死循环）
                var resumed = await WaitAndExtractAsync(site, keyword, ct);
                if (resumed.Items.Count > 0)
                {
                    return Ok(site, resumed.Items);
                }

                string retryUrl = BuildSearchUrl(site, keyword, page: 1);
                return await SearchOnceAsync(site, retryUrl, keyword, progress, ct,
                    allowCaptchaPrompt: false);
            }

            // 剩下三种：需登录 / 真没结果 / 结构失效 —— 用页面诊断区分
            return await ClassifyNoResultAsync(site, scrape, ct);
        }

        /// <summary>
        /// 等待策略：每 400ms 抓一次，最多 10s。结果出现即提前返回；
        /// 页面明确提示"没有结果"也提前返回（省时间）。
        /// </summary>
        private static async Task<ScrapeSnapshot> WaitAndExtractAsync(
            OnlineSite site, string keyword, CancellationToken ct)
        {
            string extractJs = BuildExtractScript(site.Selectors);
            string diagJs = BuildDiagnoseScript(site.Selectors);
            var deadline = DateTime.UtcNow + WaitLimit;

            ScrapeSnapshot snapshot = new();
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                snapshot = await RunScrapeAsync(extractJs, ct);

                if (snapshot.Items.Count > 0) break;                       // 搜到了
                if (snapshot.SelectorError != null) break;                 // 选择器非法，再等也没用

                var diag = await RunDiagnoseAsync(diagJs, ct);
                if (diag.HasEmptyHint) break;                              // 页面自己说没结果
                if (diag.IsLogin) break;                                   // 卡在登录页，不用再等

                // 等待期间就盯紧人机验证：一旦出现立刻交给验证窗口，不让用户干等 10s
                if (await Svc.DetectCaptchaAsync(ct)) break;

                await Task.Delay(PollInterval, ct);
            }
            return snapshot;
        }

        /// <summary>结果为 0 时的细分类：需登录 / 真没结果 / 结构失效。</summary>
        private static async Task<OnlineSearchOutcome> ClassifyNoResultAsync(
            OnlineSite site, ScrapeSnapshot scrape, CancellationToken ct)
        {
            var diag = await RunDiagnoseAsync(BuildDiagnoseScript(site.Selectors), ct);

            if (diag.IsLogin)
            {
                return Fail(site, OnlineSearchStatus.NeedLogin, "该站点需要登录");
            }

            // 页面明确说"没有找到/无结果" → 信任它
            if (diag.HasEmptyHint)
            {
                return Fail(site, OnlineSearchStatus.EmptyResult, "没有搜索到相关内容");
            }

            // 页面渲染得很完整却一个结果项都抓不到 → 大概率选择器过期
            if (diag.ElementCount >= BrokenElementThreshold)
            {
                return Fail(site, OnlineSearchStatus.Broken,
                    "页面结构可能已变化，请重新点选站点配置");
            }

            // 页面几乎是空白的 → 按"没有结果"处理（S7 自愈会再复核）
            return Fail(site, OnlineSearchStatus.EmptyResult, "没有搜索到相关内容");
        }

        // ══════════════════════════════ 人机验证中转 ══════════════════════════════

        /// <summary>
        /// 弹出验证窗口（UI 线程 ShowDialog 阻塞等待）。
        /// skipNavigate=true：页面已经停在验证页上，窗口只负责把内核切挂过来。
        /// </summary>
        private static Task<bool> ShowCaptchaWindowAsync(string currentUrl)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return Task.FromResult(false);

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(RunCaptchaWindow(currentUrl));
            }
            return dispatcher.InvokeAsync(() => RunCaptchaWindow(currentUrl)).Task;
        }

        private static bool RunCaptchaWindow(string currentUrl)
        {
            Window? owner = Application.Current?.Windows
                .OfType<Window>().FirstOrDefault(w => w.IsActive);

            var result = CaptchaWindow.Run(owner, currentUrl,
                extraVerify: CaptchaVerifyHook ?? HasResultsOnCurrentPageAsync,
                skipNavigate: true);
            return result == CaptchaResult.Passed;
        }

        /// <summary>验证窗口的"通过"判据之一：当前页面已经能抓到结果项。</summary>
        private static async Task<bool> HasResultsOnCurrentPageAsync()
        {
            var snapshot = await RunScrapeAsync(LastExtractScript, CancellationToken.None);
            return snapshot.Items.Count > 0;
        }

        /// <summary>最近一次生成的抓取脚本（验证窗口判据复用；简单缓存，线程内单流程安全）。</summary>
        private static string LastExtractScript = "";

        // ══════════════════════════════ 模板与脚本 ══════════════════════════════

        /// <summary>模板填充：{q} → URL 编码关键词，{p} → 页码。</summary>
        public static string BuildSearchUrl(OnlineSite site, string keyword, int page)
        {
            string template = (page > 1 && !string.IsNullOrWhiteSpace(site.PageTemplate))
                ? site.PageTemplate!
                : site.SearchTemplate;

            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{q}")) return "";

            return template
                .Replace("{q}", Uri.EscapeDataString(keyword ?? ""))
                .Replace("{p}", page.ToString());
        }

        /// <summary>抓取脚本返回的快照。</summary>
        private class ScrapeSnapshot
        {
            public List<OnlineResult> Items { get; } = new();
            public string? SelectorError { get; set; }
        }

        /// <summary>诊断脚本返回的快照。</summary>
        private class DiagnoseSnapshot
        {
            public bool HasEmptyHint;
            public bool IsLogin;
            public int ElementCount;
        }

        /// <summary>在当前页执行抓取脚本并解析（脚本按约定返回 JSON 字符串）。</summary>
        private static async Task<ScrapeSnapshot> RunScrapeAsync(string js, CancellationToken ct)
        {
            var snapshot = new ScrapeSnapshot();
            try
            {
                LastExtractScript = js;
                string raw = await Svc.ExecuteScriptAsync(js, ct);
                using var doc = JsonDocument.Parse(UnwrapScriptResult(raw));

                if (!doc.RootElement.TryGetProperty("error", out var err) ||
                    err.ValueKind == JsonValueKind.Null)
                {
                    if (doc.RootElement.TryGetProperty("results", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var r = new OnlineResult
                            {
                                Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                                Artist = NullIfEmpty(item, "artist"),
                                CoverUrl = NullIfEmpty(item, "cover"),
                                DetailUrl = NullIfEmpty(item, "link"),
                            };
                            if (item.TryGetProperty("duration", out var d))
                            {
                                r.Duration = ParseDurationText(d.GetString());
                            }
                            if (r.Title.Length > 0) snapshot.Items.Add(r);
                        }
                    }
                }
                else
                {
                    snapshot.SelectorError = err.GetString();
                }
            }
            catch (Exception ex)
            {
                // 页面正在跳转/被销毁时执行脚本可能失败：视为"这轮没抓到"，交给下一轮轮询
                System.Diagnostics.Debug.WriteLine($"[搜索] 抓取失败（忽略，继续轮询）: {ex.Message}");
            }
            return snapshot;
        }

        /// <summary>在当前页执行诊断脚本（空结果提示 / 登录页 / DOM 规模）。</summary>
        private static async Task<DiagnoseSnapshot> RunDiagnoseAsync(string js, CancellationToken ct)
        {
            var diag = new DiagnoseSnapshot();
            try
            {
                string raw = await Svc.ExecuteScriptAsync(js, ct);
                using var doc = JsonDocument.Parse(UnwrapScriptResult(raw));

                if (doc.RootElement.TryGetProperty("emptyHint", out var e) && e.ValueKind == JsonValueKind.True)
                    diag.HasEmptyHint = true;
                if (doc.RootElement.TryGetProperty("isLogin", out var l) && l.ValueKind == JsonValueKind.True)
                    diag.IsLogin = true;
                if (doc.RootElement.TryGetProperty("elemCount", out var c) && c.TryGetInt32(out int n))
                    diag.ElementCount = n;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[搜索] 诊断失败（按未知页面处理）: {ex.Message}");
            }
            return diag;
        }

        /// <summary>
        /// 生成抓取脚本：把选择器配置拼进一段 JS，逐项收集
        /// 标题（必填）/ 艺人 / 时长 / 封面 / 详情链接（可选），相对链接转绝对。
        /// 选择器以 JSON 字符串字面量注入，天然防注入。
        /// </summary>
        private static string BuildExtractScript(SiteSelectors sel)
        {
            string conf = JsonSerializer.Serialize(new
            {
                item = sel.Item ?? "",
                title = sel.Title ?? "",
                artist = sel.Artist ?? "",
                duration = sel.Duration ?? "",
                cover = sel.Cover ?? "",
                link = sel.Link ?? ""
            });

            return $@"(function(){{
  var conf = {conf};
  try {{
    var items = document.querySelectorAll(conf.item);
    var out = [];
    for (var i = 0; i < items.length && i < 100; i++) {{
      var it = items[i];
      var txt = function(sel) {{
        if (!sel) return '';
        var e = it.querySelector(sel);
        return e ? (e.textContent || '').trim() : '';
      }};
      var title = txt(conf.title);
      if (!title) continue;
      var cover = '';
      if (conf.cover) {{
        var c = it.querySelector(conf.cover);
        if (c) cover = c.getAttribute('src') || c.getAttribute('data-src') || '';
      }}
      var link = '';
      if (conf.link) {{
        var a = it.querySelector(conf.link);
        if (a && a.getAttribute('href')) {{
          try {{ link = new URL(a.getAttribute('href'), location.href).href; }}
          catch (e2) {{ link = a.getAttribute('href'); }}
        }}
      }}
      out.push({{ title: title, artist: txt(conf.artist), duration: txt(conf.duration),
                  cover: cover, link: link }});
    }}
    return JSON.stringify({{ error: null, results: out }});
  }} catch (err) {{
    return JSON.stringify({{ error: String(err && err.message || err), results: [] }});
  }}
}})()";
        }

        /// <summary>
        /// 生成诊断脚本：结果项数量（顺带校验选择器合法性）、
        /// "没有结果"提示文案、登录页特征、DOM 元素总数。
        /// </summary>
        private static string BuildDiagnoseScript(SiteSelectors sel)
        {
            string conf = JsonSerializer.Serialize(new { item = sel.Item ?? "" });

            return $@"(function(){{
  var conf = {conf};
  var d = {{ emptyHint: false, isLogin: false, elemCount: 0 }};
  try {{
    document.querySelectorAll(conf.item);          // 只为触发非法选择器的 SyntaxError
    d.elemCount = document.querySelectorAll('*').length;

    var text = (document.body && document.body.innerText || '').toLowerCase();
    var hints = ['没有找到', '无结果', '没有相关', '暂无相关', '未找到相关',
                 'no results', 'no result', 'not found any', 'didn.t match'];
    for (var i = 0; i < hints.length; i++) {{
      if (text.indexOf(hints[i]) >= 0) {{ d.emptyHint = true; break; }}
    }}

    // 登录页启发式：路径含登录词，或页面同时有可见密码框 + 「登录」字样
    var p = location.pathname.toLowerCase();
    if (/(login|signin|sign-in|passport|auth)/.test(p)) {{
      d.isLogin = true;
    }} else {{
      var pw = document.querySelector('input[type=password]');
      if (pw && text.indexOf('登录') >= 0) d.isLogin = true;
    }}
  }} catch (err) {{ }}
  return JSON.stringify(d);
}})()";
        }

        // ══════════════════════════════ 工具函数 ══════════════════════════════

        /// <summary>
        /// ExecuteScriptAsync 的返回值是"脚本返回值的 JSON 表示"：
        /// 我们脚本返回 JSON 字符串，所以外层会再包一层引号——这里剥掉外层。
        /// </summary>
        private static string UnwrapScriptResult(string raw)
        {
            raw = raw?.Trim() ?? "null";
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(raw) ?? "null";
                }
                catch (JsonException) { return raw; }
            }
            return raw;
        }

        private static string? NullIfEmpty(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out var v)) return null;
            string s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return s.Length == 0 ? null : s;
        }

        /// <summary>解析时长文本："03:45" / "1:02:03" / "3分45秒"，失败返回 Zero。</summary>
        public static TimeSpan ParseDurationText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;

            // 中式写法 "3分45秒" / "1时02分03秒" → 冒号格式
            // ⚠ 坑：命名组替换必须写 ${name}——实测 $name（不带花括号）会被当字面量输出
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"(?<h>\d+)\s*[时小点]\s*(?<m>\d+)\s*分\s*(?<s>\d+)\s*秒?", "${h}:${m}:${s}");
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"(?<m>\d+)\s*分\s*(?<s>\d+)\s*秒?", "${m}:${s}");

            var parts = text.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                return parts.Length switch
                {
                    2 => new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1])),
                    3 => new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])),
                    _ => TimeSpan.Zero
                };
            }
            catch (FormatException)
            {
                return TimeSpan.Zero;
            }
        }

        /// <summary>结果项的可下载初判：有详情页链接就认为可下载（音频探测在 S8 播放时做）。</summary>
        private static OnlineSearchOutcome Ok(OnlineSite site, List<OnlineResult> items)
        {
            foreach (var r in items)
            {
                r.SiteId = site.Id;
                r.SiteName = site.Name;
                r.CanDownload = !string.IsNullOrWhiteSpace(r.DetailUrl);
                r.NotDownloadReason = r.CanDownload ? "" : "未找到详情页链接，无法探测音频";
            }
            return new OnlineSearchOutcome
            {
                Site = site,
                Status = OnlineSearchStatus.Ok,
                Results = items,
                Message = $"找到 {items.Count} 条结果"
            };
        }

        private static OnlineSearchOutcome Fail(OnlineSite site, OnlineSearchStatus status, string message) =>
            new() { Site = site, Status = status, Message = message };

        private static void Report(IProgress<OnlineSearchProgress>? progress,
            OnlineSearchPhase phase, string siteName, string message) =>
            progress?.Report(new OnlineSearchProgress
            {
                Phase = phase,
                SiteName = siteName,
                Message = message
            });

        private static string Describe(OnlineSearchOutcome o) =>
            o.Status switch
            {
                OnlineSearchStatus.Ok => o.Message,
                _ => $"{o.Status}: {o.Message}"
            };
    }
}
