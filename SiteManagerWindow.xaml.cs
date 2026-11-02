using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using musicPlayer.Data;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>站点面板关闭后回传给主窗口的结果。</summary>
    public sealed class SiteManagerResult
    {
        /// <summary>配置/登录态是否发生过变化（需要刷新主界面的站点下拉）。</summary>
        public bool Changed { get; init; }

        /// <summary>是否删除过站点（主界面的搜索结果可能来自已删站点，需要清空）。</summary>
        public bool SitesDeleted { get; init; }

        /// <summary>最后操作的站点 id（让下拉尽量停在它上面）。</summary>
        public long? FocusSiteId { get; init; }
    }

    /// <summary>
    /// 站点列表里的一行（纯展示用，重新读库时整批重建，所以不需要 INotifyPropertyChanged）。
    /// </summary>
    public sealed class SiteRowVm
    {
        public SiteRowVm(OnlineSite site) => Site = site;

        public OnlineSite Site { get; }

        public string Name => Site.Name;

        public string Host => OnlineSiteInfo.HostOf(Site.SearchTemplate);

        public string Initial => OnlineSiteInfo.InitialOf(Site.Name);

        public string? FaviconUrl => OnlineSiteInfo.FaviconUrlOf(Site.SearchTemplate);

        public string StatusText => Site.StatusText;

        /// <summary>最近成功时间的人话（"从未成功" / "今天 15:32" …）。</summary>
        public string LastOkText => "最近成功：" + OnlineSiteInfo.DescribeLastOk(Site.LastOkAt);

        /// <summary>状态徽章配色：正常绿 / 需登录橙 / 失效红 / 不支持灰。</summary>
        public Brush StatusBg => new SolidColorBrush(Site.Status switch
        {
            OnlineSiteStatus.Ok => Color.FromArgb(0x26, 0x7B, 0xD8, 0x8F),
            OnlineSiteStatus.NeedLogin => Color.FromArgb(0x26, 0xFF, 0xB0, 0x20),
            OnlineSiteStatus.Broken => Color.FromArgb(0x26, 0xFF, 0x6B, 0x6B),
            _ => Color.FromArgb(0x22, 0xAA, 0xAA, 0xAA)
        });

        public Brush StatusFg => new SolidColorBrush(Site.Status switch
        {
            OnlineSiteStatus.Ok => Color.FromRgb(0x9C, 0xE8, 0xAE),
            OnlineSiteStatus.NeedLogin => Color.FromRgb(0xFF, 0xC5, 0x5C),
            OnlineSiteStatus.Broken => Color.FromRgb(0xFF, 0x8F, 0x8F),
            _ => Color.FromRgb(0xBD, 0xBD, 0xBD)
        });

        /// <summary>状态提示（只有需要用户处理时才显示）。</summary>
        public string Hint => Site.Status switch
        {
            OnlineSiteStatus.NeedLogin => "需要登录：点「登录」在弹出的页面里登一次，登录态会保存在本机",
            OnlineSiteStatus.Broken => "配置失效：点「重配」重新点选结果项与标题（2 分钟就能修好）",
            OnlineSiteStatus.Unsupported => "不支持：站点有强反爬或验证无法通过，建议删除",
            _ => ""
        };

        public bool HasHint => Hint.Length > 0;
    }

    /// <summary>
    /// 已添加站点面板（《在线曲库开发步骤清单》S7）。
    ///
    /// 一个列表 + 四个动作：
    /// - **测试**：用面板顶部关键词真搜一次（走 S4 全流程，含验证中转），结果只影响状态徽章；
    /// - **重配**：复用 S5 向导的编辑模式（预填旧配置，只重选选择器），保存后状态自动回正；
    /// - **登录**：打开可见页面让用户登一次（S7 的 SiteLoginWindow），Cookie 存本机；
    /// - **删除**：ConfirmDialog 二次确认，清配置 + 清本站 Cookie，已下载文件保留（PRD 决策）。
    ///
    /// 搜索时的自动状态回写在 <see cref="OnlineSearchService"/> 里，本窗口只负责展示与修。
    /// </summary>
    public partial class SiteManagerWindow : Window
    {
        /// <summary>面板是否开着（主界面据此暂停在线搜索，避免抢同一个隐藏内核）。</summary>
        public static bool IsOpen { get; private set; }

        private readonly HiddenBrowserService _svc = HiddenBrowserService.Instance;
        private readonly ObservableCollection<SiteRowVm> rows = new();

        private List<OnlineSite> sites = new();
        private bool busy;
        private bool changed;
        private bool sitesDeleted;
        private long? focusSiteId;

        public SiteManagerWindow()
        {
            InitializeComponent();
            listSites.ItemsSource = rows;

            Loaded += (_, _) =>
            {
                IsOpen = true;
                ReloadRows(null);
                txtKeyword.Focus();
                txtKeyword.SelectAll();
            };
            Closed += (_, _) => IsOpen = false;
        }

        /// <summary>面板里的行（自检用：验证徽章/提示文本）。</summary>
        public IReadOnlyList<SiteRowVm> Rows => rows;

        /// <summary>打开面板并异步等待关闭结果（非模态）。</summary>
        /// <param name="owner">父窗口。</param>
        /// <param name="keyword">预填的测试关键词（主界面会把当前搜索词带过来）。</param>
        public static Task<SiteManagerResult> ShowAsync(Window? owner, string? keyword = null)
        {
            var win = new SiteManagerWindow();
            if (!string.IsNullOrWhiteSpace(keyword)) win.txtKeyword.Text = keyword.Trim();

            var tcs = new TaskCompletionSource<SiteManagerResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            win.Closed += (_, _) => tcs.TrySetResult(new SiteManagerResult
            {
                Changed = win.changed,
                SitesDeleted = win.sitesDeleted,
                FocusSiteId = win.focusSiteId
            });

            try
            {
                if (owner != null && owner.IsVisible) win.Owner = owner;
            }
            catch { /* Owner 设置失败不影响功能 */ }

            win.Show();
            win.Activate();
            return tcs.Task;
        }

        // ══════════════════════════════ 列表 ══════════════════════════════

        /// <summary>重读站点表并重建列表（每次操作后都走这里，状态与"最近成功"才会最新）。</summary>
        private void ReloadRows(long? focus)
        {
            try
            {
                sites = Db.GetOnlineSites();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 读取站点失败: {ex.Message}");
                sites = new List<OnlineSite>();
            }

            rows.Clear();
            foreach (var s in sites) rows.Add(new SiteRowVm(s));

            int healthy = sites.Count(s => s.Status == OnlineSiteStatus.Ok);
            txtSummary.Text = sites.Count == 0
                ? "共 0 个站点"
                : $"共 {sites.Count} 个站点 · 正常 {healthy} · 需处理 {sites.Count - healthy}";

            emptyState.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (focus is > 0) focusSiteId = focus;
        }

        /// <summary>当前测试关键词（空就用默认词）。</summary>
        private string CurrentKeyword()
        {
            string k = txtKeyword.Text.Trim();
            return k.Length > 0 ? k : "夜曲";
        }

        private void SetBusy(bool value, string? status = null)
        {
            busy = value;
            listSites.IsEnabled = !value;
            btnAdd.IsEnabled = !value;
            if (status != null) txtStatus.Text = status;
        }

        private static SiteRowVm? RowOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as SiteRowVm;

        private static string? OriginOf(OnlineSite site) =>
            Uri.TryCreate(site.SearchTemplate, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority) + "/"
                : null;

        // ══════════════════════════════ 四个动作 ══════════════════════════════

        /// <summary>立即测试：跑一次真实的 S4 搜索（含人机验证中转），结果写进状态徽章。</summary>
        private async void btnTest_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var row = RowOf(sender);
            if (row == null) return;
            await TestSiteAsync(row);
        }

        /// <summary>测试一个站点（按钮与"关键词回车"共用）。</summary>
        private async System.Threading.Tasks.Task TestSiteAsync(SiteRowVm row)
        {
            if (busy) return;

            var site = row.Site;
            string keyword = CurrentKeyword();

            SetBusy(true, $"正在测试「{site.Name}」（关键词：{keyword}）…");
            try
            {
                var outcome = await OnlineSearchService.SearchAsync(
                    site, keyword, progress: null, CancellationToken.None);

                txtStatus.Text = DescribeOutcome(site.Name, outcome);
                if (outcome.Status == OnlineSearchStatus.Ok) changed = true;

                // 测试可能改写了状态（ok/need_login/broken），重新读库刷新徽章
                ReloadRows(site.Id);
                focusSiteId = site.Id;
                App.LogCrash($"[站点面板] 测试「{site.Name}」→ {outcome.Status}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 测试异常: {ex}");
                txtStatus.Text = "测试失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>重配：复用 S5 向导的编辑模式（同一个内核，Cookie 继续共享）。</summary>
        private async void btnReconfig_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var row = RowOf(sender);
            if (row == null) return;

            var site = row.Site;
            SetBusy(true, $"正在打开「{site.Name}」的编辑向导…");
            try
            {
                var saved = await AddSiteWizardWindow.ShowAsync(this, site, CurrentKeyword());
                if (saved == null)
                {
                    txtStatus.Text = "已取消重新点选";
                    return;
                }

                changed = true;
                focusSiteId = saved.Id;
                ReloadRows(saved.Id);
                txtStatus.Text = $"「{saved.Name}」配置已更新，可点「测试」再确认一次";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 重配异常: {ex}");
                txtStatus.Text = "打开向导失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>登录：打开可见页面完成一次登录，成功后 Cookie 存本机并解除"需登录"。</summary>
        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var row = RowOf(sender);
            if (row == null) return;

            var site = row.Site;
            SetBusy(true, $"正在打开「{site.Name}」的登录页面…");
            try
            {
                var result = await SiteLoginWindow.ShowAsync(this, site);
                if (result.Success)
                {
                    changed = true;
                    focusSiteId = site.Id;
                    ReloadRows(site.Id);
                    txtStatus.Text = $"「{site.Name}」{result.Message}，可点「测试」确认能搜到";
                }
                else
                {
                    txtStatus.Text = $"「{site.Name}」登录未完成：{result.Message}";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 登录异常: {ex}");
                txtStatus.Text = "打开登录窗口失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>删除：清配置 + 清本站 Cookie；已下载的音乐文件与曲库记录保留。</summary>
        private async void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var row = RowOf(sender);
            if (row == null) return;

            var site = row.Site;
            bool ok = ConfirmDialog.Confirm(this, "删除站点",
                $"确定删除「{site.Name}」吗？\n" +
                "站点的搜索配置和本站 Cookie 会被清除。\n" +
                "已经下载的音乐文件不受影响。",
                "删除", danger: true);
            if (!ok) return;

            SetBusy(true, $"正在删除「{site.Name}」…");
            try
            {
                Db.DeleteOnlineSite(site.Id);

                // 清 Cookie 失败不影响删除结果（下次搜索顶多还带着旧登录态）
                string? origin = OriginOf(site);
                if (origin != null)
                {
                    try
                    {
                        int n = await _svc.ClearCookiesForAsync(origin);
                        App.LogCrash($"[站点面板] 删除「{site.Name}」清了 {n} 个 Cookie");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[站点面板] 清 Cookie 失败: {ex.Message}");
                        App.LogCrash("[站点面板] 清 Cookie 失败: " + ex.Message);
                    }
                }

                changed = true;
                sitesDeleted = true;
                focusSiteId = null;
                ReloadRows(null);
                txtStatus.Text = $"已删除「{site.Name}」（已下载的音乐文件保留）";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 删除异常: {ex}");
                txtStatus.Text = "删除失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>添加站点：复用 S5 向导。</summary>
        private async void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;

            SetBusy(true, "正在打开添加向导…");
            try
            {
                var saved = await AddSiteWizardWindow.ShowAsync(this, null, CurrentKeyword());
                if (saved == null)
                {
                    txtStatus.Text = "已取消添加";
                    return;
                }

                changed = true;
                focusSiteId = saved.Id;
                ReloadRows(saved.Id);
                txtStatus.Text = $"已添加「{saved.Name}」，建议点「测试」跑一次确认能搜到";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[站点面板] 添加异常: {ex}");
                txtStatus.Text = "打开向导失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ══════════════════════════════ 工具 ══════════════════════════════

        /// <summary>把测试结局翻成人话 + 下一步建议。</summary>
        private static string DescribeOutcome(string siteName, OnlineSearchOutcome outcome) =>
            outcome.Status switch
            {
                OnlineSearchStatus.Ok =>
                    $"「{siteName}」测试通过，找到 {outcome.Results.Count} 条结果",
                OnlineSearchStatus.EmptyResult =>
                    $"「{siteName}」页面正常，但这个关键词没有结果（换个关键词再试）",
                OnlineSearchStatus.NeedLogin =>
                    $"「{siteName}」需要登录：点这一行的「登录」完成一次登录",
                OnlineSearchStatus.NeedCaptcha =>
                    $"「{siteName}」有人机验证没完成，再点一次「测试」重试",
                OnlineSearchStatus.Broken =>
                    $"「{siteName}」配置失效：点「重配」重新点选选择器",
                OnlineSearchStatus.Timeout =>
                    $"「{siteName}」加载超时（站点慢或网络不稳），可稍后重试",
                OnlineSearchStatus.NavFailed =>
                    $"「{siteName}」打不开页面：{outcome.Message}",
                OnlineSearchStatus.Cancelled => $"「{siteName}」测试已取消",
                _ => outcome.Message
            };

        /// <summary>favicon 加载失败（404/无网络）→ 收起图片，露出下面的首字母徽章。</summary>
        private void Favicon_ImageFailed(object sender, ExceptionRoutedEventArgs e) =>
            ((Image)sender).Visibility = Visibility.Collapsed;

        private void txtKeyword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;

            // 回车 = 测试第一行（省一次鼠标；没有站点就什么都不做）
            if (rows.Count > 0) _ = TestSiteAsync(rows[0]);
            e.Handled = true;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
