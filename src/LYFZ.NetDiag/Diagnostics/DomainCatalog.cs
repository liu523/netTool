using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LYFZ.NetDiag.Diagnostics;

internal static class DomainCatalog
{
    public const int MaxExtraDomains = 20;
    public static readonly IPAddress HistoricalBadEdge = IPAddress.Parse("113.215.230.101");

    public static IReadOnlyList<DiagnosticTarget> All { get; } =
    [
        new("app.lyfz.net", "海螺云页面入口/昆仑CDN", IPAddress.Parse("113.113.100.49"), true),
        new("login.lyfz.net", "海螺云登录/昆仑CDN", IPAddress.Parse("183.60.202.48"), true),
        new("api.lyfz.net", "海螺云业务API", IPAddress.Parse("47.99.141.216"), true,
            "根路径5xx仅表示服务已收到请求，按网络连通正常处理", true),
        new("storage.lyfz.net", "海螺云存储/昆仑CDN", IPAddress.Parse("116.253.29.46"), true, "HTTP 403可能是正常的鉴权响应"),
        new("message.lyfz.net", "海螺云消息/WSS", IPAddress.Parse("47.111.176.231"), true, "检测DNS、TCP、TLS和HTTP入口；业务WebSocket路径由应用决定"),
        new("erp-cdn.lyfz.net", "海螺云ERP静态资源/昆仑CDN", IPAddress.Parse("113.113.100.48"), true),
        new("napi.lyfz.net", "海螺云NAPI", IPAddress.Parse("47.97.157.241"), true, "根路径状态码仅用于连通性判断"),
        new("system.lyfz.net", "海螺云系统服务/昆仑CDN", IPAddress.Parse("113.113.100.43"), true, "HTTP 403可能是正常的鉴权响应"),
        new("turing.captcha.gtimg.com", "腾讯验证码", null, false),
        new("lp.open.weixin.qq.com", "微信开放平台", null, false),
        new("open.weixin.qq.com", "微信开放平台", null, false),
        new("support.weixin.qq.com", "微信支持服务", null, false),
        new("res.wx.qq.com", "微信静态资源", null, false)
    ];

    public static IReadOnlyList<string> ParseExtraDomains(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var defaultHosts = All.Select(target => target.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = Regex.Split(input, @"[\s,;，；]+")
            .Where(token => !string.IsNullOrWhiteSpace(token));

        foreach (var token in tokens)
        {
            var host = NormalizeHost(token);
            if (defaultHosts.Contains(host) || !seen.Add(host))
            {
                continue;
            }

            result.Add(host);
            if (result.Count > MaxExtraDomains)
            {
                throw new FormatException($"额外诊断域名最多允许 {MaxExtraDomains} 个。");
            }
        }

        return result;
    }

    public static IReadOnlyList<DiagnosticTarget> BuildTargets(IEnumerable<string>? extraDomains)
    {
        var targets = All.ToList();
        var seen = targets.Select(target => target.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var value in extraDomains ?? [])
        {
            var host = NormalizeHost(value);
            if (!seen.Add(host))
            {
                continue;
            }

            targets.Add(new DiagnosticTarget(
                host,
                "用户额外诊断域名",
                null,
                false,
                "由本次检测临时添加；执行DNS、Ping、TCP、TLS、HTTP和路由跟踪",
                false,
                true));
        }

        if (targets.Count > All.Count + MaxExtraDomains)
        {
            throw new FormatException($"额外诊断域名最多允许 {MaxExtraDomains} 个。");
        }

        return targets;
    }

    private static string NormalizeHost(string value)
    {
        var input = value.Trim();
        if (input.Length == 0)
        {
            throw new FormatException("诊断域名不能为空。");
        }

        string host;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https" or "ws" or "wss") || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new FormatException($"不支持的诊断地址：{value}");
            }

            host = uri.IdnHost;
        }
        else
        {
            if (input.Contains("://", StringComparison.Ordinal) || input.IndexOfAny(['/', '\\', ':', '?', '#', '@']) >= 0)
            {
                throw new FormatException($"诊断域名格式无效：{value}");
            }

            try
            {
                host = new IdnMapping().GetAscii(input.TrimEnd('.'));
            }
            catch (ArgumentException)
            {
                throw new FormatException($"诊断域名格式无效：{value}");
            }
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253 || IPAddress.TryParse(host, out _) || !host.Contains('.'))
        {
            throw new FormatException($"诊断域名格式无效：{value}");
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                throw new FormatException($"诊断域名格式无效：{value}");
            }
        }

        return host;
    }
}
