using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using musicPlayer.Data;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>
    /// 添加站点三步向导（《在线曲库开发步骤清单》S5）。
    ///
    /// 第 1 步·模板：粘贴"搜索之后的网址" + 当时的关键词 → 反推含 {q} 的模板，
    ///               同时用 HttpClient 取 &lt;title&gt;/favicon 作为默认站点名；
    /// 第 2 步·点选：把**同一个**隐藏内核切挂到本窗口，页面可见但被点选脚本接管
    ///               （悬停描边、点击拦截），先自动猜，猜不准就逐字段点选；右侧实时预览；
    /// 第 3 步·确认：命名 + 用该配置真搜一次（连通性测试）→ 通过后写库。
    ///
    /// 为什么复用同一个内核而不是新开一个 WebView2：
    /// 同一个 UserDataFolder 不允许两个实例并发使用；复用还能让站点登录态/Cookie 与正式搜索共享，
    /// 配完立刻能搜。代价是向导期间内核被占用——所以主界面在向导打开时会拒绝新的搜索。
    ///
    /// 窗口是**非模态**的（教训见 CaptchaWindow）：即使窗口没能显示到前台，主界面也不会被锁死。
    /// </summary>
    public partial class AddSiteWizardWindow : Window
    {
        private enum WizardStep { Template = 0, Pick = 1, Confirm = 2 }

        /// <summary>是否有向导开着（主界面据此拒绝新的在线搜索，避免两边抢同一个页面）。</summary>
        public static bool IsOpen { get; private set; }

        private readonly HiddenBrowserService _svc = HiddenBrowserService.Instance;
        private readonly OnlineSite? _editSite;              // 非空 = 编辑已有站点（S7 复用）
        private readonly string? _prefillTemplate;            // 测试用：跳过第 1 步直接点选
        private readonly string? _prefillKeyword;

        private readonly SiteSelectors _sel = new SiteSelectors();
        private readonly DispatcherTimer _pickPoll;          // 250ms 轮询页面点选结果

        private WizardStep _step = WizardStep.Template;
        private string _pickTarget = "";                     // item/title/artist/duration/cover/link
        private string _template = "";
        private string _keyword = "";

        private List<OnlineResult> _previewItems = new();
        private bool _busy;                                  // 正在导航/测试/初始化
        private bool _polling;                               // 轮询脚本在飞行中（避免叠加）
        private bool _coreShown;                             // 内核此刻是否挂在本窗口
        private bool _closing;
        private bool _loaded;
        private bool _testPassed;

        private bool _settingName;                           // 程序在写名称（别当成用户输入）
        private bool _userEditedName;

        private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x7B, 0xD8, 0x8F));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        /// <summary>用户点了「完成」保存后的站点（取消/关闭时为 null）。</summary>
        public OnlineSite? SavedSite { get; private set; }

        /// <param name="editSite">传入已有站点 = 编辑模式（预填模板/选择器，第 2 步不再自动猜）。</param>
        /// <param name="prefillTemplate">测试用：直接给模板并跳到第 2 步（本地模拟页没法走模板反推）。</param>
        /// <param name="prefillKeyword">测试用：配合 prefillTemplate 的关键词。</param>
        public AddSiteWizardWindow(OnlineSite? editSite = null,
            string? prefillTemplate = null, string? prefillKeyword = null)
        {
            InitializeComponent();
            _editSite = editSite;
            _prefillTemplate = prefillTemplate;
            _prefillKeyword = prefillKeyword;

            _pickPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _pickPoll.Tick += async (_, _) => await PollPickAsync();

            if (editSite != null)
            {
                // 编辑模式：把现有配置铺进去（第 1 步只需补一个用来打开页面的关键词）
                txtTemplate.Text = editSite.SearchTemplate;
                _template = editSite.SearchTemplate;
                CopySelectors(editSite.Selectors, _sel);
                SetSiteName(editSite.Name);
                _userEditedName = true;
                txtTitle.Text = "编辑站点配置";
                txtResultUrl.Text = editSite.SearchTemplate;
            }

            txtSiteName.TextChanged += (_, _) => { if (!_settingName) _userEditedName = true; };

            Loaded += OnLoaded;
            SizeChanged += (_, _) => { if (_step == WizardStep.Pick) SafeFitBounds(); };
            DpiChanged += (_, _) => { if (_step == WizardStep.Pick) SafeFitBounds(); };
            SourceInitialized += (_, _) =>
            {
                PlaceOnScreen();
                WindowEffects.ApplyRoundedCorners(this);
            };
            Closed += (_, _) => IsOpen = false;
        }

        /// <summary>
        /// 打开向导并异步等待结果（非模态）。返回用户保存的站点；取消返回 null。
        /// </summary>
        /// <param name="suggestedKeyword">
        /// 建议关键词（S7 站点面板会带当前搜索词）：编辑模式下预填，省得用户再想一个词。
        /// </param>
        public static Task<OnlineSite?> ShowAsync(Window? owner, OnlineSite? editSite = null,
            string? suggestedKeyword = null) =>
            ShowCoreAsync(owner, editSite, null, null, suggestedKeyword);

        /// <summary>
        /// **测试专用**：带模板直接打开并跳到第 2 步。
        /// 本地模拟页是 file:// 地址，走不了"粘贴网址反推模板"（那一步只接受 http/https），
        /// 所以手动验收点选体验时用这个入口（MP_ONLINE_WIZARD_TEST=2）。
        /// </summary>
        public static Task<OnlineSite?> ShowForTestAsync(Window? owner, string template, string keyword) =>
            ShowCoreAsync(owner, null, template, keyword);

        private static Task<OnlineSite?> ShowCoreAsync(Window? owner, OnlineSite? editSite,
            string? prefillTemplate, string? prefillKeyword, string? suggestedKeyword = null)
        {
            var win = new AddSiteWizardWindow(editSite, prefillTemplate, prefillKeyword);
            if (!string.IsNullOrWhiteSpace(suggestedKeyword)) win.txtKeyword.Text = suggestedKeyword.Trim();
            var tcs = new TaskCompletionSource<OnlineSite?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            win.Closed += (_, _) => tcs.TrySetResult(win.SavedSite);

            try
            {
                if (owner != null && owner.IsVisible) win.Owner = owner;
            }
            catch { /* Owner 设置失败不影响功能 */ }

            win.Show();
            win.BringToFront();
            return tcs.Task;
        }

        // ══════════════════════════════ 生命周期 ══════════════════════════════

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            IsOpen = true;
            _loaded = true;

            SetStep(_step);

            // 编辑模式：模板/选择器已经填好，只差一个"用来打开页面的关键词"
            if (_prefillTemplate != null)
            {
                txtTemplate.Text = _prefillTemplate;
                txtKeyword.Text = _prefillKeyword ?? "夜曲";
                _ = AutoStartPickAsync();
                return;
            }

            if (_editSite != null)
            {
                txtKeyword.Focus();
                txtFooterStatus.Text = "模板已载入，填个关键词后点「下一步」即可重新点选";
            }
            else
            {
                txtResultUrl.Focus();
                txtFooterStatus.Text = "粘贴网址后点「生成搜索模板」";
            }
        }

        /// <summary>测试用：预填模板后自动进入第 2 步（等窗口完成布局再切挂内核）。</summary>
        private async Task AutoStartPickAsync()
        {
            await Task.Delay(200);
            txtFooterStatus.Text = "测试模式：已跳过第 1 步，直接进入点选";
            await EnterPickStepAsync();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            _pickPoll.Stop();
            IsOpen = false;

            // 内核还挂在本窗口时，必须在窗口销毁前摘回隐藏宿主；否则父句柄失效会崩。
            // 若此刻仍有内核调用在飞（刚点完页面元素就关窗），改挂会让内核挂死 →
            // 先等最多 2 秒，还不结束就整体释放（下次搜索自动重建，约 1 秒）。
            if (_coreShown)
            {
                try
                {
                    for (int i = 0; i < 20 && _svc.ActiveOperationCount > 0; i++)
                        Thread.Sleep(100);

                    if (_svc.ActiveOperationCount > 0)
                    {
                        App.LogCrash("[添加向导] 关闭时内核仍有调用在飞，强制释放内核");
                        _svc.ForceDisposeCore();
                    }
                    else
                    {
                        _svc.HideBack();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[添加向导] 归还内核失败: {ex.Message}");
                }
                _coreShown = false;
            }

            App.LogCrash($"[添加向导] 关闭，步骤={_step} 已保存={SavedSite != null}");
            base.OnClosing(e);
        }

        private void SafeFitBounds()
        {
            try { _svc.FitBoundsTo(this, contentHost); }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 同步内核尺寸失败: {ex.Message}"); }
        }

        #region 窗口定位与置前（与验证窗口同一套保险）

        /// <summary>显式放到 Owner 所在屏幕的工作区中央（不用 CenterOwner，避免算出屏幕外坐标）。</summary>
        private void PlaceOnScreen()
        {
            try
            {
                Rect area = GetWorkArea();

                double w = Math.Min(Width, area.Width * 0.94);
                double h = Math.Min(Height, area.Height * 0.94);
                if (w > 0 && Math.Abs(w - Width) > 1) Width = w;
                if (h > 0 && Math.Abs(h - Height) > 1) Height = h;

                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[添加向导] 定位失败: {ex.Message}");
            }
        }

        private Rect GetWorkArea()
        {
            System.Windows.Forms.Screen screen =
                Owner != null && Owner.IsVisible
                    ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Owner).Handle)
                    : System.Windows.Forms.Screen.PrimaryScreen
                      ?? System.Windows.Forms.Screen.AllScreens[0];

            var wa = screen.WorkingArea;
            var src = PresentationSource.FromVisual(this) as HwndSource;
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (scale <= 0) scale = 1.0;

            return new Rect(wa.Left / scale, wa.Top / scale,
                            Math.Max(1, wa.Width / scale), Math.Max(1, wa.Height / scale));
        }

        private void BringToFront()
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!IsActive)
                {
                    Topmost = true;
                    Topmost = false;
                    Activate();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 置前失败: {ex.Message}"); }
        }

        #endregion

        #region 步骤切换

        private void SetStep(WizardStep step)
        {
            if (_step == WizardStep.Pick && step != WizardStep.Pick)
                StopPickingCore();

            _step = step;

            step1Panel.Visibility = step == WizardStep.Template ? Visibility.Visible : Visibility.Collapsed;
            step2Panel.Visibility = step == WizardStep.Pick ? Visibility.Visible : Visibility.Collapsed;
            step3Panel.Visibility = step == WizardStep.Confirm ? Visibility.Visible : Visibility.Collapsed;

            // 步骤徽章：当前=金色底，已过=白色描边，未到=淡灰
            PaintChip(chip1, chip1Text, step, WizardStep.Template);
            PaintChip(chip2, chip2Text, step, WizardStep.Pick);
            PaintChip(chip3, chip3Text, step, WizardStep.Confirm);

            btnBack.Visibility = step == WizardStep.Template ? Visibility.Collapsed : Visibility.Visible;
            btnNext.Content = step == WizardStep.Confirm ? "完成" : "下一步";

            switch (step)
            {
                case WizardStep.Template:
                    txtFooterStatus.Text = "粘贴网址后点「生成搜索模板」";
                    break;
                case WizardStep.Pick:
                    txtFooterStatus.Text = "先在网页上点选结果项与标题，右侧预览对得上再下一步";
                    break;
                case WizardStep.Confirm:
                    txtFooterStatus.Text = "建议先点「测试搜索」确认配置可用";
                    break;
            }
        }

        private static void PaintChip(Border chip, TextBlock text, WizardStep current, WizardStep mine)
        {
            if (current == mine)
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD7, 0x00));
                text.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            }
            else if ((int)current > (int)mine)
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
                text.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
                text.Foreground = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            }
        }

        private async void btnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            switch (_step)
            {
                case WizardStep.Template:
                    await EnterPickStepAsync();
                    break;

                case WizardStep.Pick:
                    if (!_sel.IsUsable)
                    {
                        SetPickStatus("还差「结果项」和「标题」两个必要字段，先点选它们");
                        txtFooterStatus.Text = "还差必要字段，请继续点选";
                        return;
                    }
                    await EnterConfirmStepAsync();
                    break;

                case WizardStep.Confirm:
                    Save();
                    break;
            }
        }

        private async void btnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_step == WizardStep.Confirm)
            {
                SetStep(WizardStep.Pick);
                await LoadSearchPageAsync();          // 测试时页面已经被导航走了，这里重新打开
            }
            else if (_step == WizardStep.Pick)
            {
                await DetachCoreAsync();
                SetStep(WizardStep.Template);
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 第 2 步的按键大多被网页吃掉（WebView2 聚焦），Esc 取消点选由页面脚本处理；
            // 这里只管"焦点在 WPF 控件上"的情况。
            if (e.Key == Key.Escape && _step != WizardStep.Pick)
            {
                Close();
                e.Handled = true;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { /* 拖动中断，忽略 */ }
        }

        #endregion

        #region 第 1 步：模板反推

        private void btnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                    txtResultUrl.Text = Clipboard.GetText().Trim();
            }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 读剪贴板失败: {ex.Message}"); }
        }

        private void txtResultUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = InferTemplateAsync(); e.Handled = true; }
        }

        private void txtKeyword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = InferTemplateAsync(); e.Handled = true; }
        }

        private void txtTemplate_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 实时校验：模板必须含 {q}
            if (!_loaded) return;

            string t = txtTemplate.Text.Trim();
            if (t.Length == 0) { txtTemplateHint.Text = ""; return; }

            if (!t.Contains("{q}"))
            {
                txtTemplateHint.Foreground = Warn;
                txtTemplateHint.Text = "⚠ 模板里缺少 {q}——它代表关键词，没有它就没法搜索";
            }
            else
            {
                txtTemplateHint.Foreground = Gold;
                txtTemplateHint.Text = "✓ 模板可用：" + t.Replace("{q}", "关键词");
            }
        }

        private void btnInfer_Click(object sender, RoutedEventArgs e) => _ = InferTemplateAsync();

        private async Task InferTemplateAsync()
        {
            string url = txtResultUrl.Text.Trim();
            string kw = txtKeyword.Text.Trim();

            var result = SiteConfigService.InferTemplate(url, kw);
            if (!result.Ok)
            {
                txtTemplateHint.Foreground = Warn;
                txtTemplateHint.Text = "✗ " + result.Message;
                txtFooterStatus.Text = "没能反推出模板，按提示调整一下";
                return;
            }

            txtTemplate.Text = result.Template;
            _template = result.Template;

            if (!_userEditedName && string.IsNullOrWhiteSpace(txtSiteName.Text))
                SetSiteName(SiteConfigService.SuggestNameFromUrl(result.Template));

            txtFooterStatus.Text = "模板已生成，可以下一步了";
            await LoadPageMetaAsync(url);
        }

        /// <summary>取页面标题/favicon 当默认名称（失败不影响流程）。</summary>
        private async Task LoadPageMetaAsync(string url)
        {
            txtPageTitle.Text = "正在读取页面标题…";
            try
            {
                var meta = await SiteConfigService.FetchPageMetaAsync(url);
                txtPageTitle.Text = meta.Title.Length > 0
                    ? meta.Title
                    : "（没读到页面标题，可以手动起名）";

                if (meta.SiteName.Length > 0 && !_userEditedName)
                    SetSiteName(meta.SiteName);

                LoadFavicon(meta.FaviconUrl);
            }
            catch (Exception ex)
            {
                txtPageTitle.Text = "（读取页面信息失败：" + ex.Message + "）";
            }
        }

        private void LoadFavicon(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(url!, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();

                imgFavicon.Source = bmp;
                imgFavicon3.Source = bmp;
            }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 图标加载失败: {ex.Message}"); }
        }

        private void SetSiteName(string name)
        {
            _settingName = true;
            txtSiteName.Text = name;
            _settingName = false;
        }

        private bool TryReadStep1(out string template, out string keyword, out string error)
        {
            template = txtTemplate.Text.Trim();
            keyword = txtKeyword.Text.Trim();
            error = "";

            if (template.Length == 0)
            {
                error = "还没有搜索模板：请粘贴网址后点「生成搜索模板」";
                return false;
            }
            if (!template.Contains("{q}"))
            {
                error = "模板里必须有 {q}（它代表关键词）";
                return false;
            }
            if (!template.Contains("://"))
            {
                error = "模板需要以 http:// 或 https:// 开头";
                return false;
            }
            if (keyword.Length == 0)
            {
                error = "请填一个关键词——用它打开页面给你点选";
                return false;
            }
            return true;
        }

        #endregion

        #region 第 2 步：打开页面 + 自动猜 + 点选

        private async Task EnterPickStepAsync()
        {
            if (!TryReadStep1(out string template, out string keyword, out string error))
            {
                txtTemplateHint.Foreground = Warn;
                txtTemplateHint.Text = "✗ " + error;
                txtFooterStatus.Text = error;
                return;
            }

            _template = template;
            _keyword = keyword;
            SetStep(WizardStep.Pick);
            await LoadSearchPageAsync();
        }

        /// <summary>把内核切挂到本窗口并打开"模板+关键词"对应的搜索页。</summary>
        private async Task LoadSearchPageAsync()
        {
            SetBusy(true, "正在打开搜索结果页…");
            txtPickHint.Text = "正在打开搜索结果页…";

            try
            {
                await _svc.InitAsync();

                if (!_coreShown)
                {
                    _svc.ShowIn(this, contentHost);
                    _coreShown = true;
                    pickPlaceholder.Visibility = Visibility.Collapsed;

                    // 布局刚完成时内容区尺寸可能还差一两帧，补一次精确对齐
                    _ = Dispatcher.BeginInvoke(new Action(SafeFitBounds), DispatcherPriority.Loaded);
                    await Task.Delay(120);
                    SafeFitBounds();
                }

                string url = SiteConfigService.FillTemplate(_template, _keyword);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    await _svc.NavigateAsync(url, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    txtPickHint.Text = "页面加载较慢——可以直接点选，或点「重新加载」";
                }
                catch (Exception ex)
                {
                    txtPickHint.Text = "页面加载失败：" + ex.Message + "（可点「重新加载」）";
                    App.LogCrash("[添加向导] 导航失败: " + ex.Message);
                    return;
                }

                SafeFitBounds();
                await Task.Delay(200);
                App.LogCrash($"[添加向导] 页面已就绪 url={url} 现有选择器可用={_sel.IsUsable}");

                if (_sel.IsUsable)
                {
                    // 编辑模式 / 从第 3 步退回来：保留已有配置，只做预览，别乱改用户的选择
                    RenderSelectorRows();
                    await RefreshPreviewAsync();
                    StopPickingCore();
                    SetPickStatus("已载入现有配置，下面是当前预览；要改就点对应字段的「点选」");
                    txtPickHint.Text = "点字段的「点选」后，在网页上单击目标元素";
                }
                else
                {
                    await AutoGuessAsync();
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void btnReloadPage_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await LoadSearchPageAsync();
        }

        private async void btnAutoGuess_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            StopPickingCore();
            await AutoGuessAsync();
        }

        /// <summary>
        /// 自动猜结果项与字段。
        /// 已经点过结果项时会传入选中的容器，只在其内部猜子元素（比整页猜准得多）。
        /// </summary>
        private async Task AutoGuessAsync()
        {
            try
            {
                SetPickStatus("正在自动识别结果列表…");

                string js = SiteConfigService.BuildAutoGuessScript(
                    string.IsNullOrWhiteSpace(_sel.Item) ? null : _sel.Item);
                string raw = await _svc.ExecuteScriptAsync(js);
                var guess = SiteConfigService.ParseAutoGuess(raw);

                if (guess == null || !guess.Ok || guess.Item.Length == 0)
                {
                    SetPickStatus("没能自动识别结果列表，请点「结果项」的「点选」，然后在网页上点一条结果");
                    await StartPickingAsync("item");
                    return;
                }

                App.LogCrash($"[添加向导] 自动猜 item={guess.Item} count={guess.Count} " +
                             $"title={guess.Title} duration={guess.Duration} cover={guess.Cover} link={guess.Link}");

                _sel.Item = guess.Item;
                if (guess.Title.Length > 0) _sel.Title = guess.Title;
                _sel.Artist = guess.Artist;
                _sel.Duration = guess.Duration;
                _sel.Cover = guess.Cover;
                _sel.Link = guess.Link;

                RenderSelectorRows();
                await RefreshPreviewAsync();

                if (_sel.IsUsable)
                {
                    StopPickingCore();
                    SetPickStatus($"自动识别到 {guess.Count} 条，看看右边预览；对不上就点对应字段的「点选」修正");
                    txtPickHint.Text = "自动识别完成；可直接「下一步」，也可以逐项修正";
                }
                else
                {
                    SetPickStatus("识别到了结果项，但没找到标题，请点「标题」的「点选」");
                    await StartPickingAsync("title");
                }
            }
            catch (Exception ex)
            {
                SetPickStatus("自动识别失败：" + ex.Message);
            }
        }

        /// <summary>点完结果项后，在新容器里再猜一次子元素（旧子选择器已随容器变化失效）。</summary>
        private async Task GuessChildrenAsync()
        {
            try
            {
                string raw = await _svc.ExecuteScriptAsync(
                    SiteConfigService.BuildAutoGuessScript(_sel.Item));
                var guess = SiteConfigService.ParseAutoGuess(raw);

                if (guess != null && guess.Ok)
                {
                    _sel.Title = guess.Title;
                    _sel.Artist = guess.Artist;
                    _sel.Duration = guess.Duration;
                    _sel.Cover = guess.Cover;
                    _sel.Link = guess.Link;
                }

                RenderSelectorRows();
                await RefreshPreviewAsync();

                if (string.IsNullOrWhiteSpace(_sel.Title))
                {
                    SetPickStatus("结果项已设定，请再点一下这条结果里的标题文字");
                    await StartPickingAsync("title");
                }
                else
                {
                    SetPickStatus("字段已自动补齐，检查右边预览；不对就点对应字段的「点选」");
                }
            }
            catch (Exception ex)
            {
                SetPickStatus("补全字段失败：" + ex.Message);
            }
        }

        private async void btnPickField_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string target = (sender as FrameworkElement)?.Tag as string ?? "";
            if (target.Length == 0) return;
            await StartPickingAsync(target);
        }

        /// <summary>进入点选模式：给页面注入拾取脚本，然后每 250ms 读一次"点了什么"。</summary>
        private async Task StartPickingAsync(string target)
        {
            if (target != "item" && string.IsNullOrWhiteSpace(_sel.Item))
            {
                SetPickStatus("请先点选「结果项」——其它字段都是相对它定位的");
                return;
            }

            _pickTarget = target;
            App.LogCrash($"[添加向导] 进入点选 target={target}");
            try
            {
                string root = target == "item" ? "" : (_sel.Item ?? "");
                await _svc.ExecuteScriptAsync(SiteConfigService.BuildPickerScript(root));

                HighlightField(target);
                _pickPoll.Start();

                string hint = target == "item"
                    ? "在网页上点一条搜索结果（点标题文字也行，会自动圈住整行）"
                    : $"在网页上点这一条里的「{FieldLabel(target)}」";
                txtPickHint.Text = "点选模式：" + hint;
                SetPickStatus("点选模式：" + hint + "；按 Esc 取消");
            }
            catch (Exception ex)
            {
                SetPickStatus("进入点选模式失败：" + ex.Message);
            }
        }

        private async Task PollPickAsync()
        {
            if (_busy || _closing || _pickTarget.Length == 0 || _polling) return;

            _polling = true;
            try
            {
                string raw = await _svc.ExecuteScriptAsync(SiteConfigService.ReadPickScript);
                var pick = SiteConfigService.ParsePick(raw);

                if (!pick.Ok)
                {
                    switch (pick.Reason)
                    {
                        case "cancel":
                            StopPickingCore();
                            SetPickStatus("已取消点选");
                            break;
                        case "outside":
                            SetPickStatus("请点在搜索结果项**内部**（刚才那下点到了行外面）");
                            break;
                        case "is-root":
                            SetPickStatus("这一下点的就是整行，请点它内部更具体的元素（比如标题文字）");
                            break;
                    }
                    return;
                }

                await ApplyPickAsync(pick);
            }
            catch (Exception ex)
            {
                // 页面跳转/刷新时脚本会失败：忽略这一轮，下一轮继续
                Debug.WriteLine($"[添加向导] 读取点选结果失败: {ex.Message}");
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task ApplyPickAsync(PickResult pick)
        {
            string target = _pickTarget;
            StopPickingCore();
            HighlightField(null);

            switch (target)
            {
                case "item":
                    _sel.Item = pick.Path;
                    // 换了行容器，旧子选择器基本都要重来
                    _sel.Title = "";
                    _sel.Artist = "";
                    _sel.Duration = "";
                    _sel.Cover = "";
                    _sel.Link = "";
                    RenderSelectorRows();
                    SetPickStatus($"结果项已设为 {Short(pick.Path)}，正在识别字段…");
                    await GuessChildrenAsync();
                    break;

                case "title": _sel.Title = pick.Path; break;
                case "artist": _sel.Artist = pick.Path; break;
                case "duration": _sel.Duration = pick.Path; break;
                case "cover": _sel.Cover = pick.Path; break;
                case "link": _sel.Link = pick.Path; break;
            }

            if (target != "item")
            {
                RenderSelectorRows();
                SetPickStatus($"{FieldLabel(target)} 已设为 {Short(pick.Path)}");
                await RefreshPreviewAsync();
            }

            InvalidateTest();     // 配置变了，之前的连通性测试结果作废
        }

        /// <summary>停止点选：先让页面撤掉遮罩与监听，再停轮询（顺序反了会有一轮空读）。</summary>
        private void StopPickingCore()
        {
            _pickTarget = "";
            _pickPoll.Stop();
            HighlightField(null);

            if (!_coreShown) return;
            try
            {
                // 不等结果：撤遮罩只是"顺手清理"，页面跳转/销毁时调用失败也无所谓
                _ = _svc.ExecuteScriptAsync(SiteConfigService.StopPickerScript)
                        .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 停止点选失败: {ex.Message}"); }
        }

        /// <summary>
        /// 把内核摘回隐藏宿主。
        /// 关键：先等正在飞行的内核调用结束——**在调用未返回时改挂父窗口会让内核挂死**
        /// （之后所有在线操作永远等不到结果，只能重启程序/系统）。
        /// </summary>
        private async Task DetachCoreAsync()
        {
            if (!_coreShown) return;

            for (int i = 0; i < 20 && _svc.ActiveOperationCount > 0; i++)
                await Task.Delay(50);

            try { _svc.HideBack(); }
            catch (Exception ex) { Debug.WriteLine($"[添加向导] 归还内核失败: {ex.Message}"); }

            _coreShown = false;
        }

        private async Task RefreshPreviewAsync()
        {
            if (!_sel.IsUsable)
            {
                _previewItems = new List<OnlineResult>();
                RenderPreview();
                return;
            }

            try
            {
                var (items, error) = await OnlineSearchService.ExtractOnceAsync(_sel);
                _previewItems = items;

                if (error != null)
                {
                    SetPickStatus("选择器无效：" + error);
                }
                RenderPreview();
                App.LogCrash($"[添加向导] 预览 {_previewItems.Count} 条");
            }
            catch (Exception ex)
            {
                SetPickStatus("预览失败：" + ex.Message);
            }
        }

        #endregion

        #region 右侧面板渲染

        private void RenderSelectorRows()
        {
            SetSelText(txtSelItem, _sel.Item, "未设置");
            SetSelText(txtSelTitle, _sel.Title, "未设置");
            SetSelText(txtSelArtist, _sel.Artist, "可选");
            SetSelText(txtSelDuration, _sel.Duration, "可选");
            SetSelText(txtSelCover, _sel.Cover, "可选");
            SetSelText(txtSelLink, _sel.Link, "可选");
        }

        private static void SetSelText(TextBlock block, string? value, string emptyText)
        {
            bool empty = string.IsNullOrWhiteSpace(value);
            block.Text = empty ? emptyText : Short(value!);
            block.ToolTip = empty ? null : value;
            block.Foreground = empty ? Dim : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }

        private void RenderPreview()
        {
            previewList.Children.Clear();
            txtPreviewCount.Text = _previewItems.Count == 0 ? "0 条" : $"共 {_previewItems.Count} 条";
            txtPreviewCount.Foreground = _previewItems.Count == 0 ? Dim : Gold;

            if (_previewItems.Count == 0)
            {
                previewList.Children.Add(new TextBlock
                {
                    Text = "还没抓到结果——说明选择器还没对上",
                    FontSize = 11.5,
                    Foreground = Dim,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            for (int i = 0; i < _previewItems.Count && i < 5; i++)
            {
                var r = _previewItems[i];
                var tb = new TextBlock
                {
                    FontSize = 11.5,
                    Margin = new Thickness(0, 0, 0, 7),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                tb.Inlines.Add(new Run($"{i + 1}. ") { Foreground = Gold });
                tb.Inlines.Add(new Run(r.Title.Length > 26 ? r.Title.Substring(0, 26) + "…" : r.Title)
                {
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
                });

                var extra = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.Artist)) extra.Add(r.Artist!);
                if (r.Duration > TimeSpan.Zero) extra.Add(r.Duration.ToString(@"mm\:ss"));
                if (extra.Count > 0)
                    tb.Inlines.Add(new Run("  " + string.Join(" · ", extra))
                    {
                        Foreground = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF))
                    });

                previewList.Children.Add(tb);
            }

            if (_previewItems.Count > 5)
            {
                previewList.Children.Add(new TextBlock
                {
                    Text = $"…还有 {_previewItems.Count - 5} 条",
                    FontSize = 11,
                    Foreground = Dim
                });
            }
        }

        private void SetPickStatus(string text)
        {
            txtPickStatus.Text = text;
        }

        private void HighlightField(string? target)
        {
            // 恢复默认色，再把目标字段点亮（让"我正在点哪一项"一眼可见）
            var normal = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            foreach (var block in new[] { txtSelItem, txtSelTitle, txtSelArtist,
                                          txtSelDuration, txtSelCover, txtSelLink })
            {
                block.FontWeight = FontWeights.Normal;
                if (block.Text != "未设置" && block.Text != "可选") block.Foreground = normal;
            }

            var hit = FieldBlock(target);
            if (hit == null) return;
            hit.FontWeight = FontWeights.SemiBold;
            hit.Foreground = Gold;
        }

        private TextBlock? FieldBlock(string? target) => target switch
        {
            "item" => txtSelItem,
            "title" => txtSelTitle,
            "artist" => txtSelArtist,
            "duration" => txtSelDuration,
            "cover" => txtSelCover,
            "link" => txtSelLink,
            _ => null
        };

        private static string FieldLabel(string target) => target switch
        {
            "item" => "结果项",
            "title" => "标题",
            "artist" => "艺人",
            "duration" => "时长",
            "cover" => "封面",
            "link" => "详情链接",
            _ => target
        };

        /// <summary>选择器太长时截断显示（完整值放 tooltip）。</summary>
        private static string Short(string value) =>
            value.Length <= 46 ? value : value.Substring(0, 20) + " … " + value.Substring(value.Length - 22);

        #endregion

        #region 第 3 步：确认与连通性测试

        private async Task EnterConfirmStepAsync()
        {
            StopPickingCore();
            await DetachCoreAsync();

            SetStep(WizardStep.Confirm);

            txtSummaryTemplate.Text = _template;
            var parts = new List<string> { $"结果项 {_sel.Item}", $"标题 {_sel.Title}" };
            if (!string.IsNullOrWhiteSpace(_sel.Artist)) parts.Add("艺人 " + _sel.Artist);
            if (!string.IsNullOrWhiteSpace(_sel.Duration)) parts.Add("时长 " + _sel.Duration);
            if (!string.IsNullOrWhiteSpace(_sel.Cover)) parts.Add("封面 " + _sel.Cover);
            if (!string.IsNullOrWhiteSpace(_sel.Link)) parts.Add("链接 " + _sel.Link);
            txtSummarySelectors.Text = string.Join("\n", parts);

            if (string.IsNullOrWhiteSpace(txtTestKeyword.Text))
                txtTestKeyword.Text = _keyword;

            _testPassed = false;
            testResultCard.Visibility = Visibility.Collapsed;
        }

        private async void btnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            string kw = txtTestKeyword.Text.Trim();
            if (kw.Length == 0)
            {
                SetTestResult("先填一个要测试的关键词。", Warn);
                return;
            }

            if (!_sel.IsUsable)
            {
                SetTestResult("选择器不完整，先回第 2 步点选「结果项」和「标题」。", Warn);
                return;
            }

            SetBusy(true, "正在测试搜索…");
            testResultCard.Visibility = Visibility.Visible;
            SetTestResult("正在用这个配置搜索一次…", Dim);

            try
            {
                var site = new OnlineSite
                {
                    Name = string.IsNullOrWhiteSpace(txtSiteName.Text) ? "测试站点" : txtSiteName.Text.Trim(),
                    SearchTemplate = _template,
                    Selectors = _sel,
                    Status = OnlineSiteStatus.Ok
                };

                var outcome = await OnlineSearchService.SearchAsync(site, kw);

                if (outcome.Status == OnlineSearchStatus.Ok)
                {
                    _testPassed = true;
                    var lines = new List<string> { $"✓ 成功返回 {outcome.Results.Count} 条" };
                    foreach (var r in outcome.Results.Take(3))
                    {
                        string extra = string.IsNullOrWhiteSpace(r.Artist) ? "" : " — " + r.Artist;
                        lines.Add("    · " + r.Title + extra);
                    }
                    lines.Add("配置可用，点「完成」保存。");
                    SetTestResult(string.Join("\n", lines), Ok);
                    txtFooterStatus.Text = "测试通过，点「完成」保存";
                }
                else
                {
                    _testPassed = false;
                    SetTestResult("✗ " + DescribeOutcome(outcome) +
                                  "\n可以回第 2 步重新点选，也可以先保存稍后再修正。", Gold);
                    txtFooterStatus.Text = "测试没通过，可回上一步调整";
                }
            }
            catch (Exception ex)
            {
                _testPassed = false;
                SetTestResult("✗ 测试出错：" + ex.Message, Warn);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetTestResult(string text, Brush brush)
        {
            txtTestResult.Text = text;
            txtTestResult.Foreground = brush;
        }

        /// <summary>把搜索结局翻译成"人话 + 下一步建议"。</summary>
        private static string DescribeOutcome(OnlineSearchOutcome outcome) => outcome.Status switch
        {
            OnlineSearchStatus.EmptyResult => "页面能打开，但这个关键词没有搜到结果（换个词再试，或检查选择器）",
            OnlineSearchStatus.Timeout => "页面加载超时（站点较慢或网络不稳定）",
            OnlineSearchStatus.NavFailed => "打不开页面：" + outcome.Message,
            OnlineSearchStatus.NeedLogin => "该站点需要登录后才能搜索",
            OnlineSearchStatus.NeedCaptcha => "该站点要求人机验证，本次未完成验证",
            OnlineSearchStatus.Broken => "页面结构变了，抓不到结果项（重新点选一次）",
            OnlineSearchStatus.Cancelled => "测试已取消",
            _ => outcome.Message
        };

        private void InvalidateTest()
        {
            if (!_testPassed) return;
            _testPassed = false;
            testResultCard.Visibility = Visibility.Collapsed;
        }

        private void Save()
        {
            if (_busy) return;

            string name = txtSiteName.Text.Trim();
            if (name.Length == 0)
            {
                txtSiteName.Focus();
                txtFooterStatus.Text = "请先给站点起个名字";
                return;
            }

            if (!_sel.IsUsable)
            {
                txtFooterStatus.Text = "选择器不完整，先回第 2 步点选";
                return;
            }

            if (!_testPassed)
            {
                bool go = ConfirmDialog.Confirm(this, "还没测试通过",
                    "这个配置还没测试通过，仍然保存吗？\n" +
                    "（比如该站点需要先登录才能搜索，可以先保存，之后在「已添加站点」里修正）",
                    "仍然保存", danger: false);
                if (!go) return;
            }

            try
            {
                var site = new OnlineSite
                {
                    Id = _editSite?.Id ?? 0,
                    Name = name,
                    SearchTemplate = _template,
                    PageTemplate = _editSite?.PageTemplate,
                    Selectors = _sel,
                    Status = OnlineSiteStatus.Ok
                };

                if (site.Id > 0) Db.UpdateOnlineSite(site);
                else site.Id = Db.AddOnlineSite(site);

                SavedSite = site;
                App.LogCrash($"[添加向导] 已保存站点 id={site.Id} name={site.Name}");
                Close();
            }
            catch (Exception ex)
            {
                txtFooterStatus.Text = "保存失败：" + ex.Message;
                App.LogCrash("[添加向导] 保存失败: " + ex);
            }
        }

        #endregion

        #region 工具

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            btnNext.IsEnabled = !busy;
            btnBack.IsEnabled = !busy;
            btnTest.IsEnabled = !busy;
            btnInfer.IsEnabled = !busy;

            if (!string.IsNullOrEmpty(status)) txtFooterStatus.Text = status;
        }

        private void CopySelectors(SiteSelectors from, SiteSelectors to)
        {
            to.Item = from.Item;
            to.Title = from.Title;
            to.Artist = from.Artist;
            to.Duration = from.Duration;
            to.Cover = from.Cover;
            to.Link = from.Link;
        }

        #endregion
    }
}
