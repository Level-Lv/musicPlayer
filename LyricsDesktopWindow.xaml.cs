using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace musicPlayer
{
    public partial class LyricsDesktopWindow : Window
    {
        private List<LyricLine> LyricsData { get; set; } = new List<LyricLine>();
        private int CurrentLineIndex = -1;

        public LyricsDesktopWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double windowWidth = this.Width;
            this.Left = (screenWidth - windowWidth) / 2;

            this.Top = 5;

            RenderDesktopLyrics(0);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        public void LoadLyrics(List<LyricLine> lyrics)
        {
            LyricsData = lyrics.Where(l => !string.IsNullOrEmpty(l.Text)).ToList();
            CurrentLineIndex = -1;

            RenderDesktopLyrics(0);
        }

        public void UpdatePosition(TimeSpan currentPosition)
        {
            if (LyricsData.Count <= 1) return;

            int targetIndex = -1;

            for (int i = 0; i < LyricsData.Count; i++)
            {
                if (currentPosition < LyricsData[i].Timestamp)
                {
                    targetIndex = i - 1;
                    break;
                }
            }

            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex >= LyricsData.Count) targetIndex = LyricsData.Count - 1;

            if (targetIndex != CurrentLineIndex)
            {
                CurrentLineIndex = targetIndex;
                RenderDesktopLyrics(CurrentLineIndex);
            }
        }

        private void RenderDesktopLyrics(int highlightIndex)
        {
            DesktopLyricsPanel.Children.Clear();

            if (highlightIndex >= 0 && highlightIndex < LyricsData.Count)
            {
                Style highlightStyle = (Style)this.FindResource("HighlightLyricTextBlockStyle");
                TextBlock currentTb = CreateLyricTextBlock(LyricsData[highlightIndex].Text, highlightStyle, true);
                DesktopLyricsPanel.Children.Add(currentTb);
            }

            int nextIndex = highlightIndex + 1;
            if (nextIndex < LyricsData.Count)
            {
                Style normalStyle = (Style)this.FindResource("NormalLyricTextBlockStyle");
                TextBlock nextTb = CreateLyricTextBlock(LyricsData[nextIndex].Text, normalStyle, false);
                DesktopLyricsPanel.Children.Add(nextTb);
            }
            else if (highlightIndex == LyricsData.Count - 1 && LyricsData.Count > 0)
            {
                Style normalStyle = (Style)this.FindResource("NormalLyricTextBlockStyle");
                TextBlock emptyTb = CreateLyricTextBlock("~ 歌曲播放完毕 ~", normalStyle, false);
                emptyTb.Opacity = 0.5;
                DesktopLyricsPanel.Children.Add(emptyTb);
            }
        }

        
        private TextBlock CreateLyricTextBlock(string? text, Style style, bool isHighlighted)
        {
            var tb = new TextBlock
            {
                Text = text ?? string.Empty,
                Style = style,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            if (!isHighlighted)
            {
                tb.Opacity = 0.7;
            }
            else
            {
                tb.Opacity = 1.0;
            }

            return tb;
        }
    }
}