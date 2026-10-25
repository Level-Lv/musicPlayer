using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using musicPlayer.Data;
using musicPlayer.Services;

namespace musicPlayer
{
    /// <summary>
    /// 设置弹窗。所有修改即时保存（写数据库）并即时生效，
    /// 没有"确定/取消"——和常见播放器的行为一致，避免"忘了点保存"。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow owner;

        /// <summary>音量滑块防抖：拖动过程中不写库，停顿 400ms 后写入。</summary>
        private readonly System.Windows.Threading.DispatcherTimer volumeSaveTimer;

        public SettingsWindow(MainWindow owner)
        {
            InitializeComponent();

            // Win11 系统级圆角（句柄创建完成后应用；Win10 静默忽略保持方形）
            SourceInitialized += (_, _) => WindowEffects.ApplyRoundedCorners(this);

            this.owner = owner;

            volumeSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            volumeSaveTimer.Tick += (_, _) =>
            {
                volumeSaveTimer.Stop();
                Db.SetSetting("volume", ((int)sliderDefaultVolume.Value).ToString());
                owner.SetVolumeFromSettings((int)sliderDefaultVolume.Value);
            };

            LoadSettingsToUi();
        }

        // =====================================================================
        // 初始化：把数据库里的当前值读出来填到界面上
        // =====================================================================

        private void LoadSettingsToUi()
        {
            // 音乐文件夹列表
            foreach (string folder in Db.GetMusicFolders())
            {
                listFolders.Items.Add(folder);
            }

            // 歌词文件夹
            txtLyricsFolder.Text = LyricService.GetLyricsFolder();

            // 背景文件
            txtBackground.Text = Db.GetSetting("background_file");

            // 播放方式
            string mode = Db.GetSetting("play_mode", "sequential");
            foreach (ComboBoxItem item in cmbPlayMode.Items)
            {
                if ((string)item.Tag! == mode)
                {
                    cmbPlayMode.SelectedItem = item;
                    break;
                }
            }

            // 默认音量
            if (double.TryParse(Db.GetSetting("volume", "100"), out double vol))
            {
                sliderDefaultVolume.Value = Math.Clamp(vol, 0, 100);
            }
            UpdateDefaultVolumeLabel();

            // 关闭行为（默认最小化到托盘）
            bool closeToTray = Db.GetSetting("close_action", "tray") != "exit";
            rbCloseToTray.IsChecked = closeToTray;
            rbCloseExit.IsChecked = !closeToTray;

            // 数据库位置
            txtDbPath.Text = Db.DbPath;
        }

        private void UpdateDefaultVolumeLabel()
            => txtDefaultVolume.Text = $"{(int)sliderDefaultVolume.Value}%";

        // =====================================================================
        // 1. 音乐文件夹管理
        // =====================================================================

        /// <summary>添加音乐文件夹：入库后立即扫描这一个文件夹（增量，不清理其他文件夹的记录）。</summary>
        private void btnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择音乐文件夹",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            if (!Db.AddMusicFolder(dialog.SelectedPath))
            {
                ShowStatusLine("该文件夹已在列表中。");
                return;
            }

            listFolders.Items.Add(dialog.SelectedPath);

