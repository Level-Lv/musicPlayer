using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using TagLib;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Controls;

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
        private ObservableCollection<MusicFile> MusicFiles { get; set; }
        private Storyboard? spinningStoryboard;

        public MainWindow()
        {
            InitializeComponent();
            MusicFiles = new ObservableCollection<MusicFile>();
            musicfilesListView.ItemsSource = MusicFiles;
            spinningStoryboard = this.FindResource("SpinningAnimation") as Storyboard;
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                DialogResult result = dialog.ShowDialog();

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;
                    LoadMusicFiles(selectedPath);
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

                if (spinningStoryboard != null)
                {
                    spinningStoryboard.Begin(VisualizerGrid, true);
                }

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
            mediaPlayer.Stop();
            if (spinningStoryboard != null)
            {
                spinningStoryboard.Stop(VisualizerGrid);
            }
            this.Title = "音乐播放器";
        }

    }
}