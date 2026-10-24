using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using musicPlayer.Data;
using musicPlayer.Services;

// csproj 开了 UseWindowsForms，其隐式全局 using 会与 WPF 的同名类型冲突，这里显式指定用 WPF 的版本
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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

        private PlayMode playMode = PlayMode.Sequential;
        private bool isPlaying;
        private bool isMuted;
        private int consecutiveFailures;

        private readonly DispatcherTimer timer;
        private bool isDraggingProgress;
        private Storyboard? spinningStoryboard;

        // 歌词状态
        private List<LyricLine> CurrentLyrics = new();
        private int CurrentLyricIndex = -1;
        private LyricsDesktopWindow? lyricsDesktopWindow;

        // MediaElement 的 Stop() 会再次触发 MediaEnded，用这个标记把"主动 Stop 引发的事件"吞掉
        private bool isHandlingAutoPlay;

        // 设置音量时抑制"写库"（启动赋值和程序改值时不需要持久化）
        private bool suppressVolumePersist;
        private readonly DispatcherTimer volumePersistTimer;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            // 初始化数据库（建库、建表、迁移旧 setting.json）
            Db.Initialize();

            musicfilesListView.ItemsSource = VisibleTracks;
            PlaylistsList.ItemsSource = Playlists;
            spinningStoryboard = FindResource("SpinningAnimation") as Storyboard;

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

            this.Closed += MainWindow_Closed;
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
        }

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

            // --- 封面图片 ---
            string cover = Db.GetSetting("cover_picture_file");
            if (File.Exists(cover)) LoadAlbumCover(cover);

            // --- 全局背景（图片或视频） ---
            string bg = Db.GetSetting("background_file");
            if (File.Exists(bg)) ApplyBackground(bg);

            // --- 桌面歌词 ---
            if (Db.GetSettingBool("desktop_lyrics_visible"))
                ShowDesktopLyricsWindow();

            // --- 上次播放歌曲：加载但不自动播放（PRD 5.6） ---
            LoadLastTrack();
        }

        /// <summary>把当前音量值反映到播放引擎。</summary>
        private void ApplyVolume()
        {
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
            txtNowPlaying.Text = item.Title;
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

        /// <summary>新建歌单：弹输入框 → 入库 → 选中。</summary>
        private void btnNewPlaylist_Click(object sender, RoutedEventArgs e)
        {
            string? name = ShowInputDialog("新建歌单", "");
            if (string.IsNullOrWhiteSpace(name)) return;

            long newId = Db.CreatePlaylist(name.Trim());
            LoadPlaylists();

            var item = Playlists.FirstOrDefault(p => p.Id == newId);
            if (item != null) PlaylistsList.SelectedItem = item;
        }

        /// <summary>右键菜单当前指向的歌单（菜单打开时由 PlaylistMenu_Opening 记录）。</summary>
        private PlaylistItem? contextMenuPlaylist;

        /// <summary>歌单方块右键菜单即将打开：记下方块对应的歌单，供菜单项点击时使用。</summary>
        private void PlaylistMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            contextMenuPlaylist = (sender as Border)?.DataContext as PlaylistItem;
        }

        /// <summary>右键重命名歌单。</summary>
        private void PlaylistRename_Click(object sender, RoutedEventArgs e)
        {
            var item = contextMenuPlaylist;
            if (item == null) return;

            string? newName = ShowInputDialog("重命名歌单", item.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

            Db.RenamePlaylist(item.Id, newName.Trim());
            item.Name = newName.Trim(); // INPC 自动刷新方块文字和 DisplayChar
        }

        /// <summary>右键删除歌单（二次确认，只删歌单和关联，不删歌曲/物理文件）。</summary>
        private void PlaylistDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = contextMenuPlaylist;
            if (item == null) return;

            var result = MessageBox.Show(
                $"确定删除歌单「{item.Name}」吗？\n（歌曲本身不会被删除）",
                "删除歌单", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Db.DeletePlaylist(item.Id);
            LoadPlaylists();

            if (currentPlaylistId == item.Id)
            {
                SelectPlaylist(0); // 当前歌单被删 → 回到全部歌曲
            }
        }

        /// <summary>简易输入对话框（新建/重命名歌单用）。</summary>
        private string? ShowInputDialog(string title, string defaultValue)
        {
            bool confirmed = false;

            var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 8, 0, 12) };
            input.Loaded += (_, _) => { input.SelectAll(); input.Focus(); };

            var ok = new Button { Content = "确定", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
            ok.Click += (_, _) => confirmed = true;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "歌单名称：" });
            panel.Children.Add(input);
            panel.Children.Add(buttons);

            var dlg = new Window
            {
                Title = title,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                Content = panel
            };
            ok.Click += (_, _) => dlg.Close();

            dlg.ShowDialog();
            return confirmed ? input.Text : null;
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

            // 搜索过滤
            if (activeSearchText != null)
            {
                bool regexMode = btnRegexToggle.IsChecked == true;
                items = items.Where(t => MatchesSearch(t.Title, activeSearchText, regexMode));
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
            shuffleBag.Clear(); // 队列变了，随机池作废重来
        }

        /// <summary>判断歌名是否命中搜索（普通包含 / 正则，均不区分大小写）。</summary>
        private static bool MatchesSearch(string? title, string text, bool regexMode)
        {
            if (string.IsNullOrEmpty(title)) return false;
            try
            {
                return regexMode
                    ? Regex.IsMatch(title, text, RegexOptions.IgnoreCase)
                    : title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
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

            // 正则模式先校验，无效直接提示（PRD 5.4）
            if (btnRegexToggle.IsChecked == true)
            {
                try
                {
                    _ = new Regex(text);
                }
                catch (ArgumentException)
                {
                    MessageBox.Show("正则表达式无效。", "搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            activeSearchText = text;
            RefreshVisibleTracks();

            if (VisibleTracks.Count == 0)
            {
                MessageBox.Show("未找到匹配歌曲。", "搜索", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show($"已把 {tracks.Count} 首歌曲添加到歌单。",
                "添加到歌单", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>从当前歌单移除选中歌曲（只解除关联）。</summary>
        private void menuRemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show("文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var result = MessageBox.Show(
                $"确定从音乐库移除 {tracks.Count} 首歌曲吗？\n（只移除记录，不会删除音乐文件）",
                "从库中移除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

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

        /// <summary>播放指定曲目（含失效检测、失败计数、状态与 UI 同步）。</summary>
        private void PlayTrack(TrackItem track)
        {
            // ---- 播放前失效检测（PRD 5.1）----
            if (!File.Exists(track.FullPath))
            {
                Db.RemoveTrackByPath(track.FullPath);
                libraryCache.RemoveAll(t => t.FullPath == track.FullPath);
                VisibleTracks.Remove(track);
                shuffleBag.Clear();
                MessageBox.Show("文件不存在，已从音乐库移除。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);

                if (currentTrack != null && currentTrack.FullPath == track.FullPath)
                {
                    currentTrack = null;
                    AdvanceToNext(manual: true); // 正在播的失效了 → 自动跳下一首
                }
                return;
            }

            // Stop() 会触发一次 MediaEnded，先打上标记吞掉它
            isHandlingAutoPlay = true;
            try
            {
                mediaPlayer.Stop();
                mediaPlayer.Source = new Uri(track.FullPath);
                mediaPlayer.Play();
                ApplyVolume();
            }
            catch (Exception ex)
            {
                isHandlingAutoPlay = false;
                Debug.WriteLine($"播放失败 {track.FullPath}: {ex.Message}");
                HandlePlayFailure();
                return;
            }

            consecutiveFailures = 0;
            currentTrack = track;
            UpdatePlayingMarkers();

            txtNowPlaying.Text = track.Title;
            Title = $"正在播放: {track.Title} - 音乐播放器";

            // 记住这次播放（重启后加载但不自动播）
            Db.SetLastTrackPath(track.FullPath);

            LoadLyricsForCurrentSong(track.FullPath);

            // 播放态 UI
            isPlaying = true;
            btnPlayPause.Content = "⏸️";
            spinningStoryboard?.Begin(VisualizerGrid, true); // 从头开始转
            timer.Start();
        }

        /// <summary>播放失败：计数，自动跳下一首；连续 5 首失败则暂停并提示（PRD 5.6）。</summary>
        private void HandlePlayFailure()
        {
            consecutiveFailures++;

            if (consecutiveFailures >= 5)
            {
                StopPlayback();
                MessageBox.Show("连续 5 首播放失败，已暂停播放。",
                    "播放错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AdvanceToNext(manual: true);
        }

        /// <summary>当前曲目在可见队列中的下标，找不到返回 -1。</summary>
        private int IndexOfCurrent()
        {
            if (currentTrack == null) return -1;
            for (int i = 0; i < VisibleTracks.Count; i++)
            {
                if (VisibleTracks[i].FullPath == currentTrack.FullPath) return i;
            }
            return -1;
        }

        /// <summary>从随机池里抽一首（一轮内不重复；池空则开启新一轮）。</summary>
        private TrackItem? PopFromShuffleBag()
        {
            // 把当前正在播的从池里去掉
            if (currentTrack != null)
            {
                shuffleBag.RemoveAll(t => t.FullPath == currentTrack.FullPath);
            }

            if (shuffleBag.Count == 0)
            {
                // 新一轮：全部可见曲目入池
                shuffleBag.AddRange(VisibleTracks);
                if (shuffleBag.Count == 0) return null;
            }

            var rnd = new Random();
            var pick = shuffleBag[rnd.Next(shuffleBag.Count)];
            shuffleBag.Remove(pick);
            return pick;
        }

        /// <summary>切到下一首。manual=false（自然播完）时顺序模式到末尾即停止。</summary>
        private void AdvanceToNext(bool manual)
        {
            if (VisibleTracks.Count == 0) return;

            TrackItem? next;
            if (playMode == PlayMode.Shuffle)
            {
                next = PopFromShuffleBag();
                if (next == null) return;
            }
            else
            {
                int nextIndex = IndexOfCurrent() + 1;
                if (nextIndex >= VisibleTracks.Count)
                {
                    if (!manual)
                    {
                        StopPlayback(); // 顺序模式：播到末尾停止（PRD 5.6）
                        return;
                    }
                    nextIndex = 0; // 手动点下一首则回环
                }
                next = VisibleTracks[nextIndex];
            }

            musicfilesListView.SelectedItem = next;
            musicfilesListView.ScrollIntoView(next);
            PlayTrack(next);
        }

        private void btnNext_Click(object sender, RoutedEventArgs e) => AdvanceToNext(manual: true);

        private void btnPrevious_Click(object sender, RoutedEventArgs e)
        {
            if (VisibleTracks.Count == 0) return;

            TrackItem? prev;
            if (playMode == PlayMode.Shuffle)
            {
                prev = PopFromShuffleBag(); // 随机模式上一首 = 随机换一首
            }
            else
            {
                int idx = IndexOfCurrent();
                int prevIndex = (idx <= 0 ? VisibleTracks.Count : idx) - 1; // 回环
                prev = VisibleTracks[prevIndex];
            }

            musicfilesListView.SelectedItem = prev;
            musicfilesListView.ScrollIntoView(prev);
            PlayTrack(prev);
        }

        private void btnPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (mediaPlayer.Source == null) return;

            if (isPlaying)
            {
                mediaPlayer.Pause();
                spinningStoryboard?.Pause(VisualizerGrid);
                timer.Stop();
                btnPlayPause.Content = "▶️";
                isPlaying = false;
            }
            else
            {
                mediaPlayer.Play();
                spinningStoryboard?.Resume(VisualizerGrid);
                timer.Start();
                btnPlayPause.Content = "⏸️";
                isPlaying = true;
            }
        }

        /// <summary>停止播放并复位 UI（不切歌）。</summary>
        private void StopPlayback()
        {
            mediaPlayer.Stop();
            mediaPlayer.Source = null; // 防止之后误触发 ended 自动下一首

            spinningStoryboard?.Stop(VisualizerGrid);
            timer.Stop();
            isPlaying = false;
            btnPlayPause.Content = "▶️";
        }

        /// <summary>自然播完 → 单曲循环 / 自动切下一首。</summary>
        private void mediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (isHandlingAutoPlay)
            {
                isHandlingAutoPlay = false;
                return; // 主动 Stop 引发的事件，忽略
            }

            if (playMode == PlayMode.RepeatOne)
            {
                mediaPlayer.Position = TimeSpan.Zero;
                mediaPlayer.Play();
                return;
            }

            isHandlingAutoPlay = true; // AdvanceToNext → PlayTrack 里的 Stop() 会再触发一次
            AdvanceToNext(manual: false);
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

        /// <summary>点击进度条任意位置直接跳转。</summary>
        private void sliderProgress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!mediaPlayer.NaturalDuration.HasTimeSpan) return;

            mediaPlayer.Position = TimeSpan.FromSeconds(sliderProgress.Value);

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

        private void btnMute_Click(object sender, RoutedEventArgs e)
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

        /// <summary>键盘 ↑/↓ 调音量。</summary>
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

        private void btnToggleLyrics_Click(object sender, RoutedEventArgs e)
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
                    MessageBox.Show("不支持的背景格式。", "背景设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"应用背景失败: {ex.Message}");
                MessageBox.Show("背景加载失败，已使用默认背景。", "背景设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>清除背景 → 回到默认深色。</summary>
        public void ClearBackground()
        {
            backgroundVideo.Stop();
            backgroundVideo.Source = null;
            this.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C));
        }

        /// <summary>加载专辑封面图（旋转圆盘）。</summary>
        public void LoadAlbumCover(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                AlbumCoverBrush.ImageSource =
                    new System.Windows.Media.Imaging.BitmapImage(new Uri(filePath, UriKind.Absolute));
            }
            catch (Exception)
            {
                AlbumCoverBrush.ImageSource = null;
            }
        }

        /// <summary>点击封面圆盘 → 选图。</summary>
        private void VisualizerGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadAlbumCover(dialog.FileName);
                Db.SetSetting("cover_picture_file", dialog.FileName);
            }
        }

        /// <summary>背景视频循环播放。</summary>
        private void backgroundVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            backgroundVideo.Position = TimeSpan.Zero;
            backgroundVideo.Play();
        }

        #endregion

        #region 设置入口

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            // TODO(步骤 8): 打开 SettingsWindow 设置弹窗
            MessageBox.Show("设置窗口将在下一步骤（8）实现。",
                "设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region 全局快捷键（PRD 5.6）

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // 输入框聚焦时：只响应 Esc（清除搜索），其余按键交给输入框
            if (Keyboard.FocusedElement is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    ClearSearch();
                    e.Handled = true;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.Space: // 播放/暂停
                    btnPlayPause_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.Left when isCtrlDown: // 上一首
                    btnPrevious_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.Right when isCtrlDown: // 下一首
                    btnNext_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.Up: // 音量 +
                    AdjustVolume(5);
                    e.Handled = true;
                    break;

                case Key.Down: // 音量 -
                    AdjustVolume(-5);
                    e.Handled = true;
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

                case Key.Delete: // 从当前歌单移除选中歌曲
                    if (currentPlaylistId != 0)
                    {
                        menuRemoveFromPlaylist_Click(null, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
            }
        }

        #endregion
    }
}
