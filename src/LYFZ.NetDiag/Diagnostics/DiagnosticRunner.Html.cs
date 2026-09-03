using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LYFZ.NetDiag.Diagnostics;

internal sealed partial class DiagnosticRunner
{
    private static async Task<string> WriteHtmlReportAsync(
        string logPath,
        DiagnosticRunOptions options,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        NetworkSnapshot networkSnapshot,
        LanBaselineResult lanBaseline,
        IReadOnlyList<TargetResult> results,
        MonitoringResult? monitoring,
        bool cancelled)
    {
        var reportPath = Path.ChangeExtension(logPath, ".html");
        await using var logStream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var logReader = new StreamReader(logStream, Encoding.UTF8, true);
        var rawLog = await logReader.ReadToEndAsync(CancellationToken.None);
        var html = BuildHtmlReport(
            options,
            startedAt,
            endedAt,
            networkSnapshot,
            lanBaseline,
            results,
            monitoring,
            cancelled,
            logPath,
            rawLog);
        await File.WriteAllTextAsync(reportPath, html, new UTF8Encoding(false), CancellationToken.None);
        return reportPath;
    }

    private static string BuildHtmlReport(
        DiagnosticRunOptions options,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        NetworkSnapshot networkSnapshot,
        LanBaselineResult lanBaseline,
        IReadOnlyList<TargetResult> results,
        MonitoringResult? monitoring,
        bool cancelled,
        string logPath,
        string rawLog)
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
        var statuses = results.Select(GetHtmlTargetStatus).ToArray();
        var failedCount = statuses.Count(item => item.CssClass == "bad");
        var warningCount = statuses.Count(item => item.CssClass == "warn");
        var healthyCount = statuses.Count(item => item.CssClass == "ok");
        var monitoringFailures = monitoring?.Aggregates.Sum(item => item.Value.Failures) ?? 0;
        var overall = GetOverallHtmlStatus(cancelled, failedCount, warningCount, monitoringFailures);
        var analysisReasons = BuildAutomaticAnalysisReasons(results, lanBaseline, monitoring);
        var duration = endedAt - startedAt;
        var builder = new StringBuilder(64 * 1024);

        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        builder.AppendLine("<title>利亚方舟海螺云网络诊断分析报告</title>");
        builder.AppendLine("""
<style>
:root{color-scheme:light;--bg:#f4f7fb;--card:#fff;--text:#172033;--muted:#667085;--line:#e4e9f2;--ok:#16865b;--okbg:#e9f8f1;--warn:#b66a00;--warnbg:#fff4df;--bad:#c83d4b;--badbg:#ffebed;--info:#2864d7;--infobg:#eaf1ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:"Microsoft YaHei UI","PingFang SC",Arial,sans-serif;line-height:1.55}.wrap{max-width:1240px;margin:0 auto;padding:28px 20px 56px}.hero{color:#fff;border-radius:20px;padding:28px 30px;background:linear-gradient(135deg,#153e75,#2463c7 58%,#168a88);box-shadow:0 16px 38px rgba(28,65,130,.22)}h1{font-size:28px;margin:0 0 8px}.hero p{margin:0;color:#dfeaff}.hero-row{display:flex;gap:16px;align-items:center;justify-content:space-between;flex-wrap:wrap}.hero .badge{font-size:16px;padding:8px 15px;background:rgba(255,255,255,.16);color:#fff;border:1px solid rgba(255,255,255,.35)}.toolbar{margin-top:16px}.button{border:0;border-radius:9px;background:#fff;color:#174a91;padding:9px 14px;font-weight:700;cursor:pointer}.grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:18px 0}.metric,.card{background:var(--card);border:1px solid var(--line);border-radius:14px;box-shadow:0 5px 18px rgba(34,54,91,.06)}.metric{padding:16px}.metric .value{font-size:26px;font-weight:800}.metric .label{font-size:13px;color:var(--muted)}.card{padding:20px;margin:16px 0}.card h2{font-size:19px;margin:0 0 14px}.card h3{font-size:15px;margin:18px 0 8px}.meta{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px 22px}.meta div{min-width:0}.meta strong{display:block;font-size:12px;color:var(--muted);font-weight:600}.badge{display:inline-flex;align-items:center;border-radius:999px;padding:4px 10px;font-size:12px;font-weight:800;white-space:nowrap}.ok{color:var(--ok);background:var(--okbg)}.warn{color:var(--warn);background:var(--warnbg)}.bad{color:var(--bad);background:var(--badbg)}.info{color:var(--info);background:var(--infobg)}.muted{color:var(--muted);background:#f1f3f7}.analysis{margin:0;padding-left:22px}.analysis li{margin:9px 0;padding-left:3px}.hint{border-left:4px solid var(--info);background:var(--infobg);padding:12px 14px;border-radius:8px;margin-top:12px;color:#244a86}.table-wrap{overflow:auto;border:1px solid var(--line);border-radius:11px}table{border-collapse:collapse;width:100%;min-width:850px;background:#fff}th,td{text-align:left;border-bottom:1px solid var(--line);padding:10px 11px;vertical-align:top;font-size:13px}th{position:sticky;top:0;background:#f7f9fc;color:#475467;font-weight:750}tr:last-child td{border-bottom:0}.host{font-weight:800;color:#234a84}.sub{display:block;color:var(--muted);font-size:11px;margin-top:3px}.files{display:flex;flex-wrap:wrap;gap:10px}.file{display:block;text-decoration:none;border:1px solid #cdd9ee;background:#f7faff;color:#2456a4;border-radius:10px;padding:11px 14px;font-weight:700}.file:hover{background:#edf4ff}details{border:1px solid var(--line);border-radius:10px;background:#fbfcfe}summary{cursor:pointer;padding:13px 15px;font-weight:750}pre{white-space:pre-wrap;word-break:break-word;margin:0;border-top:1px solid var(--line);padding:16px;background:#111827;color:#d9e2f2;border-radius:0 0 10px 10px;font:12px/1.55 Consolas,"Microsoft YaHei UI",monospace;max-height:680px;overflow:auto}.footer{text-align:center;color:var(--muted);font-size:12px;margin-top:24px}@media(max-width:900px){.grid{grid-template-columns:repeat(2,1fr)}.meta{grid-template-columns:1fr 1fr}}@media(max-width:560px){.wrap{padding:12px 9px 36px}.hero{padding:20px 18px;border-radius:14px}h1{font-size:22px}.grid,.meta{grid-template-columns:1fr}.card{padding:15px}}@media print{body{background:#fff}.wrap{max-width:none;padding:0}.hero,.card,.metric{box-shadow:none}.toolbar,details{display:none}.table-wrap{overflow:visible}table{min-width:0}th{position:static}}
</style>
""");
        builder.AppendLine("</head><body><main class=\"wrap\">");
        builder.AppendLine("<section class=\"hero\"><div class=\"hero-row\"><div>");
        builder.AppendLine("<h1>利亚方舟海螺云网络诊断分析报告</h1>");
        builder.AppendLine($"<p>{Html(SafeUserValue(options.StoreName))} · {Html(SafeUserValue(options.Carrier))} · {endedAt:yyyy-MM-dd HH:mm:ss zzz}</p></div>");
        builder.AppendLine($"<span class=\"badge\">{Html(overall.Label)}</span></div>");
        builder.AppendLine($"<p style=\"margin-top:14px\">{Html(overall.Summary)}</p>");
        builder.AppendLine("<div class=\"toolbar\"><button class=\"button\" onclick=\"window.print()\">打印 / 保存为 PDF</button></div></section>");

