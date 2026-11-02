using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// S7 站点面板与状态自愈的自检（MP_ONLINE_MANAGER_TEST=1 全自动 / =2 打开面板手动看）。
    ///
    /// 覆盖三块：
    /// 1. **数据层**：schema v3 的 last_ok_at 列、MarkOnlineSiteOk、状态读写与四态文案；
    /// 2. **展示辅助**：主机名/favicon/首字母/"最近成功"人话；
    /// 3. **状态自愈**：真跑搜索，验证 Ok→ok、坏选择器→broken、登录页→need_login、
    ///    打不开页面（NavFailed）**不**改状态；
    /// 4. **面板 UI**（仅 mode 1）：真开一次 SiteManagerWindow，核对行数、徽章与提示文本。
    ///
    /// 全程用本地模拟页（file://），不依赖外网；结束自动清理自己造的站点。
    /// </summary>
    public static class SiteManagerSelfTest
    {
        private const string Prefix = "S7测试-";

        /// <summary>跑一遍自检，逐行回调日志，返回 true 表示全部通过。</summary>
        public static async Task<bool> RunAsync(Action<string> log, CancellationToken ct = default)
        {
            int pass = 0, fail = 0;
            void Ok(string msg) { pass++; log($"  ✓ {msg}"); }
            void Bad(string msg) { fail++; log($"  ✗ {msg}"); }

            var created = new List<long>();

            try
            {
                // 清掉上次手动模式（=2）遗留的演示站点，避免越积越多
                RemoveDemoSites();

                // ══════════════ 1. 数据层 ══════════════
                log("\n── 数据层（schema v3）──");

                bool hasColumn;
                try
                {
                    using var conn = new SqliteConnection($"Data Source={Db.DbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA table_info(online_sites);";
                    using var reader = cmd.ExecuteReader();
                    hasColumn = false;
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), "last_ok_at",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            hasColumn = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    hasColumn = false;
                    log($"    （读 schema 失败：{ex.Message}）");
                }

                if (hasColumn) Ok("online_sites 已含 last_ok_at 列（v2 → v3 迁移生效）");
                else Bad("online_sites 缺少 last_ok_at 列");

                var dataSite = MakeSite(Prefix + "数据层", "https://example.com/search?q={q}");
                dataSite.Id = Db.AddOnlineSite(dataSite);
                created.Add(dataSite.Id);

                var reread = Db.GetOnlineSite(dataSite.Id);
                if (reread == null)
                {
                    Bad("新增站点后读不回来");
                }
                else
                {
                    if (reread.LastOkAt == 0 && reread.Status == OnlineSiteStatus.Ok)
                        Ok("新站点：状态正常、最近成功为空（0）");
                    else
                        Bad($"新站点初始值异常：status={reread.Status} lastOk={reread.LastOkAt}");

                    if (OnlineSiteInfo.DescribeLastOk(reread.LastOkAt) == "从未成功")
                        Ok("从未成功的文案 = 从未成功");
                    else
                        Bad($"从未成功的文案异常：{OnlineSiteInfo.DescribeLastOk(reread.LastOkAt)}");
                }

                Db.MarkOnlineSiteOk(dataSite.Id);
                var afterOk = Db.GetOnlineSite(dataSite.Id);
                if (afterOk != null && afterOk.Status == OnlineSiteStatus.Ok && afterOk.LastOkAt > 0)
                    Ok("MarkOnlineSiteOk：状态 ok + 最近成功时间已写入");
                else
                    Bad($"MarkOnlineSiteOk 异常：status={afterOk?.Status} lastOk={afterOk?.LastOkAt}");

                if (!string.IsNullOrEmpty(OnlineSiteInfo.DescribeLastOk(afterOk?.LastOkAt ?? 0))
                    && OnlineSiteInfo.DescribeLastOk(afterOk?.LastOkAt ?? 0) != "从未成功")
                    Ok("最近成功文案已变成时间描述：" +
                       OnlineSiteInfo.DescribeLastOk(afterOk!.LastOkAt));
                else
                    Bad("最近成功文案没有更新");

                foreach (var (status, text) in new[]
                         {
                             (OnlineSiteStatus.NeedLogin, "需登录"),
                             (OnlineSiteStatus.Broken, "失效"),
                             (OnlineSiteStatus.Unsupported, "不支持"),
                             (OnlineSiteStatus.Ok, "正常")
                         })
                {
                    Db.SetOnlineSiteStatus(dataSite.Id, status);
                    var s = Db.GetOnlineSite(dataSite.Id);
                    if (s != null && s.Status == status && s.StatusText == text)
                        Ok($"状态回写 {status} → 徽章「{text}」");
                    else
                        Bad($"状态回写 {status} 失败：{s?.Status}/{s?.StatusText}");
                }

                // ══════════════ 2. 展示辅助 ══════════════
                log("\n── 展示辅助（主机 / favicon / 首字母 / 最近成功）──");

                string host = OnlineSiteInfo.HostOf("https://www.gequbao.com/search?q={q}");
                if (host == "www.gequbao.com") Ok($"主机名解析：{host}");
                else Bad($"主机名解析异常：{host}");

                string? fav = OnlineSiteInfo.FaviconUrlOf("https://www.gequbao.com/search?q={q}");
                if (fav == "https://www.gequbao.com/favicon.ico") Ok($"favicon 推导：{fav}");
                else Bad($"favicon 推导异常：{fav}");

                if (OnlineSiteInfo.FaviconUrlOf("file:///C:/x/y.html") == null)
                    Ok("本地 file:// 站点不推导 favicon（退回首字母）");
                else
                    Bad("file:// 站点不应有 favicon");

                if (OnlineSiteInfo.InitialOf("gequbao") == "G" && OnlineSiteInfo.InitialOf("") == "?")
                    Ok("首字母徽章：gequbao→G，空名→?");
                else
                    Bad("首字母徽章异常");

                // 固定"今天"，避免跨天时结果漂移
                var today = new DateTime(2026, 11, 2, 20, 0, 0);
                long MsOf(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
                string d1 = OnlineSiteInfo.DescribeLastOk(MsOf(today.AddDays(-1).AddHours(-3)), today);
                string d3 = OnlineSiteInfo.DescribeLastOk(MsOf(today.AddDays(-3)), today);
                string d40 = OnlineSiteInfo.DescribeLastOk(MsOf(today.AddDays(-40)), today);
                if (d1.StartsWith("昨天") && d3 == "3 天前" && d40.Contains("月"))
                    Ok($"最近成功文案：昨天 / {d3} / {d40}");
                else
                    Bad($"最近成功文案异常：{d1} / {d3} / {d40}");

                // ══════════════ 3. 状态自愈（真跑搜索）══════════════
                log("\n── 状态自愈（真实搜索 → 回写状态）──");

                var (resultsUrl, _, challengeUrl) = OnlineSearchSelfTest.PrepareDemoPages();
                _ = challengeUrl;

                var okSite = MakeSite(Prefix + "正常站", resultsUrl + "?q={q}", ".song", ".t");
                okSite.Id = Db.AddOnlineSite(okSite);
                created.Add(okSite.Id);

                var okOutcome = await OnlineSearchService.SearchAsync(okSite, "夜曲", null, ct);
                var okRow = Db.GetOnlineSite(okSite.Id);
                if (okOutcome.Status == OnlineSearchStatus.Ok
                    && okRow?.Status == OnlineSiteStatus.Ok && okRow.LastOkAt > 0)
                    Ok($"搜索成功 → 状态 ok + 最近成功（{okOutcome.Results.Count} 条）");
                else
                    Bad($"搜索成功但状态没回写：{okOutcome.Status}/{okRow?.Status}");

                var brokenSite = MakeSite(Prefix + "坏选择器", resultsUrl + "?q={q}",
                    ".no-such-item", ".no-such-title");
                brokenSite.Id = Db.AddOnlineSite(brokenSite);
                created.Add(brokenSite.Id);

                var brokenOutcome = await OnlineSearchService.SearchAsync(brokenSite, "夜曲", null, ct);
                var brokenRow = Db.GetOnlineSite(brokenSite.Id);
                if (brokenOutcome.Status == OnlineSearchStatus.Broken
                    && brokenRow?.Status == OnlineSiteStatus.Broken)
                    Ok("坏选择器 → Broken，站点自动置「失效」");
                else
                    Bad($"坏选择器未置失效：{brokenOutcome.Status}/{brokenRow?.Status}");

                string loginUrl = WriteLoginPage();
                var loginSite = MakeSite(Prefix + "需登录站", loginUrl + "?q={q}", ".song", ".t");
                loginSite.Id = Db.AddOnlineSite(loginSite);
                created.Add(loginSite.Id);

                var loginOutcome = await OnlineSearchService.SearchAsync(loginSite, "夜曲", null, ct);
                var loginRow = Db.GetOnlineSite(loginSite.Id);
                if (loginOutcome.Status == OnlineSearchStatus.NeedLogin
                    && loginRow?.Status == OnlineSiteStatus.NeedLogin)
                    Ok("登录页 → NeedLogin，站点自动置「需登录」");
                else
                    Bad($"登录页未置需登录：{loginOutcome.Status}/{loginRow?.Status}");

                // 打不开页面属于网络/临时问题，不能把站点判成失效
                var deadSite = MakeSite(Prefix + "打不开", "file:///C:/__mp_no_such_dir__/x.html?q={q}",
                    ".song", ".t");
                deadSite.Id = Db.AddOnlineSite(deadSite);
                created.Add(deadSite.Id);
                Db.SetOnlineSiteStatus(deadSite.Id, OnlineSiteStatus.Ok);

                var deadOutcome = await OnlineSearchService.SearchAsync(deadSite, "夜曲", null, ct);
                var deadRow = Db.GetOnlineSite(deadSite.Id);
                if (deadOutcome.Status != OnlineSearchStatus.Ok
                    && deadRow?.Status == OnlineSiteStatus.Ok)
                    Ok($"打不开页面（{deadOutcome.Status}）不改站点状态");
                else
                    Bad($"打不开页面时状态被误改：{deadOutcome.Status}/{deadRow?.Status}");

                // ══════════════ 4. 面板 UI ══════════════
                log("\n── 站点面板 UI ──");

                int expected = Db.GetOnlineSites().Count;
                var win = new SiteManagerWindow();
                win.Show();
                await Task.Delay(600);

                if (win.Rows.Count == expected)
                    Ok($"面板渲染 {win.Rows.Count} 行（与库中站点数一致）");
                else
                    Bad($"面板行数异常：{win.Rows.Count} / 库中 {expected}");

                var brokenVm = win.Rows.FirstOrDefault(r => r.Site.Name == Prefix + "坏选择器");
                if (brokenVm != null && brokenVm.StatusText == "失效" && brokenVm.HasHint
                    && brokenVm.Hint.Contains("重配"))
                    Ok("失效站点显示「失效」徽章 + 引导重配的提示");
                else
                    Bad($"失效行渲染异常：{brokenVm?.StatusText} / hint={brokenVm?.Hint}");

                var loginVm = win.Rows.FirstOrDefault(r => r.Site.Name == Prefix + "需登录站");
                if (loginVm != null && loginVm.StatusText == "需登录" && loginVm.HasHint)
                    Ok("需登录站点显示「需登录」徽章 + 登录提示");
                else
                    Bad($"需登录行渲染异常：{loginVm?.StatusText}");

                var okVm = win.Rows.FirstOrDefault(r => r.Site.Name == Prefix + "正常站");
                if (okVm != null && okVm.StatusText == "正常" && !okVm.HasHint
                    && okVm.LastOkText.Contains("最近成功") && !okVm.LastOkText.Contains("从未"))
                    Ok($"正常行：无提示 + {okVm.LastOkText}");
                else
                    Bad($"正常行渲染异常：{okVm?.StatusText} / {okVm?.LastOkText}");

                win.Close();
                await Task.Delay(150);

                if (!SiteManagerWindow.IsOpen) Ok("面板关闭后 IsOpen 复位（主界面可继续搜索）");
                else Bad("面板关闭后 IsOpen 仍为 true");
            }
            catch (OperationCanceledException)
            {
                Bad("自检被取消");
            }
            catch (Exception ex)
            {
                Bad("自检异常：" + ex);
            }
            finally
            {
                // 清理自己造的站点（用户真实站点不受影响）
                foreach (long id in created)
                {
                    try { Db.DeleteOnlineSite(id); } catch { }
                }
                log($"\n清理：删除自检站点 {created.Count} 个");
                log($"结果：{pass} 通过 / {fail} 失败");
            }

            return fail == 0;
        }

        /// <summary>
        /// 手动模式用：造 3 个演示站点（正常 / 失效 / 需登录）后返回它们的 id，
        /// 便于在面板里直接看到三种徽章与四种操作。
        /// </summary>
        public static List<long> SeedDemoSites()
        {
            var (resultsUrl, _, _) = OnlineSearchSelfTest.PrepareDemoPages();
            var ids = new List<long>();

            foreach (var (name, item, title, status) in new[]
                     {
                         (Prefix + "正常站", ".song", ".t", OnlineSiteStatus.Ok),
                         (Prefix + "坏选择器", ".old-item", ".old-title", OnlineSiteStatus.Broken),
                         (Prefix + "需登录站", ".song", ".t", OnlineSiteStatus.NeedLogin)
                     })
            {
                RemoveByName(name);
                var site = MakeSite(name, resultsUrl + "?q={q}", item, title);
                site.Status = status;
                site.Id = Db.AddOnlineSite(site);
                if (status == OnlineSiteStatus.Ok) Db.MarkOnlineSiteOk(site.Id);
                ids.Add(site.Id);
            }
            return ids;
        }

        /// <summary>清理演示站点（包括历史遗留的同名站点）。</summary>
        public static void RemoveDemoSites()
        {
            foreach (var s in Db.GetOnlineSites().Where(s => s.Name.StartsWith(Prefix)))
            {
                try { Db.DeleteOnlineSite(s.Id); } catch { }
            }
        }

        private static void RemoveByName(string name)
        {
            foreach (var s in Db.GetOnlineSites().Where(s => s.Name == name))
            {
                try { Db.DeleteOnlineSite(s.Id); } catch { }
            }
        }

        private static OnlineSite MakeSite(string name, string template,
            string item = ".song", string title = ".t") => new OnlineSite
        {
            Name = name,
            SearchTemplate = template,
            Selectors = new SiteSelectors { Item = item, Title = title },
            Status = OnlineSiteStatus.Ok
        };

        /// <summary>
        /// 写一个"登录页"模拟页：可见密码框 + 「登录」字样 —— 正好命中 S4 的登录启发式。
        /// </summary>
        private static string WriteLoginPage()
        {
            const string html = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>请登录</title>
<style>
  body { margin:0; height:100vh; display:flex; align-items:center; justify-content:center;
         background:#0f1116; color:#e8e8ea; font-family:'Segoe UI','Microsoft YaHei UI',sans-serif; }
  .card { width:320px; padding:28px 26px; background:#191b22; border:1px solid #2a2d36;
          border-radius:14px; text-align:center; }
  input { width:100%; box-sizing:border-box; margin:8px 0; padding:9px 11px;
          background:#11131a; border:1px solid #2f333d; border-radius:8px; color:#eee; }
  button { width:100%; margin-top:12px; padding:10px; border:0; border-radius:9px;
           background:#ffd700; color:#201800; font-weight:700; cursor:pointer; }
</style></head>
<body>
  <div class=""card"">
    <h2>登录后才能搜索</h2>
    <input type=""text"" placeholder=""账号""/>
    <input type=""password"" placeholder=""密码""/>
    <button onclick=""document.title='已登录'"">登 录</button>
    <p style=""font-size:12px;color:#8b8d96"">（S7 自检模拟页，不会真的提交）</p>
  </div>
</body></html>";

            string path = Path.Combine(Path.GetTempPath(), "musicplayer_s7_login.html");
            File.WriteAllText(path, html, Encoding.UTF8);
            return new Uri(path).AbsoluteUri;
        }
    }
}
