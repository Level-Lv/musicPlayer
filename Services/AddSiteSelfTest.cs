using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// 添加站点向导的自检（《在线曲库开发步骤清单》S5）。
    ///
    /// 覆盖三块，全部离线（本地模拟页），可长期回归：
    /// 1. **模板反推**：中文百分号编码（大小写）/ 英文 + 号 / 路径里直接写中文 / 找不到关键词 / 已是模板；
    /// 2. **选择器**：自动猜（重复容器 + 子元素启发式）、点选脚本的路径计算（含"点标题自动圈住整行"）、
    ///    点选结果解析、非法输入兜底；
    /// 3. **端到端**：用猜出来的配置跑一次真实搜索（走 S4 的完整流程）→ 3 条结果；
    ///    再写入 online_sites 读回校验（字段/选择器 JSON 往返），最后删掉，不留脏数据。
    ///
    /// 界面本身的视觉效果（三步卡片、右侧预览、点选遮罩）由 MP_ONLINE_WIZARD_TEST=2 手动看。
    /// </summary>
    public static class AddSiteSelfTest
    {
        private const string TempSiteName = "S5向导自检站";

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

            var svc = HiddenBrowserService.Instance;
            long createdId = 0;

            try
            {
                // ══════════════ ① 模板反推（纯字符串逻辑，不需要内核）══════════════
                log("── 模板反推 ──");

                var r1 = SiteConfigService.InferTemplate(
                    "https://y.qq.com/n/ryqq/search?w=%E5%A4%9C%E6%9B%B2&t=song", "夜曲");
                Check("中文关键词（大写百分号编码）",
                    r1.Ok && r1.Template == "https://y.qq.com/n/ryqq/search?w={q}&t=song",
                    r1.Ok ? r1.Template : r1.Message);

                var r2 = SiteConfigService.InferTemplate(
                    "https://music.example/search?keyword=%e5%a4%9c%e6%9b%b2&type=1", "夜曲");
                Check("中文关键词（小写百分号编码）",
                    r2.Ok && r2.Template.Contains("keyword={q}") && r2.Template.Contains("type=1"),
                    r2.Ok ? r2.Template : r2.Message);

                var r3 = SiteConfigService.InferTemplate(
                    "https://song.example/search?q=hello+world&page=1", "hello world");
                Check("英文 + 号编码", r3.Ok && r3.Template.Contains("q={q}"),
                    r3.Ok ? r3.Template : r3.Message);

                var r4 = SiteConfigService.InferTemplate(
                    "https://x.example/search/夜曲/list", "夜曲");
                Check("路径里直接写中文", r4.Ok && r4.Template == "https://x.example/search/{q}/list",
                    r4.Ok ? r4.Template : r4.Message);

                var r5 = SiteConfigService.InferTemplate(
                    "music.163.com/#/search?q=%E5%A4%9C%E6%9B%B2", "夜曲");
                Check("只粘贴域名（自动补 https://）",
                    r5.Ok && r5.Template.StartsWith("https://"),
                    r5.Ok ? "已补全" : r5.Message);

                var r6 = SiteConfigService.InferTemplate("https://x.example/search?q=abc", "夜曲");
                Check("网址里没有关键词 → 明确报错", !r6.Ok && r6.Message.Length > 0, r6.Message);

                var r7 = SiteConfigService.InferTemplate("https://x.example/s?q={q}", "随便");
                Check("已经是模板 → 原样保留", r7.Ok && r7.Template == "https://x.example/s?q={q}",
                    r7.Template);

                string filled = SiteConfigService.FillTemplate(
                    "https://x.example/s?q={q}&p={p}", "夜 曲", 2);
                Check("模板填充（编码 + 页码）",
                    filled == "https://x.example/s?q=%E5%A4%9C%20%E6%9B%B2&p=2", filled);

                Check("默认站点名取域名主体",
                    SiteConfigService.SuggestNameFromUrl("https://www.163.com/a") == "163.com",
                    SiteConfigService.SuggestNameFromUrl("https://www.163.com/a"));

                // ══════════════ ② 选择器（本地模拟页）══════════════
                log("\n── 自动猜选择器 ──");
                await svc.InitAsync();

                var (resultsUrl, _, _) = OnlineSearchSelfTest.PrepareDemoPages();
                string pageUrl = resultsUrl + "?q=" + Uri.EscapeDataString("夜曲");
                await svc.NavigateAsync(pageUrl, ct);
                log($"  页面 = {pageUrl}");

                string guessRaw = await svc.ExecuteScriptAsync(SiteConfigService.BuildAutoGuessScript(), ct);
                var guess = SiteConfigService.ParseAutoGuess(guessRaw);
                Check("自动猜脚本有返回", guess != null && guess.Ok,
                    guess == null ? "解析失败" : "ok");

                if (guess == null || !guess.Ok)
                {
                    Bad("自动猜失败，后续用例无法继续");
                    log($"\n结果：{pass} 通过 / {fail} 失败");
                    return false;
                }

                Check("猜出结果项选择器", guess.Item.Length > 0, guess.Item);
                int itemCount = await CountAsync(svc, guess.Item, ct);
                Check("结果项能匹配 3 条", itemCount == 3, $"{itemCount} 条");
                Check("猜出标题选择器", guess.Title.Length > 0, guess.Title);
                Check("猜出时长选择器", guess.Duration.Length > 0, guess.Duration);

                // 用猜出来的配置抓一次：这就是向导右侧"实时预览"走的那条路
                var sel = new SiteSelectors
                {
                    Item = guess.Item,
                    Title = guess.Title,
                    Artist = guess.Artist,
                    Duration = guess.Duration,
                    Cover = guess.Cover,
                    Link = guess.Link
                };

                var (items, selError) = await OnlineSearchService.ExtractOnceAsync(sel, ct);
                Check("预览抓取 3 条", items.Count == 3, $"{items.Count} 条" + (selError != null ? " / " + selError : ""));
                if (items.Count > 0)
                {
                    Check("首条标题含关键词", items[0].Title.Contains("夜曲"), items[0].Title);
                    Check("时长解析（03:41 → 221 秒）",
                        Math.Abs(items[0].Duration.TotalSeconds - 221) < 0.5,
                        items[0].Duration.ToString());
                    Check("详情链接已转绝对",
                        items[0].DetailUrl != null && items[0].DetailUrl!.StartsWith("file://"),
                        items[0].DetailUrl ?? "空");
                }

                // ══════════════ ③ 点选脚本 ══════════════
                log("\n── 点选脚本 ──");

                // 结果项：点在标题文字上，应自动圈到整行（div.song），且匹配 3 条
                await svc.ExecuteScriptAsync(SiteConfigService.BuildPickerScript(""), ct);
                string clickResult = await svc.ExecuteScriptAsync(
                    "(function(){ var el = document.querySelector('.song .t');" +
                    " if (!el) return 'no-el';" +
                    " el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));" +
                    " return 'clicked'; })()", ct);

                var pickItem = SiteConfigService.ParsePick(
                    await svc.ExecuteScriptAsync(SiteConfigService.ReadPickScript, ct));

                Check("模拟点击命中页面元素", clickResult.Contains("clicked"), clickResult);
                Check("点选结果可读", pickItem.Ok, pickItem.Reason);
                int pickedCount = pickItem.Ok ? await CountAsync(svc, pickItem.Path, ct) : 0;
                Check("点标题自动圈住整行（匹配 3 条）", pickedCount == 3,
                    $"{pickItem.Path} → {pickedCount} 条");

                // 字段：在整行内部点标题，应得到相对行容器的路径
                string rowSelector = pickItem.Path;
                await svc.ExecuteScriptAsync(SiteConfigService.BuildPickerScript(rowSelector), ct);
                await svc.ExecuteScriptAsync(
                    "(function(){ var rows = document.querySelectorAll(" +
                    JsonSerializer.Serialize(rowSelector) + ");" +
                    " if (!rows.length) return 'no-row';" +
                    " var el = rows[0].querySelector('.t'); if (!el) return 'no-el';" +
                    " el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));" +
                    " return 'clicked'; })()", ct);

                var pickField = SiteConfigService.ParsePick(
                    await svc.ExecuteScriptAsync(SiteConfigService.ReadPickScript, ct));
                Check("字段点选返回相对路径",
                    pickField.Ok && pickField.Path.Contains(".t") && !pickField.Path.Contains("div.song"),
                    pickField.Ok ? pickField.Path : pickField.Reason);

                // 点在结果项外面：应明确拒绝（reason=outside），而不是给出一个坏选择器
                await svc.ExecuteScriptAsync(SiteConfigService.BuildPickerScript(rowSelector), ct);
                await svc.ExecuteScriptAsync(
                    "(function(){ var el = document.querySelector('footer span'); if (!el) return 'no-el';" +
                    " el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));" +
                    " return 'clicked'; })()", ct);
                var pickOutside = SiteConfigService.ParsePick(
                    await svc.ExecuteScriptAsync(SiteConfigService.ReadPickScript, ct));
                Check("点行外被拒绝（outside）",
                    !pickOutside.Ok && pickOutside.Reason == "outside", pickOutside.Reason);

                // 收尾：撤掉遮罩（否则后续搜索页会带着金框）
                await svc.ExecuteScriptAsync(SiteConfigService.StopPickerScript, ct);

                // ══════════════ ④ 解析容错 ══════════════
                log("\n── 解析容错 ──");
                Check("空点选结果不崩", SiteConfigService.ParsePick("null").Ok == false);
                Check("脏 JSON 不崩", SiteConfigService.ParsePick("not-json").Ok == false);
                Check("自动猜脏 JSON 返回 null", SiteConfigService.ParseAutoGuess("{oops") == null);

                // ══════════════ ⑤ 端到端：配好的站点立刻能搜 ══════════════
                log("\n── 端到端（配置 → 搜索 → 入库）──");

                var site = new OnlineSite
                {
                    Name = TempSiteName,
                    SearchTemplate = resultsUrl + "?q={q}",
                    Selectors = sel,
                    Status = OnlineSiteStatus.Ok
                };

                var outcome = await OnlineSearchService.SearchAsync(site, "夜曲", null, ct);
                Check("用向导配置能搜到 3 条",
                    outcome.Status == OnlineSearchStatus.Ok && outcome.Results.Count == 3,
                    $"{outcome.Status} / {outcome.Results.Count} 条");

                createdId = Db.AddOnlineSite(site);
                site.Id = createdId;
                var loaded = Db.GetOnlineSite(createdId);
                Check("入库后能读回", loaded != null, loaded == null ? "null" : loaded.Name);
                if (loaded != null)
                {
                    Check("模板原样保存", loaded.SearchTemplate == site.SearchTemplate, loaded.SearchTemplate);
                    Check("选择器 JSON 往返一致",
                        loaded.Selectors.Item == sel.Item && loaded.Selectors.Title == sel.Title,
                        $"{loaded.Selectors.Item} / {loaded.Selectors.Title}");
                    Check("新站点状态为正常", loaded.Status == OnlineSiteStatus.Ok, loaded.Status);
                }

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
                // 清理测试站点，别把脏数据留在用户库里
                if (createdId > 0)
                {
                    try { Db.DeleteOnlineSite(createdId); } catch { }
                }
            }
        }

        /// <summary>数一下某个选择器在页面上匹配多少元素（验证点选/猜出来的路径是否靠谱）。</summary>
        private static async Task<int> CountAsync(HiddenBrowserService svc, string selector,
            CancellationToken ct)
        {
            try
            {
                string js = "(function(){ try { return JSON.stringify(" +
                            "document.querySelectorAll(" + JsonSerializer.Serialize(selector) + ").length); }" +
                            " catch(e) { return JSON.stringify(-1); } })()";
                string raw = await svc.ExecuteScriptAsync(js, ct);
                string text = SiteConfigService.Unwrap(raw);
                return int.TryParse(text, out int n) ? n : -1;
            }
            catch { return -1; }
        }
    }
}
