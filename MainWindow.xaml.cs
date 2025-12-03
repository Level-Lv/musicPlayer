using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using TagLib;

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

        public MainWindow()
        {
            InitializeComponent();
            MusicFiles = new ObservableCollection<MusicFile>();
            musicfilesListView.ItemsSource = MusicFiles;
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
    }
}