        builder.AppendLine("<section class=\"grid\">");
        AppendMetric(builder, healthyCount.ToString(), "正常域名", "ok");
        AppendMetric(builder, warningCount.ToString(), "警告域名", warningCount > 0 ? "warn" : "muted");
        AppendMetric(builder, failedCount.ToString(), "失败域名", failedCount > 0 ? "bad" : "muted");
        AppendMetric(builder, monitoring?.Events.Count.ToString() ?? "0", "变化事件", monitoring?.Events.Count > 0 ? "warn" : "info");
        builder.AppendLine("</section>");

        builder.AppendLine("<section class=\"card\"><h2>检测信息</h2><div class=\"meta\">");
        AppendMeta(builder, "检测模式", options.MonitorDuration > TimeSpan.Zero
            ? $"连续监测 {FormatDuration(options.MonitorDuration)}，间隔 {options.MonitorInterval.TotalSeconds:F0} 秒"
            : "快速诊断一次");
        AppendMeta(builder, "开始时间", startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendMeta(builder, "结束时间", endedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendMeta(builder, "耗时", FormatDuration(duration));
        AppendMeta(builder, "工具版本", assemblyVersion);
        AppendMeta(builder, "系统 / 进程架构", $"{RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}");
        AppendMeta(builder, "设备名称", Environment.MachineName);
        AppendMeta(builder, "DNS服务器", JoinOrDash(networkSnapshot.DnsServers.Select(item => item.ToString())));
        AppendMeta(builder, "默认网关", JoinOrDash(networkSnapshot.Gateways.Select(item => item.ToString())));
        builder.AppendLine("</div></section>");

        builder.AppendLine("<section class=\"card\"><h2>自动故障归因</h2><ol class=\"analysis\">");
        foreach (var reason in analysisReasons)
        {
            builder.AppendLine($"<li>{Html(reason)}</li>");
        }
        builder.AppendLine("</ol><div class=\"hint\">自动判断用于缩小排查范围，最终结论应结合故障时段的服务端、CDN/WAF及运营商日志。API 根路径返回 5xx 仅代表网络链路已到达服务端，在本工具中按网络连通正常处理。</div></section>");

        AppendGatewaySection(builder, lanBaseline);
        AppendTargetSection(builder, results);
        AppendMonitoringSection(builder, monitoring);
        AppendArtifactSection(builder, logPath, monitoring?.CsvPath);

        builder.AppendLine("<section class=\"card\"><h2>完整原始诊断日志</h2>");
        builder.AppendLine("<details><summary>展开查看 TXT 原始证据</summary>");
        builder.AppendLine($"<pre>{Html(rawLog)}</pre></details></section>");
        builder.AppendLine($"<div class=\"footer\">报告由利亚方舟海螺云网络诊断工具 {Html(assemblyVersion)} 生成 · 本报告不包含账号、密码、Cookie或业务内容</div>");
        builder.AppendLine("</main></body></html>");
        return builder.ToString();
    }

    private static void AppendGatewaySection(StringBuilder builder, LanBaselineResult lanBaseline)
    {
        builder.AppendLine("<section class=\"card\"><h2>门店局域网 / 默认网关</h2>");
        if (lanBaseline.Gateways.Count == 0)
        {
            builder.AppendLine("<p><span class=\"badge warn\">未发现默认网关</span> 可能是网络未连接、VPN或接口配置异常。</p></section>");
            return;
        }

        builder.AppendLine("<div class=\"table-wrap\"><table><thead><tr><th>网关</th><th>状态</th><th>收到 / 发送</th><th>丢包率</th><th>平均延迟</th></tr></thead><tbody>");
        foreach (var gateway in lanBaseline.Gateways)
        {
            var css = gateway.Received == 0 ? "bad" : gateway.LossPercent >= 20 ? "warn" : "ok";
            var label = gateway.Received == 0 ? "无响应" : gateway.LossPercent >= 20 ? "丢包偏高" : "正常";
            builder.AppendLine("<tr>" +
                               $"<td class=\"host\">{Html(gateway.Address.ToString())}</td>" +
                               $"<td><span class=\"badge {css}\">{label}</span></td>" +
                               $"<td>{gateway.Received} / {gateway.Sent}</td>" +
                               $"<td>{gateway.LossPercent:F0}%</td>" +
                               $"<td>{(gateway.AverageMilliseconds is null ? "-" : $"{gateway.AverageMilliseconds:F1} ms")}</td></tr>");
        }

        builder.AppendLine("</tbody></table></div></section>");
    }

    private static void AppendTargetSection(StringBuilder builder, IReadOnlyList<TargetResult> results)
    {
        builder.AppendLine("<section class=\"card\"><h2>域名检测结果</h2>");
        if (results.Count == 0)
        {
            builder.AppendLine("<p><span class=\"badge warn\">没有完整结果</span> 检测可能在域名测试开始前被停止。</p></section>");
            return;
        }

        builder.AppendLine("<div class=\"table-wrap\"><table><thead><tr><th>域名 / 用途</th><th>结论</th><th>系统DNS</th><th>指定DNS</th><th>TCP 443</th><th>TLS</th><th>HTTP</th><th>健康对照</th></tr></thead><tbody>");
        foreach (var result in results)
        {
            var status = GetHtmlTargetStatus(result);
            var systemAddresses = result.SystemAddresses.Count == 0
                ? "-"
                : string.Join(", ", result.SystemAddresses);
            var rawAddresses = result.RawDnsAddresses.Count == 0
                ? "-"
                : string.Join(", ", result.RawDnsAddresses);
            var httpLabel = result.AnyHttpResponse
                ? result.HasServerError
                    ? result.Target.TreatServerErrorAsNormal ? "已响应（5xx按网络正常）" : "已响应（服务5xx）"
                    : "已收到响应"
                : "未收到响应";
            var httpCss = result.AnyHttpResponse && (!result.HasServerError || result.Target.TreatServerErrorAsNormal)
                ? "ok"
                : "warn";
            var comparison = result.Target.ComparisonAddress is null
                ? "不适用"
                : result.ComparisonSucceeded ? "正常" : "失败 / 未完成";
            var comparisonCss = result.Target.ComparisonAddress is null ? "muted" : result.ComparisonSucceeded ? "ok" : "warn";
            builder.AppendLine("<tr>" +
                               $"<td><span class=\"host\">{Html(result.Target.Host)}</span><span class=\"sub\">{Html(result.Target.Category)}</span></td>" +
                               $"<td><span class=\"badge {status.CssClass}\">{Html(status.Label)}</span><span class=\"sub\">{Html(result.Conclusion)}</span></td>" +
                               $"<td><span class=\"badge {(result.SystemDnsSucceeded ? "ok" : "bad")}\">{(result.SystemDnsSucceeded ? "成功" : "失败")}</span><span class=\"sub\">{Html(systemAddresses)}</span></td>" +
                               $"<td><span class=\"badge {(result.AnyRawDnsSucceeded ? "ok" : "warn")}\">{(result.AnyRawDnsSucceeded ? "有响应" : "无响应")}</span><span class=\"sub\">{Html(rawAddresses)}</span></td>" +
                               $"<td><span class=\"badge {(result.AnyTcpSucceeded ? "ok" : "bad")}\">{(result.AnyTcpSucceeded ? "成功" : "失败")}</span></td>" +
                               $"<td><span class=\"badge {(result.AnyTlsSucceeded ? "ok" : "bad")}\">{(result.AnyTlsSucceeded ? "成功" : "失败")}</span></td>" +
                               $"<td><span class=\"badge {httpCss}\">{Html(httpLabel)}</span></td>" +
                               $"<td><span class=\"badge {comparisonCss}\">{Html(comparison)}</span></td></tr>");
        }

        builder.AppendLine("</tbody></table></div></section>");
    }

    private static void AppendMonitoringSection(StringBuilder builder, MonitoringResult? monitoring)
    {
        if (monitoring is null)
        {
            return;
        }

        builder.AppendLine("<section class=\"card\"><h2>连续监测分析</h2>");
        builder.AppendLine($"<p>完成 <strong>{monitoring.Rounds}</strong> 轮采样，发现 <strong>{monitoring.Events.Count}</strong> 个DNS或状态变化事件。</p>");
        builder.AppendLine("<div class=\"table-wrap\"><table><thead><tr><th>检测对象</th><th>样本</th><th>异常</th><th>DNS失败</th><th>TCP失败</th><th>TLS失败</th><th>HTTP异常</th><th>IP / 状态变化</th><th>TCP平均 / 最大</th></tr></thead><tbody>");
        foreach (var item in monitoring.Aggregates.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var aggregate = item.Value;
            var css = aggregate.Failures == 0 ? "ok" : aggregate.Failures == aggregate.Samples ? "bad" : "warn";
            var average = aggregate.TcpMillisecondsCount == 0
                ? "-"
                : $"{aggregate.TcpMillisecondsTotal / aggregate.TcpMillisecondsCount:F1} ms";
            builder.AppendLine("<tr>" +
                               $"<td class=\"host\">{Html(item.Key)}</td><td>{aggregate.Samples}</td>" +
                               $"<td><span class=\"badge {css}\">{aggregate.Failures}</span></td>" +
                               $"<td>{aggregate.DnsFailures}</td><td>{aggregate.TcpFailures}</td><td>{aggregate.TlsFailures}</td><td>{aggregate.HttpFailures}</td>" +
                               $"<td>{aggregate.IpChanges} / {aggregate.StateChanges}</td><td>{average} / {aggregate.TcpMillisecondsMaximum:F1} ms</td></tr>");
        }

        builder.AppendLine("</tbody></table></div>");
        if (monitoring.Events.Count > 0)
        {
            builder.AppendLine("<h3>变化事件</h3><div class=\"table-wrap\"><table><thead><tr><th>时间</th><th>对象</th><th>事件</th><th>变化前</th><th>变化后</th></tr></thead><tbody>");
            foreach (var item in monitoring.Events.Take(100))
            {
                builder.AppendLine($"<tr><td>{item.Timestamp:yyyy-MM-dd HH:mm:ss zzz}</td><td class=\"host\">{Html(item.Host)}</td><td>{Html(item.EventType)}</td><td>{Html(EmptyAsDash(item.Before))}</td><td>{Html(EmptyAsDash(item.After))}</td></tr>");
            }

            builder.AppendLine("</tbody></table></div>");
        }

        builder.AppendLine("</section>");
    }

    private static void AppendArtifactSection(StringBuilder builder, string logPath, string? csvPath)
    {
        builder.AppendLine("<section class=\"card\"><h2>相关诊断文件</h2><div class=\"files\">");
        AppendFileLink(builder, logPath, "TXT 原始日志");
        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            AppendFileLink(builder, csvPath, "CSV 时间序列");
        }

        builder.AppendLine("</div><p class=\"sub\">发送给技术人员时，建议将 HTML、TXT 以及连续监测产生的 CSV 一并发送。</p></section>");
    }

