using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using musicPlayer.Data;

namespace musicPlayer.Services
{
    /// <summary>
    /// S8 在线试听自检的夹具（保留，长期可用）。
    ///
    /// 全部素材都在本地现场生成，**不联网、不依赖外网**：
    /// - <b>1 秒正弦波 WAV</b>：音量压得很低，MediaElement 真能播（不是假地址）；
    /// - <b>微型 HTTP 服务器</b>：把详情页和 WAV 用 http 提供出去——试听链路是"网络流"，
    ///   用 file:// 测不出真实行为（Cookie/Referer/Range 这些坑都在 http 侧）；
    /// - <b>详情页</b>：&lt;audio preload=none&gt; + 播放按钮，只有点了才请求音频，
    ///   正好验证"自动点播放"那一枪（很多真实站点点一下才加载音频）。
    ///
    /// 为什么要自带 HTTP 服务器：HttpListener 在部分机器上要 URL ACL（管理员）才能监听，
    /// TcpListener 不需要任何权限，随机端口 + 只绑 127.0.0.1，测试期间随用随关。
    /// </summary>
    public static class PlaybackSelfTest
    {
        /// <summary>夹具目录（临时目录下，可随时删除）。</summary>
        public static string DemoDirectory => Path.Combine(Path.GetTempPath(), "musicplayer_playtest");

        /// <summary>准备素材并启动本地服务器。返回的 server 必须 Dispose（否则端口一直占着）。</summary>
        public static (LocalHttpServer Server, PlaybackFixtures Fixtures) PrepareFixtures()
        {
            Directory.CreateDirectory(DemoDirectory);
            try { File.Delete(Path.Combine(DemoDirectory, "requests.log")); } catch { }   // 每次从干净的服务端请求记录开始

            string wavPath = CreateDemoWav(Path.Combine(DemoDirectory, "demo-tone.wav"));
            CreateDetailPage(Path.Combine(DemoDirectory, "detail.html"), "demo-tone.wav", "演示曲·静态提取");
            CreateDynamicDetailPage(Path.Combine(DemoDirectory, "detail-dynamic.html"), "demo-tone.wav", "演示曲·点了才加载");

            var server = LocalHttpServer.Start(DemoDirectory);
            var fixtures = new PlaybackFixtures
            {
                AudioFilePath = wavPath,
                DirectUrl = server.BaseUrl + "demo-tone.wav",              // 列表页就能拿到直链的站点
                DetailUrl = server.BaseUrl + "detail.html",                // 详情页 DOM 里就有 src
                DynamicDetailUrl = server.BaseUrl + "detail-dynamic.html"  // 点了播放才去请求音频
            };
            return (server, fixtures);
        }

        /// <summary>
        /// 生成 PCM WAV（16bit / 单声道 / 22.05kHz），默认 30 秒。
        ///
        /// 为什么不用 1 秒：自检里"播完自动下一首/到末尾停止"这些断言需要播放**保持进行中**，
        /// 1 秒的素材会在断言跑到一半时自然播完并触发自动切歌（实测把上一首/末尾停止两项搞挂了）。
        /// </summary>
        public static string CreateDemoWav(string path, int seconds = 30)
        {
            const int sampleRate = 22050;
            const short channels = 1;
            const short bitsPerSample = 16;
            int frames = sampleRate * seconds;
            int dataSize = frames * channels * bitsPerSample / 8;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                                   // fmt 块长度
            bw.Write((short)1);                             // PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * bitsPerSample / 8);   // 字节率
            bw.Write((short)(channels * bitsPerSample / 8));       // 块对齐
            bw.Write(bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);

            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / sampleRate;
                // 振幅只给到 8%（测试时即使外放也不刺耳）
                short v = (short)(Math.Sin(2 * Math.PI * 440 * t) * 2600);
                bw.Write(v);
            }
            return path;
        }

