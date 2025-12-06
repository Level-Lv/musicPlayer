using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TagLib;
using System.Text.RegularExpressions;

namespace musicPlayer
{
    public class MusicFile
    {
        public string? Title { get; set; }
        public TimeSpan Duration { get; set; }
        public string? FullPath { get; set; }
    }

    public partial class MainWindow : Window
    {
        private class LyricLine
        {
            public string? Text { get; set; }
            public TimeSpan Timestamp { get; set; }
            public TextBlock? Control { get; set; }
        }

        private ObservableCollection<MusicFile> MusicFiles { get; set; }
        private Storyboard? spinningStoryboard;
        private bool isPlaying = false;
        private readonly DispatcherTimer timer;
        private const string CirclePictureFileName = "circlePicture.txt";
        private const string BackgroundVideoFileName = "backgroundVideo.txt";
        private const string MusicPathFileName = "musicPath.txt";
        private bool isHandlingAutoPlay = false;
        private bool isShuffleMode = false;
        private List<LyricLine> CurrentLyrics = new List<LyricLine>();
        private int CurrentLyricIndex = -1;

        public MainWindow()
        {
            InitializeComponent();
            MusicFiles = new ObservableCollection<MusicFile>();
            musicfilesListView.ItemsSource = MusicFiles; // Bind the ListView to the ObservableCollection
            spinningStoryboard = this.FindResource("SpinningAnimation") as Storyboard;

            mediaPlayer.MediaEnded += mediaPlayer_MediaEnded;
            mediaPlayer.MediaOpened += mediaPlayer_MediaOpened;

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += timer_Tick;
            UpdateShuffleButtonContent();

            string? savedMusicPath = LoadPathFromFile(MusicPathFileName); // Load saved music path
            if (!string.IsNullOrEmpty(savedMusicPath))
            {
                LoadMusicFiles(savedMusicPath);
            }

            string? savedCircleImagePath = LoadPathFromFile(CirclePictureFileName); // Load saved circle picture path
            if (!string.IsNullOrEmpty(savedCircleImagePath))
            {
                LoadAlbumCover(savedCircleImagePath);
            }

            string? savedVideoPath = LoadPathFromFile(BackgroundVideoFileName); // Load saved background video path
            if (!string.IsNullOrEmpty(savedVideoPath))
            {
                LoadBackgroundVideo(savedVideoPath);
            }

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
            int nextIndex = -1;

            for (int i = CurrentLyricIndex + 1; i < CurrentLyrics.Count; i++)
            {
                if (currentPosition >= CurrentLyrics[i].Timestamp)
                {
                    continue;
                }

                nextIndex = i;
                break;
            }

            int targetIndex = (nextIndex == -1) ? CurrentLyrics.Count - 1 : nextIndex - 1;

            if (targetIndex != CurrentLyricIndex && targetIndex >= 0)
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

                foreach (string line in lines)
                {
                    var match = Regex.Match(line, @"\[(\d{1,2}):(\d{2})\.(\d{2,3})\](.*)");

                    if (match.Success)
                    {
                        int minutes = int.Parse(match.Groups[1].Value);
                        int seconds = int.Parse(match.Groups[2].Value);
                        string millisecondsString = match.Groups[3].Value.PadRight(3, '0');
                        int milliseconds = int.Parse(millisecondsString);

                        TimeSpan timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                        string text = match.Groups[4].Value.Trim();

                        if (!string.IsNullOrEmpty(text))
                        {
                            lyrics.Add(new LyricLine { Text = text, Timestamp = timestamp });
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

        private void SavePathToFile(string fileName, string filePath)
        {
            try
            {
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                string fullPath = Path.Combine(directory, fileName);
                System.IO.File.WriteAllText(fullPath, filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存路径到文件 {fileName} 失败: {ex.Message}");
            }
        }

        private string? LoadPathFromFile(string fileName)
        {
            try
            {
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                string fullPath = Path.Combine(directory, fileName);

                if (System.IO.File.Exists(fullPath))
                {
                    string content = System.IO.File.ReadAllText(fullPath).Trim();
                    return string.IsNullOrWhiteSpace(content) ? null : content;
                }
                else
                {
                    System.IO.File.Create(fullPath).Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载路径从文件 {fileName} 失败: {ex.Message}");
                return null;
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
                        SavePathToFile(MusicPathFileName, selectedPath);
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

                    string title = string.IsNullOrWhiteSpace(file.Tag.Title)
                                   ? Path.GetFileNameWithoutExtension(filePath)
                                   : file.Tag.Title;

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

                SavePathToFile(CirclePictureFileName, selectedImagePath);
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

                SavePathToFile(BackgroundVideoFileName, selectedVideoPath);
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
            UpdateShuffleButtonContent();
        }
    }
}