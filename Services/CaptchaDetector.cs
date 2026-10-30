using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace musicPlayer.Services
{
    /// <summary>
    /// 人机验证（Captcha / Cloudflare 挑战）识别与通过判定（《在线曲库开发步骤清单》S3）。
    ///
    /// 三道判据，任一命中即认为"当前卡在验证上"：
    /// 1. URL 特征（challenges.cloudflare.com / cf-chl / captcha / verify …）；
    /// 2. 页面里存在挑战元素（Turnstile、reCAPTCHA、hCaptcha、geetest、#challenge-form）；
    /// 3. 标题表征（"Just a moment…"、"安全验证"、"Attention Required" …）。
    ///
    /// "通过"的判定走另一条路：授权 Cookie（cf_clearance 等）出现，或者
    /// URL 已回到目标站点且页面上不再有挑战元素。
    ///
    /// 注意：这里只做"识别"，不做任何绕过——用户必须自己把验证做完。
    /// </summary>
    public static class CaptchaDetector
    {
        /// <summary>能代表"验证已通过"的授权 Cookie 名。</summary>
        private static readonly string[] ClearanceCookies =
        {
            "cf_clearance",
            "geetest_validate",
            "gokuprops"
        };

        /// <summary>URL 是否指向验证页（最省事的判据，导航后立刻可用）。</summary>
        public static bool IsCaptchaUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath.ToLowerInvariant();

            // ① 验证服务自己的域名（Cloudflare Turnstile / hCaptcha / GeeTest / reCAPTCHA）
            if (host.Contains("challenges.cloudflare.com", StringComparison.Ordinal)) return true;
            if (host.EndsWith("hcaptcha.com", StringComparison.Ordinal)) return true;
            if (host.Contains("geetest.com", StringComparison.Ordinal)) return true;
            if (path.Contains("/recaptcha/", StringComparison.Ordinal)) return true;

            // ② Cloudflare 的挑战平台路径（interstitial 页面）
            if (path.Contains("/cdn-cgi/challenge-platform", StringComparison.Ordinal)) return true;
            if (path.Contains("cf-chl", StringComparison.Ordinal)) return true;

            // ③ 路径**段**本身就是验证页——只比整段，
            //    避免把 .../musicplayer_captcha_demo.html 这类普通文件名误判成挑战页
            foreach (string seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == "captcha" || seg == "verify" || seg == "challenge"
                    || seg == "cf-chl" || seg.StartsWith("captcha.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 注入到页面里跑的检测脚本。返回 true / false。
        /// 只查"挑战是否真的渲染出来了"的元素，避免普通页面挂个 recaptcha 脚本就误判。
        /// </summary>
        public const string ProbeScript = @"(function(){
  try {
    var sels = [
      'iframe[src*=""challenges.cloudflare.com""]',
      '#challenge-form',
      '#challenge-running',
      '#challenge-stage',
      'iframe[src*=""recaptcha/api2/anchor""]',
      'iframe[src*=""hcaptcha.com""]',
      '.geetest_holder',
      '.geetest_panel',
      'div.cf-turnstile',
      '[id*=""captcha"" i][class*=""box"" i]'
    ];
    for (var i = 0; i < sels.length; i++) {
      if (document.querySelector(sels[i])) return true;
    }
    var t = (document.title || '').toLowerCase();
    var keys = ['just a moment', 'attention required', 'verify you are human',
                'checking your browser', '安全验证', '人机验证', '请完成验证', '正在验证'];
    for (var j = 0; j < keys.length; j++) {
      if (t.indexOf(keys[j]) >= 0) return true;
    }
    return false;
  } catch (e) { return false; }
})()";

        /// <summary>页面当前是否显示着验证挑战。</summary>
        public static async Task<bool> IsChallengeVisibleAsync(CoreWebView2 core)
        {
            try
            {
                string raw = await core.ExecuteScriptAsync(ProbeScript);
                return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false; // 页面正在跳转/被销毁时读不到，按"没有挑战"处理，交给下一轮轮询
            }
        }

        /// <summary>授权 Cookie 是否已出现（Cloudflare 通过后典型标志是 cf_clearance）。</summary>
        public static async Task<bool> HasClearanceCookieAsync(CoreWebView2 core, string url)
        {
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(url);
                foreach (var c in cookies)
                {
                    foreach (string name in ClearanceCookies)
                    {
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // 读 Cookie 失败按"未通过"处理
            }
            return false;
        }

        /// <summary>
        /// 综合判定验证是否已经通过：
        /// ① 出现授权 Cookie；或 ② 当前 URL 已不属于验证页、且 host 与目标站点一致、且页面上没有挑战元素。
        /// </summary>
        public static async Task<bool> IsPassedAsync(CoreWebView2 core, string targetUrl)
        {
            if (await HasClearanceCookieAsync(core, targetUrl)) return true;

            string current = core.Source ?? "";
            if (string.IsNullOrEmpty(current)) return false;
            if (IsCaptchaUrl(current)) return false;

            // 回到目标站点（同 host）才算通过一次挑战；被跳到别的域名不算
            if (!SameHost(current, targetUrl)) return false;

            return !await IsChallengeVisibleAsync(core);
        }

        /// <summary>两个 URL 是否同主机（含端口）。</summary>
        public static bool SameHost(string a, string b)
        {
            try
            {
                return string.Equals(new Uri(a).Authority, new Uri(b).Authority,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>验证通过后建议轮询的 Cookie 域名（取目标 URL 的根域）。</summary>
        public static IEnumerable<string> CookieProbeUrls(string targetUrl)
        {
            var list = new List<string> { targetUrl };
            try
            {
                var u = new Uri(targetUrl);
                list.Add($"{u.Scheme}://{u.Authority}/");
            }
            catch
            {
                // URL 不合法时只用原样值
            }
            return list;
        }
    }
}
