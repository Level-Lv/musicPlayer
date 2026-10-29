using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace musicPlayer.Services;

/// <summary>
/// 在线曲库前置环境自检（对应《在线曲库开发步骤清单》S0）。
/// Win10/11 一般自带 Edge → 自带 WebView2 Runtime；缺失时在线功能整体禁用，本地功能不受影响。
/// </summary>
public static class OnlineEnvironment
{
    private static string? _cachedRuntimeVersion;

    /// <summary>WebView2 Runtime 是否可用（探测成功后缓存，进程内只探一次）。</summary>
    public static bool WebView2Available => _cachedRuntimeVersion != null;

    /// <summary>
    /// 探测本机 WebView2 Runtime 版本；不可用返回 null。
    /// 成功结果会缓存；失败不缓存，允许之后重试（例如用户装好运行时后不重启程序也能恢复）。
    /// 说明：CreateAsync 只创建环境对象、不启动浏览器进程（进程在创建 Controller 时才拉起），
    /// 运行时缺失时抛 WebView2RuntimeNotFoundException，统一按不可用处理。
    /// </summary>
    public static async Task<string?> GetWebView2VersionAsync()
    {
        if (_cachedRuntimeVersion != null) return _cachedRuntimeVersion;

        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            _cachedRuntimeVersion = env.BrowserVersionString;
        }
        catch (Exception)
        {
            return null;
        }
        return _cachedRuntimeVersion;
    }

    /// <summary>ffmpeg.exe 输出目录路径（csproj 已配置 Assets\ffmpeg\*.exe 随构建复制）。</summary>
    public static string FfmpegExePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");

    /// <summary>ffprobe.exe 输出目录路径（下载产物校验用）。</summary>
    public static string FfprobeExePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffprobe.exe");

    /// <summary>ffmpeg 组件是否就位。</summary>
    public static bool FfmpegAvailable => File.Exists(FfmpegExePath);
}