    private static void AppendFileLink(StringBuilder builder, string path, string label)
    {
        var fileName = Path.GetFileName(path);
        builder.AppendLine($"<a class=\"file\" href=\"{Html(Uri.EscapeDataString(fileName))}\">{Html(label)}<span class=\"sub\">{Html(fileName)}</span></a>");
    }

    private static void AppendMetric(StringBuilder builder, string value, string label, string cssClass) =>
        builder.AppendLine($"<div class=\"metric\"><div class=\"value {cssClass}\" style=\"background:none\">{Html(value)}</div><div class=\"label\">{Html(label)}</div></div>");

    private static void AppendMeta(StringBuilder builder, string label, string value) =>
        builder.AppendLine($"<div><strong>{Html(label)}</strong><span>{Html(value)}</span></div>");

    private static HtmlReportStatus GetHtmlTargetStatus(TargetResult result)
    {
        if (result.ResolvedHistoricalBadEdge)
        {
            return new HtmlReportStatus("bad", "危险", "命中历史异常CDN节点");
        }

        if (!result.SystemDnsSucceeded || !result.AnyTcpSucceeded || !result.AnyTlsSucceeded)
        {
            return new HtmlReportStatus("bad", "失败", result.Conclusion);
        }

        if (!result.AnyHttpResponse || result.HasServerError && !result.Target.TreatServerErrorAsNormal)
        {
            return new HtmlReportStatus("warn", "警告", result.Conclusion);
        }

        return new HtmlReportStatus("ok", "正常", result.Conclusion);
    }

    private static HtmlReportStatus GetOverallHtmlStatus(
        bool cancelled,
        int failedCount,
        int warningCount,
        int monitoringFailures)
    {
        if (cancelled)
        {
            return new HtmlReportStatus("warn", "检测未完整完成", "检测被提前停止，报告包含停止前已经采集的全部证据。");
        }

        if (failedCount > 0)
        {
            return new HtmlReportStatus("bad", "发现网络链路异常", $"有 {failedCount} 个域名出现 DNS、TCP、TLS 异常或命中历史异常节点，请优先查看自动故障归因。");
        }

        if (warningCount > 0 || monitoringFailures > 0)
        {
            return new HtmlReportStatus("warn", "网络可达但存在告警", "基础网络链路总体可达，但检测到HTTP告警或连续监测异常采样，需要结合服务端及CDN日志确认。");
        }

        return new HtmlReportStatus("ok", "本次检测链路正常", "本次未发现 DNS、TCP 443、TLS 或 HTTP 连通性异常；若问题为间歇性，请在故障期间运行连续检测。");
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record HtmlReportStatus(string CssClass, string Label, string Summary);
}
