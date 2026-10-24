using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// 歌词服务：
    /// - 在用户配置的歌词文件夹中【递归】查找与音乐文件同名的 .lrc；
    ///   例：D:\music\晴天.mp3 → 歌词文件夹内任意深度的 晴天.lrc
    /// - 编码自动识别：UTF-8（含 BOM）→ UTF-16（BOM）→ 严格 UTF-8 校验 → 兜底 GB18030（兼容 GBK）
    /// - 解析规则：
    ///   * 支持一行多个时间标签，如 [01:23.45][03:45.67]副歌歌词
    ///   * 支持时间标签的 [分:秒] / [分:秒.厘秒] / [分:秒.毫秒] 三种精度
    ///   * 支持 [offset:+500] 全局偏移标签（正值 = 歌词提前显示）
    ///   * 自动剥离逐字时间标签，如 &lt;00:12.34&gt;（增强型 LRC），只留文字
    ///   * 跳过 [ti:] [ar:] [al:] 等元数据行和无法解析的行
    /// - 找不到歌词 / 文件损坏时返回空列表，由界面显示"暂无歌词"
    /// </summary>
    public static class LyricService
    {
        /// <summary>歌词文件夹在 settings 表中的键名。</summary>
        private const string LyricsFolderKey = "lyrics_folder";

        /// <summary>
        /// 注册 GB18030（GBK 超集）编码支持。
        /// .NET 默认只带 Unicode 系编码，读 GBK 老歌词必须先注册 CodePages 提供程序。
        /// 静态构造函数保证第一次用到本类时自动执行，调用方无感知。
        /// </summary>
        static LyricService()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        #region 设置

        /// <summary>设置/更换歌词文件夹（设置页调用）。</summary>
        public static void SetLyricsFolder(string path) => Db.SetSetting(LyricsFolderKey, path);

        /// <summary>读取当前歌词文件夹，未配置时返回空字符串。</summary>
        public static string GetLyricsFolder() => Db.GetSetting(LyricsFolderKey);

        #endregion

        #region 歌词查找

        /// <summary>
        /// 查找指定音频文件的歌词并解析。
        /// 匹配规则：与音频文件【同名】（不含扩展名）的 .lrc，大小写不敏感，
        /// 在歌词文件夹内递归查找，找到第一个即返回。
        /// </summary>
        /// <param name="audioFilePath">音频文件完整路径</param>
        /// <returns>按时间升序的歌词行；找不到或解析失败返回空列表</returns>
        public static List<LyricLine> FindLyrics(string audioFilePath)
        {
            string lyricsFolder = GetLyricsFolder();
            if (string.IsNullOrEmpty(lyricsFolder) || !Directory.Exists(lyricsFolder))
            {
                return new List<LyricLine>();
            }
            if (string.IsNullOrEmpty(audioFilePath))
            {
                return new List<LyricLine>();
            }

            // 目标歌词文件名，如 "晴天.lrc"（比较时统一小写，大小写不敏感）
            string targetName = Path.GetFileNameWithoutExtension(audioFilePath)
                                      + ".lrc";

            string? lrcPath = FindLrcRecursive(lyricsFolder, targetName.ToLowerInvariant());
            if (lrcPath == null)
            {
                return new List<LyricLine>();
            }

            return ParseLrcFile(lrcPath);
        }

        /// <summary>
        /// 深度优先递归查找目标 .lrc 文件。
        /// 用栈遍历 + 每层独立 try/catch：
        /// 某个子目录无权限/IO 异常时只跳过它，不中断整棵目录树。
        /// </summary>
        private static string? FindLrcRecursive(string folder, string targetNameLower)
        {
            var stack = new Stack<string>();
            stack.Push(folder);

            while (stack.Count > 0)
            {
                string current = stack.Pop();

                // 先查当前目录的文件（找同目录歌词的概率远大于深层子目录，尽早返回）
                string[] files;
                try
                {
                    files = Directory.GetFiles(current, "*.lrc");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue; // 该目录读不了，跳过
                }

                foreach (var file in files)
                {
                    if (Path.GetFileName(file).ToLowerInvariant() == targetNameLower)
                    {
                        return file; // 命中，立即返回
                    }
                }

                // 当前目录没命中，把子目录压栈（跳过符号链接，防目录环）
                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(current);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        if (!File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                        {
                            stack.Push(sub);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // 拿不到属性就算了，跳过该子目录
                    }
                }
            }

            return null; // 整棵目录树都没找到
        }

        #endregion

        #region LRC 解析

        /// <summary>时间标签正则：[分:秒] / [分:秒.厘秒] / [分:秒.毫秒]，分和秒都允许 1~3 位数字。</summary>
        private static readonly System.Text.RegularExpressions.Regex TimeTagRegex =
            new System.Text.RegularExpressions.Regex(
                @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>逐字时间标签正则（增强型 LRC），如 &lt;00:12.34&gt;，解析后要从文字里剥掉。</summary>
        private static readonly System.Text.RegularExpressions.Regex WordTagRegex =
            new System.Text.RegularExpressions.Regex(
                @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>读取 lrc 文件（自动识别编码）并解析为歌词行列表。</summary>
        public static List<LyricLine> ParseLrcFile(string lrcFilePath)
        {
            string content;
            try
            {
                content = ReadFileWithEncodingDetection(lrcFilePath);
            }
            catch (Exception ex)
            {
                // 文件读不出来（被占用/权限/损坏）→ 当作无歌词处理，不让播放器报错
                System.Diagnostics.Debug.WriteLine($"歌词文件读取失败 {lrcFilePath}: {ex.Message}");
                return new List<LyricLine>();
            }

            return ParseLrc(content);
        }

        /// <summary>解析 LRC 文本内容（方便单测，与文件读取解耦）。</summary>
        public static List<LyricLine> ParseLrc(string content)
        {
            var lyrics = new List<LyricLine>();

            // [offset:毫秒] 全局偏移：正数 = 歌词提前显示（播放时间要加上偏移）
            int offsetMs = 0;
            var offsetMatch = System.Text.RegularExpressions.Regex.Match(
                content, @"^\s*\[offset:\s*([+-]?\d+)\s*\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Multiline);
            if (offsetMatch.Success)
            {
                int.TryParse(offsetMatch.Groups[1].Value, out offsetMs);
            }

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                // 找出这一行开头的所有时间标签（可能多个：[01:23.45][03:45.67]文字）
                var matches = TimeTagRegex.Matches(line);
                if (matches.Count == 0) continue; // 没有时间标签 = 元数据行或非法行，跳过

                // 歌词文字 = 最后一个时间标签之后的部分，剥掉逐字标签，去掉首尾空白
                int textStart = matches[^1].Index + matches[^1].Length;
                string text = line[textStart..].Trim();
                text = WordTagRegex.Replace(text, "").Trim();
                if (text.Length == 0) continue; // 纯时间标签没有文字的行，跳过

                // 同一行有几个时间标签，就生成几条歌词（副歌重复的标准表示法）
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    int minutes = int.Parse(m.Groups[1].Value);
                    int seconds = int.Parse(m.Groups[2].Value);

                    // 小数部分可能是 1~3 位：统一按毫秒解释
                    // ".5"  = 500ms（5 → 补零成 500，即厘秒/十分秒按位数右对齐）
                    // ".50" = 500ms
                    // ".500"= 500ms
                    string fracStr = m.Groups[3].Success ? m.Groups[3].Value : "0";
                    int fracMs = int.Parse(fracStr.PadRight(3, '0')[..3]);

                    // 时间戳 = 分 + 秒 + 小数 + 全局偏移，负值截断为 0
                    long totalMs = (long)minutes * 60_000
                                 + (long)seconds * 1_000
                                 + fracMs
                                 + offsetMs;
                    if (totalMs < 0) totalMs = 0;

                    lyrics.Add(new LyricLine
                    {
                        Text = text,
                        Timestamp = TimeSpan.FromMilliseconds(totalMs)
                    });
                }
            }

            // LRC 文件时间戳可能乱序，排序后歌词滚动逻辑才正确
            return lyrics.OrderBy(l => l.Timestamp).ToList();
        }

        #endregion

        #region 编码识别

        /// <summary>
        /// 读取文本文件并自动识别编码：
        /// 1. 有 BOM：UTF-8 / UTF-16LE / UTF-16BE 直接按 BOM 解码，最可靠；
        /// 2. 无 BOM：先按【严格模式 UTF-8】尝试——遇到非法字节序列立刻抛异常；
        ///    UTF-8 是 ASCII 的超集且自描述性强，GBK 文件几乎必含非法 UTF-8 序列，
        ///    所以"严格 UTF-8 通过 = 大概率真是 UTF-8"；
        /// 3. 严格 UTF-8 失败 → 按 GB18030 解码（GBK 的超集，兼容整个中文 Windows 圈）。
        /// </summary>
        private static string ReadFileWithEncodingDetection(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);

            // ---- 1. 检查 BOM（字节顺序标记）----
            if (bytes.Length >= 3
                && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); // UTF-8 BOM
            }
            if (bytes.Length >= 2
                && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); // UTF-16 LE BOM
            }
            if (bytes.Length >= 2
                && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); // UTF-16 BE BOM
            }

            // ---- 2. 无 BOM：严格 UTF-8 校验 ----
            // throwOnInvalidBytes = true：遇到非法字节直接抛 DecoderFallbackException
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                              throwOnInvalidBytes: true);
            try
            {
                return strictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // 不是合法 UTF-8，落到 GBK 分支
            }

            // ---- 3. GB18030 兜底（GBK 超集，简繁中文歌词都能吃下）----
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }

        #endregion
    }
}
