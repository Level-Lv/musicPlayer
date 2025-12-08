using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TagLib;

namespace musicPlayer
{
    public class MusicFile : INotifyPropertyChanged
    {
        public string? Title { get; set; }
        public TimeSpan Duration { get; set; }
        public string? FullPath { get; set; }

        private bool isSearchMatch;
        public bool IsSearchMatch
        {
            get { return isSearchMatch; }
            set
            {
                if (isSearchMatch != value)
                {
                    isSearchMatch = value;
                    OnPropertyChanged(nameof(IsSearchMatch));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class MainWindow : Window
    {
        public AppSettings CurrentSettings { get; set; } = new AppSettings();
        private ObservableCollection<MusicFile> MusicFiles { get; set; }
        private Storyboard? spinningStoryboard;
        private bool isPlaying = false;
        private readonly DispatcherTimer timer;
        private bool isHandlingAutoPlay = false;
        private List<LyricLine> CurrentLyrics = new List<LyricLine>();
        private int CurrentLyricIndex = -1;
        private LyricsDesktopWindow? lyricsDesktopWindow;
        private const string SettingsFileName = "setting.json";
        private bool isShuffleMode = false;
        private List<MusicFile> searchResults = new List<MusicFile>();
        private int currentSearchIndex = -1;

        public MainWindow()
        {
            InitializeComponent();
            MusicFiles = new ObservableCollection<MusicFile>();
            musicfilesListView.ItemsSource = MusicFiles;
            LoadSettings();
            ApplySettings(CurrentSettings);
            musicfilesListView.ItemsSource = MusicFiles; // Bind the ListView to the ObservableCollection
            spinningStoryboard = this.FindResource("SpinningAnimation") as Storyboard;

            mediaPlayer.MediaEnded += mediaPlayer_MediaEnded;
            mediaPlayer.MediaOpened += mediaPlayer_MediaOpened;

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += timer_Tick;
            UpdateShuffleButtonContent();

            string lrcDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lrc");
            if (!Directory.Exists(lrcDirectory))
            {
                try
                {
                    Directory.CreateDirectory(lrcDirectory);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to create LRC directory: {ex.Message}");
                }
            }

            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (lyricsDesktopWindow != null)
            {
                if (lyricsDesktopWindow.Dispatcher.CheckAccess())
                {
                    lyricsDesktopWindow.Close();
                }
                else
                {
                    lyricsDesktopWindow.Dispatcher.Invoke(() => lyricsDesktopWindow.Close());
                }
                lyricsDesktopWindow = null;
            }

            timer.Stop();
        }

        private void txtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            string searchText = txtSearch.Text.Trim();

            if (string.IsNullOrEmpty(searchText))
            {
                ResetSearchState();
                return;
            }

            bool shouldRerunSearch = true;

            if (currentSearchIndex >= 0 && currentSearchIndex < searchResults.Count)
            {
                var currentSong = searchResults[currentSearchIndex];

                if (currentSong.Title is not null &&
                    currentSong.Title.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    shouldRerunSearch = false;
                }
            }

            if (shouldRerunSearch)
            {
                ResetSearchState();

                searchResults = MusicFiles
                    .Where(m => m.Title is not null && m.Title.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (searchResults.Count == 0)
                {
                    System.Windows.MessageBox.Show("未找到匹配的歌曲。", "搜索结果", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                currentSearchIndex = 0;
            }
            else
            {
                searchResults[currentSearchIndex].IsSearchMatch = false;

                currentSearchIndex = (currentSearchIndex + 1) % searchResults.Count;
            }

            if (currentSearchIndex >= 0)
            {
                var targetSong = searchResults[currentSearchIndex];

                targetSong.IsSearchMatch = true;

                musicfilesListView.SelectedItem = targetSong;
                musicfilesListView.ScrollIntoView(targetSong);
            }
        }

        private void ResetSearchState()
        {
            foreach (var item in searchResults)
            {
                item.IsSearchMatch = false;
            }
            searchResults.Clear();
            currentSearchIndex = -1;
        }

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

            var btn = this.FindName("btnToggleLyrics") as System.Windows.Controls.Button;
            if (btn != null && btn.Content is System.Windows.Controls.TextBlock textBlock)
            {
                textBlock.Foreground = System.Windows.Media.Brushes.Yellow;
            }
        }

        private void LoadSettings()
        {
            if (System.IO.File.Exists(SettingsFileName))
            {
                try
                {
                    string jsonString = System.IO.File.ReadAllText(SettingsFileName);
                    CurrentSettings = JsonSerializer.Deserialize<AppSettings>(jsonString)
                                    ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"加载设置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    CurrentSettings = new AppSettings();
                    SaveSettings();
                }
            }
            else
            {
                CurrentSettings = new AppSettings();
                SaveSettings();
            }
        }

        public void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(CurrentSettings, options);
                System.IO.File.WriteAllText(SettingsFileName, jsonString);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置文件失败: {ex.Message}");
            }
        }

        private void ApplySettings(AppSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.MusicPathFileName) && Directory.Exists(settings.MusicPathFileName))
            {
                LoadMusicFiles(settings.MusicPathFileName);
            }

            isShuffleMode = settings.IsShuffleMode;
            UpdateShuffleButtonContent();

            if (!string.IsNullOrEmpty(settings.CirclePictureFileName))
            {
                LoadAlbumCover(settings.CirclePictureFileName);
            }

            if (!string.IsNullOrEmpty(settings.BackgroundVideoFileName))
            {
                LoadBackgroundVideo(settings.BackgroundVideoFileName);
            }

            if (settings.IsDesktopLyricsVisible)
            {
                ShowDesktopLyricsWindow();
            }
        }

        private void btnToggleLyrics_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            var textBlock = (System.Windows.Controls.TextBlock)btn.Content;

            bool willBeVisible;

            if (lyricsDesktopWindow == null)
            {
                lyricsDesktopWindow = new LyricsDesktopWindow();
                lyricsDesktopWindow.Show();
                lyricsDesktopWindow.LoadLyrics(CurrentLyrics);
                textBlock.Foreground = System.Windows.Media.Brushes.Yellow;
                willBeVisible = true;
            }
            else if (lyricsDesktopWindow.IsVisible)
            {
                lyricsDesktopWindow.Hide();
                textBlock.Foreground = System.Windows.Media.Brushes.White;
                willBeVisible = false;
            }
            else
            {
                lyricsDesktopWindow.Show();
                textBlock.Foreground = System.Windows.Media.Brushes.Yellow;
                willBeVisible = true;
            }

            CurrentSettings.IsDesktopLyricsVisible = willBeVisible;
            SaveSettings();
        }

