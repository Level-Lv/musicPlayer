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
                //（注意长度为 1 时 trimmed[1] 会越界，必须先判长度）
                if (trimmed.Length >= 2 && char.IsSurrogatePair(trimmed[0], trimmed[1]))
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

        /// <summary>曲目来源：local（本地扫描）/ online（在线下载入库）。</summary>
        public string Source { get; set; } = "local";

        /// <summary>在线来源页地址（仅 Source = online 时有值）。</summary>
        public string? SourceUrl { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // ================= 在线曲库（对应《在线曲库 PRD》） =================

    /// <summary>
    /// 站点状态常量，与 online_sites.status 的存储值一一对应。
    /// 用常量而非 enum：直接与数据库/UI 文本映射，避免额外转换。
    /// </summary>
    public static class OnlineSiteStatus
    {
        public const string Ok = "ok";                  // 正常
        public const string NeedLogin = "need_login";    // 需登录（Cookie 过期/首次要求登录）
        public const string Broken = "broken";           // 失效（页面结构变了，需重新点选）
        public const string Unsupported = "unsupported"; // 不支持（强反爬/验证过不去）
    }

    /// <summary>
    /// 站点选择器配置，序列化为 JSON 存入 online_sites.selectors。
    /// 除 Item/Title 外均为可选：站点页面没这些信息就留空。
    /// </summary>
    public class SiteSelectors
    {
        /// <summary>结果项容器选择器（列表里的一条）。</summary>
        public string? Item { get; set; }

        /// <summary>标题（容器内相对选择器）。</summary>
        public string? Title { get; set; }

        /// <summary>艺人（可选）。</summary>
        public string? Artist { get; set; }

        /// <summary>时长（可选）。</summary>
        public string? Duration { get; set; }

        /// <summary>封面图（可选）。</summary>
        public string? Cover { get; set; }

        /// <summary>详情页链接（可选，用于后续嗅探音频）。</summary>
        public string? Link { get; set; }

        /// <summary>是否具备最小可用配置（结果项 + 标题）。</summary>
        public bool IsUsable =>
            !string.IsNullOrWhiteSpace(Item) && !string.IsNullOrWhiteSpace(Title);
    }

    /// <summary>
    /// 在线站点模型（对应 online_sites 表）。
    /// 左侧"已添加站点"面板与该站搜索都基于它。
    /// </summary>
    public class OnlineSite : INotifyPropertyChanged
    {
        private string name = "";
        private string status = OnlineSiteStatus.Ok;

        /// <summary>数据库主键。</summary>
        public long Id { get; set; }

        /// <summary>站点显示名（添加向导自动取 &lt;title&gt;，可改）。</summary>
        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>搜索模板，含 {q}；可选分页占位 {p}。如 https://site/search?q={q}</summary>
        public string SearchTemplate { get; set; } = "";

        /// <summary>分页模板（可选）。为空表示该站只取首页结果。</summary>
        public string? PageTemplate { get; set; }

        /// <summary>选择器配置（已从 JSON 反序列化）。</summary>
        public SiteSelectors Selectors { get; set; } = new SiteSelectors();

        /// <summary>状态：见 <see cref="OnlineSiteStatus"/>。</summary>
        public string Status
        {
            get => status;
            set
            {
                if (status != value)
                {
                    status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsHealthy));
                }
            }
        }

        /// <summary>状态下拉/徽章显示文本。</summary>
        public string StatusText => Status switch
        {
            OnlineSiteStatus.Ok => "正常",
            OnlineSiteStatus.NeedLogin => "需登录",
            OnlineSiteStatus.Broken => "失效",
            OnlineSiteStatus.Unsupported => "不支持",
            _ => Status
        };

        /// <summary>是否处于可用状态（正常）。</summary>
        public bool IsHealthy => Status == OnlineSiteStatus.Ok;

        /// <summary>Cookie 最近更新时间（unix ms，0 表示从未保存）。</summary>
        public long CookieUpdatedAt { get; set; }

        /// <summary>创建时间（unix ms）。</summary>
        public long CreatedAt { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>在线结果项的单次下载状态。</summary>
    public enum DownloadState
    {
        None,       // 未下载
        Pending,    // 排队中
        Running,    // 下载中
        Done,       // 已完成
        Failed,     // 失败
        Cancelled   // 已取消
    }

    /// <summary>
    /// 在线搜索结果项（不落库，仅内存）。
    /// 列表行只显示【标题 + 下载按钮】，其余字段供播放/下载/入库使用。
    /// </summary>
    public class OnlineResult : INotifyPropertyChanged
    {
        private DownloadState downloadState = DownloadState.None;
        private double downloadProgress;
        private string downloadError = "";

        /// <summary>标题。</summary>
        public string Title { get; set; } = "";

        /// <summary>艺人（可能为空）。</summary>
        public string? Artist { get; set; }

        /// <summary>时长（解析失败为 Zero）。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>封面地址（可能为空）。</summary>
        public string? CoverUrl { get; set; }

        /// <summary>详情页地址（可能为空）。</summary>
        public string? DetailUrl { get; set; }

        /// <summary>音频直链（抓取阶段可能拿不到，播放时再嗅探）。</summary>
        public string? AudioUrl { get; set; }

        /// <summary>来源站点 id。</summary>
        public long SiteId { get; set; }

        /// <summary>来源站点名（"全部站点"模式分组显示用）。</summary>
        public string SiteName { get; set; } = "";

        /// <summary>是否可下载（判定规则见 PRD F6.1）。</summary>
        public bool CanDownload { get; set; }

        /// <summary>不可下载的原因（置灰 tooltip 用）。</summary>
        public string NotDownloadReason { get; set; } = "";

        /// <summary>下载状态。</summary>
        public DownloadState DownloadState
        {
            get => downloadState;
            set
            {
                if (downloadState != value)
                {
                    downloadState = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DownloadButtonText));
                    OnPropertyChanged(nameof(IsDownloadEnabled));
                }
            }
        }

        /// <summary>下载进度 0~100。</summary>
        public double DownloadProgress
        {
            get => downloadProgress;
            set
            {
                if (Math.Abs(downloadProgress - value) > 0.01)
                {
                    downloadProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DownloadButtonText));
                }
            }
        }

        /// <summary>失败原因（Toast/ tooltip 用）。</summary>
        public string DownloadError
        {
            get => downloadError;
            set
            {
                if (downloadError != value)
                {
                    downloadError = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>下载按钮显示文本：下载 / 进度百分比 / 已入库 / 不可下载。</summary>
        public string DownloadButtonText => DownloadState switch
        {
            DownloadState.Pending => "排队中",
            DownloadState.Running => $"{DownloadProgress:0}%",
            DownloadState.Done => "已入库",
            DownloadState.Failed => "重试",
            DownloadState.Cancelled => "重试",
            _ => CanDownload ? "下载" : "不可下载"
        };

        /// <summary>下载按钮是否可点（未下载/失败可重试；下载中禁用）。</summary>
        public bool IsDownloadEnabled =>
            CanDownload && DownloadState is DownloadState.None
                or DownloadState.Failed or DownloadState.Cancelled;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 下载队列记录（对应 downloads 表）。
    /// 只服务于队列管理与状态恢复，不提供历史浏览界面（PRD 决策⑥）。
    /// </summary>
    public class DownloadRecord
    {
        public long Id { get; set; }
        public long SiteId { get; set; }
        public string Title { get; set; } = "";
        public string? Artist { get; set; }

        /// <summary>下载源地址（直链 / m3u8 / m4s）。</summary>
        public string AudioUrl { get; set; } = "";

        /// <summary>落盘路径（完成后写入）。</summary>
        public string? FilePath { get; set; }

        /// <summary>状态字符串：pending / running / done / failed / cancelled。</summary>
        public string Status { get; set; } = "pending";

        public long SizeBytes { get; set; }
        public string? Error { get; set; }
        public long CreatedAt { get; set; }
    }
}
