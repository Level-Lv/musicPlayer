using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>扫描进度快照（通过 IProgress 回调传给 UI，UI 侧自行 Dispatcher 切换）。</summary>
    public class ScanProgress
    {
        public int ScannedFiles;      // 已处理文件数
        public int TotalFiles;        // 本次扫描的文件总数
        public int Added;             // 新增入库
        public int Updated;           // 更新已有
        public int Removed;           // 清理失效
        public string CurrentFile = ""; // 当前正在读取的文件名（进度条旁显示）
        public bool Finished;         // 是否已结束
        public bool Cancelled;        // 是否被用户取消
    }

    /// <summary>一次扫描的结果汇总。</summary>
    public class ScanResult
    {
        public int Added;
        public int Updated;
        public int Removed;
        public int TotalFiles;
        public bool Cancelled;
    }

    /// <summary>
    /// 音乐库扫描服务：
    /// - 递归扫描多个音乐文件夹（mp3 / flac / wav / m4a）；
    /// - 歌名 = 文件名去扩展名（PRD 约定，不读 ID3 标题）；
    /// - 时长用 TagLib 读取，失败记 0（界面显示 --:--）；
    /// - 按完整路径去重（Windows 路径大小写不敏感）；
    /// - 全库重扫结束后，删除本次没见到的路径记录（失效清理）；
    /// - 只操作数据库记录，绝不删除物理文件；
    /// - 支持取消（CancelScan 由设置弹窗的取消按钮调用）。
    /// </summary>
    public static class LibraryService
    {
        private static readonly string[] SupportedExtensions = { ".mp3", ".flac", ".wav", ".m4a" };

        private static volatile bool cancelRequested;

        /// <summary>请求取消当前扫描（幂等，扫描结束后自动复位）。</summary>
        public static void CancelScan() => cancelRequested = true;

        #region 对外入口

        /// <summary>
        /// 重扫全部启用的音乐文件夹，完成后清理失效记录。
        /// 这是设置页"重新扫描"按钮调用的入口。
        /// </summary>
        public static ScanResult RescanAll(IProgress<ScanProgress>? progress = null)
        {
            cancelRequested = false;
            var state = new ScanProgress();
            var result = new ScanResult();

            // 1. 收集所有启用文件夹下的音频文件清单（先列全，进度条才有总数）
            var folders = Db.GetMusicFolders();
            var files = new List<string>();
            foreach (var folder in folders)
            {
                CollectAudioFiles(folder, files);
            }
            state.TotalFiles = files.Count;
            result.TotalFiles = files.Count;
            Report(progress, state);

            // 2. 领取本次扫描的会话号：每条曲目入库时都会打上这个标记，
            //    扫完后凡是 last_seen_at < 会话号的记录，都是"本次没看到的"= 失效
            long session = Db.NextScanSession();

            // 3. 逐个读取元数据并入库
            foreach (var file in files)
            {
                if (cancelRequested) break;
                ProcessOneFile(file, state, session);
                state.ScannedFiles = state.Added + state.Updated;
                if (state.ScannedFiles % 25 == 0)
                {
                    state.CurrentFile = Path.GetFileName(file);
                    Report(progress, state);
                }
            }

            // 4. 未取消 → 执行失效清理；取消了 → 保留现状，下次重扫再清
            if (!cancelRequested)
            {
                state.CurrentFile = "清理失效记录...";
                result.Removed = (int)Db.RemoveStaleTracks(session);
                state.Removed = result.Removed;

                foreach (var folder in folders)
                {
                    Db.TouchMusicFolder(folder);
                }
            }

            result.Added = state.Added;
            result.Updated = state.Updated;
            result.Cancelled = cancelRequested;
            cancelRequested = false;

            state.Finished = true;
            Report(progress, state);
            return result;
        }

        /// <summary>
        /// 只扫描单个文件夹并入库（新增/更新），不清理失效记录。
        /// 这是设置页"添加文件夹"时调用的入口——此时其他文件夹还没扫，
        /// 清理会误删它们的记录，所以清理只留给 RescanAll。
        /// </summary>
        public static ScanResult ScanFolder(string folderPath, IProgress<ScanProgress>? progress = null)
        {
            cancelRequested = false;
            var state = new ScanProgress();
            var result = new ScanResult();

            var files = new List<string>();
            CollectAudioFiles(folderPath, files);
            state.TotalFiles = files.Count;
            result.TotalFiles = files.Count;
            Report(progress, state);

            long session = Db.NextScanSession();

            foreach (var file in files)
            {
                if (cancelRequested) break;
                ProcessOneFile(file, state, session);
                state.ScannedFiles = state.Added + state.Updated;
                if (state.ScannedFiles % 25 == 0)
                {
                    state.CurrentFile = Path.GetFileName(file);
                    Report(progress, state);
                }
            }

            if (!cancelRequested)
            {
                Db.TouchMusicFolder(folderPath);
            }

            result.Added = state.Added;
            result.Updated = state.Updated;
            result.Cancelled = cancelRequested;
            cancelRequested = false;

            state.Finished = true;
            Report(progress, state);
            return result;
        }

        #endregion

        #region 内部实现

        /// <summary>处理单个文件：读元数据 → 打会话标记入库，并累加新增/更新计数。</summary>
        private static void ProcessOneFile(string filePath, ScanProgress state, long session)
        {
            var (title, durationMs, format, mtime) = ReadMetadata(filePath);
            var (_, inserted) = Db.UpsertTrack(filePath, title, durationMs, format, mtime, session);
            if (inserted) state.Added++; else state.Updated++;
        }

        /// <summary>
        /// 读取音频元数据。歌名固定取文件名去扩展名（PRD 约定）；
        /// 时长用 TagLib 读取，任何失败都静默降级为 0（界面显示 --:--），不中断扫描。
        /// </summary>
        private static (string Title, long DurationMs, string Format, long Mtime) ReadMetadata(string filePath)
        {
            string title = Path.GetFileNameWithoutExtension(filePath);
            string format = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            long mtime = 0;
            long durationMs = 0;
            try
            {
                mtime = File.GetLastWriteTimeUtc(filePath).ToFileTime();
            }
            catch (IOException) { }

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var duration = tagFile.Properties?.Duration ?? TimeSpan.Zero;
                if (duration > TimeSpan.Zero)
                {
                    durationMs = (long)duration.TotalMilliseconds;
                }
            }
            catch (Exception)
            {
                // 损坏/不被识别的文件：时长记 0，歌曲仍入库（PRD：读取失败显示 --:--）
            }

            return (title, durationMs, format, mtime);
        }

        /// <summary>
        /// 递归收集文件夹下的音频文件。
        /// 手动递归而不是 Directory.EnumerateFiles(AllDirectories)，
        /// 因为后者遇到一个无权限子目录就整批中断；
        /// 这里按目录逐个 try/catch，无权限的直接跳过。
        /// </summary>
        private static void CollectAudioFiles(string folder, List<string> output)
        {
            if (!Directory.Exists(folder)) return;

            var stack = new Stack<string>();
            stack.Push(folder);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(current);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue; // 无权限/IO 异常的目录直接跳过，不影响其他目录
                }

                foreach (var entry in entries)
                {
                    FileAttributes attrs;
                    try
                    {
                        attrs = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        continue;
                    }

                    if (attrs.HasFlag(FileAttributes.Directory))
                    {
                        // 跳过符号链接/联接点，防止目录环导致死循环
                        if (!attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            stack.Push(entry);
                        }
                    }
                    else if (SupportedExtensions.Contains(
                        Path.GetExtension(entry).ToLowerInvariant()))
                    {
                        output.Add(entry);
                    }
                }
            }
        }

        private static void Report(IProgress<ScanProgress>? progress, ScanProgress state)
        {
            progress?.Report(CloneProgress(state));
        }

        private static ScanProgress CloneProgress(ScanProgress s) => new ScanProgress
        {
            ScannedFiles = s.ScannedFiles,
            TotalFiles = s.TotalFiles,
            Added = s.Added,
            Updated = s.Updated,
            Removed = s.Removed,
            CurrentFile = s.CurrentFile,
            Finished = s.Finished,
            Cancelled = s.Cancelled
        };

        #endregion
    }
}