        private void UpdateLyricsDisplay()
        {
            LyricsPanel.Children.Clear();

            Style normalStyle = (Style)this.FindResource("NormalLyricTextBlockStyle");
            Style highlightStyle = (Style)this.FindResource("HighlightLyricTextBlockStyle");

            if (CurrentLyrics.Count == 0) return;

            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                var lyric = CurrentLyrics[i];
                var tb = new TextBlock
                {
                    Text = lyric.Text,
                    Style = (i == 0 && CurrentLyrics.Count == 1) ? highlightStyle : normalStyle,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Opacity = (i == 0 && CurrentLyrics.Count == 1) ? 1.0 : 0.6
                };
                lyric.Control = tb;
                LyricsPanel.Children.Add(tb);
            }

            if (CurrentLyrics.Count == 1)
            {
                LyricsPanel.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                LyricsPanel.VerticalAlignment = VerticalAlignment.Top;
            }
        }

        private void CheckAndUpdateLyrics()
        {
            if (CurrentLyrics.Count <= 1 || mediaPlayer.Source == null) return;

            TimeSpan currentPosition = mediaPlayer.Position;
            int targetIndex = -1;
            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                if (currentPosition < CurrentLyrics[i].Timestamp)
                {
                    targetIndex = i - 1;
                    break;
                }
            }

            if (targetIndex == -1)
            {
                if (currentPosition >= CurrentLyrics[CurrentLyrics.Count - 1].Timestamp)
                {
                    targetIndex = CurrentLyrics.Count - 1;
                }
                else
                {
                    targetIndex = 0;
                }
            }

            if (targetIndex < 0)
            {
                targetIndex = 0;
            }

