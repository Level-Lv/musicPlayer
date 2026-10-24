using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace musicPlayer.Data
{
    /// <summary>
    /// 歌单模型（对应数据库 playlists 表）。
    /// 用于左侧歌单方块栏的绑定显示。
    /// </summary>
    public class PlaylistItem : INotifyPropertyChanged
    {
        private string name = "";
        private string sortDir = "asc";

        /// <summary>数据库主键。"全部歌曲"虚拟歌单固定为 0。</summary>
        public long Id { get; set; }

        /// <summary>歌单名。</summary>
        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayChar));
                }
            }
        }

        /// <summary>歌单在左侧栏中的排序位置。</summary>
        public int SortOrder { get; set; }

        /// <summary>该歌单记住的排序方向：asc / desc（排序字段 MVP 固定为歌名）。</summary>
        public string SortDir
        {
            get => sortDir;
            set
            {
                if (sortDir != value)
                {
                    sortDir = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>是否为固定的"全部歌曲"视图（不可删除）。</summary>
        public bool IsAllTracks { get; init; }

        /// <summary>
        /// 方块上显示的字符：
        /// 中文取第一个汉字，英文取首字母大写，数字取首字符，Emoji 原样显示。
        /// </summary>
        public string DisplayChar
        {
            get
            {
                if (IsAllTracks) return "全";
                string trimmed = name?.Trim() ?? "";
                if (trimmed.Length == 0) return "?";

                // 处理 Emoji 等代理对字符，取第一个完整文本元素
                if (char.IsSurrogatePair(trimmed[0], trimmed[1]))
                {
                    return trimmed.Substring(0, 2);
                }
                return trimmed[..1].ToUpperInvariant();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 曲目模型（对应数据库 tracks 表）。
    /// 用于歌曲列表绑定显示。
    /// </summary>
    public class TrackItem : INotifyPropertyChanged
    {
        private bool isSearchMatch;
        private bool isPlaying;

        /// <summary>数据库主键（tracks.id）。</summary>
        public long Id { get; set; }

        /// <summary>歌名（文件名去扩展名）。</summary>
        public string Title { get; set; } = "";

        /// <summary>时长，读取失败为 TimeSpan.Zero，显示为 --:--。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>完整文件路径。</summary>
        public string FullPath { get; set; } = "";

        /// <summary>音频格式小写：mp3 / flac / wav / m4a。</summary>
        public string Format { get; set; } = "";

        /// <summary>搜索匹配标记（用于列表高亮）。</summary>
        public bool IsSearchMatch
        {
            get => isSearchMatch;
            set
            {
                if (isSearchMatch != value)
                {
                    isSearchMatch = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>正在播放标记（用于列表高亮当前曲目）。</summary>
        public bool IsPlaying
        {
            get => isPlaying;
            set
            {
                if (isPlaying != value)
                {
                    isPlaying = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>列表显示用的时长文本，未读到时长时显示 --:--。</summary>
        public string DurationText =>
            Duration > TimeSpan.Zero ? Duration.ToString(@"mm\:ss") : "--:--";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
