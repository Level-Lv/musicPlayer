using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace musicPlayer.Data
{
    /// <summary>
    /// SQLite 数据层（单例静态类）。
    /// 数据库文件：%%APPDATA%%\musicPlayer\data.db
    ///
    /// 使用约定：
    /// - 每次操作短连接（using），打开时自动开启外键约束（PRAGMA foreign_keys = ON）；
    /// - 批量写入用 ExecuteInTransaction 包裹，避免逐条开关连接；
    /// - "全部歌曲"是虚拟歌单（Id = 0），不落库，由界面层处理。
    /// </summary>
    public static class Db
    {
        private static string? connectionString;
        private static readonly object initLock = new object();

        /// <summary>数据库文件完整路径（初始化后可读，用于设置页展示）。</summary>
        public static string DbPath { get; private set; } = "";

        /// <summary>
        /// 当前数据库结构版本，结构变更时递增并补充迁移逻辑。
        /// v1：本地曲库（tracks/playlists/...）；v2：在线曲库（online_sites/downloads + tracks.source）。
        /// </summary>
        private const int SchemaVersion = 2;

        /// <summary>旧版 setting.json 文件名。</summary>
        private const string LegacySettingsFileName = "setting.json";

        #region 初始化

        /// <summary>
        /// 初始化数据库：建目录、建表、跑迁移（含旧 setting.json 迁移）。
        /// 可重复调用，内部有锁和幂等保护。
        /// </summary>
        public static void Initialize()
        {
            lock (initLock)
            {
                if (connectionString != null) return;

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "musicPlayer");
                Directory.CreateDirectory(dir);

                DbPath = Path.Combine(dir, "data.db");
                connectionString = $"Data Source={DbPath}";

                using var conn = OpenConnection();

                // WAL 模式：读写并发性能更好，崩溃后不易损坏
                ExecuteNonQuery(conn, "PRAGMA journal_mode = WAL;");

                CreateTables(conn);
                MigrateTracksColumns(conn); // v1 → v2：tracks 扩列（在线来源标记）
                ExecuteNonQuery(conn,
                    $"INSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaVersion});");

                MigrateLegacySettings();
            }
        }

        private static void CreateTables(SqliteConnection conn)
        {
            // 曲目表：path 唯一，作为去重键
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS tracks (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    path         TEXT NOT NULL UNIQUE,
                    file_name    TEXT NOT NULL DEFAULT '',
                    title        TEXT NOT NULL DEFAULT '',
                    duration_ms  INTEGER NOT NULL DEFAULT 0,
                    format       TEXT NOT NULL DEFAULT '',
                    mtime        INTEGER NOT NULL DEFAULT 0,
                    created_at   INTEGER NOT NULL DEFAULT 0,
                    updated_at   INTEGER NOT NULL DEFAULT 0,
                    last_seen_at INTEGER NOT NULL DEFAULT 0
                );");

            // 歌单表
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS playlists (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    name       TEXT NOT NULL,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    sort_dir   TEXT NOT NULL DEFAULT 'asc',
                    created_at INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL DEFAULT 0
                );");

            // 歌单-曲目关联：唯一约束防重复添加；两侧级联删除
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS playlist_tracks (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                    track_id    INTEGER NOT NULL REFERENCES tracks(id)    ON DELETE CASCADE,
                    position    INTEGER NOT NULL DEFAULT 0,
                    added_at    INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(playlist_id, track_id)
                );");

            // 音乐库文件夹
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS music_folders (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    path            TEXT NOT NULL UNIQUE,
                    enabled         INTEGER NOT NULL DEFAULT 1,
                    last_scanned_at INTEGER NOT NULL DEFAULT 0
                );");

            // 通用设置键值表
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL DEFAULT ''
                );");

            // 播放历史：只记最后一次播放的歌。
            // 注意：这里存完整路径而不是 track_id——重新扫描会重建 tracks 行，
            // id 会变，路径才是稳定标识（与 PRD 的 last_track_id 有意不同）。
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS play_history (
                    id             INTEGER PRIMARY KEY CHECK (id = 1),
                    last_track_path TEXT NOT NULL DEFAULT '',
                    updated_at     INTEGER NOT NULL DEFAULT 0
                );");

            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );");

            // 在线站点配置（《在线曲库 PRD》第 6 章）
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS online_sites (
                    id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    name              TEXT NOT NULL,
                    search_template   TEXT NOT NULL,
                    page_template     TEXT,
                    selectors         TEXT NOT NULL DEFAULT '{}',
                    status            TEXT NOT NULL DEFAULT 'ok',
                    cookie_updated_at INTEGER NOT NULL DEFAULT 0,
                    created_at        INTEGER NOT NULL DEFAULT 0
                );");

            // 下载队列记录：只服务队列管理与状态恢复，不提供历史浏览界面
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS downloads (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    site_id    INTEGER NOT NULL DEFAULT 0,
                    title      TEXT NOT NULL DEFAULT '',
                    artist     TEXT,
                    audio_url  TEXT NOT NULL,
                    file_path  TEXT,
                    status     TEXT NOT NULL DEFAULT 'pending',
                    size_bytes INTEGER NOT NULL DEFAULT 0,
                    error      TEXT,
                    created_at INTEGER NOT NULL DEFAULT 0
                );");
        }

        /// <summary>
        /// v1 → v2 迁移：给 tracks 补 source / source_url 两列。
        /// 幂等——先查 PRAGMA table_info，缺哪列补哪列，旧库重复启动不会报错。
        /// </summary>
        private static void MigrateTracksColumns(SqliteConnection conn)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(tracks);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1)); // 第 1 列是列名
            }

            if (!columns.Contains("source"))
            {
                ExecuteNonQuery(conn,
                    "ALTER TABLE tracks ADD COLUMN source TEXT NOT NULL DEFAULT 'local';");
            }

            if (!columns.Contains("source_url"))
            {
                ExecuteNonQuery(conn, "ALTER TABLE tracks ADD COLUMN source_url TEXT;");
            }
        }

        #endregion

        #region 旧设置迁移（setting.json → 数据库）

        private class LegacySettings
        {
            public string? CirclePictureFileName { get; set; }
            public string? BackgroundVideoFileName { get; set; }
            public string? MusicPathFileName { get; set; }
            public bool IsShuffleMode { get; set; }
            public bool IsDesktopLyricsVisible { get; set; }
            public string? LastOneSong { get; set; }
        }

        /// <summary>
        /// 旧版本把设置存在 exe 旁的 setting.json。
        /// 首次运行新版本时：把值搬进 settings 表 / music_folders 表，
        /// 然后把 setting.json 改名为 setting.json.bak 留底。
        /// </summary>
        private static void MigrateLegacySettings()
        {
            string legacyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                LegacySettingsFileName);

            if (!File.Exists(legacyFile)) return;
            if (GetSetting("legacy_settings_migrated") == "1") return;

            try
            {
                string json = File.ReadAllText(legacyFile);
                var legacy = JsonSerializer.Deserialize<LegacySettings>(json);

                if (legacy != null)
                {
                    if (!string.IsNullOrEmpty(legacy.BackgroundVideoFileName))
                        SetSetting("background_file", legacy.BackgroundVideoFileName);

                    if (!string.IsNullOrEmpty(legacy.CirclePictureFileName))
                        SetSetting("cover_picture_file", legacy.CirclePictureFileName);

                    if (!string.IsNullOrEmpty(legacy.LastOneSong))
                        SetLastTrackPath(legacy.LastOneSong);

                    SetSetting("play_mode", legacy.IsShuffleMode ? "shuffle" : "sequential");
                    SetSetting("desktop_lyrics_visible", legacy.IsDesktopLyricsVisible ? "1" : "0");

                    if (!string.IsNullOrEmpty(legacy.MusicPathFileName)
                        && Directory.Exists(legacy.MusicPathFileName))
                    {
                        AddMusicFolder(legacy.MusicPathFileName);
                    }
                }

                SetSetting("legacy_settings_migrated", "1");

                // 改名留底，避免下次重复迁移
                // （File.Replace 要求目标文件已存在，首次运行 .bak 不存在会抛异常，故用 Move）
                string backup = legacyFile + ".bak";
                File.Move(legacyFile, backup, overwrite: true);
            }
            catch (Exception ex)
            {
                // 迁移失败不阻塞启动：只写日志，下次再试
                System.Diagnostics.Debug.WriteLine($"旧设置迁移失败: {ex.Message}");
            }
        }

        #endregion

        #region 基础工具

        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            ExecuteNonQuery(conn, "PRAGMA foreign_keys = ON;");
            return conn;
        }

        private static void ExecuteNonQuery(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void ExecuteNonQuery(SqliteConnection conn, string sql,
            Action<SqliteParameterCollection> bindParams)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bindParams(cmd.Parameters);
            cmd.ExecuteNonQuery();
        }

        private static object? ExecuteScalar(SqliteConnection conn, string sql,
            Action<SqliteParameterCollection>? bindParams = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bindParams?.Invoke(cmd.Parameters);
            return cmd.ExecuteScalar();
        }

        /// <summary>批量写入的事务包装：异常自动回滚，正常提交。</summary>
        public static void ExecuteInTransaction(Action<SqliteConnection> work)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                work(conn);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        #endregion

        #region settings 键值

        public static string GetSetting(string key, string defaultValue = "")
        {
            using var conn = OpenConnection();
            var result = ExecuteScalar(conn,
                "SELECT value FROM settings WHERE key = @key;",
                p => p.AddWithValue("@key", key));
            return result as string ?? defaultValue;
        }

        public static void SetSetting(string key, string value)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                INSERT INTO settings (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                p => { p.AddWithValue("@key", key); p.AddWithValue("@value", value); });
        }

        public static bool GetSettingBool(string key, bool defaultValue = false) =>
            GetSetting(key, defaultValue ? "1" : "0") == "1";

        public static void SetSettingBool(string key, bool value) =>
            SetSetting(key, value ? "1" : "0");

        #endregion

        #region music_folders 音乐库文件夹

        public static List<string> GetMusicFolders()
        {
            var list = new List<string>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM music_folders WHERE enabled = 1 ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }

        /// <summary>添加音乐库文件夹（已存在则忽略），返回是否新添加。</summary>
        public static bool AddMusicFolder(string path)
        {
            using var conn = OpenConnection();
            var existing = ExecuteScalar(conn,
                "SELECT id FROM music_folders WHERE path = @path;",
                p => p.AddWithValue("@path", path));
            if (existing != null) return false;

            ExecuteNonQuery(conn, @"
                INSERT INTO music_folders (path, enabled, last_scanned_at)
                VALUES (@path, 1, 0);",
                p => p.AddWithValue("@path", path));
            return true;
        }

        public static void RemoveMusicFolder(string path)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, "DELETE FROM music_folders WHERE path = @path;",
                p => p.AddWithValue("@path", path));
        }

        public static void TouchMusicFolder(string path)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                UPDATE music_folders SET last_scanned_at = @now WHERE path = @path;",
                p => { p.AddWithValue("@now", Now()); p.AddWithValue("@path", path); });
        }

        #endregion

        #region tracks 曲目

        /// <summary>
        /// 按路径新增或更新一首曲目（去重的关键：path UNIQUE + ON CONFLICT 处理）。
        /// lastSeen 传扫描会话号（见 LibraryService.RescanAll），用于失效清理。
        /// source / sourceUrl 仅在**首次插入**时写入：本地重扫不会把"在线下载"的曲目改回 local。
        /// 返回 (曲目 id, 是否为新增)。
        /// </summary>
        public static (long Id, bool Inserted) UpsertTrack(string path, string title,
            long durationMs, string format, long mtime, long lastSeen,
            string source = "local", string? sourceUrl = null)
        {
            using var conn = OpenConnection();

            // 先尝试插入，冲突则跳过（DO NOTHING 不抛异常）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO tracks (path, file_name, title, duration_ms, format,
                                        mtime, created_at, updated_at, last_seen_at,
                                        source, source_url)
                    VALUES (@path, @fileName, @title, @durationMs, @format,
                            @mtime, @now, @now, @lastSeen,
                            @source, @sourceUrl)
                    ON CONFLICT(path) DO NOTHING;";
                BindTrackParams(cmd.Parameters, path, title, durationMs, format, mtime, lastSeen);
                cmd.Parameters.AddWithValue("@source", source);
                cmd.Parameters.AddWithValue("@sourceUrl", (object?)sourceUrl ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // 检查上一条语句是否真的插入了行
            var changes = Convert.ToInt64(ExecuteScalar(conn, "SELECT changes();"));
            if (changes > 0)
            {
                var newId = Convert.ToInt64(ExecuteScalar(conn, "SELECT last_insert_rowid();"));
                return (newId, true);
            }

            // 已存在 → 更新
            var id = Convert.ToInt64(ExecuteScalar(conn, @"
                UPDATE tracks SET
                    title        = @title,
                    duration_ms  = @durationMs,
                    format       = @format,
                    mtime        = @mtime,
                    updated_at   = @now,
                    last_seen_at = @lastSeen
                WHERE path = @path
                RETURNING id;",
                p => BindTrackParams(p, path, title, durationMs, format, mtime, lastSeen)));
            return (id, false);
        }

        private static void BindTrackParams(SqliteParameterCollection p, string path, string title,
            long durationMs, string format, long mtime, long lastSeen)
        {
            p.AddWithValue("@path", path);
            p.AddWithValue("@fileName", Path.GetFileName(path));
            p.AddWithValue("@title", title);
            p.AddWithValue("@durationMs", durationMs);
            p.AddWithValue("@format", format);
            p.AddWithValue("@mtime", mtime);
            p.AddWithValue("@lastSeen", lastSeen);
            p.AddWithValue("@now", Now());
        }

        /// <summary>取下一个扫描会话号（单调递增，存于 settings.scan_session）。</summary>
        public static long NextScanSession()
        {
            using var conn = OpenConnection();
            var next = Convert.ToInt64(ExecuteScalar(conn,
                "SELECT COALESCE(CAST(value AS INTEGER), 0) + 1 FROM settings WHERE key = 'scan_session';"));
            if (next <= 0) next = 1;
            ExecuteNonQuery(conn, @"
                INSERT INTO settings (key, value) VALUES ('scan_session', @value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                p => p.AddWithValue("@value", next.ToString()));
            return next;
        }

        /// <summary>
        /// 删除"失效曲目"：last_seen_at 落后于本次会话号的记录，
        /// 即本次全库重扫没有看到的路径（文件被删 / 文件夹被移除）。
        /// playlist_tracks 由外键级联自动清理。返回删除条数。
        /// </summary>
        public static long RemoveStaleTracks(long session)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE last_seen_at < @session;";
            cmd.Parameters.AddWithValue("@session", session);
            return cmd.ExecuteNonQuery();
        }

        public static List<TrackItem> GetAllTracks()
        {
            var list = new List<TrackItem>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, path, title, duration_ms, format, source, source_url
                FROM tracks;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TrackItem
                {
                    Id = reader.GetInt64(0),
                    FullPath = reader.GetString(1),
                    Title = reader.GetString(2),
                    Duration = TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                    Format = reader.GetString(4),
                    Source = reader.IsDBNull(5) ? "local" : reader.GetString(5),
                    SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
            return list;
        }

        public static TrackItem? GetTrackByPath(string path)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, path, title, duration_ms, format, source, source_url
                FROM tracks WHERE path = @path;";
            cmd.Parameters.AddWithValue("@path", path);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new TrackItem
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                Title = reader.GetString(2),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                Format = reader.GetString(4),
                Source = reader.IsDBNull(5) ? "local" : reader.GetString(5),
                SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }

        /// <summary>从库中移除曲目（playlist_tracks 由外键级联自动清理）。不删物理文件。</summary>
        public static void RemoveTrackByPath(string path)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, "DELETE FROM tracks WHERE path = @path;",
                p => p.AddWithValue("@path", path));
        }

        /// <summary>曲目数（供状态栏/扫描结果显示）。</summary>
        public static long GetTrackCount()
        {
            using var conn = OpenConnection();
            return Convert.ToInt64(ExecuteScalar(conn, "SELECT COUNT(*) FROM tracks;"));
        }

        #endregion

        #region playlists 歌单

        public static List<PlaylistItem> GetPlaylists()
        {
            var list = new List<PlaylistItem>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, name, sort_order, sort_dir
                FROM playlists ORDER BY sort_order, id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new PlaylistItem
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    SortOrder = reader.GetInt32(2),
                    SortDir = reader.GetString(3)
                });
            }
            return list;
        }

        /// <summary>创建歌单，排到末尾。返回新歌单 id。</summary>
        public static long CreatePlaylist(string name)
        {
            using var conn = OpenConnection();
            var maxOrder = Convert.ToInt64(ExecuteScalar(conn,
                "SELECT COALESCE(MAX(sort_order), 0) FROM playlists;")) + 1;

            var id = ExecuteScalar(conn, @"
                INSERT INTO playlists (name, sort_order, sort_dir, created_at, updated_at)
                VALUES (@name, @order, 'asc', @now, @now)
                RETURNING id;",
                p => { p.AddWithValue("@name", name); p.AddWithValue("@order", maxOrder); p.AddWithValue("@now", Now()); });
            return Convert.ToInt64(id);
        }

        public static void RenamePlaylist(long playlistId, string newName)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                UPDATE playlists SET name = @name, updated_at = @now WHERE id = @id;",
                p => { p.AddWithValue("@name", newName); p.AddWithValue("@now", Now()); p.AddWithValue("@id", playlistId); });
        }

        public static void DeletePlaylist(long playlistId)
        {
            using var conn = OpenConnection();
            // playlist_tracks 由 ON DELETE CASCADE 自动清理，歌曲本身不动
            ExecuteNonQuery(conn, "DELETE FROM playlists WHERE id = @id;",
                p => p.AddWithValue("@id", playlistId));
        }

        /// <summary>写入歌单顺序（界面拖拽排序后调用，orderedIds 为最终从上到下的顺序）。</summary>
        public static void ReorderPlaylists(List<long> orderedIds)
        {
            ExecuteInTransaction(conn =>
            {
                for (int i = 0; i < orderedIds.Count; i++)
                {
                    ExecuteNonQuery(conn, @"
                        UPDATE playlists SET sort_order = @order, updated_at = @now WHERE id = @id;",
                        p => { p.AddWithValue("@order", i + 1); p.AddWithValue("@now", Now()); p.AddWithValue("@id", orderedIds[i]); });
                }
            });
        }

        public static void SetPlaylistSortDir(long playlistId, string dir)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                UPDATE playlists SET sort_dir = @dir WHERE id = @id;",
                p => { p.AddWithValue("@dir", dir); p.AddWithValue("@id", playlistId); });
        }

        #endregion

        #region playlist_tracks 歌单内容

        /// <summary>把歌曲加入歌单（同一歌单内不允许重复，重复添加直接忽略）。</summary>
        public static void AddTrackToPlaylist(long playlistId, long trackId)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                INSERT INTO playlist_tracks (playlist_id, track_id, position, added_at)
                SELECT @playlistId, @trackId,
                       COALESCE(MAX(position), 0) + 1, @now
                FROM playlist_tracks WHERE playlist_id = @playlistId
                ON CONFLICT(playlist_id, track_id) DO NOTHING;",
                p =>
                {
                    p.AddWithValue("@playlistId", playlistId);
                    p.AddWithValue("@trackId", trackId);
                    p.AddWithValue("@now", Now());
                });
        }

        /// <summary>从歌单移除歌曲（只解除关联，歌曲记录保留）。</summary>
        public static void RemoveTrackFromPlaylist(long playlistId, long trackId)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                DELETE FROM playlist_tracks
                WHERE playlist_id = @playlistId AND track_id = @trackId;",
                p => { p.AddWithValue("@playlistId", playlistId); p.AddWithValue("@trackId", trackId); });
        }

        /// <summary>取指定歌单内的曲目。playlistId = 0 表示"全部歌曲"。</summary>
        public static List<TrackItem> GetPlaylistTracks(long playlistId)
        {
            if (playlistId == 0) return GetAllTracks();

            var list = new List<TrackItem>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT t.id, t.path, t.title, t.duration_ms, t.format, t.source, t.source_url
                FROM tracks t
                JOIN playlist_tracks pt ON pt.track_id = t.id
                WHERE pt.playlist_id = @playlistId
                ORDER BY pt.position;";
            cmd.Parameters.AddWithValue("@playlistId", playlistId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TrackItem
                {
                    Id = reader.GetInt64(0),
                    FullPath = reader.GetString(1),
                    Title = reader.GetString(2),
                    Duration = TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                    Format = reader.GetString(4),
                    Source = reader.IsDBNull(5) ? "local" : reader.GetString(5),
                    SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
            return list;
        }

        /// <summary>歌曲是否已在指定歌单内（右键菜单"添加到歌单"置灰用）。</summary>
        public static bool IsTrackInPlaylist(long playlistId, long trackId)
        {
            using var conn = OpenConnection();
            return ExecuteScalar(conn, @"
                SELECT 1 FROM playlist_tracks
                WHERE playlist_id = @playlistId AND track_id = @trackId LIMIT 1;",
                p => { p.AddWithValue("@playlistId", playlistId); p.AddWithValue("@trackId", trackId); }) != null;
        }

        #endregion

        #region online_sites 在线站点

        /// <summary>读取全部在线站点（按添加顺序）。</summary>
        public static List<OnlineSite> GetOnlineSites()
        {
            var list = new List<OnlineSite>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, name, search_template, page_template, selectors,
                       status, cookie_updated_at, created_at
                FROM online_sites ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadOnlineSite(reader));
            return list;
        }

        /// <summary>按 id 读取单个站点（不存在返回 null）。</summary>
        public static OnlineSite? GetOnlineSite(long id)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, name, search_template, page_template, selectors,
                       status, cookie_updated_at, created_at
                FROM online_sites WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadOnlineSite(reader) : null;
        }

        /// <summary>新增站点，返回新 id。</summary>
        public static long AddOnlineSite(OnlineSite site)
        {
            using var conn = OpenConnection();
            var id = ExecuteScalar(conn, @"
                INSERT INTO online_sites
                    (name, search_template, page_template, selectors, status,
                     cookie_updated_at, created_at)
                VALUES (@name, @template, @pageTemplate, @selectors, @status, 0, @now)
                RETURNING id;",
                p =>
                {
                    p.AddWithValue("@name", site.Name);
                    p.AddWithValue("@template", site.SearchTemplate);
                    p.AddWithValue("@pageTemplate", (object?)site.PageTemplate ?? DBNull.Value);
                    p.AddWithValue("@selectors", SerializeSelectors(site.Selectors));
                    p.AddWithValue("@status", site.Status);
                    p.AddWithValue("@now", Now());
                });
            return Convert.ToInt64(id);
        }

        /// <summary>更新站点的名称/模板/选择器/状态（Cookie 时间戳另有专用方法）。</summary>
        public static void UpdateOnlineSite(OnlineSite site)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                UPDATE online_sites SET
                    name            = @name,
                    search_template = @template,
                    page_template   = @pageTemplate,
                    selectors       = @selectors,
                    status          = @status
                WHERE id = @id;",
                p =>
                {
                    p.AddWithValue("@name", site.Name);
                    p.AddWithValue("@template", site.SearchTemplate);
                    p.AddWithValue("@pageTemplate", (object?)site.PageTemplate ?? DBNull.Value);
                    p.AddWithValue("@selectors", SerializeSelectors(site.Selectors));
                    p.AddWithValue("@status", site.Status);
                    p.AddWithValue("@id", site.Id);
                });
        }

        /// <summary>只更新状态（搜索失败/需登录/自愈时用）。</summary>
        public static void SetOnlineSiteStatus(long siteId, string status)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, "UPDATE online_sites SET status = @status WHERE id = @id;",
                p => { p.AddWithValue("@status", status); p.AddWithValue("@id", siteId); });
        }

        /// <summary>标记站点 Cookie 刚更新（一次性登录/验证通过后调用）。</summary>
        public static void TouchOnlineSiteCookie(long siteId)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn,
                "UPDATE online_sites SET cookie_updated_at = @now WHERE id = @id;",
                p => { p.AddWithValue("@now", Now()); p.AddWithValue("@id", siteId); });
        }

        /// <summary>删除站点（配置随之消失；已下载的音乐文件与 tracks 记录保留）。</summary>
        public static void DeleteOnlineSite(long siteId)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, "DELETE FROM online_sites WHERE id = @id;",
                p => p.AddWithValue("@id", siteId));
        }

        private static OnlineSite ReadOnlineSite(SqliteDataReader reader) => new OnlineSite
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            SearchTemplate = reader.GetString(2),
            PageTemplate = reader.IsDBNull(3) ? null : reader.GetString(3),
            Selectors = DeserializeSelectors(reader.IsDBNull(4) ? null : reader.GetString(4)),
            Status = reader.GetString(5),
            CookieUpdatedAt = reader.GetInt64(6),
            CreatedAt = reader.GetInt64(7)
        };

        /// <summary>选择器 JSON 序列化（紧凑格式，便于直接入库排查）。</summary>
        private static string SerializeSelectors(SiteSelectors selectors) =>
            JsonSerializer.Serialize(selectors);

        /// <summary>选择器 JSON 反序列化；脏数据不抛异常，退化为空配置。</summary>
        private static SiteSelectors DeserializeSelectors(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new SiteSelectors();
            try
            {
                return JsonSerializer.Deserialize<SiteSelectors>(json) ?? new SiteSelectors();
            }
            catch (JsonException)
            {
                return new SiteSelectors();
            }
        }

        #endregion

        #region downloads 下载队列

        /// <summary>插入一条下载任务，返回任务 id。</summary>
        public static long InsertDownload(DownloadRecord record)
        {
            using var conn = OpenConnection();
            var id = ExecuteScalar(conn, @"
                INSERT INTO downloads
                    (site_id, title, artist, audio_url, file_path, status,
                     size_bytes, error, created_at)
                VALUES (@siteId, @title, @artist, @audioUrl, @filePath, @status,
                        @sizeBytes, @error, @now)
                RETURNING id;",
                p =>
                {
                    p.AddWithValue("@siteId", record.SiteId);
                    p.AddWithValue("@title", record.Title);
                    p.AddWithValue("@artist", (object?)record.Artist ?? DBNull.Value);
                    p.AddWithValue("@audioUrl", record.AudioUrl);
                    p.AddWithValue("@filePath", (object?)record.FilePath ?? DBNull.Value);
                    p.AddWithValue("@status", record.Status);
                    p.AddWithValue("@sizeBytes", record.SizeBytes);
                    p.AddWithValue("@error", (object?)record.Error ?? DBNull.Value);
                    p.AddWithValue("@now", Now());
                });
            return Convert.ToInt64(id);
        }

        /// <summary>更新下载任务状态（完成时补 file_path / size_bytes，失败时补 error）。</summary>
        public static void UpdateDownload(long downloadId, string status,
            string? filePath = null, long sizeBytes = 0, string? error = null)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                UPDATE downloads SET
                    status     = @status,
                    file_path  = COALESCE(@filePath, file_path),
                    size_bytes = CASE WHEN @sizeBytes > 0 THEN @sizeBytes ELSE size_bytes END,
                    error      = @error
                WHERE id = @id;",
                p =>
                {
                    p.AddWithValue("@status", status);
                    p.AddWithValue("@filePath", (object?)filePath ?? DBNull.Value);
                    p.AddWithValue("@sizeBytes", sizeBytes);
                    p.AddWithValue("@error", (object?)error ?? DBNull.Value);
                    p.AddWithValue("@id", downloadId);
                });
        }

        /// <summary>
        /// 取未完成的任务（pending / running）。
        /// 程序异常退出后残留的 running 由调用方（DownloadService）重置为 failed。
        /// </summary>
        public static List<DownloadRecord> GetActiveDownloads()
        {
            var list = new List<DownloadRecord>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, site_id, title, artist, audio_url, file_path,
                       status, size_bytes, error, created_at
                FROM downloads WHERE status IN ('pending', 'running') ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DownloadRecord
                {
                    Id = reader.GetInt64(0),
                    SiteId = reader.GetInt64(1),
                    Title = reader.GetString(2),
                    Artist = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AudioUrl = reader.GetString(4),
                    FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Status = reader.GetString(6),
                    SizeBytes = reader.GetInt64(7),
                    Error = reader.IsDBNull(8) ? null : reader.GetString(8),
                    CreatedAt = reader.GetInt64(9)
                });
            }
            return list;
        }

        #endregion

        #region play_history 播放记忆

        public static string GetLastTrackPath()
        {
            using var conn = OpenConnection();
            var result = ExecuteScalar(conn, "SELECT last_track_path FROM play_history WHERE id = 1;");
            return result as string ?? "";
        }

        public static void SetLastTrackPath(string path)
        {
            using var conn = OpenConnection();
            ExecuteNonQuery(conn, @"
                INSERT INTO play_history (id, last_track_path, updated_at) VALUES (1, @path, @now)
                ON CONFLICT(id) DO UPDATE SET last_track_path = @path, updated_at = @now;",
                p => { p.AddWithValue("@path", path); p.AddWithValue("@now", Now()); });
        }

        #endregion
    }
}