        /// <summary>
        /// 生成"详情页"：音频 preload=none，只有点播放按钮才真正请求 mp3/wav。
        /// 这样能验证嗅探里的"自动点播放"脚本确实起作用。
        /// </summary>
        public static string CreateDetailPage(string path, string audioRelativeUrl, string title)
        {
            string html = $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>{title}</title></head>
<body style=""background:#0b0b12;color:#dcdcdc;font-family:system-ui,sans-serif;padding:40px"">
  <h1 style=""font-size:20px"">{title}</h1>
  <p style=""color:#888;font-size:13px"">播放地址在点击后才加载（模拟真实站点）</p>
  <audio id=""player"" src=""{audioRelativeUrl}"" preload=""none""></audio>
  <button class=""play"" id=""play"" style=""padding:8px 16px;font-size:14px"">播放</button>
  <script>
    document.getElementById('play').addEventListener('click', function () {{
      var a = document.getElementById('player');
      a.muted = true;          // 页面自己静音：双声测试只关心我们这边有没有出声
      a.play();
    }});
  </script>
</body></html>";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// 生成"点了才加载"的详情页：&lt;audio&gt; 一开始<b>没有 src</b>，
        /// 只有点击按钮后由 JS 赋 src 并 play —— 静态提取拿不到，必须靠动态嗅探。
        /// </summary>
        public static string CreateDynamicDetailPage(string path, string audioRelativeUrl, string title)
        {
            string html = $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>{title}</title></head>
<body style=""background:#0b0b12;color:#dcdcdc;font-family:system-ui,sans-serif;padding:40px"">
  <h1 style=""font-size:20px"">{title}</h1>
  <p style=""color:#888;font-size:13px"">音频地址在点击后才写进 &lt;audio&gt;（模拟必须点播放的真实站点）</p>
  <audio id=""player"" preload=""none""></audio>
  <button class=""play"" id=""play"" style=""padding:8px 16px;font-size:14px"">播放</button>
  <script>
    document.getElementById('play').addEventListener('click', function () {{
      var a = document.getElementById('player');
      if (!a.src) a.src = '{audioRelativeUrl}';
      a.muted = true;
      a.play();
    }});
  </script>
</body></html>";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// 造 5 条演示结果：
        /// ① 带直链（不用解析） ② 只有"点了才加载"的详情页（要走动态嗅探）
        /// ③ 流媒体地址（能下载不能试听） ④ 什么都没有（队列里应被排除）
        /// ⑤ 详情页 DOM 里就有 src（静态提取，秒出地址）。
        /// </summary>
        public static List<OnlineResult> CreateDemoResults(PlaybackFixtures fx)
        {
            return new List<OnlineResult>
            {
                new()
                {
                    Title = "演示曲·直链", Artist = "自检", SiteName = "本地演示站",
                    DetailUrl = fx.DetailUrl, AudioUrl = fx.DirectUrl, CanDownload = true,
                    Duration = TimeSpan.FromSeconds(30)
                },
                new()
                {
                    Title = "演示曲·需嗅探", Artist = "自检", SiteName = "本地演示站",
                    DetailUrl = fx.DynamicDetailUrl, CanDownload = true,
                    Duration = TimeSpan.FromSeconds(30)
                },
                new()
                {
                    Title = "演示曲·流媒体", Artist = "自检", SiteName = "本地演示站",
                    DetailUrl = fx.DetailUrl, AudioUrl = fx.DirectUrl.Replace("demo-tone.wav", "stream.m3u8"),
                    CanDownload = true
                },
                new()
                {
                    Title = "演示曲·无链接", Artist = "自检", SiteName = "本地演示站"
                },
                new()
                {
                    Title = "演示曲·静态提取", Artist = "自检", SiteName = "本地演示站",
                    DetailUrl = fx.DetailUrl, CanDownload = true
                }
            };
        }

        /// <summary>夹具地址集合。</summary>
        public class PlaybackFixtures
        {
            public string AudioFilePath { get; init; } = "";
            public string DirectUrl { get; init; } = "";
            public string DetailUrl { get; init; } = "";
            public string DynamicDetailUrl { get; init; } = "";

            /// <summary>本地 WAV 的 file:// 地址（MediaElement 最稳的播放源，兜底用例用）。</summary>
            public string FileUrl => new Uri(AudioFilePath).AbsoluteUri;
        }

        /// <summary>
        /// 极简 HTTP 服务器（TcpListener，只服务测试夹具目录）：
        /// 支持 GET/HEAD、单段 Range（MediaElement 会发 Range: bytes=0-）。
        /// </summary>
        public sealed class LocalHttpServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly string rootDir;
            private readonly CancellationTokenSource cts = new();
            private volatile bool disposed;

            public string BaseUrl { get; }

            private LocalHttpServer(TcpListener listener, string baseUrl, string rootDir)
            {
                this.listener = listener;
                this.rootDir = rootDir;
                BaseUrl = baseUrl;
            }

            public static LocalHttpServer Start(string rootDir)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = new LocalHttpServer(listener, $"http://127.0.0.1:{port}/", rootDir);
                _ = server.AcceptLoopAsync();
                return server;
            }

            private async Task AcceptLoopAsync()
            {
                while (!disposed)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }                       // 停止监听 → 结束循环
                    _ = Task.Run(() => HandleAsync(client));
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                        string? requestLine = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(requestLine)) return;

