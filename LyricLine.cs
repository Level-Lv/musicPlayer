using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace musicPlayer
{
    public class LyricLine
    {
        public string? Text { get; set; }
        public TimeSpan Timestamp { get; set; }
        public TextBlock? Control { get; set; }
    }
}