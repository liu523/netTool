using System.Net;

namespace LYFZ.NetDiag.Diagnostics;

internal static class DomainCatalog
{
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
}
