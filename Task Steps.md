### 阶段一：数据层（步骤 1-3）

**步骤 1 — 添加 NuGet 依赖**

- `musicPlayer.csproj` 加入 `Microsoft.Data.Sqlite`，保留 taglib-sharp，确认还原成功

**步骤 2 — `Data/Models.cs` 数据模型**

- `PlaylistItem`：Id / Name / SortOrder / SortDir，带属性变更通知
- `TrackItem`：Id / Title / Duration / FullPath / Format，含 IsSearchMatch、IsPlaying 标记

**步骤 3 — `Data/Db.cs` SQLite 数据层**

- 数据库路径：`%APPDATA%\musicPlayer\data.db`
- 建表：`tracks`（path 唯一）、`playlists`、`playlist_tracks`（唯一约束+级联删除）、`music_folders`、`settings`、`play_history`
- **旧数据迁移**：首次启动读取 `setting.json` → 写入 settings 表和 music_folders → 旧文件改名 `setting.json.bak`

### 阶段二：服务层（步骤 4-5）

**步骤 4 — `Services/LibraryService.cs` 扫描服务**

- 多文件夹递归扫描 mp3/flac/wav/m4a，TagLib 读时长（失败显示 `--:--`）
- 按完整路径去重、增量同步 DB；重扫时自动清理已失效路径记录
- 带进度回调 + 取消标志（供设置弹窗使用）

**步骤 5 — `Services/LyricService.cs` 歌词服务**

- 歌词文件夹递归找同名 `.lrc`，UTF-8/GBK 编码自动识别
- 解析时间戳（支持一行多时间标签），返回歌词列表

### 阶段三：界面（步骤 6-8）

**步骤 6 — 重构 `MainWindow.xaml`**

- 三列主区：歌单方块栏 80px ｜ 歌曲列表 340px（搜索框+范围下拉+正则开关+排序按钮）｜ 歌词主区域
- GridSplitter 可拖拽调整列表宽度（280~480px）
- 底部通栏播放条：小旋转封面（点击换图）+ 歌名 ｜ 进度/时间 ｜ ⏮▶⏭ ｜ 随机/顺序/单曲循环三态 ｜ 音量+静音 ｜ 词
- 歌单方块：首字/首字母/Emoji 显示、Tooltip、选中高亮、右键重命名/删除、拖拽排序
- 背景视频铺底逻辑不变

**步骤 7 — 重构 `MainWindow.xaml.cs` 主逻辑**

- 全部改为 DB 驱动：启动加载文件夹→扫描→曲目→按歌单过滤
- 歌单：新建/重命名/删除（二次确认）/拖拽排序持久化
- 搜索：回车触发、范围切换、正则开关（无效正则提示）、搜索后播放队列=过滤结果
- 排序：歌名自然排序（拼音+数字感知），每个歌单记忆升降序
- 播放：三态播放方式、记忆上次歌曲不自动播、音量持久化、失败自动跳下一首、连续失败 5 首暂停、失效文件自动移库+提示
- 右键菜单：加到歌单/从歌单移除/打开文件位置/从库移除
- 快捷键切换为 PRD 标准：空格、Ctrl+←/→、↑↓音量、M 静音、L 歌词、Ctrl+F 聚焦搜索、Esc 清除、Delete 移除
- 歌词：当前行高亮滚动 + **点击歌词行跳转播放**

**步骤 8 — `SettingsWindow` 设置弹窗**

- 七个分区：音乐文件夹管理（含进度条）｜ 歌词文件夹 ｜ 背景（图片/视频/清除）｜ 播放设置 ｜ 主题（暗色固定）｜ 快捷键只读表 ｜ 数据位置展示

### 阶段四：收尾（步骤 9-10）

**步骤 9 — 编译验证与修复**：`dotnet build` + 运行实测迁移/扫描/播放/歌单/搜索全流程

**步骤 10 — 更新 README**：新布局说明、快捷键表、数据库位置、歌词规则、迁移说明