                        var parts = requestLine.Split(' ');
                        string method = parts[0];
                        string path = parts.Length > 1 ? parts[1] : "/";
                        if (path.Contains('?')) path = path[..path.IndexOf('?')];

                        // 诊断：把收到的请求记到文件里（嗅探自检失败时用来看"浏览器到底有没有发请求"）
                        try
                        {
                            File.AppendAllText(Path.Combine(rootDir, "requests.log"),
                                $"{DateTime.Now:HH:mm:ss.fff} {requestLine}" + Environment.NewLine);
                        }
                        catch { }

                        string? rangeHeader = null;
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                        {
                            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                                rangeHeader = line["Range:".Length..].Trim();
                        }

                        string file = Path.Combine(rootDir,
                            path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (path is "/" or "") file = Path.Combine(rootDir, "detail.html");

                        if (!File.Exists(file))
                        {
                            await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                                Array.Empty<byte>());
                            return;
                        }

                        byte[] body = await File.ReadAllBytesAsync(file);
                        string contentType = Path.GetExtension(file).ToLowerInvariant() switch
                        {
                            ".html" => "text/html; charset=utf-8",
                            ".wav" => "audio/wav",
                            ".mp3" => "audio/mpeg",
                            _ => "application/octet-stream"
                        };

                        // 单段 Range：MediaElement/WMP 拿不到 206 时可能整段放弃播放
                        if (rangeHeader != null && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            var range = rangeHeader["bytes=".Length..].Split('-');
                            long start = 0;
                            if (range.Length > 0 && long.TryParse(range[0], out long s)) start = s;
                            if (start >= body.Length) start = 0;
                            int len = (int)(body.Length - start);

                            string header =
                                "HTTP/1.1 206 Partial Content\r\n" +
                                $"Content-Type: {contentType}\r\n" +
                                $"Content-Range: bytes {start}-{body.Length - 1}/{body.Length}\r\n" +
                                $"Content-Length: {len}\r\n" +
                                "Accept-Ranges: bytes\r\n" +
                                "Connection: close\r\n\r\n";

                            await WriteAsync(stream, header, body.AsSpan((int)start, len).ToArray());
                            return;
                        }

                        await WriteAsync(stream,
                            "HTTP/1.1 200 OK\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {body.Length}\r\n" +
                            "Accept-Ranges: bytes\r\n" +
                            "Connection: close\r\n\r\n",
                            method == "HEAD" ? Array.Empty<byte>() : body);
                    }
                    catch
                    {
                        // 客户端提前断开（MediaElement 换源时会这样）——测试里直接忽略
                    }
                }
            }

            private static async Task WriteAsync(NetworkStream stream, string header, byte[] body)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                if (body.Length > 0) await stream.WriteAsync(body);
                await stream.FlushAsync();
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                cts.Cancel();
                try { listener.Stop(); } catch { }
            }
        }
    }
}
