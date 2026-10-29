# ffmpeg（在线曲库下载功能依赖）

本目录需要两个可执行文件，因体积过大（各约 100MB）**不入版本库**，克隆仓库后需自行放置：

| 文件 | 用途 |
|---|---|
| `ffmpeg.exe` | HLS（m3u8，含 AES-128）/ DASH（m4s）下载合并、音频转封装 |
| `ffprobe.exe` | 下载产物校验（S9 验收用） |

## 获取方式（Windows PowerShell）

```powershell
cd D:\gitProject\study\musicPlayer\Assets\ffmpeg
curl.exe -L -o ffmpeg.zip https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip
Expand-Archive ffmpeg.zip -DestinationPath . -Force
Copy-Item .\ffmpeg-*-essentials_build\bin\ffmpeg.exe, .\ffmpeg-*-essentials_build\bin\ffprobe.exe .
Remove-Item -Recurse -Force ffmpeg.zip, .\ffmpeg-*-essentials_build
```

## 许可说明

gyan.dev 的 essentials 为 **GPL 构建**（原先打算用的 LGPL 构建已停更）。课程项目自用/演示无影响；如将来公开分发，需评估 GPL 传染性，或自行编译 LGPL 版本替换。

## 程序侧行为

启动时 `Services/OnlineEnvironment.cs` 会检测本目录：缺失则 Toast 提示"缺少 ffmpeg 组件，在线下载功能不可用"，本地播放功能不受影响。csproj 已将 `Assets\ffmpeg\*.exe` 配置为随构建复制到输出目录（`bin\Debug\net8.0-windows\Assets\ffmpeg\`）。