            // 新文件夹立即扫描入库（ScanFolder 不做失效清理，不会误删其他文件夹的记录）
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Finished)
                {
                    SetScanUi(false, "");
                    owner.ReloadAfterLibraryScan();
                    ShowStatusLine($"扫描完成：新增 {p.Added} 首，更新 {p.Updated} 首。");
                }
            });
            SetScanUi(true, "正在扫描新文件夹…");
            System.Threading.Tasks.Task.Run(() => LibraryService.ScanFolder(dialog.SelectedPath, progress));
        }

        /// <summary>移除选中的音乐文件夹（只解除关联，不删除库中已收录的歌曲，物理文件更不动）。</summary>
        private void btnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (listFolders.SelectedItem is not string folder) return;

            var result = MessageBox.Show(
                $"确定移除文件夹「{folder}」吗？\n\n" +
                "（只是不再扫描这个位置，已入库的歌曲和物理文件不会被删除）",
                "移除文件夹", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Db.RemoveMusicFolder(folder);
            listFolders.Items.Remove(folder);
        }

        /// <summary>重新扫描全部文件夹：完成后清理失效记录，刷新主窗口列表。</summary>
        private void btnRescan_Click(object sender, RoutedEventArgs e)
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.TotalFiles > 0)
                {
                    progressScan.Value = 100.0 * p.ScannedFiles / p.TotalFiles;
                    txtScanStatus.Text = $"正在扫描 {p.ScannedFiles}/{p.TotalFiles}：{p.CurrentFile}";
                }
                if (p.Finished)
                {
                    SetScanUi(false, "");
                    owner.ReloadAfterLibraryScan();

                    // 取消的扫描没有做失效清理（避免误删还没扫到的记录）
                    ShowStatusLine(p.Cancelled
                        ? "扫描已取消（本次未清理失效记录）。"
                        : $"扫描完成：新增 {p.Added} 首，更新 {p.Updated} 首，清理失效 {p.Removed} 首。");
                }
            });

            SetScanUi(true, "正在准备扫描…");
            System.Threading.Tasks.Task.Run(() => LibraryService.RescanAll(progress));
        }

        private void btnCancelScan_Click(object sender, RoutedEventArgs e)
            => LibraryService.CancelScan();

        /// <summary>
        /// 在扫描进度区显示一条结果/提示文字（进度条隐藏，只留文字）。
        /// 替代系统 MessageBox 弹窗——扫描结果留在原地看完即走，不打断操作。
        /// </summary>
        private void ShowStatusLine(string text)
        {
            panelScanProgress.Visibility = Visibility.Visible;
            progressScan.Visibility = Visibility.Collapsed;
            txtScanStatus.Text = text;
        }

        /// <summary>扫描时禁用相关按钮 + 显示进度区；结束后恢复。</summary>
        private void SetScanUi(bool scanning, string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                panelScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
                progressScan.Visibility = Visibility.Visible; // 扫描时恢复进度条（结果提示态会隐藏它）
                txtScanStatus.Text = statusText;
                foreach (var child in FindVisualChildren<Button>(this))
                {
                    if (child.Name is nameof(btnAddFolder) or nameof(btnRemoveFolder) or nameof(btnRescan))
                        child.IsEnabled = !scanning;
                }
            });
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        // =====================================================================
        // 2. 歌词文件夹
        // =====================================================================

        private void btnPickLyricsFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择歌词文件夹（.lrc 文件所在位置）",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            LyricService.SetLyricsFolder(dialog.SelectedPath);
            txtLyricsFolder.Text = dialog.SelectedPath;

            // 歌词目录变了：当前这首歌的歌词立即按新目录重新加载
            owner.ReloadCurrentLyrics();
        }

        private void btnClearLyricsFolder_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定清除歌词文件夹设置吗？", "清除",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            LyricService.SetLyricsFolder("");
            txtLyricsFolder.Text = "";
            owner.ReloadCurrentLyrics();
        }

        // =====================================================================
        // 3. 背景
        // =====================================================================

        /// <summary>选背景文件（图片或视频按扩展名分流），存库并即时应用。</summary>
        private void btnPickBgImage_Click(object sender, RoutedEventArgs e)
            => PickBackground("图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif");

        private void btnPickBgVideo_Click(object sender, RoutedEventArgs e)
            => PickBackground("视频文件|*.mp4;*.webm;*.wmv;*.avi");

        private void PickBackground(string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog() != true) return;

            Db.SetSetting("background_file", dialog.FileName);
            txtBackground.Text = dialog.FileName;
            owner.ApplyBackground(dialog.FileName);
        }

        private void btnClearBg_Click(object sender, RoutedEventArgs e)
        {
            Db.SetSetting("background_file", "");
            txtBackground.Text = "";
            owner.ClearBackground();
        }

        // =====================================================================
        // 4. 播放设置
        // =====================================================================

        private void cmbPlayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbPlayMode.SelectedItem is not ComboBoxItem item) return;

            string mode = (string)item.Tag!;
            Db.SetSetting("play_mode", mode);
            owner.ApplyPlayModeFromSettings(mode);
        }

        private void sliderDefaultVolume_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtDefaultVolume == null) return; // XAML 初始化过程中会触发一次
            UpdateDefaultVolumeLabel();

            // 防抖：停顿后才真正写库并应用到主窗口
            volumeSaveTimer.Stop();
            volumeSaveTimer.Start();
        }

        /// <summary>关闭行为单选切换：写库即时生效（窗口未加载完成时的触发忽略）。</summary>
        private void CloseAction_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            Db.SetSetting("close_action", rbCloseExit.IsChecked == true ? "exit" : "tray");
        }

        // =====================================================================
        // 关闭
        // =====================================================================

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
