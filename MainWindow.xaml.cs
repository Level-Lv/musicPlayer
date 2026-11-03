// all done ver1.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using musicPlayer.Data;
using musicPlayer.Services;

// csproj 开了 UseWindowsForms，其隐式全局 using 会与 WPF 的同名类型冲突，这里显式指定用 WPF 的版本
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

// 托盘图标用的 WinForms 类型统一走这个别名，避免和 WPF 类型混淆
using WinForms = System.Windows.Forms;

namespace musicPlayer
{
    /// <summary>播放方式：顺序 / 随机 / 单曲循环（对应 PRD 5.6）。</summary>
    public enum PlayMode
    {
        Sequential = 0,   // 顺序：播到列表末尾停止
        Shuffle = 1,      // 随机：一轮内不重复，播完重新随机
        RepeatOne = 2     // 单曲循环
    }

    public partial class MainWindow : Window
    {
        #region 状态字段

        /// <summary>当前列表显示（并绑定到 ListView）的曲目 = 库缓存 过滤搜索 排序 后的结果，同时就是播放队列。</summary>
        private readonly ObservableCollection<TrackItem> VisibleTracks = new();

        /// <summary>左侧歌单方块（不含"全部歌曲"固定项）。</summary>
        private readonly ObservableCollection<PlaylistItem> Playlists = new();

        /// <summary>当前歌单的曲目缓存（未经过滤），由 DB 加载。</summary>
        private List<TrackItem> libraryCache = new();

        /// <summary>当前生效的搜索词；null 表示未在搜索状态。</summary>
        private string? activeSearchText;

        /// <summary>当前选中的歌单 id；0 = "全部歌曲"固定视图。</summary>
        private long currentPlaylistId;

        /// <summary>正在播放的曲目（跨视图引用，刷新列表后按路径匹配回显）。</summary>
        private TrackItem? currentTrack;

        /// <summary>随机模式下"这一轮"还没播过的歌（一轮不重复的关键）。</summary>
        private readonly List<TrackItem> shuffleBag = new();

        /// <summary>
        /// 在线播放队列（S8）：点在线结果试听时建立，元素是"在线 TrackItem"（不落盘）。
        /// 为空表示当前队列就是本地可见列表——两条来源共用下面这一套 next/prev/随机逻辑。
        /// </summary>
        private readonly List<TrackItem> onlineQueue = new();

        /// <summary>
        /// 播放代次：每次"发起播放意图"（点播/切歌/停止）都 +1。
        /// 在线地址解析是异步的（最多 6 秒），回来时若代次已变，说明用户早切走了，结果直接丢弃。
        /// </summary>
        private int playGeneration;

        /// <summary>
        /// 当前播放队列：在线试听 = 在线队列；否则 = 本地可见列表。
        /// 上一首/下一首/播放方式/自然播完全部只看这里，不区分曲目来源（PRD F5.2"与本地队列混排"）。
        /// </summary>
        private IReadOnlyList<TrackItem> PlayQueue =>
            onlineQueue.Count > 0 ? (IReadOnlyList<TrackItem>)onlineQueue : VisibleTracks;

        private PlayMode playMode = PlayMode.Sequential;
        private bool isPlaying;
        private bool isMuted;
        private int consecutiveFailures;

        private readonly DispatcherTimer timer;
        private bool isDraggingProgress;

        // 歌词状态
        private List<LyricLine> CurrentLyrics = new();
        private int CurrentLyricIndex = -1;
        private LyricsDesktopWindow? lyricsDesktopWindow;

        // 设置音量时抑制"写库"（启动赋值和程序改值时不需要持久化）
        private bool suppressVolumePersist;
        private readonly DispatcherTimer volumePersistTimer;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            // Win11 系统级圆角（句柄创建完成后应用；Win10 静默忽略保持方形）
            SourceInitialized += (_, _) => WindowEffects.ApplyRoundedCorners(this);

            // 在线曲库前置环境自检（WebView2 Runtime / ffmpeg），缺失仅提示不阻塞本地功能
            _ = CheckOnlineEnvironmentAsync();

            // 无界面自检入口（保留）：设置环境变量 MP_ONLINE_SELFTEST=1 启动时，
            // 自动跑一遍在线内核自检并把结果写入 %TEMP%\musicplayer_onlinetest.log
            _ = RunOnlineSelfTestIfRequestedAsync();

            // 诊断窗口冒烟入口（保留）：MP_ONLINE_DIAG=1 时自动打开诊断窗口并记录加载结果，
            // 用于验证 XAML/样式改动没有把窗口写崩（结果写 %TEMP%\musicplayer_diag.log）
            _ = OpenDiagnosticWindowIfRequestedAsync();

            // 人机验证窗口测试入口（保留）：MP_ONLINE_CAPTCHA_TEST=1/2，见方法注释
            _ = RunCaptchaWindowTestIfRequestedAsync();

            // 搜索引擎自检入口（S4，保留）：MP_ONLINE_SEARCH_TEST=1/2，见方法注释
            _ = RunSearchSelfTestIfRequestedAsync();

            // 在线视图自检入口（S6，保留）：MP_ONLINE_UI_TEST=1/2，见方法注释
            _ = RunOnlineUiTestIfRequestedAsync();

            // 添加站点向导自检入口（S5，保留）：MP_ONLINE_WIZARD_TEST=1/2，见方法注释
            _ = RunAddSiteWizardTestIfRequestedAsync();

            // 站点面板自检入口（S7，保留）：MP_ONLINE_MANAGER_TEST=1/2，见方法注释
            _ = RunSiteManagerTestIfRequestedAsync();

            // 在线试听自检入口（S8，保留）：MP_ONLINE_PLAY_TEST=1/2，见方法注释
            _ = RunPlaybackTestIfRequestedAsync();

            // 下载与入库自检入口（S9，保留）：MP_ONLINE_DOWNLOAD_TEST=1/2，见方法注释
            _ = RunDownloadTestIfRequestedAsync();

            // 站点状态自愈（S7）：搜索把站点标成 need_login/broken 时，就地提示 + 同步内存状态
            OnlineSearchService.SiteStatusChanged += OnOnlineSiteStatusChanged;

            // 初始化数据库（建库、建表、迁移旧 setting.json）
            Db.Initialize();

            musicfilesListView.ItemsSource = VisibleTracks;
            PlaylistsList.ItemsSource = Playlists;

            // 在线结果列表（S6）：数据源常驻，切视图只切可见性，搜索词与结果都保留
            onlineResultsList.ItemsSource = OnlineResults;

            // 音量持久化用的延迟写库定时器（拖动滑块时避免每帧写一次数据库）
            volumePersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            volumePersistTimer.Tick += (_, _) =>
            {
                volumePersistTimer.Stop();
                Db.SetSetting("volume", ((int)sliderVolume.Value).ToString());
            };

            // 加载歌单 → 当前歌单曲目 → 应用到列表
            LoadPlaylists();
            LoadLibraryCache();
            RefreshVisibleTracks();

            // 应用持久化设置（音量/播放方式/封面/背景/桌面歌词/上次歌曲）
            ApplyStartupSettings();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += timer_Tick;

            this.Closing += MainWindow_Closing;
            this.Closed += MainWindow_Closed;

            InitDownloadEvents();
            DownloadService.ResetInterruptedDownloads();   // 上次崩溃残留的 running → failed
            InitTrayIcon();
        }

        /// <summary>订阅下载队列事件（S9）：入库刷新 / 失败 Toast（事件在后台线程触发）。</summary>
        private void InitDownloadEvents()
        {
            DownloadService.Completed += (item, path) => Dispatcher.BeginInvoke(() =>
            {
                LoadLibraryCache();
                RefreshVisibleTracks();
                ShowToast($"已入库「{item.Result.Title}」");
                App.LogCrash($"[下载] 完成并入库：{path}");
            });

            DownloadService.Failed += (item, reason) => Dispatcher.BeginInvoke(() =>
            {
                // 403 且没带 Cookie → 大概率是站点要求登录，联动 S7 的状态徽章
                if (reason.Contains("需要登录") && item.Result.SiteId > 0)
                {
                    Db.SetOnlineSiteStatus(item.Result.SiteId, "need_login");
                    ShowToast($"「{item.Result.Title}」下载失败：{reason}（可在站点面板点「登录」）",
                        warning: true);
                }
                else
                {
                    ShowToast($"「{item.Result.Title}」下载失败：{reason}", warning: true);
                }
            });

            // 取消不用提示：按钮状态自己会变"重试"，点击时已经 Toast 过了
            DownloadService.Cancelled += _ => { };
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (lyricsDesktopWindow != null)
            {
                if (lyricsDesktopWindow.Dispatcher.CheckAccess())
                    lyricsDesktopWindow.Close();
                else
                    lyricsDesktopWindow.Dispatcher.Invoke(() => lyricsDesktopWindow.Close());
                lyricsDesktopWindow = null;
            }
            timer.Stop();
            DisposeTrayIcon();

            // 释放在线曲库隐藏内核（S2）
            HiddenBrowserService.Instance.Dispose();
        }

        // =====================================================================
        // 关闭行为 + 系统托盘（设置里可选"最小化到托盘"或"直接退出"，默认托盘）
        // =====================================================================

        private WinForms.NotifyIcon? trayIcon;

        /// <summary>托盘菜单里点"退出"时置 true，让 Closing 放行真正关闭。</summary>
        private bool isRealExit;

        /// <summary>气泡提示一个会话只弹一次，之后最小化到托盘就安静地藏起来。</summary>
        private bool trayBalloonShown;

        /// <summary>窗口关闭请求：下载中先确认；托盘模式下取消关闭并隐藏，否则放行。</summary>
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // 退出保护（S9）：还有下载没干完 → 二次确认
            if (DownloadService.HasWork)
            {
                int n = DownloadService.ActiveCount + DownloadService.PendingCount;
                if (!ConfirmDialog.Confirm(this, "还有下载任务",
                        $"有 {n} 个下载任务还没完成，退出会中止它们。\n确定要退出吗？", "退出"))
                {
                    e.Cancel = true;
                    return;
                }
                DownloadService.CancelAll();
            }

            if (isRealExit)
            {
                DisposeTrayIcon();
                return;
            }

