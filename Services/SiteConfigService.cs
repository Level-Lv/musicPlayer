using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace musicPlayer.Services
{
    /// <summary>模板反推结果（第 1 步用）。</summary>
    public class TemplateInferResult
    {
        /// <summary>是否成功反推出模板。</summary>
        public bool Ok { get; init; }

        /// <summary>反推出的模板（含 {q}）；失败时为空。</summary>
        public string Template { get; init; } = "";

        /// <summary>失败原因（直接展示给用户）。</summary>
        public string Message { get; init; } = "";
    }

    /// <summary>站点页面的基础信息（标题 / 名称 / 图标）。</summary>
    public class PageMeta
    {
        /// <summary>页面 &lt;title&gt; 原文。</summary>
        public string Title { get; init; } = "";

        /// <summary>推测的站点名（优先 og:site_name，其次标题）。</summary>
        public string SiteName { get; init; } = "";

        /// <summary>favicon 绝对地址（可能为空）。</summary>
        public string? FaviconUrl { get; init; }
    }

    /// <summary>自动猜选择器的结果。</summary>
    public class AutoGuessResult
    {
        public bool Ok { get; init; }

        /// <summary>结果项容器选择器。</summary>
        public string Item { get; init; } = "";

        /// <summary>页面上匹配到的结果项数量（判断猜得对不对的重要信号）。</summary>
        public int Count { get; init; }

        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Duration { get; init; } = "";
        public string Cover { get; init; } = "";
        public string Link { get; init; } = "";

        /// <summary>容器内第一条的文本（给用户看"猜的是哪一块"）。</summary>
        public string SampleText { get; init; } = "";
    }

    /// <summary>页面上一次点选的结果。</summary>
    public class PickResult
    {
        public bool Ok { get; init; }
        public string Path { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Text { get; init; } = "";

        /// <summary>失败原因：outside = 点在结果项外面（字段点选时）。</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// 添加站点向导的"配置能力"层（《在线曲库开发步骤清单》S5）。
    ///
    /// 把三件与界面无关的事独立出来，方便单独调试、也方便 S7 复用：
    /// 1. **模板反推**：用户粘贴"搜索后的网址" + 当时的关键词 → 反推含 {q} 的模板；
    /// 2. **页面信息**：HttpClient 取 &lt;title&gt;/og:site_name/favicon（只为了给站点起个默认名字）；
    /// 3. **选择器脚本**：自动猜（重复容器 + 启发式子元素）、可视化点选（悬停描边 + 点击回传路径）、
    ///    读取点选结果。
    ///
    /// 选择器路径策略（稳定性优先）：
    /// - 结果项要"能匹配多条"，所以优先用 `tag.class1.class2` 这种**重复型**选择器；
    /// - 字段（标题/艺人/…）是相对结果项查的，用 `> ` 逐级拼到容器为止；
    /// - class 里明显是构建哈希（css-1x2y3z、_3aBcD1234）的会被剔掉，避免换版本就失效。
    /// </summary>
    public static class SiteConfigService
    {
        // ══════════════════════════════ ① 模板反推 ══════════════════════════════

        /// <summary>
        /// 从"搜索结果页网址"反推搜索模板。
        /// 关键词在网址里可能是原样、百分号编码（大小写两种）、或空格变 +，这里都会试。
        /// </summary>
        public static TemplateInferResult InferTemplate(string rawUrl, string keyword)
        {
            string url = (rawUrl ?? "").Trim();
            if (url.Length == 0) return Fail("请先粘贴搜索结果页的网址");

            // 已经是模板（用户手动改过）就直接认
            if (url.Contains("{q}"))
                return new TemplateInferResult { Ok = true, Template = url };

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 只粘了 "music.163.com/#/search?..." 这种，补个协议
                if (url.Contains(".") && !url.Contains(" "))
                    url = "https://" + url;
                else
                    return Fail("网址格式不对，需要以 http:// 或 https:// 开头");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return Fail("网址解析失败，请检查是否复制完整");

            string kw = (keyword ?? "").Trim();
            if (kw.Length == 0)
                return Fail("请填写当时搜索用的关键词——程序靠它找到 {q} 该放在哪");

            // ── 策略 A：查询参数的值正好等于关键词（最常见，也最准确）──
            string query = uri.Query;
            if (query.Length > 1)
            {
                string[] pairs = query.Substring(1).Split('&');
                for (int i = 0; i < pairs.Length; i++)
                {
                    int eq = pairs[i].IndexOf('=');
                    if (eq <= 0) continue;

                    string name = pairs[i].Substring(0, eq);
                    string value = pairs[i].Substring(eq + 1);
                    if (DecodeQueryValue(value) != kw) continue;

                    pairs[i] = name + "={q}";
                    string rebuilt = uri.GetLeftPart(UriPartial.Path)
                                     + "?" + string.Join("&", pairs)
                                     + uri.Fragment;
                    return new TemplateInferResult { Ok = true, Template = rebuilt };
                }
            }

            // ── 策略 B：关键词被百分号编码后出现在路径/查询里（中文最常见的形态）──
            string encoded = Uri.EscapeDataString(kw);
            if (!string.Equals(encoded, kw, StringComparison.Ordinal))
            {
                string replaced = Regex.Replace(url, Regex.Escape(encoded), "{q}",
                    RegexOptions.IgnoreCase);
                if (!string.Equals(replaced, url, StringComparison.Ordinal))
                    return new TemplateInferResult { Ok = true, Template = replaced };
            }

            // ── 策略 C：关键词原样出现在网址里（英文站常见；路径里直接写中文也会走到这里）──
            // 只对"够长"或"含非 ASCII"的关键词启用：像 "ab" 这种会把 about/table 里的字母也换掉
            bool safe = kw.Length >= 3 || kw.Any(c => c > 127);
            if (safe)
            {
                int hits = Regex.Matches(url, Regex.Escape(kw), RegexOptions.IgnoreCase).Count;
                if (hits >= 1 && hits <= 3)
                {
                    string replaced = Regex.Replace(url, Regex.Escape(kw), "{q}",
                        RegexOptions.IgnoreCase);
                    return new TemplateInferResult { Ok = true, Template = replaced };
                }
            }

            return Fail("网址里找不到这个关键词。请确认关键词与网址来自同一次搜索，" +
                        "或直接把网址里的关键词改成 {q}");
        }

        private static TemplateInferResult Fail(string message) =>
            new TemplateInferResult { Ok = false, Message = message };

        /// <summary>查询参数值是 URL 编码的：先解 + 为空格再解码，才能和用户输入的关键词比。</summary>
        private static string DecodeQueryValue(string value)
        {
            try { return Uri.UnescapeDataString(value.Replace("+", " ")); }
            catch { return value; }
        }

        /// <summary>
        /// 把模板填成具体网址（向导预览、连通性测试共用；等价于 S4 的 BuildSearchUrl 单页版）。
        /// </summary>
        public static string FillTemplate(string template, string keyword, int page = 1) =>
            (template ?? "")
                .Replace("{q}", Uri.EscapeDataString(keyword ?? ""))
                .Replace("{p}", page.ToString());

        /// <summary>从网址里取一个像样的默认站点名（域名主体）。</summary>
        public static string SuggestNameFromUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    string host = uri.Host;
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                        host = host.Substring(4);
                    return host.Length > 0 ? host : "新站点";
                }
            }
            catch { /* 解析失败就走兜底 */ }
            return "新站点";
        }

        // ══════════════════════════════ ② 页面信息（HttpClient）══════════════════════════════

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            // 用普通浏览器 UA：不少站点对空 UA 直接返回 403/验证页
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language",
                "zh-CN,zh;q=0.9,en;q=0.8");
            return client;
        }

        /// <summary>
        /// 用 HttpClient 抓页面 &lt;title&gt; / og:site_name / favicon（失败不抛异常，返回空）。
        /// 只读前 200KB：这些信息都在 &lt;head&gt; 里，没必要拉整页。
        /// </summary>
        public static async Task<PageMeta> FetchPageMetaAsync(string url, CancellationToken ct = default)
        {
            string html = "";
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                    var buffer = new char[200_000];
                    int read = await reader.ReadAsync(buffer, ct);
                    html = new string(buffer, 0, Math.Max(0, read));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[向导] 取页面信息失败: {ex.Message}");
            }

            string title = FirstGroup(html, @"<title[^>]*>(.*?)</title>");
            string siteName = FirstGroup(html, @"<meta[^>]+property\s*=\s*[""']og:site_name[""'][^>]*>");

            // og:site_name 的 content 在标签属性里，单独再取一次
            if (siteName.Length == 0)
            {
                var ogTag = Regex.Match(html,
                    @"<meta[^>]+property\s*=\s*[""']og:site_name[""'][^>]*>",
                    RegexOptions.IgnoreCase);
                if (ogTag.Success)
                    siteName = AttrValue(ogTag.Value, "content");
            }

            title = CleanText(title);
            siteName = CleanText(siteName);
            if (siteName.Length == 0) siteName = title;
            if (siteName.Length > 30) siteName = siteName.Substring(0, 30);

            return new PageMeta
            {
                Title = title,
                SiteName = siteName,
                FaviconUrl = ExtractFavicon(html, url)
            };
        }

        private static string FirstGroup(string html, string pattern)
        {
            var m = Regex.Match(html ?? "", pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string AttrValue(string tag, string attr)
        {
            var m = Regex.Match(tag, attr + @"\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>HTML 实体解码 + 空白折叠（标题里常见 &amp;nbsp; / 换行）。</summary>
        private static string CleanText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string text = WebUtility.HtmlDecode(raw);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        /// <summary>取 favicon：优先 &lt;link rel="...icon..."&gt;，其次 /favicon.ico。</summary>
        private static string? ExtractFavicon(string html, string pageUrl)
        {
            try
            {
                foreach (Match link in Regex.Matches(html ?? "", @"<link\b[^>]*>",
                             RegexOptions.IgnoreCase))
                {
                    string tag = link.Value;
                    string rel = AttrValue(tag, "rel");
                    if (rel.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string href = AttrValue(tag, "href");
                    if (href.Length == 0) continue;

                    return new Uri(new Uri(pageUrl), href).AbsoluteUri;
                }

                var baseUri = new Uri(pageUrl);
                return baseUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico";
            }
            catch { return null; }
        }

        // ══════════════════════════════ ③ 自动猜选择器 ══════════════════════════════

        /// <summary>
        /// 生成"自动猜"脚本。
        /// itemSelector 为空 → 先在整页里找重复出现、内部含链接的容器（结果项）；
        /// itemSelector 有值 → 只在该容器里猜子元素（点完结果项后立刻补全标题/链接等）。
        /// </summary>
        public static string BuildAutoGuessScript(string? itemSelector = null)
        {
            string forced = JsonSerializer.Serialize(itemSelector ?? "");
            return "(function(){\n  var forced = " + forced + ";\n" + AutoGuessBody;
        }

        /// <summary>自动猜脚本主体（纯 JS；与上面拼成完整脚本）。</summary>
        private const string AutoGuessBody = """
  function classes(el) {
    var c = el.className;
    if (!c || typeof c !== 'string') return [];
    var list = c.split(/\s+/), out = [];
    for (var i = 0; i < list.length; i++) {
      var s = list[i];
      if (!s || s.length > 28) continue;
      if (!/^[A-Za-z_][\w-]*$/.test(s)) continue;        // 排除奇怪的字符
      if (/[0-9a-f]{8,}/i.test(s)) continue;             // 排除构建哈希
      if (/^[a-z]{1,3}\d{4,}$/i.test(s)) continue;       // 排除 css-1a2b3
      out.push(s);
      if (out.length >= 2) break;
    }
    return out;
  }
  function sig(el) {
    var cs = classes(el);
    if (!cs.length) return '';
    return el.tagName.toLowerCase() + '.' + cs.join('.');
  }
  function step(el) {
    var p = el.tagName.toLowerCase();
    var cs = classes(el);
    if (cs.length) p += '.' + cs.join('.');
    var parent = el.parentElement;
    if (parent) {
      var same = 0;
      for (var i = 0; i < parent.children.length; i++) {
        if (parent.children[i].tagName === el.tagName) same++;
      }
      if (same > 1) {
        var idx = 1, s = el;
        while ((s = s.previousElementSibling)) {
          if (s.tagName === el.tagName) idx++;
        }
        p += ':nth-of-type(' + idx + ')';
      }
    }
    return p;
  }
  function relPath(el, root) {
    if (!el || el === root) return '';
    var parts = [], cur = el;
    while (cur && cur !== root && parts.length < 6) {
      parts.unshift(step(cur));
      cur = cur.parentElement;
    }
    return cur === root ? parts.join(' > ') : '';
  }
  function countOf(sel) {
    try { return document.querySelectorAll(sel).length; } catch (e) { return -1; }
  }
  function leafTexts(root) {
    var out = [], nodes = root.querySelectorAll('*');
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i];
      if (n.children.length > 2) continue;
      var t = (n.textContent || '').trim();
      if (!t || t.length > 120) continue;
      out.push({ el: n, text: t });
    }
    return out;
  }

  try {
    var best = null;

    if (forced) {
      var n = countOf(forced);
      if (n <= 0) return JSON.stringify({ ok: false, reason: 'not-found' });
      best = { sel: forced, count: n, sample: document.querySelector(forced) };
    } else {
      // ① 统计"内部有链接、有多个子元素"的重复容器
      var groups = {}, all = document.body ? document.body.querySelectorAll('*') : [];
      for (var i = 0; i < all.length; i++) {
        var el = all[i];
        if (el.children.length < 2) continue;
        if (!el.querySelector('a[href]')) continue;
        var s = sig(el);
        if (!s) continue;
        var g = groups[s] || (groups[s] = { sel: s, count: 0, sample: el });
        g.count++;
      }
      // ② 选出最像"结果列表"的一组：条数适中优先，其次选择器越具体越好
      for (var k in groups) {
        var g = groups[k];
        if (g.count < 3) continue;
        var score = g.count;
        if (g.count <= 60) score += 30;
        if (g.count > 400) score -= 200;
        score += g.sel.length / 10;
        if (!best || score > best.score) { g.score = score; best = g; }
      }
      if (!best) {
        // 兜底：li/tr/article 直接重复也算（有些站的行容器没有 class）
        var tags = ['li', 'tr', 'article'];
        for (var ti = 0; ti < tags.length; ti++) {
          var c = countOf(tags[ti]);
          if (c >= 3 && c <= 300) { best = { sel: tags[ti], count: c, sample: document.querySelector(tags[ti]) }; break; }
        }
      }
      if (!best) return JSON.stringify({ ok: false, reason: 'no-repeat-container' });
    }

    var sample = best.sample;
    var title = '', duration = '', cover = '', link = '';

    // 标题：容器内文本最长的叶子节点
    var leaves = leafTexts(sample), bestLeaf = null;
    for (var j = 0; j < leaves.length; j++) {
      if (!bestLeaf || leaves[j].text.length > bestLeaf.text.length) bestLeaf = leaves[j];
    }
    if (bestLeaf) title = relPath(bestLeaf.el, sample);

    // 详情链接：第一个带 href 的 a
    var a = sample.querySelector('a[href]');
    if (a) link = relPath(a, sample);

    // 封面：第一个 img
    var img = sample.querySelector('img');
    if (img) cover = relPath(img, sample);

    // 时长：形如 03:45 / 1:02:03 / 03分45秒
    for (var m = 0; m < leaves.length; m++) {
      var t = leaves[m].text;
      if (/^(\d{1,2}:)?\d{1,2}:\d{2}$/.test(t) || /^\d{1,2}分\d{1,2}秒?$/.test(t)) {
        duration = relPath(leaves[m].el, sample);
        break;
      }
    }

    // 标题若和链接/封面撞成同一个元素，优先保留标题（链接仍可点到，只是选择器相同）
    return JSON.stringify({
      ok: true,
      item: best.sel,
      count: best.count,
      title: title,
      artist: '',
      duration: duration,
      cover: cover,
      link: link,
      sampleText: (sample.textContent || '').trim().slice(0, 80)
    });
  } catch (err) {
    return JSON.stringify({ ok: false, reason: String(err && err.message || err) });
  }
})()
""";

        // ══════════════════════════════ ④ 可视化点选 ══════════════════════════════

        /// <summary>
        /// 生成"点选"脚本：鼠标悬停描边 + 点击拦截（不让页面跳走）→ 把选择器写进 window.__mpPick。
        ///
        /// role=item：点到的元素会**向上扩张**到"重复出现的行容器"（用户常点在标题文字上，
        ///            但真正想要的是整行）；role=field：只接受结果项内部的元素，路径相对结果项。
        /// </summary>
        /// <param name="rootSelector">结果项选择器；为空表示正在点结果项本身。</param>
        public static string BuildPickerScript(string? rootSelector = null)
        {
            string root = JsonSerializer.Serialize(rootSelector ?? "");
            return "(function(){\n  var CONF = { root: " + root + " };\n" + PickerBody;
        }

        private const string PickerBody = """
  function classes(el) {
    var c = el.className;
    if (!c || typeof c !== 'string') return [];
    var list = c.split(/\s+/), out = [];
    for (var i = 0; i < list.length; i++) {
      var s = list[i];
      if (!s || s.length > 28) continue;
      if (!/^[A-Za-z_][\w-]*$/.test(s)) continue;
      if (/[0-9a-f]{8,}/i.test(s)) continue;
      if (/^[a-z]{1,3}\d{4,}$/i.test(s)) continue;
      out.push(s);
      if (out.length >= 2) break;
    }
    return out;
  }
  function sig(el) {
    var cs = classes(el);
    return cs.length ? el.tagName.toLowerCase() + '.' + cs.join('.') : '';
  }
  function step(el) {
    var p = el.tagName.toLowerCase();
    var cs = classes(el);
    if (cs.length) p += '.' + cs.join('.');
    var parent = el.parentElement;
    if (parent) {
      var same = 0;
      for (var i = 0; i < parent.children.length; i++) {
        if (parent.children[i].tagName === el.tagName) same++;
      }
      if (same > 1) {
        var idx = 1, s = el;
        while ((s = s.previousElementSibling)) {
          if (s.tagName === el.tagName) idx++;
        }
        p += ':nth-of-type(' + idx + ')';
      }
    }
    return p;
  }
  function relPath(el, root) {
    if (!el || el === root) return '';
    var parts = [], cur = el;
    while (cur && cur !== root && parts.length < 6) {
      parts.unshift(step(cur));
      cur = cur.parentElement;
    }
    return cur === root ? parts.join(' > ') : '';
  }
  function countOf(sel) {
    try { return document.querySelectorAll(sel).length; } catch (e) { return -1; }
  }
  // 结果项的路径：优先"能匹配多条"的重复型选择器
  function itemPath(el) {
    var s = sig(el);
    if (s) {
      var n = countOf(s);
      if (n >= 2 && n <= 500) return s;
    }
    // 兜底：从自己往上拼，直到唯一（这种只能选中这一条，但总比没有强）
    var parts = [], cur = el;
    while (cur && cur !== document.documentElement && parts.length < 6) {
      parts.unshift(step(cur));
      var sel = parts.join(' > ');
      var c = countOf(sel);
      if (c === 1) return sel;
      cur = cur.parentElement;
    }
    return parts.join(' > ');
  }
  // 从点到的元素向上找"重复出现的行容器"，让点击标题也能选中整行。
  // 判据：同一选择器在页面里出现 3~300 次（少了不像列表，多了多半是按钮/图标之流）；
  // 越靠上越优先（标题 > 整行，但整行通常才是我们想要的）。
  function expandToRow(el) {
    var bestEl = el, bestCount = 0, cur = el;
    for (var i = 0; i < 6; i++) {
      if (!cur || cur === document.body || cur === document.documentElement) break;
      var s = sig(cur);
      if (s) {
        var c = countOf(s);
        if (c >= 3 && c <= 300 && c >= bestCount) { bestEl = cur; bestCount = c; }
      }
      cur = cur.parentElement;
    }
    return bestEl;
  }

  // 清掉上一次的拾取器（重复注入时不会叠出两层遮罩）
  try { if (window.__mpPicker && window.__mpPicker.stop) window.__mpPicker.stop(); } catch (e) { }
  window.__mpPick = null;

  var box = document.createElement('div');
  box.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;pointer-events:none;' +
    'z-index:2147483647;border:2px solid #FFD700;background:rgba(255,215,0,0.12);' +
    'border-radius:3px;display:none;box-sizing:border-box';
  var tip = document.createElement('div');
  tip.style.cssText = 'position:fixed;left:0;top:0;pointer-events:none;z-index:2147483647;' +
    'background:#FFD700;color:#1a1a1a;font:11px/16px Consolas,monospace;padding:1px 5px;' +
    'border-radius:3px;display:none;max-width:460px;overflow:hidden;white-space:nowrap';
  document.documentElement.appendChild(box);
  document.documentElement.appendChild(tip);

  function target(e) {
    var t = e.target;
    if (!t || t.nodeType !== 1 || t === box || t === tip) return null;
    return t;
  }
  function onMove(e) {
    var t = target(e);
    if (!t) return;
    var r = t.getBoundingClientRect();
    box.style.display = 'block';
    box.style.left = r.left + 'px';
    box.style.top = r.top + 'px';
    box.style.width = Math.max(0, r.width) + 'px';
    box.style.height = Math.max(0, r.height) + 'px';

    var cs = classes(t);
    tip.textContent = t.tagName.toLowerCase() + (cs.length ? '.' + cs.join('.') : '');
    tip.style.display = 'block';
    tip.style.left = Math.max(0, r.left) + 'px';
    tip.style.top = Math.max(0, r.top - 19) + 'px';
  }
  function swallow(e) {
    if (!target(e)) return;
    e.preventDefault();
    e.stopPropagation();
  }
  function onClick(e) {
    var t = target(e);
    if (!t) return;
    e.preventDefault();
    e.stopPropagation();
    e.stopImmediatePropagation();

    if (CONF.root) {
      var rootEl = null;
      try { rootEl = t.closest(CONF.root); } catch (err) { rootEl = null; }
      if (!rootEl) { window.__mpPick = { ok: false, reason: 'outside' }; return false; }
      var path = relPath(t, rootEl);
      if (!path) { window.__mpPick = { ok: false, reason: 'is-root' }; return false; }
      window.__mpPick = { ok: true, path: path, tag: t.tagName.toLowerCase(),
                          text: (t.textContent || '').trim().slice(0, 60) };
    } else {
      var row = expandToRow(t);
      window.__mpPick = { ok: true, path: itemPath(row), tag: row.tagName.toLowerCase(),
                          text: (row.textContent || '').trim().slice(0, 60) };
    }
    return false;
  }

  window.__mpPicker = { stop: function() {
    document.removeEventListener('mousemove', onMove, true);
    document.removeEventListener('mousedown', swallow, true);
    document.removeEventListener('click', onClick, true);
    if (box.parentNode) box.parentNode.removeChild(box);
    if (tip.parentNode) tip.parentNode.removeChild(tip);
  } };
  document.addEventListener('mousemove', onMove, true);
  document.addEventListener('mousedown', swallow, true);
  document.addEventListener('click', onClick, true);
  return 'ok';
})()
""";

        /// <summary>读取并清空一次点选结果（向导每 250ms 轮询一次）。</summary>
        public const string ReadPickScript =
            "(function(){ var p = window.__mpPick; window.__mpPick = null;" +
            " return JSON.stringify(p || { ok: false }); })()";

        /// <summary>关闭点选模式（切步骤/关窗时调用；页面跳转后本来就没了）。</summary>
        public const string StopPickerScript =
            "(function(){ try { if (window.__mpPicker && window.__mpPicker.stop) window.__mpPicker.stop(); }" +
            " catch(e) { } window.__mpPicker = null; window.__mpPick = null; return 'ok'; })()";

        // ══════════════════════════════ ⑤ 结果解析 ══════════════════════════════

        /// <summary>解析自动猜脚本的返回值（失败返回 null）。</summary>
        public static AutoGuessResult? ParseAutoGuess(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(Unwrap(raw));
                var root = doc.RootElement;

                // 脚本没返回值时是 null / 字符串 / 数组——TryGetProperty 只对 Object 有效，
                // 对 null 会抛 InvalidOperationException（不是 JsonException），必须在这里挡掉
                if (root.ValueKind != JsonValueKind.Object) return null;

                return new AutoGuessResult
                {
                    Ok = GetBool(root, "ok"),
                    Item = GetString(root, "item"),
                    Count = GetInt(root, "count"),
                    Title = GetString(root, "title"),
                    Artist = GetString(root, "artist"),
                    Duration = GetString(root, "duration"),
                    Cover = GetString(root, "cover"),
                    Link = GetString(root, "link"),
                    SampleText = GetString(root, "sampleText")
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>解析点选脚本的返回值（未点选时 Ok=false）。</summary>
        public static PickResult ParsePick(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(Unwrap(raw));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new PickResult();

                return new PickResult
                {
                    Ok = GetBool(root, "ok"),
                    Path = GetString(root, "path"),
                    Tag = GetString(root, "tag"),
                    Text = GetString(root, "text"),
                    Reason = GetString(root, "reason")
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return new PickResult();
            }
        }

        /// <summary>
        /// ExecuteScriptAsync 的返回值是"脚本返回值的 JSON 表示"：
        /// 脚本返回字符串时，外层会多包一层引号——这里剥掉。
        /// </summary>
        public static string Unwrap(string? raw)
        {
            raw = (raw ?? "null").Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                try { return JsonSerializer.Deserialize<string>(raw) ?? "null"; }
                catch (JsonException) { return raw; }
            }
            return raw;
        }

        private static string GetString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static bool GetBool(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static int GetInt(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
    }
}