            if (targetIndex != CurrentLyricIndex)
            {
                if (CurrentLyricIndex >= 0 && CurrentLyricIndex < CurrentLyrics.Count)
                {
                    var oldLyric = CurrentLyrics[CurrentLyricIndex].Control;
                    if (oldLyric != null)
                    {
                        oldLyric.Style = (Style)this.FindResource("NormalLyricTextBlockStyle");
                        oldLyric.Opacity = 0.6;
                    }
                }

                var newLyric = CurrentLyrics[targetIndex].Control;
                if (newLyric != null)
                {
                    newLyric.Style = (Style)this.FindResource("HighlightLyricTextBlockStyle");
                    newLyric.Opacity = 1.0;

                    newLyric.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        System.Windows.Point lyricPosition = newLyric.TranslatePoint(new System.Windows.Point(0, 0), LyricsPanel);

                        double viewportHeight = LyricsScrollViewer.ViewportHeight;
                        double lyricHeight = newLyric.ActualHeight;

                        double targetOffset = lyricPosition.Y - (viewportHeight / 2.0) + (lyricHeight / 2.0);

                        if (targetOffset < 0)
                        {
                            targetOffset = 0;
                        }

                        double maxOffset = LyricsScrollViewer.ScrollableHeight;
                        if (targetOffset > maxOffset)
                        {
                            targetOffset = maxOffset;
                        }

                        LyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
                    }));
                }

                CurrentLyricIndex = targetIndex;
            }
        }

        private List<LyricLine> ParseLrcFile(string lrcFilePath)
        {
            var lyrics = new List<LyricLine>();
            try
            {
                string[] lines = System.IO.File.ReadAllLines(lrcFilePath);
                var lineRegex = new Regex(@"\[(\d+):(\d{2})(\.(\d+))?\](.*)", RegexOptions.Compiled);

                foreach (string line in lines)
                {
                    if (line.StartsWith("[") && !lineRegex.IsMatch(line))
                    {
                        continue;
                    }

                    var matches = lineRegex.Matches(line);

                    foreach (Match match in matches)
                    {
                        if (match.Success)
                        {
                            int minutes = int.Parse(match.Groups[1].Value);
                            int seconds = int.Parse(match.Groups[2].Value);

                            string rawMillisecondsString = match.Groups[4].Success ? match.Groups[4].Value : "0";
                            string text = match.Groups[5].Value.Trim();

                            string normalizedMillisecondsString = rawMillisecondsString.PadRight(3, '0');

                            normalizedMillisecondsString = normalizedMillisecondsString.Substring(0, 3);

                            int milliseconds = int.Parse(normalizedMillisecondsString);

                            TimeSpan timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);

                            if (!string.IsNullOrEmpty(text))
                            {
                                lyrics.Add(new LyricLine { Text = text, Timestamp = timestamp });
                            }
                        }
                    }
                }

                return lyrics.OrderBy(l => l.Timestamp).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LRC file parsing failed for {lrcFilePath}: {ex.Message}");
                return new List<LyricLine>();
            }
        }

        private void LoadLyricsForCurrentSong(string? audioFilePath)
        {
            CurrentLyrics.Clear();
            LyricsPanel.Children.Clear();

            if (string.IsNullOrEmpty(audioFilePath))
            {
                CurrentLyrics.Add(new LyricLine { Text = "未选择歌曲" });
                UpdateLyricsDisplay();
                return;
            }

            string musicFileNameWithoutExtension = Path.GetFileNameWithoutExtension(audioFilePath);
            string lrcFileName = $"{musicFileNameWithoutExtension}.lrc";
            string lrcDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lrc");
            string lrcFilePath = Path.Combine(lrcDirectory, lrcFileName);

            if (System.IO.File.Exists(lrcFilePath))
            {
                CurrentLyrics = ParseLrcFile(lrcFilePath);
                if (CurrentLyrics.Count == 0)
                {
                    CurrentLyrics.Add(new LyricLine { Text = "歌词文件为空或解析失败" });
                }
            }
            else
            {
                CurrentLyrics.Add(new LyricLine { Text = "当前歌曲无歌词" });
            }

            CurrentLyricIndex = -1;
            UpdateLyricsDisplay();
        }

        private void UpdateShuffleButtonContent()
        {
            if (btnShuffleToggle != null)
            {
                if (!isShuffleMode)
                {
                    btnShuffleToggle.Content = "🔁";
                }
                else
                {
                    btnShuffleToggle.Content = "🔀";
                }
            }
        }

        private void LoadAlbumCover(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                BitmapImage newImage = new BitmapImage(new Uri(filePath, UriKind.Absolute));
                AlbumCoverBrush.ImageSource = newImage;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"加载图片失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AlbumCoverBrush.ImageSource = null;
            }
        }

        private void timer_Tick(object? sender, EventArgs e)
        {
            if (mediaPlayer.Source != null && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                sliderProgress.Value = mediaPlayer.Position.TotalSeconds;
                txtCurrentTime.Text = mediaPlayer.Position.ToString(@"mm\:ss");

                CheckAndUpdateLyrics();

                if (lyricsDesktopWindow != null && lyricsDesktopWindow.IsVisible)
                {
                    lyricsDesktopWindow.UpdatePosition(mediaPlayer.Position);
                }
            }
        }

        private void mediaPlayer_MediaOpened(object? sender, RoutedEventArgs e)
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

        private void sliderProgress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            TimeSpan newPosition = TimeSpan.FromSeconds(sliderProgress.Value);
            mediaPlayer.Position = newPosition;

            if (!isPlaying)
            {
                mediaPlayer.Play();
                if (spinningStoryboard != null) spinningStoryboard.Begin(VisualizerGrid, true);
                timer.Start();
                isPlaying = true;
            }
        }

        private void btnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (mediaPlayer.Source == null) return;

            if (isPlaying)
            {
                mediaPlayer.Pause();
                if (spinningStoryboard != null) spinningStoryboard.Pause(VisualizerGrid);
                timer.Stop();
                btnPlayPause.Content = "▶️";
                isPlaying = false;
            }
            else
            {
                mediaPlayer.Play();
                if (spinningStoryboard != null) spinningStoryboard.Resume(VisualizerGrid);
                timer.Start();
                btnPlayPause.Content = "⏸️";
                isPlaying = true;
            }
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                DialogResult result = dialog.ShowDialog();

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;

                    LoadMusicFiles(selectedPath);

                    if (MusicFiles.Count > 0)
                    {
                        CurrentSettings.MusicPathFileName = selectedPath;
                        SaveSettings();
                    }
                }
            }
        }

        private void LoadMusicFiles(string folderPath)
        {
            MusicFiles.Clear();

            string[] supportedExtensions = { "*.mp3", "*.flac", "*.wav", "*.m4a" };
            var allFiles = new List<string>();

            foreach (string extension in supportedExtensions)
            {
                try
                {
                    var files = Directory.EnumerateFiles(folderPath, extension, SearchOption.TopDirectoryOnly);
                    allFiles.AddRange(files);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Windows.MessageBox.Show("访问文件夹权限不足。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                catch (System.Exception ex)
                {
                    System.Windows.MessageBox.Show($"读取文件时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            var sortedFiles = allFiles.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);


            foreach (string filePath in sortedFiles)
            {
                try
                {
                    TagLib.File file = TagLib.File.Create(filePath);

                    string title = Path.GetFileNameWithoutExtension(filePath);

                    MusicFiles.Add(new MusicFile
                    {
                        Title = title,
                        Duration = file.Properties.Duration,
                        FullPath = filePath
                    });

                    file.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"无法读取文件信息: {filePath}. 错误: {ex.Message}");
                }
            }
        }

        private void musicfilesListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (musicfilesListView.SelectedItem is MusicFile selectedFile)
            {
                PlayMusic(selectedFile.FullPath);
                btnPlayPause.Content = "⏸️";
            }
        }

        private void PlayMusic(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                System.Windows.MessageBox.Show("未找到文件路径。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                mediaPlayer.Stop();
                mediaPlayer.Source = new Uri(filePath);
                mediaPlayer.Play();
                LoadLyricsForCurrentSong(filePath);

                if (lyricsDesktopWindow != null)
                {
                    lyricsDesktopWindow.LoadLyrics(CurrentLyrics);
                }

                if (spinningStoryboard != null) spinningStoryboard.Begin(VisualizerGrid, true);
                timer.Start();

                isPlaying = true;

                this.Title = $"正在播放: {Path.GetFileNameWithoutExtension(filePath)} - 音乐播放器";

            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"播放文件失败: {ex.Message}", "播放错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void PauseMusic()
        {
            mediaPlayer.Pause();
            if (spinningStoryboard != null)
            {
                spinningStoryboard.Pause(VisualizerGrid);
            }
        }

        public void StopMusic()
        {
            if (spinningStoryboard != null)
            {
                spinningStoryboard.Stop(VisualizerGrid);
            }
            timer.Stop();
        }

        private void mediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (isHandlingAutoPlay)
            {
                isHandlingAutoPlay = false;
                return;
            }

            isHandlingAutoPlay = true;
            StopMusic();

            if (MusicFiles.Count == 0)
            {
                isHandlingAutoPlay = false;
                return;
            }

            int nextIndex;
            if (isShuffleMode)
            {
                Random random = new Random();
                int currentIndex = musicfilesListView.SelectedIndex;

                do
                {
                    nextIndex = random.Next(0, MusicFiles.Count);
                } while (MusicFiles.Count > 1 && nextIndex == currentIndex);
            }
            else
            {
                int currentIndex = musicfilesListView.SelectedIndex;
                nextIndex = (currentIndex + 1) % MusicFiles.Count;
            }

            MusicFile nextFile = MusicFiles[nextIndex];
            musicfilesListView.SelectedIndex = nextIndex;
            PlayMusic(nextFile.FullPath);

            btnPlayPause.Content = "⏸️";
        }

        private void backgroundVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            backgroundVideo.Position = TimeSpan.Zero;
            backgroundVideo.Play();
        }

        private void VisualizerGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            System.Windows.Forms.OpenFileDialog openFileDialog = new System.Windows.Forms.OpenFileDialog();

            openFileDialog.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";

            if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string selectedImagePath = openFileDialog.FileName;

                LoadAlbumCover(selectedImagePath);

                CurrentSettings.CirclePictureFileName = selectedImagePath;
                SaveSettings();
            }
        }

        private void btnSetBackgroundVideo_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();

            openFileDialog.Filter = "视频文件|*.mp4;*.avi;*.wmv;*.mov|所有文件|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                string selectedVideoPath = openFileDialog.FileName;

                LoadBackgroundVideo(selectedVideoPath);

                CurrentSettings.BackgroundVideoFileName = selectedVideoPath;
                SaveSettings();
            }
        }

        private void LoadBackgroundVideo(string filePath)
        {
            try
            {
                backgroundVideo.Stop();
                backgroundVideo.Source = new Uri(filePath, UriKind.Absolute);

                backgroundVideo.Play();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"加载背景视频失败: {ex.Message}", "视频错误", MessageBoxButton.OK, MessageBoxImage.Error);
                backgroundVideo.Source = null;
            }
        }

        private void btnShuffle_Click(object sender, RoutedEventArgs e)
        {
            if (MusicFiles.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择包含音乐文件的文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Random random = new Random();
            int randomIndex = random.Next(0, MusicFiles.Count);
            MusicFile selectedFile = MusicFiles[randomIndex];
            musicfilesListView.SelectedIndex = randomIndex;

            PlayMusic(selectedFile.FullPath);
            btnPlayPause.Content = "⏸️";
        }

        private void btnNext_Click(object sender, RoutedEventArgs e)
        {
            if (MusicFiles.Count == 0) return;

            int currentIndex = musicfilesListView.SelectedIndex;

            if (currentIndex == -1)
            {
                currentIndex = 0;
            }

            int nextIndex;

            if (isShuffleMode)
            {
                Random random = new Random();

                do
                {
                    nextIndex = random.Next(0, MusicFiles.Count);
                } while (MusicFiles.Count > 1 && nextIndex == currentIndex);
            }
            else
            {
                nextIndex = (currentIndex + 1) % MusicFiles.Count;
            }

            MusicFile nextFile = MusicFiles[nextIndex];
            musicfilesListView.SelectedIndex = nextIndex;
            PlayMusic(nextFile.FullPath);
            btnPlayPause.Content = "⏸️";
        }

        private void btnPrevious_Click(object sender, RoutedEventArgs e)
        {
            if (MusicFiles.Count == 0) return;

            int currentIndex = musicfilesListView.SelectedIndex;

            if (currentIndex == -1)
            {
                currentIndex = 0;
            }

            int prevIndex;

            if (isShuffleMode)
            {
                Random random = new Random();

                do
                {
                    prevIndex = random.Next(0, MusicFiles.Count);
                } while (MusicFiles.Count > 1 && prevIndex == currentIndex);
            }
            else
            {
                prevIndex = (currentIndex - 1 + MusicFiles.Count) % MusicFiles.Count;
            }

            MusicFile prevFile = MusicFiles[prevIndex];
            musicfilesListView.SelectedIndex = prevIndex;
            PlayMusic(prevFile.FullPath);

            btnPlayPause.Content = "⏸️";
        }

        private void btnShuffleToggle_Click(object sender, RoutedEventArgs e)
        {
            isShuffleMode = !isShuffleMode;

            CurrentSettings.IsShuffleMode = isShuffleMode;
            SaveSettings();

            UpdateShuffleButtonContent();
        }
    }
}