            if (Db.GetSetting("close_action", "tray") != "exit")
            {
                e.Cancel = true;
                Hide();
                if (trayIcon != null) trayIcon.Visible = true;

                if (!trayBalloonShown)
                {
                    trayBalloonShown = true;
                    try
                    {
                        trayIcon?.ShowBalloonTip(2000, "音乐播放器",
                            "已最小化到系统托盘，双击图标恢复窗口。", WinForms.ToolTipIcon.Info);
                    }
                    catch
                    {
                        // 气泡失败无伤大雅，不影响托盘本身
                    }
                }
            }
            // close_action == "exit"：走正常关闭流程，Closed 里做收尾
        }

        /// <summary>创建托盘图标：双击恢复窗口；右键菜单 = 上一首 / 下一首 / 退出。</summary>
        private void InitTrayIcon()
        {
            trayIcon = new WinForms.NotifyIcon
            {
                Text = "音乐播放器",
                Visible = false // 只有关闭到托盘后才显示
            };

            // 应用图标来自 exe 内嵌的 musicNote.ico
            try
            {
                if (Environment.ProcessPath is string exePath)
                    trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch
            {
                // 图标取不到就退化为系统默认图标，不影响功能
            }

            var menu = new WinForms.ContextMenuStrip();
            var miPrev = new WinForms.ToolStripMenuItem("上一首");
            miPrev.Click += (_, _) => PlayPreviousTrack();
            var miNext = new WinForms.ToolStripMenuItem("下一首");
            miNext.Click += (_, _) => PlayNextTrack();
            var miExit = new WinForms.ToolStripMenuItem("退出");
            miExit.Click += (_, _) => ExitFromTray();
            menu.Items.AddRange(new WinForms.ToolStripItem[] { miPrev, miNext, new WinForms.ToolStripSeparator(), miExit });

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }

        /// <summary>从托盘恢复主窗口并带到前台。</summary>
        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        /// <summary>托盘菜单"退出"：真正退出程序。</summary>
        private void ExitFromTray()
        {
            isRealExit = true;
            Close();
            Application.Current.Shutdown();
        }

        private void DisposeTrayIcon()
        {
            if (trayIcon == null) return;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }

        /// <summary>托盘菜单 / 外部调用：切上一首。</summary>
        public void PlayPreviousTrack() => btnPrevious_Click(null, new RoutedEventArgs());

        /// <summary>托盘菜单 / 外部调用：切下一首。</summary>
        public void PlayNextTrack() => AdvanceToNext(manual: true);

        #region 启动与设置应用

        /// <summary>启动时从数据库应用所有持久化设置（对应 PRD 5.6 记忆项）。</summary>
        private void ApplyStartupSettings()
        {
            // --- 音量 / 静音 ---
            suppressVolumePersist = true;
            if (double.TryParse(Db.GetSetting("volume", "100"), out double vol))
            {
                sliderVolume.Value = Math.Clamp(vol, 0, 100);
            }
            suppressVolumePersist = false;

            isMuted = Db.GetSettingBool("muted");
            mediaPlayer.IsMuted = isMuted;
            UpdateMuteIcon();

            // --- 播放方式 ---
            playMode = Db.GetSetting("play_mode", "sequential") switch
            {
                "shuffle" => PlayMode.Shuffle,
                "repeat_one" => PlayMode.RepeatOne,
                _ => PlayMode.Sequential
            };
            UpdatePlayModeButton();

            // --- 全局背景（图片或视频） ---
            string bg = Db.GetSetting("background_file");
            if (File.Exists(bg)) ApplyBackground(bg);

            // --- 桌面歌词 ---
            if (Db.GetSettingBool("desktop_lyrics_visible"))
                ShowDesktopLyricsWindow();

            // --- 侧栏收起状态（启动直接摆好，不带动画） ---
            sidebarCollapsed = Db.GetSettingBool("sidebar_collapsed");
            ApplySidebarState(animate: false);

            // --- 上次播放歌曲：加载但不自动播放（PRD 5.6） ---
            LoadLastTrack();
        }

        /// <summary>把当前音量值反映到播放引擎。</summary>
        private void ApplyVolume()
        {
            // XAML 加载过程中滑块 Value 一赋初值就会触发 ValueChanged，
            // 此时 mediaPlayer 元素尚未创建，直接跳过（启动后 ApplyStartupSettings 会再正确设一次）
            if (mediaPlayer == null) return;

            mediaPlayer.Volume = sliderVolume.Value / 100.0;
            mediaPlayer.IsMuted = isMuted;
        }

        /// <summary>加载上次播放的歌曲：选中、就绪、加载歌词，但不自动播放。</summary>
        private void LoadLastTrack()
        {
            string lastPath = Db.GetLastTrackPath();
            if (string.IsNullOrEmpty(lastPath) || !File.Exists(lastPath)) return;

            var item = libraryCache.FirstOrDefault(t => t.FullPath == lastPath)
                       ?? Db.GetAllTracks().FirstOrDefault(t => t.FullPath == lastPath);
            if (item == null) return;

            currentTrack = item;
            var visible = VisibleTracks.FirstOrDefault(t => t.FullPath == lastPath);
            if (visible != null)
            {
                musicfilesListView.SelectedItem = visible;
                musicfilesListView.ScrollIntoView(visible);
            }

            try
            {
                mediaPlayer.Source = new Uri(item.FullPath); // 只就绪，不 Play
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载上次歌曲失败: {ex.Message}");
            }

            LoadLyricsForCurrentSong(item.FullPath);
            txtWindowTitle.Text = item.Title;
            Title = $"已就绪: {item.Title} - 音乐播放器";
            isPlaying = false;
            btnPlayPause.Content = "▶️";
        }

        #endregion

        #region 歌单栏

        /// <summary>从数据库刷新歌单方块列表。</summary>
        private void LoadPlaylists()
        {
            Playlists.Clear();
            foreach (var p in Db.GetPlaylists())
            {
                Playlists.Add(p);
            }
        }

        /// <summary>切换当前歌单视图（0 = 全部歌曲）。</summary>
        private void SelectPlaylist(long playlistId)
        {
            // 点任何歌单/全部歌曲 = 回到本地列表视图（在线视图与本地列表互斥）
            ExitOnlineView();

            currentPlaylistId = playlistId;

            // 两个互斥的高亮：固定方块 vs ListBox 选中项
            AllTracksBox.BorderBrush = playlistId == 0
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x88, 0xFF, 0xFF)); // 恢复默认 #88FFFFFF

            if (playlistId == 0)
            {
                suppressPlaylistSelection = true;
                PlaylistsList.SelectedIndex = -1;
                suppressPlaylistSelection = false;
            }

            LoadLibraryCache();
            RefreshVisibleTracks();
        }

        private bool suppressPlaylistSelection;

        /// <summary>点击固定"全部歌曲"方块。</summary>
        private void AllTracksBox_Click(object sender, MouseButtonEventArgs e)
            => SelectPlaylist(0);

        private void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressPlaylistSelection) return;
            if (PlaylistsList.SelectedItem is PlaylistItem p)
            {
                SelectPlaylist(p.Id);
            }
        }

        /// <summary>新建歌单：＋按钮原地变成内联输入框（不再弹独立窗口）。</summary>
        private void btnNewPlaylist_Click(object sender, RoutedEventArgs e)
            => ShowPlaylistEditor(null);

        /// <summary>内联编辑框当前模式："create" / "rename"，null = 未在编辑。</summary>
        private string? playlistEditMode;
        private PlaylistItem? playlistRenameTarget;

        /// <summary>展开歌单名内联编辑框：target=null 为新建，否则重命名 target。</summary>
        private void ShowPlaylistEditor(PlaylistItem? target)
        {
            playlistRenameTarget = target;
            playlistEditMode = target == null ? "create" : "rename";
            txtPlaylistEditor.Text = target?.Name ?? "";
            btnNewPlaylist.Visibility = Visibility.Collapsed;
            txtPlaylistEditor.Visibility = Visibility.Visible;
            txtPlaylistEditor.Focus();
            txtPlaylistEditor.SelectAll();
        }

        private void HidePlaylistEditor()
        {
            playlistEditMode = null;
            playlistRenameTarget = null;
            txtPlaylistEditor.Visibility = Visibility.Collapsed;
            btnNewPlaylist.Visibility = Visibility.Visible;
        }

        private void txtPlaylistEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPlaylistEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HidePlaylistEditor();
                e.Handled = true;
            }
        }

        /// <summary>编辑框失焦 = 确认（和回车一致；Esc 取消时模式已被清空，不会走到这里）。</summary>
        private void txtPlaylistEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (playlistEditMode != null) CommitPlaylistEditor();
        }

        private void CommitPlaylistEditor()
        {
            string name = txtPlaylistEditor.Text.Trim();
            string mode = playlistEditMode ?? "create";
            var target = playlistRenameTarget;
            HidePlaylistEditor();

            if (string.IsNullOrEmpty(name)) return;

            if (mode == "create")
            {
                long newId = Db.CreatePlaylist(name);
                LoadPlaylists();
                var item = Playlists.FirstOrDefault(p => p.Id == newId);
                if (item != null) PlaylistsList.SelectedItem = item;
                ShowToast($"已创建歌单「{name}」");
            }
            else if (target != null && name != target.Name)
            {
                Db.RenamePlaylist(target.Id, name);
                target.Name = name; // INPC 自动刷新方块文字和 DisplayChar
                ShowToast($"已重命名为「{name}」");
            }
        }

        /// <summary>右键菜单当前指向的歌单（菜单打开时由 PlaylistMenu_Opening 记录）。</summary>
        private PlaylistItem? contextMenuPlaylist;

        /// <summary>歌单方块右键菜单即将打开：记下方块对应的歌单，供菜单项点击时使用。</summary>
        private void PlaylistMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            contextMenuPlaylist = (sender as Border)?.DataContext as PlaylistItem;
        }

        /// <summary>右键重命名歌单：同样用内联编辑框。</summary>
        private void PlaylistRename_Click(object sender, RoutedEventArgs e)
        {
            var item = contextMenuPlaylist;
            if (item == null) return;
            ShowPlaylistEditor(item);
        }

        /// <summary>右键删除歌单（二次确认，只删歌单和关联，不删歌曲/物理文件）。</summary>
        private void PlaylistDelete_Click(object sender, RoutedEventArgs e)
            => DeletePlaylistWithConfirm(contextMenuPlaylist);

        /// <summary>删除歌单：应用风格确认弹窗 + 落库 + 界面回退（右键菜单和 Delete 键共用）。</summary>
        private void DeletePlaylistWithConfirm(PlaylistItem? item)
        {
            if (item == null) return;

            if (!ConfirmDialog.Confirm(this, "删除歌单",
                $"确定删除歌单「{item.Name}」吗？\n只删除歌单本身，歌曲和音乐文件都不受影响。", "删除"))
                return;

            Db.DeletePlaylist(item.Id);
            LoadPlaylists();

            if (currentPlaylistId == item.Id)
            {
                SelectPlaylist(0); // 当前歌单被删 → 回到全部歌曲
            }
        }

        #endregion

        #region 歌单拖拽排序

        /// <summary>按住左键拖动歌单方块时启动拖拽。</summary>
        private void PlaylistsList_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item?.Content is not PlaylistItem) return;

            // 拖拽数据 = 歌单 id 的字符串形式（Drop 里解析）
            DragDrop.DoDragDrop(item, ((PlaylistItem)item.Content).Id.ToString(), DragDropEffects.Move);
        }

        private void PlaylistsList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat)
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>放下：按鼠标 Y 坐标算出插入位置，重排并落库。</summary>
        private void PlaylistsList_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.StringFormat) is string idStr)) return;
            if (!long.TryParse(idStr, out long draggedId)) return;

            // 计算插入目标索引：鼠标位于哪个方块中线之上/之下
            int insertIndex = Playlists.Count;
            for (int i = 0; i < Playlists.Count; i++)
            {
                if (PlaylistsList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container)
                {
                    Point pos = e.GetPosition(container);
                    if (pos.Y < container.ActualHeight / 2)
                    {
                        insertIndex = i;
                        break;
                    }
                }
            }

            // 组装新顺序：摘出被拖项，插到目标位置
            var ordered = Playlists.Select(p => p.Id).ToList();
            ordered.Remove(draggedId);

            // 摘出后如果原位置在插入点之前，插入索引要左移一位
            int originalIndex = Playlists.ToList().FindIndex(p => p.Id == draggedId);
            if (originalIndex >= 0 && originalIndex < insertIndex) insertIndex--;

            insertIndex = Math.Clamp(insertIndex, 0, ordered.Count);
            ordered.Insert(insertIndex, draggedId);

            Db.ReorderPlaylists(ordered);
            LoadPlaylists();

            // 恢复拖拽项的选中状态
            var moved = Playlists.FirstOrDefault(p => p.Id == draggedId);
            if (moved != null) PlaylistsList.SelectedItem = moved;
        }

        private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
        {
            while (start != null && start is not T)
            {
                start = start is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(start)
                    : LogicalTreeHelper.GetParent(start);
            }
            return start as T;
        }

        #endregion

        #region 曲目列表与搜索

        /// <summary>从数据库加载当前歌单（或全部歌曲）的曲目缓存。</summary>
        private void LoadLibraryCache()
        {
            libraryCache = Db.GetPlaylistTracks(currentPlaylistId);
        }

        /// <summary>当前视图的排序方向（每个歌单记忆自己的升降序，全部歌曲也存在 settings）。</summary>
        private string GetCurrentSortDir()
        {
            if (currentPlaylistId == 0)
                return Db.GetSetting("sort_dir_all", "asc");

            return Playlists.FirstOrDefault(p => p.Id == currentPlaylistId)?.SortDir ?? "asc";
        }

        /// <summary>
        /// 自然排序比较器（PRD 5.3）：不区分大小写 + 中文按系统拼音序 + 数字感知（歌2 < 歌10）。
        /// 实现：把字符串切成"文字段 / 数字段"交替的序列，逐段比较——
        /// 数字段按数值比（先比有效位数再比内容），文字段用当前区域文化比较（中文即拼音序）。
        /// </summary>
        private static int NaturalCompare(string? a, string? b)
        {
            a ??= "";
            b ??= "";

            // 切段：连续数字算一段，其余连续文字算一段
            static List<string> Split(string s)
            {
                var parts = new List<string>();
                int start = 0;
                for (int i = 1; i <= s.Length; i++)
                {
                    // 数字→非数字 或 非数字→数字 的边界处切开
                    if (i == s.Length || char.IsDigit(s[i]) != char.IsDigit(s[start]))
                    {
                        parts.Add(s[start..i]);
                        start = i;
                    }
                }
                return parts;
            }

            var pa = Split(a);
            var pb = Split(b);

            for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            {
                string sa = pa[i], sb = pb[i];
                int cmp;

                if (char.IsDigit(sa[0]) && char.IsDigit(sb[0]))
                {
                    // 数字段：去掉前导零后，位数多的大；位数相同按字典序比
                    string na = sa.TrimStart('0');
                    string nb = sb.TrimStart('0');
                    cmp = na.Length != nb.Length
                        ? na.Length.CompareTo(nb.Length)
                        : string.CompareOrdinal(na, nb);
                }
                else
                {
                    // 文字段：当前文化比较（zh-CN 下即拼音序），不区分大小写
                    cmp = string.Compare(sa, sb, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                }

                if (cmp != 0) return cmp;
            }

            // 前缀相同 → 段数少的小（如 "歌1" < "歌1a"）
            return pa.Count.CompareTo(pb.Count);
        }

        /// <summary>把 库缓存 →(搜索过滤)→(排序) 应用到可见列表，同时就是新的播放队列。</summary>
        private void RefreshVisibleTracks()
        {
            // 搜索基础集：搜索中且范围选"全部歌曲"时跨歌单搜，否则用当前歌单缓存
            IEnumerable<TrackItem> items = libraryCache;
            if (activeSearchText != null && cmbSearchScope.SelectedIndex == 1)
            {
                items = Db.GetAllTracks();
            }

            // 搜索过滤（默认正则模式；Regex.IsMatch 天然支持子串匹配，普通文字直接搜也符合直觉）
            if (activeSearchText != null)
            {
                items = items.Where(t => MatchesSearch(t.Title, activeSearchText));
            }

            // 排序（只按歌名）
            var ordered = GetCurrentSortDir() == "desc"
                ? items.OrderByDescending(t => t.Title, Comparer<string>.Create((a, b) => NaturalCompare(a, b)))
                : items.OrderBy(t => t.Title, Comparer<string>.Create((a, b) => NaturalCompare(a, b)));

            VisibleTracks.Clear();
            foreach (var t in ordered)
            {
                VisibleTracks.Add(t);
            }

            UpdatePlayingMarkers();
            RestoreSelection();

            // 搜索后默认选中第一条结果（配合 ↑↓ 微调、回车即播）
            if (activeSearchText != null && VisibleTracks.Count > 0 && musicfilesListView.SelectedItem == null)
            {
                musicfilesListView.SelectedIndex = 0;
                musicfilesListView.ScrollIntoView(VisibleTracks[0]);
            }

            shuffleBag.Clear(); // 队列变了，随机池作废重来
        }

        /// <summary>↑↓ 键在可见列表中移动选中项（不抢焦点，搜索框里可以边输边选）。</summary>
        private void NavigateSelection(int delta)
        {
            if (VisibleTracks.Count == 0 || playlistEditMode != null) return;

            int idx = musicfilesListView.SelectedIndex;
            int next = idx < 0
                ? (delta > 0 ? 0 : VisibleTracks.Count - 1) // 未选中时：↓ 到第一条、↑ 到最后一条
                : Math.Clamp(idx + delta, 0, VisibleTracks.Count - 1);

            musicfilesListView.SelectedIndex = next;
            musicfilesListView.ScrollIntoView(musicfilesListView.SelectedItem);
        }

        /// <summary>判断歌名是否命中搜索（默认正则模式，不区分大小写）。</summary>
        private static bool MatchesSearch(string? title, string text)
        {
            if (string.IsNullOrEmpty(title)) return false;
            try
            {
                return Regex.IsMatch(title, text, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return false; // 无效正则不应让程序崩溃（入口处已有提示）
            }
        }

        /// <summary>列表刷新后，把正在播放的歌重新选中并滚到可见位置。</summary>
        private void RestoreSelection()
        {
            if (currentTrack == null) return;
            var visible = VisibleTracks.FirstOrDefault(t => t.FullPath == currentTrack.FullPath);
            if (visible != null)
            {
                musicfilesListView.SelectedItem = visible;
                musicfilesListView.ScrollIntoView(visible);
            }
        }

        /// <summary>同步"正在播放"金色高亮标记。</summary>
        private void UpdatePlayingMarkers()
        {
            foreach (var t in VisibleTracks)
            {
                t.IsPlaying = currentTrack != null && t.FullPath == currentTrack.FullPath;
            }
        }

        /// <summary>回车触发搜索（PRD 5.4：不做实时搜索）。</summary>
        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RunSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearSearch();
                e.Handled = true;
            }
        }

        private void RunSearch()
        {
            string text = txtSearch.Text.Trim();

            if (text.Length == 0)
            {
                ClearSearch();
                return;
            }

            // 正则校验（默认正则模式，无效直接提示）
            try
            {
                _ = new Regex(text);
            }
            catch (ArgumentException)
            {
                ShowToast("正则表达式无效。", warning: true);
                return;
            }

            activeSearchText = text;
            RefreshVisibleTracks();

            if (VisibleTracks.Count == 0)
            {
                ShowToast("未找到匹配歌曲。");
            }
        }

        private void ClearSearch()
        {
            if (activeSearchText == null && txtSearch.Text.Length == 0) return;
            activeSearchText = null;
            txtSearch.Text = "";
            RefreshVisibleTracks();
        }

        /// <summary>切换搜索范围后：若在搜索状态，立刻按新范围重搜。</summary>
        private void cmbSearchScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (activeSearchText != null && IsLoaded)
            {
                RefreshVisibleTracks();
            }
        }

        /// <summary>排序按钮：升降序切换，并持久化到当前歌单 / 全部歌曲设置。</summary>
        private void btnSort_Click(object sender, RoutedEventArgs e)
        {
            string newDir = GetCurrentSortDir() == "asc" ? "desc" : "asc";

            if (currentPlaylistId == 0)
            {
                Db.SetSetting("sort_dir_all", newDir);
            }
            else
            {
                Db.SetPlaylistSortDir(currentPlaylistId, newDir);
                var p = Playlists.FirstOrDefault(x => x.Id == currentPlaylistId);
                if (p != null) p.SortDir = newDir;
            }

            RefreshVisibleTracks();
        }

        /// <summary>双击播放。</summary>
        private void musicfilesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (musicfilesListView.SelectedItem is TrackItem track)
            {
                PlayTrack(track);
            }
        }

        #endregion

        #region 歌曲列表右键菜单

        /// <summary>右键菜单打开前：动态生成"添加到歌单"子菜单，控制菜单项可用性。</summary>
        private void SongListContextMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            // 1. "添加到歌单"子菜单 = 当前所有用户歌单
            menuAddToPlaylist.Items.Clear();
            foreach (var p in Playlists)
            {
                var mi = new MenuItem { Header = p.Name, Tag = p.Id };
                mi.Click += MenuAddToPlaylistItem_Click;
                menuAddToPlaylist.Items.Add(mi);
            }
            menuAddToPlaylist.IsEnabled = Playlists.Count > 0;

            // 2. "从当前歌单移除"只在具体歌单视图里可用
            menuRemoveFromPlaylist.Visibility = currentPlaylistId == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>把选中的（支持多选）歌曲加到指定歌单。</summary>
        private void MenuAddToPlaylistItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: long playlistId }) return;
            var tracks = musicfilesListView.SelectedItems.Cast<TrackItem>().ToList();
            if (tracks.Count == 0) return;

            foreach (var t in tracks)
            {
                Db.AddTrackToPlaylist(playlistId, t.Id); // 重复添加被唯一约束忽略
            }
            ShowToast($"已把 {tracks.Count} 首歌曲添加到歌单。");
        }

        /// <summary>从当前歌单移除选中歌曲（只解除关联）。</summary>
        private void menuRemoveFromPlaylist_Click(object? sender, RoutedEventArgs e)
        {
            if (currentPlaylistId == 0) return;

            var tracks = musicfilesListView.SelectedItems.Cast<TrackItem>().ToList();
            foreach (var t in tracks)
            {
                Db.RemoveTrackFromPlaylist(currentPlaylistId, t.Id);
            }

            LoadLibraryCache();
            RefreshVisibleTracks();
        }

        /// <summary>在资源管理器中定位文件。</summary>
        private void menuOpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (musicfilesListView.SelectedItem is not TrackItem track) return;
            if (!File.Exists(track.FullPath))
            {
                ShowToast("文件不存在。", warning: true);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.FullPath}\"")
            {
                UseShellExecute = true
            });
        }

        /// <summary>从库中移除（二次确认；删 DB 记录，绝不删物理文件）。</summary>
        private void menuRemoveFromLibrary_Click(object sender, RoutedEventArgs e)
        {
            var tracks = musicfilesListView.SelectedItems.Cast<TrackItem>().ToList();
            if (tracks.Count == 0) return;

            if (!ConfirmDialog.Confirm(this, "从库中移除",
                $"确定从音乐库移除 {tracks.Count} 首歌曲吗？\n只移除记录，音乐文件不会被删除。", "移除"))
                return;

            bool removingCurrent = tracks.Any(t => currentTrack != null && t.FullPath == currentTrack.FullPath);

            foreach (var t in tracks)
            {
                Db.RemoveTrackByPath(t.FullPath);
            }

            LoadLibraryCache();
            RefreshVisibleTracks();

            if (removingCurrent)
            {
                // 正在播的被移除了 → 自动跳下一首（PRD 5.1 失效处理的变体）
                currentTrack = null;
                StopPlayback();
                AdvanceToNext(manual: true);
            }
        }

        #endregion

        #region 播放控制

        /// <summary>
        /// 播放指定曲目（含失效检测、失败计数、状态与 UI 同步）。
        /// 本地曲目与在线曲目走同一个入口：唯一的区别是"播放源从哪来"
        /// （本地 = 文件路径，在线 = StreamUrl），其余播放器 UI 完全一致（S8）。
        /// </summary>
        private void PlayTrack(TrackItem track)
        {
            playGeneration++;   // 新的播放意图：让还在飞的在线地址解析结果作废

            if (!track.IsOnline)
            {
                // 播本地曲目 → 队列回落到本地可见列表（在线队列作废，避免"上一首"跳到上次搜索结果里）
                if (onlineQueue.Count > 0) onlineQueue.Clear();

                // ---- 播放前失效检测（PRD 5.1）----
                if (!File.Exists(track.FullPath))
                {
                    Db.RemoveTrackByPath(track.FullPath);
                    libraryCache.RemoveAll(t => t.FullPath == track.FullPath);
                    VisibleTracks.Remove(track);
                    shuffleBag.Clear();
                    ShowToast("文件不存在，已从音乐库移除。", warning: true);

                    if (currentTrack != null && currentTrack.IdentityKey == track.IdentityKey)
                    {
                        currentTrack = null;
                        AdvanceToNext(manual: true); // 正在播的失效了 → 自动跳下一首
                    }
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(track.StreamUrl))
            {
                // 调用方必须先解析出地址（PlayQueueTrackAsync / PlayOnlineResultAsync 负责）
                ShowToast("这条结果还没解析出播放地址", warning: true);
                return;
            }

            // 防双声断言（S8）：播放前再确认一次隐藏内核静音。
            // 详情页嗅探时注入的"自动点播放"脚本会让页面里的 <audio> 出声，
            // 若内核没静音就会和 MediaElement 一起响（同一首歌叠两遍，非常刺耳）。
            bool coreMuted = HiddenBrowserService.Instance.EnsureMutedCore();
            if (!coreMuted)
                App.LogCrash("[播放] 警告：隐藏内核未静音，可能出现双声");

            // 直接换 Source：MediaElement 会自动关闭旧媒体并打开新的，
            // 这个过程不会触发 MediaEnded，所以不会误触发"自动切下一首"
            try
            {
                mediaPlayer.Source = new Uri(track.IsOnline ? track.StreamUrl! : track.FullPath);
                mediaPlayer.Play();
                ApplyVolume();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"播放失败 {track.PlayUrl}: {ex.Message}");
                HandlePlayFailure();
                return;
            }

            consecutiveFailures = 0;
            currentTrack = track;
            UpdatePlayingMarkers();

            txtWindowTitle.Text = track.Title;
            Title = track.IsOnline
                ? $"正在播放(在线): {track.Title} - 音乐播放器"
                : $"正在播放: {track.Title} - 音乐播放器";

            // 记住这次播放（重启后加载但不自动播）——只记本地文件，在线地址会过期
            if (!track.IsOnline) Db.SetLastTrackPath(track.FullPath);

            // 歌词：在线曲目没有 .lrc 可找，改显示曲目信息卡（PRD F5.3）
            if (track.IsOnline)
                ShowOnlineTrackInfoCard(track);
            else
                LoadLyricsForCurrentSong(track.FullPath);

            // 播放态 UI
            isPlaying = true;
            btnPlayPause.Content = "⏸️";
            timer.Start();

            if (track.IsOnline)
            {
                App.LogCrash($"[播放] 在线流播「{track.Title}」源={track.StreamUrl} 内核静音={coreMuted}");
            }
        }

        /// <summary>播放失败：计数，自动跳下一首；连续 5 首失败则暂停并提示（PRD 5.6）。</summary>
        private void HandlePlayFailure()
        {
            consecutiveFailures++;

            if (consecutiveFailures >= 5)
            {
                StopPlayback();
                ShowToast("连续 5 首播放失败，已暂停播放。", warning: true);
                return;
            }

            AdvanceToNext(manual: true);
        }

        /// <summary>
        /// 当前曲目在**当前播放队列**中的下标，找不到返回 -1。
        /// 队列可能是本地可见列表，也可能是在线试听队列（S8），两者用 IdentityKey 比较
        /// （在线条目没有文件路径，不能拿 FullPath 比）。
        /// </summary>
        private int IndexOfCurrent()
        {
            if (currentTrack == null) return -1;
            var queue = PlayQueue;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].IdentityKey == currentTrack.IdentityKey) return i;
            }
            return -1;
        }

        /// <summary>
        /// 把"当前曲目"同步到界面上对应的列表（本地列表 or 在线结果列表），并滚动到可见位置。
        /// 在线条目回指 <see cref="TrackItem.OnlineSource"/>，才能在结果列表里选中同一行。
        /// </summary>
        private void SelectInQueue(TrackItem track)
        {
            if (track.IsOnline)
            {
                if (track.OnlineSource is { } source)
                {
                    onlineResultsList.SelectedItem = source;
                    onlineResultsList.ScrollIntoView(source);
                }
                return;
            }

            musicfilesListView.SelectedItem = track;
            musicfilesListView.ScrollIntoView(track);
        }

        /// <summary>从随机池里抽一首（一轮内不重复；池空则开启新一轮）。</summary>
        private TrackItem? PopFromShuffleBag()
        {
            // 把当前正在播的从池里去掉
            if (currentTrack != null)
            {
                shuffleBag.RemoveAll(t => t.IdentityKey == currentTrack.IdentityKey);
            }

            if (shuffleBag.Count == 0)
            {
                // 新一轮：全部可见曲目入池
                shuffleBag.AddRange(PlayQueue);
                if (shuffleBag.Count == 0) return null;
            }

            var rnd = new Random();
            var pick = shuffleBag[rnd.Next(shuffleBag.Count)];
            shuffleBag.Remove(pick);
            return pick;
        }

        /// <summary>
        /// 切到下一首。manual=false（自然播完）时顺序模式到末尾即停止。
        /// 队列来源不区分本地/在线：在线条目播之前会先确保地址已解析（S8）。
        /// </summary>
        private void AdvanceToNext(bool manual)
        {
            var queue = PlayQueue;
            if (queue.Count == 0) return;

            TrackItem? next;
            if (playMode == PlayMode.Shuffle)
            {
                next = PopFromShuffleBag();
                if (next == null) return;
            }
            else
            {
                int nextIndex = IndexOfCurrent() + 1;
                if (nextIndex >= queue.Count)
                {
                    if (!manual)
                    {
                        StopPlayback(); // 顺序模式：播到末尾停止（PRD 5.6）
                        return;
                    }
                    nextIndex = 0; // 手动点下一首则回环
                }
                next = queue[nextIndex];
            }

            SelectInQueue(next);
            _ = PlayQueueTrackAsync(next);
        }

        /// <summary>
        /// 播放队列里的一条：本地直接播；在线若还没地址就先解析（详情页嗅探），拿到再播。
        /// 解析失败只提示、不跳歌——自动连环失败比停下来更让人困惑。
        ///
        /// 解析可能耗时几秒（嗅探上限 6s），期间用户很可能又点了别的/按了停止：
        /// 用 playGeneration 做"代次"校验，过期的解析结果直接丢弃，不会把已经切走的播放拽回来。
        /// </summary>
        private async System.Threading.Tasks.Task PlayQueueTrackAsync(TrackItem track)
        {
            int generation = ++playGeneration;

            if (!track.IsOnline)
            {
                PlayTrack(track);
                return;
            }

            if (string.IsNullOrWhiteSpace(track.StreamUrl))
            {
                bool ok = await ResolveOnlineTrackAsync(track);
                if (generation != playGeneration) return;   // 期间又发起了一次播放 → 这次作废
                if (!ok) return;
            }
            PlayTrack(track);
        }

        private void btnNext_Click(object? sender, RoutedEventArgs e) => AdvanceToNext(manual: true);

        private void btnPrevious_Click(object? sender, RoutedEventArgs e)
        {
            var queue = PlayQueue;
            if (queue.Count == 0) return;

            TrackItem? prev;
            if (playMode == PlayMode.Shuffle)
            {
                prev = PopFromShuffleBag(); // 随机模式上一首 = 随机换一首
            }
            else
            {
                int idx = IndexOfCurrent();
                int prevIndex = (idx <= 0 ? queue.Count : idx) - 1; // 回环
                prev = queue[prevIndex];
            }

            if (prev == null) return;

            SelectInQueue(prev);
            _ = PlayQueueTrackAsync(prev);
        }

        private void btnPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (mediaPlayer.Source == null) return;

            if (isPlaying)
            {
                mediaPlayer.Pause();
                timer.Stop();
                btnPlayPause.Content = "▶️";
                isPlaying = false;
            }
            else
            {
                mediaPlayer.Play();
                timer.Start();
                btnPlayPause.Content = "⏸️";
                isPlaying = true;
            }
        }

        /// <summary>停止播放并复位 UI（不切歌）。</summary>
        private void StopPlayback()
        {
            playGeneration++;   // 停播也让在飞的解析作废（否则解析回来会把歌又播起来）

            // 先摘掉源再 Stop：媒体关闭后 Stop 不会引发 MediaEnded，
            // 避免误触发"播完自动切下一首"
            mediaPlayer.Source = null;
            mediaPlayer.Stop();

            timer.Stop();
            isPlaying = false;
            btnPlayPause.Content = "▶️";
        }

        /// <summary>自然播完 → 单曲循环 / 自动切下一首。</summary>
        private void mediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (mediaPlayer.Source == null) return;

            if (playMode == PlayMode.RepeatOne)
            {
                mediaPlayer.Position = TimeSpan.Zero;
                mediaPlayer.Play();
                return;
            }

            AdvanceToNext(manual: false);
        }

        /// <summary>
        /// 打开媒体失败（S8 新增）：在线流最常见的失败是 403/Cookie 过期或地址带时效。
        /// 在线曲目不自动跳歌——连环跳会让用户连失败原因都看不清，只提示一次。
        /// </summary>
        private void mediaPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string reason = e.ErrorException?.Message ?? "未知原因";
            App.LogCrash($"[播放] MediaFailed 「{currentTrack?.Title}」({(currentTrack?.IsOnline == true ? "在线" : "本地")}): {reason}");

            if (currentTrack?.IsOnline == true)
            {
                ShowToast("在线播放失败：链接可能已失效，或该站点需要登录后播放", warning: true);
                StopPlayback();
                return;
            }

            HandlePlayFailure();
        }

        /// <summary>播放方式按钮：顺序 → 随机 → 单曲循环 三态循环。</summary>
        private void btnPlayMode_Click(object sender, RoutedEventArgs e)
        {
            playMode = (PlayMode)(((int)playMode + 1) % 3);
            UpdatePlayModeButton();

            Db.SetSetting("play_mode", playMode switch
            {
                PlayMode.Shuffle => "shuffle",
                PlayMode.RepeatOne => "repeat_one",
                _ => "sequential"
            });
            shuffleBag.Clear();
        }

        private void UpdatePlayModeButton()
        {
            btnPlayMode.Content = playMode switch
            {
                PlayMode.Shuffle => "🔀",
                PlayMode.RepeatOne => "🔂",
                _ => "🔁"
            };
            btnPlayMode.ToolTip = playMode switch
            {
                PlayMode.Shuffle => "随机播放（点击切换为单曲循环）",
                PlayMode.RepeatOne => "单曲循环（点击切换为顺序播放）",
                _ => "顺序播放（点击切换为随机播放）"
            };
        }

        #endregion

        #region 进度条与计时器

        private void timer_Tick(object? sender, EventArgs e)
        {
            if (isDraggingProgress || !mediaPlayer.NaturalDuration.HasTimeSpan) return;

            sliderProgress.Value = mediaPlayer.Position.TotalSeconds;
            txtCurrentTime.Text = mediaPlayer.Position.ToString(@"mm\:ss");

            CheckAndUpdateLyrics();
            lyricsDesktopWindow?.UpdatePosition(mediaPlayer.Position);
        }

        private void mediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                sliderProgress.Maximum = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                txtTotalTime.Text = mediaPlayer.NaturalDuration.TimeSpan.ToString(@"mm\:ss");
            }
            else
            {
                sliderProgress.Maximum = 0;
                txtTotalTime.Text = "00:00";
            }
        }

        private void sliderProgress_DragStarted(object sender, DragStartedEventArgs e)
            => isDraggingProgress = true;

        private void sliderProgress_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isDraggingProgress = false;
            if (mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                mediaPlayer.Position = TimeSpan.FromSeconds(sliderProgress.Value);
            }
        }

        /// <summary>
        /// 鼠标按下进度条即视为"用户正在操作"：冻结定时器回写。
        /// 否则点击跳转后的新值会在松手前被定时器打回当前播放位置，导致点击跳转失效。
        /// </summary>
        private void sliderProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => isDraggingProgress = true;

        /// <summary>点击进度条任意位置直接跳转。</summary>
        private void sliderProgress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingProgress = false; // 操作结束，恢复定时器回写

            if (!mediaPlayer.NaturalDuration.HasTimeSpan) return;

            mediaPlayer.Position = TimeSpan.FromSeconds(sliderProgress.Value);
            txtCurrentTime.Text = TimeSpan.FromSeconds(sliderProgress.Value).ToString(@"mm\:ss");

            if (!isPlaying && mediaPlayer.Source != null)
            {
                // 暂停状态下点击进度条：跳转后继续播放（沿用旧版行为）
                btnPlayPause_Click(null, new RoutedEventArgs());
            }
        }

        #endregion

        #region 音量控制

        private void sliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyVolume();

            // 启动赋值 / 程序改值不落库；用户拖动才落库（延迟 500ms 合并写入）
            if (!suppressVolumePersist && IsLoaded)
            {
                volumePersistTimer.Stop();
                volumePersistTimer.Start();
            }
        }

        private void btnMute_Click(object? sender, RoutedEventArgs e)
        {
            isMuted = !isMuted;
            ApplyVolume();
            UpdateMuteIcon();
            Db.SetSettingBool("muted", isMuted);
        }

        private void UpdateMuteIcon()
        {
            btnMute.Content = isMuted ? "🔇" : "🔊";
        }

        /// <summary>键盘 Ctrl+W / Ctrl+S 调音量（步长 5）。</summary>
        private void AdjustVolume(double delta)
        {
            sliderVolume.Value = Math.Clamp(sliderVolume.Value + delta, sliderVolume.Minimum, sliderVolume.Maximum);
        }

        #endregion

        #region 歌词显示与交互

        /// <summary>加载当前歌曲的歌词（LyricService 负责找文件和解析）。</summary>
        private void LoadLyricsForCurrentSong(string? audioFilePath)
        {
            CurrentLyricIndex = -1;

            if (string.IsNullOrEmpty(audioFilePath))
            {
                CurrentLyrics = new List<LyricLine> { new LyricLine { Text = "未选择歌曲" } };
                UpdateLyricsDisplay();
                return;
            }

            CurrentLyrics = LyricService.FindLyrics(audioFilePath);
            if (CurrentLyrics.Count == 0)
            {
                CurrentLyrics.Add(new LyricLine { Text = "暂无歌词" });
            }

            UpdateLyricsDisplay();
            lyricsDesktopWindow?.LoadLyrics(CurrentLyrics);
        }

        /// <summary>把歌词行渲染进主区域。</summary>
        private void UpdateLyricsDisplay()
        {
            LyricsPanel.Children.Clear();

            Style normalStyle = (Style)FindResource("NormalLyricTextBlockStyle");

            // 占位（未选歌/无歌词）：单行灰色居中，无时间戳
            if (CurrentLyrics.Count == 1 && CurrentLyrics[0].Timestamp == default)
            {
                var placeholder = new TextBlock
                {
                    Text = CurrentLyrics[0].Text,
                    Foreground = Brushes.Gray,
                    FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                LyricsPanel.Children.Add(placeholder);
                return;
            }

            if (CurrentLyrics.Count == 0) return;

            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                var lyric = CurrentLyrics[i];
                var tb = new TextBlock
                {
                    Text = lyric.Text,
                    Style = normalStyle,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6
                };
                lyric.Control = tb;
                LyricsPanel.Children.Add(tb);
            }
            // 高亮切换由 CheckAndUpdateLyrics 按播放进度维护
        }

        /// <summary>按播放进度更新歌词高亮行并自动滚动居中（沿用旧版已验证的滚动逻辑）。</summary>
        private void CheckAndUpdateLyrics()
        {
            if (CurrentLyrics.Count <= 1 || mediaPlayer.Source == null) return;

            TimeSpan pos = mediaPlayer.Position;
            int targetIndex = -1;
            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                if (pos < CurrentLyrics[i].Timestamp)
                {
                    targetIndex = i - 1;
                    break;
                }
            }

            if (targetIndex == -1)
            {
                targetIndex = pos >= CurrentLyrics[^1].Timestamp
                    ? CurrentLyrics.Count - 1
                    : 0;
            }
            if (targetIndex < 0) targetIndex = 0;

            if (targetIndex == CurrentLyricIndex) return;

            // 旧行恢复普通样式
            if (CurrentLyricIndex >= 0 && CurrentLyricIndex < CurrentLyrics.Count)
            {
                var old = CurrentLyrics[CurrentLyricIndex].Control;
                if (old != null)
                {
                    old.Style = (Style)FindResource("NormalLyricTextBlockStyle");
                    old.Opacity = 0.6;
                }
            }

            // 新行高亮 + 滚动到视口中央
            var newLine = CurrentLyrics[targetIndex].Control;
            if (newLine != null)
            {
                newLine.Style = (Style)FindResource("HighlightLyricTextBlockStyle");
                newLine.Opacity = 1.0;

                newLine.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    Point lyricPos = newLine.TranslatePoint(new Point(0, 0), LyricsPanel);
                    double viewportHeight = LyricsScrollViewer.ViewportHeight;
                    double targetOffset = lyricPos.Y - viewportHeight / 2.0 + newLine.ActualHeight / 2.0;

                    targetOffset = Math.Max(0, targetOffset);
                    targetOffset = Math.Min(LyricsScrollViewer.ScrollableHeight, targetOffset);

                    LyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
                }));
            }

            CurrentLyricIndex = targetIndex;
        }

        /// <summary>点击歌词行 → 跳转到该行时间并开始播放（PRD 4.3）。</summary>
        private void LyricsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not TextBlock clicked || CurrentLyrics.Count == 0) return;

            var line = CurrentLyrics.FirstOrDefault(l => l.Control == clicked);
            if (line == null) return;

            mediaPlayer.Position = line.Timestamp;

            if (!isPlaying && mediaPlayer.Source != null)
            {
                btnPlayPause_Click(null, new RoutedEventArgs()); // 暂停中点击 → 继续播放
            }
            CheckAndUpdateLyrics();
        }

        #endregion

        #region 桌面歌词窗口

        private void ShowDesktopLyricsWindow()
        {
            if (lyricsDesktopWindow == null)
            {
                lyricsDesktopWindow = new LyricsDesktopWindow();
            }
            if (!lyricsDesktopWindow.IsVisible)
            {
                lyricsDesktopWindow.Show();
            }
            lyricsDesktopWindow.LoadLyrics(CurrentLyrics);

            if (btnToggleLyrics.Content is TextBlock tb)
            {
                tb.Foreground = Brushes.Yellow;
            }
        }

        private void btnToggleLyrics_Click(object? sender, RoutedEventArgs e)
        {
            bool willBeVisible;

            if (lyricsDesktopWindow == null)
            {
                lyricsDesktopWindow = new LyricsDesktopWindow();
                lyricsDesktopWindow.Show();
                lyricsDesktopWindow.LoadLyrics(CurrentLyrics);
                willBeVisible = true;
            }
            else if (lyricsDesktopWindow.IsVisible)
            {
                lyricsDesktopWindow.Hide();
                willBeVisible = false;
            }
            else
            {
                lyricsDesktopWindow.Show();
                willBeVisible = true;
            }

            if (btnToggleLyrics.Content is TextBlock tb)
            {
                tb.Foreground = willBeVisible ? Brushes.Yellow : Brushes.White;
            }

            Db.SetSettingBool("desktop_lyrics_visible", willBeVisible);
        }

        #endregion

        #region 背景与封面

        /// <summary>按扩展名应用全局背景：视频走 MediaElement，图片铺窗口背景（PRD 5.5）。</summary>
        public void ApplyBackground(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string[] videoExts = { ".mp4", ".webm", ".avi", ".wmv", ".mov" };
            string[] imageExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

            try
            {
                if (videoExts.Contains(ext))
                {
                    // 撤掉可能存在的图片背景，恢复默认底色
                    this.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C));

                    backgroundVideo.Stop();
                    backgroundVideo.Source = new Uri(path, UriKind.Absolute);
                    backgroundVideo.Play();
                }
                else if (imageExts.Contains(ext))
                {
                    backgroundVideo.Stop();
                    backgroundVideo.Source = null;

                    var brush = new ImageBrush
                    {
                        ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(path, UriKind.Absolute)),
                        Stretch = Stretch.UniformToFill
                    };
                    this.Background = brush;
                }
                else
                {
                    ShowToast("不支持的背景格式。", warning: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"应用背景失败: {ex.Message}");
                ShowToast("背景加载失败，已使用默认背景。", warning: true);
            }
        }

        /// <summary>清除背景 → 回到默认深色。</summary>
        public void ClearBackground()
        {
            backgroundVideo.Stop();
            backgroundVideo.Source = null;
            this.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C));
        }

        /// <summary>背景视频循环播放。</summary>
        private void backgroundVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            backgroundVideo.Position = TimeSpan.Zero;
            backgroundVideo.Play();
        }

        #endregion

        #region 侧栏收起/展开

        /// <summary>侧栏（歌单栏 + 歌曲列表）是否处于收起状态，持久化到 settings。</summary>
        private bool sidebarCollapsed;

        private void btnCollapseSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void btnExpandSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            Db.SetSettingBool("sidebar_collapsed", sidebarCollapsed);
            ApplySidebarState(animate: true);
        }

        /// <summary>应用侧栏状态：收起时歌词铺满整个窗口，展开时恢复原宽度。</summary>
        private void ApplySidebarState(bool animate)
        {
            double playlistWidth = sidebarCollapsed ? 0 : 60;
            double songsWidth = sidebarCollapsed ? 0 : 340;

            // MinWidth 会顶住 0 宽度，收起时先放开，展开时再恢复
            ColSongs.MinWidth = sidebarCollapsed ? 0 : 280;

            SidebarSplitter.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            btnExpandSidebar.Visibility = sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
            btnCollapseSidebar.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

            if (animate)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                AnimateColumnWidth(ColPlaylists, playlistWidth, ease);
                AnimateColumnWidth(ColSongs, songsWidth, ease);
            }
            else
            {
                ColPlaylists.Width = new GridLength(playlistWidth, GridUnitType.Pixel);
                ColSongs.Width = new GridLength(songsWidth, GridUnitType.Pixel);
            }
        }

        /// <summary>用 GridLengthAnimation 做列宽过渡（WPF 原生不支持 GridLength 动画）。</summary>
        private static void AnimateColumnWidth(ColumnDefinition column, double to, IEasingFunction ease)
        {
            var anim = new GridLengthAnimation
            {
                From = column.Width.Value,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = ease
            };
            column.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        #endregion

        #region 应用内轻提示（Toast）

        private System.Windows.Threading.DispatcherTimer? toastTimer;

        /// <summary>
        /// 在线曲库前置环境自检：WebView2 Runtime 与 ffmpeg 组件。
        /// 任一缺失仅 Toast 提示（在线功能后续禁用），不影响本地功能使用。
        /// </summary>
        private async System.Threading.Tasks.Task CheckOnlineEnvironmentAsync()
        {
            var runtimeVersion = await OnlineEnvironment.GetWebView2VersionAsync();
            if (runtimeVersion == null)
            {
                ShowToast("未检测到 WebView2 运行时，在线曲库功能不可用", warning: true);
                return;
            }
            Debug.WriteLine($"[OnlineEnv] WebView2 Runtime: {runtimeVersion}");

            if (!OnlineEnvironment.FfmpegAvailable)
            {
                ShowToast("缺少 ffmpeg 组件，在线下载功能不可用", warning: true);
            }
        }

        /// <summary>
        /// 无界面自检入口（保留的测试代码）：
        /// 启动前设置环境变量 <c>MP_ONLINE_SELFTEST=1</c>，程序启动 2 秒后自动跑一遍在线内核自检，
        /// 结果写入 <c>%TEMP%\musicplayer_onlinetest.log</c>，方便命令行 / CI 式回归验证。
        /// 结束后**不主动释放内核**，用于观察空闲回收是否生效。
        /// </summary>
        private async System.Threading.Tasks.Task RunOnlineSelfTestIfRequestedAsync()
        {
            if (Environment.GetEnvironmentVariable("MP_ONLINE_SELFTEST") != "1") return;

            await System.Threading.Tasks.Task.Delay(2000); // 等窗口与数据库就绪

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] 在线内核自检（MP_ONLINE_SELFTEST=1）");
            bool ok = false;
            try
            {
                ok = await OnlineSelfTest.RunCoreSelfTestAsync(line => sb.AppendLine(line));
            }
            catch (Exception ex)
            {
                sb.AppendLine("自检异常: " + ex);
            }
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] 结束：{(ok ? "全部通过" : "存在失败项")}");

            string path = Path.Combine(Path.GetTempPath(), "musicplayer_onlinetest.log");
            void Flush()
            {
                try { File.WriteAllText(path, sb.ToString()); }
                catch (Exception ex) { Debug.WriteLine("写自检日志失败: " + ex.Message); }
            }
            Flush();

            // 空闲回收观察：每 10 秒记录一次内核状态，直到进程真正归零（或 90 秒超时）。
            // 目的：验证"空闲 60 秒后 msedgewebview2 退出、内存归还"这一条真的成立。
            for (int i = 0; i < 9; i++)
            {
                await System.Threading.Tasks.Task.Delay(10000);

                var svc = Services.HiddenBrowserService.Instance;
                var (procCount, procMb) = Services.HiddenBrowserService.GetBrowserProcessStats();
                sb.AppendLine($"[+{(i + 1) * 10,2}s] 内核={(svc.IsInitialized ? "运行中" : "已释放")} " +
                              $"进程={procCount} 内核内存={procMb}MB 主进程={Services.HiddenBrowserService.GetOwnWorkingSetMb()}MB " +
                              $"倒计时={svc.IdleRemainingSeconds}s");
                Flush();

                if (!svc.IsInitialized && procCount == 0)
                {
                    sb.AppendLine("空闲回收验证通过：内核进程与内存已归零");
                    Flush();
                    break;
                }
            }

            ShowToast(ok ? "在线自检完成：全部通过" : "在线自检完成：存在失败项", warning: !ok);
        }

        /// <summary>
        /// 诊断窗口冒烟入口（保留的测试代码）：
        /// 设置 <c>MP_ONLINE_DIAG=1</c> 启动，2 秒后自动打开诊断窗口，记录是否正常加载，
        /// 5 秒后自动关闭。用于验证诊断窗口/样式改动没有写崩（结果写 %TEMP%\musicplayer_diag.log）。
        /// </summary>
        private async System.Threading.Tasks.Task OpenDiagnosticWindowIfRequestedAsync()
        {
            if (Environment.GetEnvironmentVariable("MP_ONLINE_DIAG") != "1") return;

            await System.Threading.Tasks.Task.Delay(2000);
            string path = Path.Combine(Path.GetTempPath(), "musicplayer_diag.log");
            OnlineDiagnosticWindow? win = null;
            try
            {
                win = new OnlineDiagnosticWindow { Owner = this };
                win.Show();
                await System.Threading.Tasks.Task.Delay(1200);

                File.WriteAllText(path,
                    $"diag_window_created=OK\n" +
                    $"is_loaded={win.IsLoaded}\n" +
                    $"is_visible={win.IsVisible}\n" +
                    $"title={win.Title}\n" +
                    $"width={win.ActualWidth:0} height={win.ActualHeight:0}\n");

                await System.Threading.Tasks.Task.Delay(4000);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path, "diag_window_created=FAIL\n" + ex); } catch { }
            }
            finally
            {
                win?.Close();
            }
        }

        /// <summary>
        /// 人机验证中转窗口测试（保留的测试代码，S3 对应）：
        /// - <c>MP_ONLINE_CAPTCHA_TEST=1</c>：打开验证窗口加载**本地模拟挑战页**，你手动点页面里的
        ///   「模拟完成验证」按钮，窗口应自动关闭（完整走一遍"人点 → 程序检测 → 自动关窗"）；
        /// - <c>MP_ONLINE_CAPTCHA_TEST=2</c>：全自动模式，3 秒后程序注入脚本移除挑战元素，
        ///   用来回归"自动关窗"这条逻辑（无需人工操作）。
        /// 结果写 <c>%TEMP%\musicplayer_captchatest.log</c>。
        /// 注意：不要和 MP_ONLINE_SELFTEST 同时开，两者会抢同一个内核。
        /// </summary>
        private async System.Threading.Tasks.Task RunCaptchaWindowTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_CAPTCHA_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(2500); // 等主窗口显示

            var sb = new System.Text.StringBuilder();
            void Log(string s) => sb.AppendLine(s);

            CaptchaWindow? win = null;
            try
            {
                string demoUrl = Services.OnlineSelfTest.PrepareCaptchaDemoPage();
                Log($"[{DateTime.Now:HH:mm:ss}] 人机验证窗口测试（模式 {mode}）");
                Log($"模拟挑战页 = {demoUrl}");

                win = new CaptchaWindow(demoUrl) { Owner = this };
                win.Show();
                win.BringToFront();   // 非模态窗口：必须显式置前，否则可能被主窗口盖住
                Log($"窗口已打开 visible={win.IsVisible} rect=({win.Left:F0},{win.Top:F0})");

                await System.Threading.Tasks.Task.Delay(2500); // 等导航完成

                bool detected = await Services.HiddenBrowserService.Instance.DetectCaptchaAsync();
                Log($"挑战识别 = {detected}");
                Log($"此时判定已通过 = {await Services.HiddenBrowserService.Instance.IsCaptchaPassedAsync(demoUrl)}");

                if (mode == "2")
                {
                    await Services.HiddenBrowserService.Instance.ExecuteScriptAsync(
                        "var e=document.getElementById('challenge-running'); if(e) e.remove(); document.title='验证完成';");
                    Log("已注入模拟通过脚本（自动模式）");
                    ShowToast("自动模式：已模拟验证通过，窗口应在 1~2 秒内自动关闭");
                }
                else
                {
                    ShowToast("请在弹出的窗口里点「模拟完成验证」按钮");
                }

                var sw = Stopwatch.StartNew();
                while (win.IsVisible && sw.ElapsedMilliseconds < 40000)
                {
                    await System.Threading.Tasks.Task.Delay(300);
                }

                Log($"窗口已关闭 = {!win.IsVisible}");
                Log($"耗时 = {sw.ElapsedMilliseconds} ms");
                Log($"结果 = {win.Result}");

                ShowToast(win.Result == CaptchaResult.Passed
                    ? "验证窗口测试通过：已自动关闭"
                    : $"验证窗口结果是 {win.Result}", warning: win.Result != CaptchaResult.Passed);
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("验证窗口测试失败，详情见 %TEMP%\\musicplayer_captchatest.log", warning: true);
            }
            finally
            {
                try { win?.Close(); } catch { }

                string path = Path.Combine(Path.GetTempPath(), "musicplayer_captchatest.log");
                try { File.WriteAllText(path, sb.ToString()); } catch { }
            }
        }

        /// <summary>
        /// 搜索引擎自检（保留的测试代码，S4 对应，全部用本地模拟页、不依赖外网）：
        /// - <c>MP_ONLINE_SEARCH_TEST=1</c>：全自动跑全部 7 组用例（含"挑战页 → 自动过验证 → 续抓"）；
        /// - <c>MP_ONLINE_SEARCH_TEST=2</c>：同上，但验证环节需要你**手动点**模拟挑战页里的
        ///   「模拟完成验证」按钮（验证"人点 → 程序检测 → 续抓"的真实链路）。
        /// 结果写 <c>%TEMP%\musicplayer_searchtest.log</c>。
        /// 注意：不要和 MP_ONLINE_SELFTEST / MP_ONLINE_CAPTCHA_TEST 同时开，会抢同一个内核。
        /// </summary>
        private async System.Threading.Tasks.Task RunSearchSelfTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_SEARCH_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(2500); // 等主窗口与数据库就绪

            var sb = new System.Text.StringBuilder();
            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_searchtest.log");

            // 逐行立刻落盘：卡死/崩溃时也能看到走到哪一步（最后不再一次性覆盖写）
            void Log(string s)
            {
                sb.AppendLine(s);
                try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
            }

            Services.OnlineSearchSelfTest.ShowToastHint = msg => ShowToast(msg, warning: false);

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 搜索引擎自检（MP_ONLINE_SEARCH_TEST={mode}）");

                // 硬超时：自检再怎么样也不能永远挂着（挂住时用户会以为程序卡死）。
                // 3 分钟足够跑完全部用例（正常约 25 秒），超时就记日志并结束等待。
                var runTask = Services.OnlineSearchSelfTest.RunAsync(Log, autoCaptcha: mode == "1");
                var first = await System.Threading.Tasks.Task.WhenAny(runTask,
                    System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(3)));
                bool ok = first == runTask && await runTask;
                if (first != runTask)
                    Log($"[{DateTime.Now:HH:mm:ss}] ✗ 自检超时（3 分钟未结束），已中止等待");

                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {ok}");
                ShowToast(ok ? "搜索引擎自检：全部通过" : "搜索引擎自检：存在失败项，详见日志",
                    warning: !ok);
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("搜索引擎自检失败，详见 %TEMP%\\musicplayer_searchtest.log", warning: true);
            }
            finally
            {
                Services.OnlineSearchSelfTest.ShowToastHint = null;
            }
        }

        /// <summary>
        /// 在线视图自检（保留的测试代码，S6 对应）：
        /// - <c>MP_ONLINE_UI_TEST=1</c>：全自动——建演示站点 → 进入在线视图 → 搜索 → 断言行结构与状态
        ///   → 切回本地再切回在线（验证状态保留）→ 清理演示站点；结果写 <c>%TEMP%\musicplayer_uitest.log</c>；
        /// - <c>MP_ONLINE_UI_TEST=2</c>：只铺场——建演示站点、进入在线视图、搜一次把结果填满，
        ///   然后把界面留给你肉眼检查（可继续自己输入关键词/上下键/点下载按钮）。
        ///
        /// 演示站点指向本地模拟结果页（file://），不联网。S5 向导做好后，这个入口仍可用于界面回归。
        /// </summary>
        private async System.Threading.Tasks.Task RunOnlineUiTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_UI_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(1500);   // 等主窗口就绪

            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_uitest.log");
            int pass = 0, fail = 0;
            void Log(string s)
            {
                try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
            }
            void Ok(string s) { pass++; Log("  ✓ " + s); }
            void Bad(string s) { fail++; Log("  ✗ " + s); }

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 在线视图自检（MP_ONLINE_UI_TEST={mode}）");

                long siteId = Services.OnlineSearchSelfTest.EnsureDemoSite();
                Log($"演示站点 id = {siteId}（{Services.OnlineSearchSelfTest.DemoSiteName}）");

                // ① 入口方块 → 在线视图
                EnterOnlineView();
                if (OnlinePanel.Visibility == Visibility.Visible
                    && SongsPanel.Visibility == Visibility.Collapsed)
                    Ok("点在线方块：在线视图显示、本地列表隐藏");
                else
                    Bad($"视图切换异常：Online={OnlinePanel.Visibility} Songs={SongsPanel.Visibility}");

                if (cmbOnlineScope.Items.Count >= 2)
                    Ok($"范围下拉项数 = {cmbOnlineScope.Items.Count}（全部站点 + {onlineSites.Count} 个站点）");
                else
                    Bad($"范围下拉项数异常 = {cmbOnlineScope.Items.Count}");

                if (cmbOnlineScope.SelectedIndex == 1)
                    Ok($"默认选中第一个已添加站点（{cmbOnlineScope.SelectedItem}）");
                else
                    Bad($"默认范围应为第一个站点，实际 index={cmbOnlineScope.SelectedIndex}");

                if (onlineEmptySites.Visibility == Visibility.Collapsed)
                    Ok("已有站点时不显示空态引导");
                else
                    Bad("有站点却显示空态引导");

                // 入口方块位置：必须位于＋（新建歌单）下方、设置键上方（PRD F1.1）
                double plusY = btnNewPlaylist.TransformToVisual(this).Transform(new Point(0, 0)).Y;
                double onlineY = OnlineBox.TransformToVisual(this).Transform(new Point(0, 0)).Y;
                double settingsY = btnSettings.TransformToVisual(this).Transform(new Point(0, 0)).Y;
                if (plusY < onlineY && onlineY < settingsY)
                    Ok($"在线方块位置正确：＋({plusY:F0}) < 在线({onlineY:F0}) < 设置({settingsY:F0})");
                else
                    Bad($"在线方块位置异常：＋={plusY:F0} 在线={onlineY:F0} 设置={settingsY:F0}");

                // ② 搜索
                txtOnlineSearch.Text = "夜曲";
                var sw = Stopwatch.StartNew();
                await RunOnlineSearchAsync();
                long ms = sw.ElapsedMilliseconds;
                Log($"搜索耗时 {ms} ms，结果 {OnlineResults.Count} 条，状态行 = {txtOnlineStatus.Text}");

                if (OnlineResults.Count == 3) Ok($"搜索返回 3 条（{ms} ms）");
                else Bad($"结果数应为 3，实际 {OnlineResults.Count}");

                if (onlineResultsList.Items.Count == 3) Ok("列表渲染 3 行");
                else Bad($"列表行数应为 3，实际 {onlineResultsList.Items.Count}");

                var first = OnlineResults.FirstOrDefault();
                if (first != null && !string.IsNullOrWhiteSpace(first.DownloadButtonText))
                    Ok($"行内按钮：文案「{first.DownloadButtonText}」可点={first.IsDownloadEnabled}");
                else
                    Bad("行内下载按钮文案为空");

                if (onlineResultsList.SelectedIndex == 0) Ok("搜索完成自动选中第一条");
                else Bad($"应自动选中第一条，实际 {onlineResultsList.SelectedIndex}");

                if (onlineNoResult.Visibility == Visibility.Collapsed)
                    Ok("有结果时不显示「没有找到」");
                else
                    Bad("有结果却显示空结果文案");

                // ③ 视图互切：状态保留
                SelectPlaylist(0);
                if (SongsPanel.Visibility == Visibility.Visible
                    && OnlinePanel.Visibility == Visibility.Collapsed)
                    Ok("点「全部歌曲」回到本地列表");
                else
                    Bad("点歌单未回到本地列表");

                EnterOnlineView();
                if (txtOnlineSearch.Text == "夜曲" && OnlineResults.Count == 3)
                    Ok("互切后保留搜索词与结果");
                else
                    Bad($"互切丢状态：关键词「{txtOnlineSearch.Text}」结果 {OnlineResults.Count}");

                if (mode == "1")
                {
                    // ④ 清理：不留演示站点在用户库里
                    Services.OnlineSearchSelfTest.RemoveDemoSite();
                    RefreshOnlineSites();
                    Log("已清理演示站点");

                    if (onlineSites.Count == 0 && onlineEmptySites.Visibility == Visibility.Visible)
                        Ok("清理后回到「添加第一个站点」空态");
                    else
                        Bad($"清理后空态异常：站点 {onlineSites.Count} 个，空态 {onlineEmptySites.Visibility}");
                }
                else
                {
                    txtOnlineSearch.Focus();
                    txtOnlineSearch.SelectAll();
                    ShowToast("在线视图已就绪：可自己输入关键词回车搜索（演示站点指向本地模拟页）");
                }

                Log($"\n结果：{pass} 通过 / {fail} 失败");
                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {fail == 0}");

                if (mode == "1")
                {
                    ShowToast(fail == 0 ? "在线视图自检：全部通过" : "在线视图自检：存在失败项，详见日志",
                        warning: fail != 0);

                    // 全自动模式跑完自己退出：不留下一个占着单实例/内核的窗口等用户手动关，
                    // 也避免下次启动被"已有实例在运行"挡掉（这个坑踩过好几次）
                    await System.Threading.Tasks.Task.Delay(1200);
                    Application.Current?.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("在线视图自检异常，详见 %TEMP%\\musicplayer_uitest.log", warning: true);
            }
        }

        /// <summary>
        /// 添加站点向导自检（保留的测试代码，S5 对应）：
        /// - <c>MP_ONLINE_WIZARD_TEST=1</c>：全自动——模板反推 9 项 + 自动猜/点选脚本 11 项 +
        ///   端到端（配置→搜索→入库→读回），结果写 <c>%TEMP%\musicplayer_wizardtest.log</c>，跑完自动退出；
        /// - <c>MP_ONLINE_WIZARD_TEST=2</c>：打开向导并直接跳到第 2 步（页面 = 本地模拟结果页），
        ///   让你亲手点选、看右侧预览、再走第 3 步测试与保存。
        ///
        /// 为什么 mode 2 跳过第 1 步：本地模拟页是 file:// 地址，而"粘贴网址反推模板"只接受
        /// http/https（真实站点才有意义）。mode 1 里用构造的 http 地址覆盖了反推逻辑。
        /// </summary>
        private async System.Threading.Tasks.Task RunAddSiteWizardTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_WIZARD_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(1500);   // 等主窗口就绪

            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_wizardtest.log");
            void Log(string s)
            {
                try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
            }

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 添加站点向导自检（MP_ONLINE_WIZARD_TEST={mode}）");

                if (mode == "2")
                {
                    var (resultsUrl, _, _) = Services.OnlineSearchSelfTest.PrepareDemoPages();
                    EnterOnlineView();
                    Log($"模拟结果页 = {resultsUrl}");

                    var saved = await AddSiteWizardWindow.ShowForTestAsync(
                        this, resultsUrl + "?q={q}", "夜曲");
                    if (saved != null)
                    {
                        RefreshOnlineSites(saved.Id);
                        ShowToast($"测试：已保存站点「{saved.Name}」");
                        Log($"保存站点 id={saved.Id} name={saved.Name} 模板={saved.SearchTemplate}");
                        Log($"选择器 item={saved.Selectors.Item} title={saved.Selectors.Title}");
                    }
                    else
                    {
                        Log("向导被关闭，未保存");
                    }
                    Log($"[{DateTime.Now:HH:mm:ss}] 手动模式结束");
                    return;
                }

                bool ok = await Services.AddSiteSelfTest.RunAsync(Log);
                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {ok}");
                ShowToast(ok ? "站点向导自检：全部通过" : "站点向导自检：存在失败项，详见日志",
                    warning: !ok);

                await System.Threading.Tasks.Task.Delay(1200);
                Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("站点向导自检异常，详见 %TEMP%\\musicplayer_wizardtest.log", warning: true);
            }
        }

        /// <summary>
        /// S7 站点面板自检入口（保留）：MP_ONLINE_MANAGER_TEST=1 全自动 / =2 打开面板手动看。
        /// 日志写 %TEMP%\musicplayer_managertest.log（逐行落盘，卡住也能定位）。
        /// </summary>
        private async System.Threading.Tasks.Task RunSiteManagerTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_MANAGER_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(1500);   // 等主窗口与数据库就绪

            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_managertest.log");
            void Log(string s)
            {
                try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
            }

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 站点面板自检（MP_ONLINE_MANAGER_TEST={mode}）");

                if (mode == "2")
                {
                    EnterOnlineView();
                    Services.SiteManagerSelfTest.RemoveDemoSites();
                    var ids = Services.SiteManagerSelfTest.SeedDemoSites();
                    Log($"已造演示站点 {ids.Count} 个：正常站 / 坏选择器 / 需登录站");
                    RefreshOnlineSites(ids.FirstOrDefault());

                    ShowToast("测试：已造 3 个演示站点，试试测试/重配/登录/删除");
                    _ = SiteManagerWindow.ShowAsync(this, "夜曲");   // 非模态，留给用户玩
                    return;
                }

                bool ok = await Services.SiteManagerSelfTest.RunAsync(Log);
                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {ok}");
                ShowToast(ok ? "站点面板自检：全部通过" : "站点面板自检：存在失败项，详见日志",
                    warning: !ok);

                await System.Threading.Tasks.Task.Delay(1200);
                Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("站点面板自检异常，详见 %TEMP%\\musicplayer_managertest.log", warning: true);
            }
        }

        /// <summary>
        /// 下载与入库自检入口（保留的测试代码，S9 对应）：<c>MP_ONLINE_DOWNLOAD_TEST=1/2</c>。
        /// =1 全自动：跑 <see cref="Services.DownloadSelfTest.RunAsync"/>（本地 http 夹具 + ffmpeg 真流），
        ///   结果逐行落盘 %TEMP%\musicplayer_downloadtest.log，完成后自动退出；
        /// =2 手动：本地服务器铺一条可下载的演示结果进在线列表，用户点行内"下载"按钮试真实链路。
        /// </summary>
        private async System.Threading.Tasks.Task RunDownloadTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_DOWNLOAD_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(1500);   // 等主窗口与数据库就绪

            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_downloadtest.log");
            void Log(string s)
            {
                try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
            }

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 下载自检（MP_ONLINE_DOWNLOAD_TEST={mode}）");

                // ── 手动模式：铺一条可下载的演示结果就走人 ──────────────────
                if (mode == "2")
                {
                    string dir = Path.Combine(Path.GetTempPath(), "musicplayer_downloadtest");
                    Directory.CreateDirectory(dir);
                    string wav = Services.PlaybackSelfTest.CreateDemoWav(Path.Combine(dir, "manual-dl.wav"));
                    var server = Services.PlaybackSelfTest.LocalHttpServer.Start(dir);

                    EnterOnlineView();
                    long siteId = Services.OnlineSearchSelfTest.EnsureDemoSite("演示站·下载");

                    OnlineResults.Clear();
                    onlineTrackMap.Clear();
                    var r = new OnlineResult
                    {
                        Title = "演示曲·可下载",
                        Artist = "自检",
                        SiteName = "本地演示站",
                        SiteId = siteId,
                        AudioUrl = server.BaseUrl + "manual-dl.wav",
                        DetailUrl = server.BaseUrl + "manual-dl.wav",
                        CanDownload = true,
                        Duration = TimeSpan.FromSeconds(30)
                    };
                    OnlineResults.Add(r);

                    onlineSearched = true;
                    RefreshOnlineSites(siteId);
                    txtOnlineStatus.Text = "演示结果 1 条：点行内「下载」按钮试下载链路";
                    UpdateOnlineEmptyState();
                    ShowToast("测试：点「下载」，完成后去音乐库找「演示曲·可下载」");
                    Log("手动模式：已铺可下载演示结果，等服务退出");
                    return;
                }

                bool ok = await Services.DownloadSelfTest.RunAsync(Log);
                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {ok}");
                ShowToast(ok ? "下载自检：全部通过" : "下载自检：存在失败项，详见日志",
                    warning: !ok);

                await System.Threading.Tasks.Task.Delay(1200);
                Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("下载自检异常，详见 %TEMP%\\musicplayer_downloadtest.log", warning: true);
            }
        }

        /// <summary>
        /// 在线试听自检（保留的测试代码，S8 对应）：<c>MP_ONLINE_PLAY_TEST=1/2</c>。
        ///
        /// mode 1（全自动，跑完自动退出）：用本地 http 夹具走完整条试听链路，断言 18 项——
        /// 地址判定（直链/HLS/blob）、IdentityKey、详情页嗅探（含"自动点播放"那一枪）、
        /// 解析器三条分支（嗅探成功/无详情页/流媒体）、在线队列构建（无链接条目被排除）、
        /// 真播放（MediaOpened）、播放态与标题同步、歌词信息卡、内核静音（防双声）、
        /// 队列前进/后退、播本地曲目后队列回落、顺序模式到末尾停止。
        ///
        /// mode 2（手动）：铺 4 条演示结果 + 一个演示站点，让你直接点行试听；
        /// 本地 http 服务留着不关（进程退出即释放），文件在 %TEMP%\musicplayer_playtest\。
        /// </summary>
        private async System.Threading.Tasks.Task RunPlaybackTestIfRequestedAsync()
        {
            string mode = Environment.GetEnvironmentVariable("MP_ONLINE_PLAY_TEST") ?? "";
            if (mode != "1" && mode != "2") return;

            await System.Threading.Tasks.Task.Delay(1500);   // 等主窗口就绪

            string logPath = Path.Combine(Path.GetTempPath(), "musicplayer_playtest.log");
            int pass = 0, fail = 0;
            void Log(string s) { try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { } }
            void Ok(string what) { pass++; Log($"  ✓ {what}"); }
            void Bad(string what) { fail++; Log($"  ✗ {what}"); }

            Services.PlaybackSelfTest.LocalHttpServer? server = null;
            Services.PlaybackSelfTest.PlaybackFixtures? fx = null;
            string originalLastTrack = "";
            bool keepFixtures = false;     // mode 2 要留着服务给用户点播
            bool originalMute = mediaPlayer.IsMuted;

            // 播放方式必须锁定"顺序"：用户设置里可能是随机/单曲循环，
            // 那样"下一首"是随机抽的、"播到末尾"也不会停，断言就变成看运气（踩过）。
            PlayMode originalPlayMode = playMode;
            playMode = PlayMode.Sequential;
            UpdatePlayModeButton();
            shuffleBag.Clear();

            try
            {
                try { File.WriteAllText(logPath, ""); } catch { }
                Log($"[{DateTime.Now:HH:mm:ss}] 在线试听自检（MP_ONLINE_PLAY_TEST={mode}）");

                originalLastTrack = Db.GetLastTrackPath();

                (server, fx) = Services.PlaybackSelfTest.PrepareFixtures();
                Log($"夹具音频 = {fx.AudioFilePath}");
                Log($"直链 = {fx.DirectUrl}");
                Log($"详情页 = {fx.DetailUrl}");

                // ── 手动模式：铺好界面就走人 ────────────────────────────────
                if (mode == "2")
                {
                    keepFixtures = true;
                    EnterOnlineView();
                    long siteId = Services.OnlineSearchSelfTest.EnsureDemoSite("演示站·试听");

                    var demo = Services.PlaybackSelfTest.CreateDemoResults(fx);
                    OnlineResults.Clear();
                    onlineTrackMap.Clear();
                    foreach (var r in demo) OnlineResults.Add(r);

                    onlineSearched = true;
                    RefreshOnlineSites(siteId);
                    txtOnlineStatus.Text = "演示结果 5 条：点一行试听（第 2 条要走动态嗅探）";
                    UpdateOnlineEmptyState();
                    ShowToast("测试：点任意一行试听；第 2 条会先解析播放地址");
                    Log("手动模式：已铺 5 条演示结果，等服务退出");
                    return;
                }

                mediaPlayer.IsMuted = true;   // 自检不出声（WAV 振幅也很低，双保险）

                // ① 地址判定（纯函数）
                var direct = OnlineSearchService.DecideForUrl("https://example.com/a.mp3");
                if (direct.CanPlay) Ok("直链 mp3 → 可在线播放");
                else Bad($"直链 mp3 应可播放，实际 reason={direct.Reason}");

                var hls = OnlineSearchService.DecideForUrl("https://example.com/live/index.m3u8");
                if (!hls.CanPlay && hls.IsStream && hls.Reason.Contains("下载"))
                    Ok("m3u8 → 判为流媒体，提示下载后播放");
                else Bad($"m3u8 判定异常：CanPlay={hls.CanPlay} IsStream={hls.IsStream}");

                var blob = OnlineSearchService.DecideForUrl("blob:https://example.com/abcd");
                if (!blob.CanPlay && blob.Reason.Contains("临时流")) Ok("blob: → 判为页面内部临时流");
                else Bad($"blob 判定异常：{blob.Reason}");

                // ② IdentityKey（队列定位靠它，不能拿 FullPath 比）
                var demoResults = Services.PlaybackSelfTest.CreateDemoResults(fx);
                var t1 = GetOrCreateOnlineTrack(demoResults[0]);
                var t1Again = GetOrCreateOnlineTrack(demoResults[0]);
                var t2 = GetOrCreateOnlineTrack(demoResults[1]);
                bool localDiffers = new TrackItem { Title = "同名", FullPath = Path.Combine(Path.GetTempPath(), "a.mp3") }.IdentityKey
                                    != new TrackItem { Title = "同名", FullPath = Path.Combine(Path.GetTempPath(), "b.mp3") }.IdentityKey;
                if (ReferenceEquals(t1, t1Again) && t1.IdentityKey == t1Again.IdentityKey
                    && t1.IdentityKey != t2.IdentityKey && localDiffers)
                    Ok("IdentityKey：同结果复用同一对象、不同条目/不同文件互不相等");
                else Bad("IdentityKey 判定异常");

                // ③ 解析器第一层：详情页 DOM 里就有 <audio src> → 静态提取，秒出（不该等 6s 嗅探）
                var staticResult = demoResults[4];
                var swStatic = Stopwatch.StartNew();
                var staticResolved = await OnlineSearchService.ResolveAudioAsync(staticResult);
                swStatic.Stop();
                if (staticResolved.CanPlay && !string.IsNullOrWhiteSpace(staticResult.AudioUrl)
                    && swStatic.ElapsedMilliseconds < 4000)
                    Ok($"静态提取命中（{swStatic.ElapsedMilliseconds} ms，{staticResult.AudioUrl}）");
                else Bad($"静态提取异常：CanPlay={staticResolved.CanPlay} 耗时={swStatic.ElapsedMilliseconds}ms reason={staticResolved.Reason}");

                // ④ 解析器第二层：音频地址点了播放才写入 → 动态嗅探（1.2s 后注入"自动点播放"）
                var needSniff = demoResults[1];
                var swSniff = Stopwatch.StartNew();
                var resolved = await OnlineSearchService.ResolveAudioAsync(needSniff);
                swSniff.Stop();
                if (resolved.CanPlay && !string.IsNullOrWhiteSpace(needSniff.AudioUrl))
                    Ok($"动态嗅探命中并回填（{swSniff.ElapsedMilliseconds} ms，{needSniff.AudioUrl}）");
                else Bad($"动态嗅探异常：CanPlay={resolved.CanPlay} reason={resolved.Reason}；" +
                         $"内核嗅探事件 {HiddenBrowserService.Instance.SniffEventCount} 个，最后 {HiddenBrowserService.Instance.LastSniffedUrl ?? "无"}");

                // ④b 缓存：同一条结果再解析不该重新开页面
                var swCache = Stopwatch.StartNew();
                var cached = await OnlineSearchService.ResolveAudioAsync(needSniff);
                swCache.Stop();
                if (cached.CanPlay && swCache.ElapsedMilliseconds < 300)
                    Ok($"二次解析走缓存（{swCache.ElapsedMilliseconds} ms）");
                else Bad($"缓存未生效：{swCache.ElapsedMilliseconds} ms");

                // ⑤ 解析器：无详情页 → 明确原因
                var noLink = await OnlineSearchService.ResolveAudioAsync(demoResults[3]);
                if (!noLink.CanPlay && noLink.Reason.Contains("详情页"))
                    Ok("无详情页 → 明确提示无法解析");
                else Bad($"无详情页判定异常：{noLink.Reason}");

                // ⑥ 解析器：流媒体 → 不可试听但可下载
                var streamOnly = await OnlineSearchService.ResolveAudioAsync(demoResults[2]);
                if (!streamOnly.CanPlay && streamOnly.IsStream)
                    Ok("流媒体结果 → 拒绝试听（地址留给 S9 下载）");
                else Bad($"流媒体判定异常：CanPlay={streamOnly.CanPlay}");

                // ⑦ 在线队列：5 条结果里"既无直链又无详情页"和"已知流媒体"的都不进队列
                OnlineResults.Clear();
                onlineTrackMap.Clear();
                foreach (var r in demoResults) OnlineResults.Add(r);
                BuildOnlineQueue();
                if (onlineQueue.Count == 3) Ok("在线队列 3 条（无链接/流媒体条目被排除）");
                else Bad($"在线队列应为 3 条，实际 {onlineQueue.Count}（{string.Join("、", onlineQueue.Select(t => t.Title))}）");

                // ⑧ 真播放：MediaElement 打开 http 流并出声（测试已静音）
                var playTrackItem = GetOrCreateOnlineTrack(demoResults[0]);
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOpened(object? s, RoutedEventArgs e) => opened.TrySetResult(true);
                mediaPlayer.MediaOpened += OnOpened;
                PlayTrack(playTrackItem);
                bool openedInTime = await Task.WhenAny(opened.Task, Task.Delay(5000)) == opened.Task;
                mediaPlayer.MediaOpened -= OnOpened;

                if (mediaPlayer.Source != null && openedInTime)
                    Ok($"在线曲目真播放：MediaOpened 触发，时长 {(mediaPlayer.NaturalDuration.HasTimeSpan ? mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + "s" : "未知")}");
                else Bad($"在线播放未打开：Source={mediaPlayer.Source} opened={openedInTime}");

                // ⑨ 播放态 UI 同步（与本地曲目同一套）
                if (isPlaying && txtWindowTitle.Text == playTrackItem.Title && Title.Contains("在线"))
                    Ok("播放态/标题同步（在线标识就位）");
                else Bad($"播放态异常：isPlaying={isPlaying} 标题={txtWindowTitle.Text} 窗口={Title}");

                // ⑩ 歌词区：没有 .lrc → 显示曲目信息卡
                bool infoCard = CurrentLyrics.Count == 0
                                && LyricsPanel.Children.Count == 1
                                && LyricsPanel.Children[0] is StackPanel card && card.Children.Count >= 2;
                if (infoCard) Ok("在线曲目的歌词区显示信息卡（浏览器无 .lrc 时不显示「暂无歌词」）");
                else Bad($"信息卡异常：歌词行={CurrentLyrics.Count} 子元素={LyricsPanel.Children.Count}");

                // ⑪ 防双声：内核必须静音
                bool muted = HiddenBrowserService.Instance.EnsureMutedCore()
                             && HiddenBrowserService.Instance.IsMutedCore;
                if (muted) Ok("隐藏内核静音确认（页面里的 <audio> 不会和我们一起响）");
                else Bad("隐藏内核未静音，存在双声风险");

                // ⑫ 下一首：在**在线队列**内前进
                AdvanceToNext(manual: true);
                await System.Threading.Tasks.Task.Delay(400);
                var second = GetOrCreateOnlineTrack(demoResults[1]);
                if (currentTrack != null && currentTrack.IdentityKey == second.IdentityKey)
                    Ok("下一首在在线队列内前进（第 2 条，地址已解析）");
                else Bad($"下一首异常：当前={currentTrack?.Title}");

                // ⑬ 上一首：回到第 1 条
                btnPrevious_Click(null, new RoutedEventArgs());
                await System.Threading.Tasks.Task.Delay(400);
                if (currentTrack != null && currentTrack.IdentityKey == playTrackItem.IdentityKey)
                    Ok("上一首回到队列上一条");
                else Bad($"上一首异常：当前={currentTrack?.Title}");

                // ⑭ 混排：播本地曲目 → 队列回落到本地可见列表
                var localTrack = new TrackItem { Title = "本地测试曲", FullPath = fx.AudioFilePath };
                PlayTrack(localTrack);
                if (onlineQueue.Count == 0 && ReferenceEquals(PlayQueue, VisibleTracks))
                    Ok("播本地曲目 → 在线队列清空，next/prev 回到本地列表");
                else Bad($"队列回落异常：onlineQueue={onlineQueue.Count}");

                // ⑮ 流媒体结果拒绝试听（StreamUrl 保持为空）
                var streamTrack = GetOrCreateOnlineTrack(demoResults[2]);
                bool streamPlayable = await ResolveOnlineTrackAsync(streamTrack);
                if (!streamPlayable && string.IsNullOrEmpty(streamTrack.StreamUrl))
                    Ok("流媒体结果点试听被拒绝（提示下载后播放）");
                else Bad($"流媒体试听应被拒绝：playable={streamPlayable} stream={streamTrack.StreamUrl}");

                // ⑯ 顺序模式：播到队列末尾即停止
                OnlineResults.Clear();
                OnlineResults.Add(demoResults[0]);
                BuildOnlineQueue();
                PlayTrack(onlineQueue[0]);
                AdvanceToNext(manual: false);
                if (!isPlaying && mediaPlayer.Source == null) Ok("顺序模式播到末尾停止（不越界、不回环）");
                else Bad($"末尾停止异常：isPlaying={isPlaying} Source={mediaPlayer.Source}");

                // ⑰ 停止播放后 UI 复位
                StopPlayback();
                if (!isPlaying && btnPlayPause.Content?.ToString() == "▶️") Ok("停止播放后按钮/状态复位");
                else Bad($"停止后 UI 异常：isPlaying={isPlaying} 按钮={btnPlayPause.Content}");

                // ⑱ 本地曲目仍可正常播放（改动没有把老路径弄坏）
                var opened2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOpened2(object? s, RoutedEventArgs e) => opened2.TrySetResult(true);
                mediaPlayer.MediaOpened += OnOpened2;
                PlayTrack(new TrackItem { Title = "本地测试曲2", FullPath = fx.AudioFilePath });
                bool localOk = await Task.WhenAny(opened2.Task, Task.Delay(5000)) == opened2.Task;
                mediaPlayer.MediaOpened -= OnOpened2;
                if (localOk) Ok("本地曲目播放不受影响（回归）");
                else Bad("本地曲目播放失败（回归项）");

                Log($"\n结果：{pass} 通过 / {fail} 失败");
                Log($"[{DateTime.Now:HH:mm:ss}] 结束，全部通过 = {fail == 0}");

                ShowToast(fail == 0 ? "在线试听自检：全部通过" : "在线试听自检：存在失败项，详见日志",
                    warning: fail != 0);

                await System.Threading.Tasks.Task.Delay(1200);
                Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log("异常: " + ex);
                ShowToast("在线试听自检异常，详见 %TEMP%\\musicplayer_playtest.log", warning: true);
            }
            finally
            {
                // 清理：先停播（http 服务还在），再恢复设置、关服务
                try
                {
                    if (!keepFixtures)
                    {
                        StopPlayback();
                        currentTrack = null;
                        mediaPlayer.IsMuted = originalMute;

                        OnlineResults.Clear();
                        onlineTrackMap.Clear();
                        onlineQueue.Clear();
                        shuffleBag.Clear();
                        onlineSearched = false;
                        if (originalLastTrack.Length > 0) Db.SetLastTrackPath(originalLastTrack);

                        HiddenBrowserService.Instance.StopLoading();
                        server?.Dispose();

                        playMode = originalPlayMode;
                        UpdatePlayModeButton();
                    }
                }
                catch (Exception ex)
                {
                    Log("清理异常（忽略）: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 应用内轻提示：主区顶部居中淡入，停留约 2.4 秒后淡出。
        /// 替代系统 MessageBox 弹窗（信息/警告类）；需要用户做选择的确认框仍用弹窗。
        /// </summary>
        public void ShowToast(string message, bool warning = false)
        {
            txtToast.Text = message;
            toastBorder.Background = new SolidColorBrush(warning
                ? Color.FromArgb(0xF0, 0x4A, 0x1E, 0x24)  // 警告：暗红底
                : Color.FromArgb(0xF0, 0x1E, 0x1E, 0x28)); // 信息：深灰底
            toastBorder.Visibility = Visibility.Visible;
            toastBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            if (toastTimer == null)
            {
                toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
                toastTimer.Tick += (_, _) =>
                {
                    toastTimer.Stop();
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (_, _) => toastBorder.Visibility = Visibility.Collapsed;
                    toastBorder.BeginAnimation(OpacityProperty, fadeOut);
                };
            }
            toastTimer.Stop();
            toastTimer.Start();
        }

        #endregion

        #region 自绘标题栏按钮

        private void btnTitleMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        /// <summary>最大化 / 还原切换（无边框窗口由 WindowChrome 负责最大化时避开任务栏）。</summary>
        private void btnTitleMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void btnTitleClose_Click(object sender, RoutedEventArgs e) => Close();

        #endregion

        #region 设置入口

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow(this) { Owner = this };
            settings.ShowDialog();
        }

        /// <summary>设置弹窗：库扫描（或新文件夹入库）完成后刷新歌曲列表缓存。</summary>
        public void ReloadAfterLibraryScan()
        {
            LoadLibraryCache();
            RefreshVisibleTracks();
            UpdatePlayingMarkers();
        }

        /// <summary>设置弹窗：歌词文件夹变更后，重新加载当前歌曲的歌词。</summary>
        public void ReloadCurrentLyrics()
        {
            if (currentTrack != null)
                LoadLyricsForCurrentSong(currentTrack.FullPath);
        }

        /// <summary>设置弹窗：播放方式变更（sequential / shuffle / repeat_one）。</summary>
        public void ApplyPlayModeFromSettings(string mode)
        {
            playMode = mode switch
            {
                "shuffle" => PlayMode.Shuffle,
                "repeat_one" => PlayMode.RepeatOne,
                _ => PlayMode.Sequential
            };
            UpdatePlayModeButton();
        }

        /// <summary>设置弹窗：默认音量变更。</summary>
        public void SetVolumeFromSettings(int volume)
        {
            suppressVolumePersist = true; // 值本来就来自数据库，不要再写回去
            sliderVolume.Value = Math.Clamp(volume, 0, 100);
            suppressVolumePersist = false;
            ApplyVolume();
        }

        #endregion

        #region 在线视图（S6：入口方块 / 搜索工具条 / 结果列表 / 底部操作栏）

        // ── 状态 ────────────────────────────────────────────────────────────────
        // 结果只存在内存里（PRD 决策⑤：搜索结果不进缓存/不落库）
        private readonly ObservableCollection<OnlineResult> OnlineResults = new();

        /// <summary>已添加站点（每次进入在线视图时从库里重读，S5/S7 改完配置能立刻生效）。</summary>
        private List<OnlineSite> onlineSites = new();

        /// <summary>当前是否处于在线视图（与本地歌曲列表互斥）。</summary>
        private bool isOnlineMode;

        /// <summary>是否已经搜过一次（决定"没有找到"文案要不要显示，避免一进来就报空）。</summary>
        private bool onlineSearched;

        private bool isOnlineSearching;
        private CancellationTokenSource? onlineSearchCts;

        /// <summary>默认选中"第一个已添加站点"（PRD 决策③：默认单站搜索）。</summary>
        private const int OnlineScopeAll = 0;

        // ── 入口：左侧"在线"方块 ────────────────────────────────────────────────

        private void OnlineBox_Click(object sender, MouseButtonEventArgs e) => EnterOnlineView();

        /// <summary>进入在线视图：隐藏本地歌曲列表，显示在线面板，刷新站点与范围下拉。</summary>
        private void EnterOnlineView()
        {
            if (isOnlineMode) return;
            isOnlineMode = true;

            SongsPanel.Visibility = Visibility.Collapsed;
            OnlinePanel.Visibility = Visibility.Visible;

            // 方块高亮 + 清掉歌单栏的选中态（两个视图互斥，避免"看着是高亮歌单其实在在线页"）
            SetOnlineBoxActive(true);
            AllTracksBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xFF, 0xFF));
            suppressPlaylistSelection = true;
            PlaylistsList.SelectedIndex = -1;
            suppressPlaylistSelection = false;

            RefreshOnlineSites();

            if (string.IsNullOrWhiteSpace(txtOnlineSearch.Text))
                txtOnlineSearch.Focus();
            else
                txtOnlineSearch.SelectAll();
        }

        /// <summary>回到本地歌曲列表（点歌单 / 点"全部歌曲"时调用）。</summary>
        private void ExitOnlineView()
        {
            if (!isOnlineMode) return;
            isOnlineMode = false;

            OnlinePanel.Visibility = Visibility.Collapsed;
            SongsPanel.Visibility = Visibility.Visible;
            SetOnlineBoxActive(false);

            // 搜索还在跑就取消，避免用户已经回本地列表了后台还在抓
            onlineSearchCts?.Cancel();
        }

        private void SetOnlineBoxActive(bool active)
        {
            OnlineBox.Background = active
                ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD7, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            OnlineBox.BorderBrush = active
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        }

        // ── 站点与范围下拉 ──────────────────────────────────────────────────────

        /// <summary>
        /// 重读站点表并刷新范围下拉；尽量保留用户当前选中的站点。
        /// </summary>
        /// <param name="selectSiteId">新增/编辑完指定站点后传它的 id，让下拉直接切过去。</param>
        private void RefreshOnlineSites(long? selectSiteId = null)
        {
            string? keepName = null;
            if (cmbOnlineScope.SelectedIndex > OnlineScopeAll
                && cmbOnlineScope.SelectedIndex - 1 < onlineSites.Count)
            {
                keepName = onlineSites[cmbOnlineScope.SelectedIndex - 1].Name;
            }

            try
            {
                onlineSites = Db.GetOnlineSites();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[在线] 读取站点失败: {ex.Message}");
                onlineSites = new List<OnlineSite>();
            }

            cmbOnlineScope.Items.Clear();
            cmbOnlineScope.Items.Add("全部站点");
            foreach (var s in onlineSites) cmbOnlineScope.Items.Add(s.Name);

            int index = onlineSites.Count == 0
                ? OnlineScopeAll
                : 1;                                    // 默认第一个已添加站点
            if (keepName != null)
            {
                int found = onlineSites.FindIndex(s => s.Name == keepName);
                if (found >= 0) index = found + 1;
            }
            if (selectSiteId is > 0)
            {
                int found = onlineSites.FindIndex(s => s.Id == selectSiteId.Value);
                if (found >= 0) index = found + 1;      // 刚添加/编辑的站点优先
            }
            cmbOnlineScope.SelectedIndex = index;

            btnManageSites.Content = onlineSites.Count > 0
                ? $"已添加站点 {onlineSites.Count}"
                : "已添加站点";

            UpdateOnlineEmptyState();

            if (onlineSites.Count == 0)
                txtOnlineStatus.Text = "先添加一个站点，才能搜索在线曲库";
            else if (OnlineResults.Count == 0 && !isOnlineSearching)
                txtOnlineStatus.Text = "输入关键词，回车搜索";
        }

        private void cmbOnlineScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 只切换搜索范围，不发请求（回车/按钮才触发搜索），避免误触频繁打站点
            if (isOnlineMode && !isOnlineSearching && OnlineResults.Count == 0
                && onlineSites.Count > 0)
            {
                txtOnlineStatus.Text = "输入关键词，回车搜索";
            }
        }

        /// <summary>本次搜索要用的站点：全部站点 or 下拉里选中的单站。</summary>
        private List<OnlineSite> SelectedOnlineSites()
        {
            int index = cmbOnlineScope.SelectedIndex;
            if (index <= OnlineScopeAll) return new List<OnlineSite>(onlineSites);
            int i = index - 1;
            return i >= 0 && i < onlineSites.Count
                ? new List<OnlineSite> { onlineSites[i] }
                : new List<OnlineSite>();
        }

        private void UpdateOnlineEmptyState()
        {
            bool noSites = onlineSites.Count == 0;
            onlineEmptySites.Visibility = noSites && !isOnlineSearching
                ? Visibility.Visible : Visibility.Collapsed;

            bool showNoResult = !noSites && !isOnlineSearching && onlineSearched
                                && OnlineResults.Count == 0;
            onlineNoResult.Visibility = showNoResult ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StartOnlineBusy()
        {
            isOnlineSearching = true;
            onlineBusy.Visibility = Visibility.Visible;
            onlineEmptySites.Visibility = Visibility.Collapsed;
            onlineNoResult.Visibility = Visibility.Collapsed;

            // 旋转动画：0→360 循环（停的时候由 StopOnlineBusy 移除动画）
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            onlineSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void StopOnlineBusy()
        {
            isOnlineSearching = false;
            onlineBusy.Visibility = Visibility.Collapsed;
            onlineSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            UpdateOnlineEmptyState();
        }

        // ── 搜索 ────────────────────────────────────────────────────────────────

        private void txtOnlineSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = RunOnlineSearchAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (isOnlineSearching) CancelOnlineSearch();
                else txtOnlineSearch.Clear();
                e.Handled = true;
            }
        }

        private void CancelOnlineSearch()
        {
            onlineSearchCts?.Cancel();
            txtOnlineStatus.Text = "正在取消…";
        }

        /// <summary>
        /// 执行一次在线搜索（结果只进内存）。
        /// "全部站点"按站点**串行**跑（同一个隐藏内核只有一个标签页，并发会互相顶掉页面），
        /// 结果按站点分组展示。
        /// </summary>
        private async System.Threading.Tasks.Task RunOnlineSearchAsync()
        {
            if (isOnlineSearching) return;              // 搜索中忽略重复触发，Esc 才是取消

            // 向导/站点面板打开时内核被它们占着（页面在那些窗口里），此时搜索会把页面顶掉
            if (AddSiteWizardWindow.IsOpen || SiteManagerWindow.IsOpen)
            {
                ShowToast("先把站点窗口关掉再搜索");
                return;
            }

            string keyword = txtOnlineSearch.Text.Trim();
            if (keyword.Length == 0)
            {
                ShowToast("请输入要搜索的关键词");
                txtOnlineSearch.Focus();
                return;
            }

            var sites = SelectedOnlineSites();
            if (sites.Count == 0)
            {
                ShowToast("还没有添加站点，先添加一个吧", warning: true);
                UpdateOnlineEmptyState();
                return;
            }

            onlineSearched = true;

            // 新一轮搜索：上一次的播放地址解析立刻停掉（内核要让给搜索），结果缓存一并作废
            onlinePlayCts?.Cancel();
            onlineTrackMap.Clear();
            OnlineResults.Clear();
            ApplyOnlineGrouping(sites.Count > 1);
            StartOnlineBusy();
            txtOnlineStatus.Text = "正在搜索…";

            // 每次搜索独立取消令牌；旧的一次性令牌先释放
            onlineSearchCts?.Dispose();
            onlineSearchCts = new CancellationTokenSource();
            var ct = onlineSearchCts.Token;

            var progress = new Progress<OnlineSearchProgress>(p =>
            {
                if (isOnlineSearching) txtOnlineStatus.Text = $"{p.SiteName}：{p.Message}";
            });

            var problems = new List<string>();    // 各站点的异常，最后合并成一条 Toast
            int okSites = 0;

            try
            {
                foreach (var site in sites)
                {
                    if (ct.IsCancellationRequested) break;

                    var outcome = await OnlineSearchService.SearchAsync(site, keyword, progress, ct);
                    switch (outcome.Status)
                    {
                        case OnlineSearchStatus.Ok:
                            okSites++;
                            foreach (var r in outcome.Results) OnlineResults.Add(r);
                            break;

                        case OnlineSearchStatus.EmptyResult:
                            break;   // 该站确实没结果，不算错误

                        case OnlineSearchStatus.Cancelled:
                            break;

                        case OnlineSearchStatus.NeedCaptcha:
                            problems.Add($"{site.Name}：未完成人机验证");
                            break;

                        case OnlineSearchStatus.NeedLogin:
                            problems.Add($"{site.Name}：需要登录");
                            break;

                        case OnlineSearchStatus.Broken:
                            problems.Add($"{site.Name}：站点配置可能已失效");
                            break;

                        case OnlineSearchStatus.Timeout:
                            problems.Add($"{site.Name}：加载超时");
                            break;

                        case OnlineSearchStatus.NavFailed:
                            problems.Add($"{site.Name}：打不开页面");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                txtOnlineStatus.Text = "已取消";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[在线] 搜索异常: {ex}");
                problems.Add("搜索出错：" + ex.Message);
            }
            finally
            {
                StopOnlineBusy();
            }

            if (ct.IsCancellationRequested)
            {
                txtOnlineStatus.Text = OnlineResults.Count > 0
                    ? $"已取消，保留 {OnlineResults.Count} 条结果"
                    : "已取消";
                return;
            }

            // 结果与状态行
            if (OnlineResults.Count > 0)
            {
                txtOnlineStatus.Text = sites.Count > 1
                    ? $"共 {OnlineResults.Count} 条（{okSites}/{sites.Count} 个站点有结果）"
                    : $"共 {OnlineResults.Count} 条结果";
                onlineResultsList.SelectedIndex = 0;
            }
            else
            {
                txtOnlineStatus.Text = problems.Count > 0 ? "搜索失败" : "没有找到相关内容";
            }

            UpdateOnlineEmptyState();

            if (problems.Count > 0)
            {
                // 一次只弹一条，避免 N 个站点失败时 Toast 刷屏
                string msg = problems.Count == 1
                    ? problems[0]
                    : $"{problems[0]} 等 {problems.Count} 个站点有问题";
                ShowToast(msg, warning: true);
            }
        }

        /// <summary>结果分组：全部站点模式按 SiteName 分组，单站模式不分组。</summary>
        private void ApplyOnlineGrouping(bool groupBySite)
        {
            var view = (System.Windows.Data.ListCollectionView)
                System.Windows.Data.CollectionViewSource.GetDefaultView(OnlineResults);
            view.GroupDescriptions.Clear();
            if (groupBySite)
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("SiteName"));
        }

        // ── 列表交互 ────────────────────────────────────────────────────────────

        /// <summary>在线列表的上下移动（↑↓ 共用）。</summary>
        private void NavigateOnlineSelection(int delta)
        {
            int count = onlineResultsList.Items.Count;
            if (count == 0) return;

            int index = onlineResultsList.SelectedIndex + delta;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;

            onlineResultsList.SelectedIndex = index;
            onlineResultsList.ScrollIntoView(onlineResultsList.SelectedItem);
        }

        /// <summary>试听入口（S8）：单击结果 / 回车 / 双击都走这里。</summary>
        /// <param name="target">点中/选中的结果；null 表示用列表当前选中项。</param>
        private async void PlayOnlineSelected(OnlineResult? target = null)
        {
            var r = target ?? onlineResultsList.SelectedItem as OnlineResult;
            if (r == null)
            {
                ShowToast("先选一条结果");
                return;
            }

            // 内核只有一个标签页：搜索/解析同时跑会互相顶掉页面
            if (isOnlineSearching)
            {
                ShowToast("搜索还在进行，完成后再试听");
                return;
            }
            if (isResolvingOnlineAudio)
            {
                // 双击的第二下会落在这里：同一条还在解析就安静等结果，别弹提示吓人
                if (!ReferenceEquals(r, _resolvingResult))
                    ShowToast("正在解析上一条的播放地址，稍等");
                return;
            }

            if (string.IsNullOrWhiteSpace(r.DetailUrl) && string.IsNullOrWhiteSpace(r.AudioUrl))
            {
                ShowToast("这条结果没有详情页链接，无法在线试听", warning: true);
                return;
            }

            // 队列：把当前结果列表里有播放可能的条目全部转成"在线 TrackItem"，
            // 之后的上一首/下一首/随机/播完自动切就在这份队列里走（与本地队列同一套代码）
            BuildOnlineQueue();
            var track = GetOrCreateOnlineTrack(r);
            SelectInQueue(track);

            isResolvingOnlineAudio = true;
            _resolvingResult = r;
            onlinePlayCts?.Dispose();
            onlinePlayCts = new CancellationTokenSource();
            string statusBackup = txtOnlineStatus.Text;

            try
            {
                if (string.IsNullOrWhiteSpace(track.StreamUrl))
                {
                    txtOnlineStatus.Text = $"正在解析「{r.Title}」的播放地址…";
                    if (!await ResolveOnlineTrackAsync(track, onlinePlayCts.Token)) return;
                }
                PlayTrack(track);
            }
            finally
            {
                isResolvingOnlineAudio = false;
                _resolvingResult = null;
                // 状态行别卡在"正在解析…"：恢复成结果条数那类信息
                if (txtOnlineStatus.Text.StartsWith("正在解析", StringComparison.Ordinal))
                    txtOnlineStatus.Text = statusBackup;
            }
        }

        /// <summary>
        /// 解析一条在线曲目的播放地址并写回 <paramref name="track"/>。
        /// 失败/只有流媒体时已经弹过 Toast，返回 false（调用方不用再提示）。
        /// </summary>
        private async System.Threading.Tasks.Task<bool> ResolveOnlineTrackAsync(TrackItem track,
            CancellationToken ct = default)
        {
            if (track.OnlineSource is not { } r)
            {
                ShowToast("这条在线曲目缺少来源信息，无法播放", warning: true);
                return false;
            }

            AudioResolveResult resolved;
            try
            {
                resolved = await OnlineSearchService.ResolveAudioAsync(r, ct);
            }
            catch (OperationCanceledException)
            {
                return false;   // 用户取消/换了一条，静默处理
            }
            catch (Exception ex)
            {
                App.LogCrash("[播放] 解析在线地址异常: " + ex);
                ShowToast("解析播放地址失败：" + ex.Message, warning: true);
                return false;
            }

            if (string.IsNullOrWhiteSpace(resolved.Url))
            {
                ShowToast(resolved.Reason.Length > 0 ? resolved.Reason : "无法在线播放这条结果", warning: true);
                return false;
            }

            if (resolved.IsStream)
            {
                // HLS/DASH：MediaElement 播不了，但地址对 S9 下载有用，留在 result.AudioUrl 里
                r.AudioUrl = resolved.Url;
                ShowToast(resolved.Reason, warning: true);
                return false;
            }

            track.StreamUrl = resolved.Url;
            return true;
        }

        /// <summary>结果 → 在线曲目的缓存（同一条结果反复点：复用同一个 TrackItem 与已解析地址）。</summary>
        private readonly Dictionary<OnlineResult, TrackItem> onlineTrackMap = new();

        /// <summary>把一条搜索结果包成"在线曲目"（不落库、不落盘，只是播放队列里的一条）。</summary>
        private TrackItem GetOrCreateOnlineTrack(OnlineResult r)
        {
            if (!onlineTrackMap.TryGetValue(r, out var track))
            {
                track = new TrackItem
                {
                    Title = string.IsNullOrWhiteSpace(r.Artist) ? r.Title : $"{r.Title} - {r.Artist}",
                    IsOnline = true,
                    Source = "online",
                    SourceUrl = r.DetailUrl,
                    OnlineSource = r,
                    Format = "stream",
                    Duration = r.Duration
                };
                onlineTrackMap[r] = track;
            }

            // 地址可能在别处刚解析好（S9 下载前也会解析），保持同步——
            // 但只有"MediaElement 能直接播"的地址才写进来：m3u8/mpd 是下载用的索引，
            // 塞给播放器只会立刻报错。
            if (!string.IsNullOrWhiteSpace(r.AudioUrl))
            {
                var decision = OnlineSearchService.DecideForUrl(r.AudioUrl!);
                if (decision.CanPlay) track.StreamUrl = decision.Url;
            }
            return track;
        }

        /// <summary>
        /// 用当前结果列表重建在线播放队列：**有播放可能**的条目才进队列——
        /// 既无直链又无详情页的、以及已知是 HLS/DASH 流媒体的，都不参与"下一首"轮转
        /// （后者列表里仍能看到、也能下载，只是试听不了）。
        /// </summary>
        private void BuildOnlineQueue()
        {
            onlineQueue.Clear();
            foreach (var r in OnlineResults)
            {
                bool hasDirect = !string.IsNullOrWhiteSpace(r.AudioUrl);
                bool hasDetail = !string.IsNullOrWhiteSpace(r.DetailUrl);
                if (!hasDirect && !hasDetail) continue;
                if (hasDirect && !OnlineSearchService.DecideForUrl(r.AudioUrl!).CanPlay) continue;

                onlineQueue.Add(GetOrCreateOnlineTrack(r));
            }
            shuffleBag.Clear();     // 队列换了，随机池作废
        }

        /// <summary>正在解析播放地址（同一时刻只解析一条）。</summary>
        private bool isResolvingOnlineAudio;

        /// <summary>正在解析的是哪条结果（双击同一条时安静等待，不弹提示）。</summary>
        private OnlineResult? _resolvingResult;
        private CancellationTokenSource? onlinePlayCts;

        /// <summary>
        /// 在线曲目的"歌词区"：没有 .lrc 可找，改显示曲目信息卡
        /// （标题 / 来源站 / 原链接，PRD F5.3），桌面歌词同步显示两行文字。
        /// </summary>
        private void ShowOnlineTrackInfoCard(TrackItem track)
        {
            // 清空歌词状态：CheckAndUpdateLyrics 见到空列表就直接空转，不会误高亮
            CurrentLyrics = new List<LyricLine>();
            CurrentLyricIndex = -1;
            LyricsScrollViewer.ScrollToTop();
            LyricsPanel.Children.Clear();

            string site = track.OnlineSource?.SiteName ?? "";
            string link = track.OnlineSource?.DetailUrl ?? "";

            var card = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560
            };

            card.Children.Add(new TextBlock
            {
                Text = "在线试听 · 未下载",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xD7, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });

            card.Children.Add(new TextBlock
            {
                Text = track.Title,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            if (site.Length > 0)
            {
                card.Children.Add(new TextBlock
                {
                    Text = $"来源：{site}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }

            if (link.Length > 0)
            {
                var linkRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 16, 0, 0)
                };

                linkRow.Children.Add(new TextBlock
                {
                    Text = link,
                    FontSize = 11,
                    MaxWidth = 360,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = link
                });

                var copy = new Button
                {
                    Content = "复制链接",
                    Tag = link,
                    Style = (Style)FindResource("GhostButton"),
                    FontSize = 11,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                copy.Click += btnCopyOnlineLink_Click;

                linkRow.Children.Add(copy);
                card.Children.Add(linkRow);
            }

            LyricsPanel.Children.Add(card);

            // 桌面歌词：没有时间轴，就显示标题 + 来源两行
            lyricsDesktopWindow?.LoadLyrics(new List<LyricLine>
            {
                new() { Text = track.Title },
                new() { Text = site.Length > 0 ? $"在线试听 · {site}" : "在线试听" }
            });
        }

        /// <summary>复制在线曲目的原链接（信息卡上的按钮）。</summary>
        private void btnCopyOnlineLink_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string url || url.Length == 0) return;
            try
            {
                Clipboard.SetText(url);
                ShowToast("已复制原链接");
            }
            catch (Exception ex)
            {
                ShowToast("复制失败：" + ex.Message, warning: true);
            }
        }

        /// <summary>
        /// 单击结果行试听（S8）：点下载按钮不算（按钮自己处理点击），点空白处也不算。
        /// </summary>
        private void onlineResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 从点击源向上找 Button：行内的下载按钮不能触发试听
            for (var node = e.OriginalSource as DependencyObject; node != null;
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is Button) return;
                if (node is ListView) break;
            }

            var container = ItemsControl.ContainerFromElement(onlineResultsList,
                e.OriginalSource as DependencyObject) as ListViewItem;
            if (container?.DataContext is not OnlineResult r) return;

            onlineResultsList.SelectedItem = r;
            PlayOnlineSelected(r);
        }

        /// <summary>
        /// 行内下载按钮（S9）：
        /// 空闲 → 解析地址并入队；排队/下载中 → 再点一下 = 取消；失败/取消 → 点 = 重试。
        /// </summary>
        private void btnOnlineDownload_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not OnlineResult r) return;

            if (!r.CanDownload)
            {
                ShowToast(string.IsNullOrWhiteSpace(r.NotDownloadReason)
                    ? "这条结果不支持下载" : r.NotDownloadReason, warning: true);
                return;
            }

            // 排队/下载中 → 再点一下 = 取消
            if (r.DownloadState is DownloadState.Pending or DownloadState.Running)
            {
                bool cancelled = DownloadService.Cancel(r);
                if (downloadResolveCts.TryGetValue(r, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    cancelled = true;
                }
                ShowToast(cancelled ? $"已取消「{r.Title}」的下载" : "这条任务已经结束了");
                return;
            }

            if (isOnlineSearching)
            {
                ShowToast("搜索还在进行，完成后再下载");
                return;
            }
            if (isResolvingOnlineAudio)
            {
                ShowToast("正在解析播放地址，稍等");
                return;
            }

            // 已有直链（搜索时抓到 / 之前播放或下载解析过）→ 免解析直接入队
            if (!string.IsNullOrWhiteSpace(r.AudioUrl))
            {
                var direct = OnlineSearchService.DecideForUrl(r.AudioUrl!);
                if (direct.Url != null)
                {
                    DownloadService.Enqueue(r, direct.Url, direct.IsStream, r.DetailUrl);
                    ShowToast($"已加入下载队列「{r.Title}」");
                    return;
                }
                r.DownloadState = DownloadState.Failed;
                r.DownloadError = direct.Reason;
                ShowToast($"无法下载「{r.Title}」：{direct.Reason}", warning: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(r.DetailUrl))
            {
                r.DownloadState = DownloadState.Failed;
                r.DownloadError = "没有详情页链接，无法解析音频";
                ShowToast(r.DownloadError, warning: true);
                return;
            }

            // 需要先解析（详情页嗅探，最长约 8 秒）——按钮先显示"排队中"，后台解析
            r.DownloadState = DownloadState.Pending;
            r.DownloadError = "";
            isResolvingOnlineAudio = true;
            _resolvingResult = r;
            var resolveCts = new CancellationTokenSource();
            downloadResolveCts[r] = resolveCts;
            _ = ResolveThenDownloadAsync(r, resolveCts.Token);
        }

        /// <summary>下载前的地址解析（与播放共用 ResolveAudioAsync，流媒体地址也能下载）。</summary>
        private async System.Threading.Tasks.Task ResolveThenDownloadAsync(OnlineResult r,
            CancellationToken ct)
        {
            try
            {
                var resolved = await OnlineSearchService.ResolveAudioAsync(r, ct);
                if (resolved.Url == null)
                {
                    r.DownloadState = DownloadState.Failed;
                    r.DownloadError = resolved.Reason;
                    ShowToast($"无法下载「{r.Title}」：{resolved.Reason}", warning: true);
                    return;
                }

                DownloadService.Enqueue(r, resolved.Url, resolved.IsStream, r.DetailUrl);
                ShowToast($"已加入下载队列「{r.Title}」");
            }
            catch (OperationCanceledException)
            {
                r.DownloadState = DownloadState.Cancelled;   // 解析阶段取消 → 直接回"重试"
            }
            catch (Exception ex)
            {
                App.LogCrash("[下载] 解析地址异常: " + ex);
                r.DownloadState = DownloadState.Failed;
                r.DownloadError = ex.Message;
                ShowToast($"解析播放地址失败：{ex.Message}", warning: true);
            }
            finally
            {
                isResolvingOnlineAudio = false;
                _resolvingResult = null;
                if (downloadResolveCts.Remove(r, out var usedCts)) usedCts.Dispose();
            }
        }

        /// <summary>下载前地址解析的取消源登记表（按结果条目取消）。</summary>
        private readonly Dictionary<OnlineResult, CancellationTokenSource> downloadResolveCts = new();

        // ── 底部操作栏 ────────────────────────────────────────────────────────

        /// <summary>＋添加站点：打开三步向导（S5），保存后立刻刷新站点与搜索范围。</summary>
        private async void btnAddSite_Click(object sender, RoutedEventArgs e)
        {
            // 向导要独占同一个隐藏内核（页面会被切到向导窗口里），
            // 所以先把正在跑的搜索停掉，否则两边会互相顶掉页面。
            if (isOnlineSearching) onlineSearchCts?.Cancel();

            var saved = await AddSiteWizardWindow.ShowAsync(this);
            if (saved == null) return;

            RefreshOnlineSites(saved.Id);
            ShowToast($"已添加站点「{saved.Name}」");
            txtOnlineStatus.Text = "输入关键词，回车搜索";
            txtOnlineSearch.Focus();
        }

        /// <summary>
        /// 打开"已添加站点"面板（S7）：测试 / 重配 / 登录 / 删除都在面板里完成。
        /// 面板自己会与库同步，关闭后这里统一刷新下拉与空态。
        /// </summary>
        private async void btnManageSites_Click(object sender, RoutedEventArgs e)
        {
            // 面板里的"测试/登录"要用同一个隐藏内核，先把正在跑的搜索停掉
            if (isOnlineSearching) onlineSearchCts?.Cancel();

            var result = await SiteManagerWindow.ShowAsync(this, txtOnlineSearch.Text);

            // 删过站点：当前结果里可能有"已删站点"的条目，清掉避免误导
            if (result.SitesDeleted)
            {
                OnlineResults.Clear();
                onlineSearched = false;
                txtOnlineStatus.Text = "站点已变更，请重新搜索";
            }

            RefreshOnlineSites(result.FocusSiteId);

            if (result.Changed && !result.SitesDeleted)
                txtOnlineStatus.Text = "站点配置已更新";
        }

        /// <summary>
        /// 搜索结果自动改了站点状态（S7 自愈）：
        /// 只同步内存状态并在"不是搜索中"时提示用户去修，搜索中的提示由搜索结果统一给出。
        /// </summary>
        private void OnOnlineSiteStatusChanged(long siteId, string status)
        {
            void Apply()
            {
                var site = onlineSites.FirstOrDefault(s => s.Id == siteId);
                if (site != null) site.Status = status;

                // 搜索进行中：RunOnlineSearchAsync 结束时会统一 Toast，这里不重复打扰
                if (isOnlineSearching) return;

                string name = site?.Name ?? "该站点";
                if (status == OnlineSiteStatus.Broken)
                {
                    ShowToast($"{name}：站点配置已失效，到「已添加站点」里重新点选", warning: true);
                }
                else if (status == OnlineSiteStatus.NeedLogin)
                {
                    ShowToast($"{name}：需要登录，可在「已添加站点」里完成一次登录", warning: true);
                }
            }

            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply));
        }

        #endregion

        #region 全局快捷键（PRD 5.6）

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // 歌单名内联编辑框聚焦：所有按键交给编辑框自己（回车/Esc 由其 KeyDown 处理）
            if (Keyboard.FocusedElement == txtPlaylistEditor) return;

            // ── 在线视图（S6）：↑↓ 选结果、回车试听、Esc 取消/清空、Ctrl+F 聚焦在线搜索框 ──
            // 其余按键（空格播放/暂停、Ctrl+A/D 切歌、M 静音…）继续走下面的全局分支
            if (isOnlineMode)
            {
                if (Keyboard.FocusedElement == txtOnlineSearch)
                    return;   // 输入框内的回车/Esc 由 txtOnlineSearch_KeyDown 处理

                switch (e.Key)
                {
                    case Key.Up:
                        NavigateOnlineSelection(-1);
                        e.Handled = true;
                        return;

                    case Key.Down:
                        NavigateOnlineSelection(1);
                        e.Handled = true;
                        return;

                    case Key.Enter:
                        PlayOnlineSelected();
                        e.Handled = true;
                        return;

                    case Key.Escape:
                        if (isOnlineSearching) CancelOnlineSearch();
                        else txtOnlineSearch.Clear();
                        e.Handled = true;
                        return;

                    case Key.F when isCtrlDown:
                        txtOnlineSearch.Focus();
                        txtOnlineSearch.SelectAll();
                        e.Handled = true;
                        return;

                    case Key.Delete:
                        // 在线结果不是本地曲库条目，Delete 不应该去动歌单
                        e.Handled = true;
                        return;
                }
            }


            // 搜索框聚焦时：Esc 清除搜索、↑↓ 移动选中项、回车执行搜索（搜完焦点移到歌曲列表）
            if (Keyboard.FocusedElement == txtSearch)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        ClearSearch();
                        e.Handled = true;
                        break;

                    case Key.Up:
                        NavigateSelection(-1);
                        e.Handled = true;
                        break;

                    case Key.Down:
                        NavigateSelection(1);
                        e.Handled = true;
                        break;

                    case Key.Enter:
                        // 搜索栏里回车 = 执行搜索（第一条结果自动选中），
                        // 焦点随之移到歌曲列表，之后回车才是"播放选中歌曲"
                        RunSearch();
                        musicfilesListView.Focus();
                        e.Handled = true;
                        break;
                }
                return; // 其余按键交给搜索框自己处理（输入文字等）
            }

            switch (e.Key)
            {
                case Key.Space: // 播放/暂停
                    btnPlayPause_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.A when isCtrlDown: // 上一首
                    btnPrevious_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.D when isCtrlDown: // 下一首
                    btnNext_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.W when isCtrlDown: // 音量 +
                    AdjustVolume(5);
                    e.Handled = true;
                    break;

                case Key.S when isCtrlDown: // 音量 -
                    AdjustVolume(-5);
                    e.Handled = true;
                    break;

                case Key.Up: // 选中上一首
                    NavigateSelection(-1);
                    e.Handled = true;
                    break;

                case Key.Down: // 选中下一首
                    NavigateSelection(1);
                    e.Handled = true;
                    break;

                case Key.Enter: // 直接播放当前选中的歌曲
                    if (musicfilesListView.SelectedItem is TrackItem selectedTrack)
                    {
                        PlayTrack(selectedTrack);
                        e.Handled = true;
                    }
                    break;

                case Key.M: // 静音切换
                    btnMute_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.L: // 桌面歌词开关
                    btnToggleLyrics_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.F when isCtrlDown: // 聚焦搜索框
                    txtSearch.Focus();
                    txtSearch.SelectAll();
                    e.Handled = true;
                    break;

                case Key.Escape: // 清除搜索
                    ClearSearch();
                    e.Handled = true;
                    break;

                case Key.Delete:
                    // 焦点在歌单方块列表上 → 删除选中的歌单
                    if (PlaylistsList.IsKeyboardFocusWithin)
                    {
                        if (PlaylistsList.SelectedItem is PlaylistItem playlist)
                            DeletePlaylistWithConfirm(playlist);
                        e.Handled = true;
                        break;
                    }

                    // 否则：从当前歌单移除选中歌曲
                    if (currentPlaylistId != 0)
                    {
                        menuRemoveFromPlaylist_Click(null, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;

                case Key.S: // 收起/展开侧栏（歌词铺满）
                    ToggleSidebar();
                    e.Handled = true;
                    break;
            }
        }

        #endregion
    }
}
