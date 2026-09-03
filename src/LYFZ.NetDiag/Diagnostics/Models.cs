using System.Net;

namespace LYFZ.NetDiag.Diagnostics;

internal sealed record DiagnosticTarget(
    string Host,
    string Category,
    IPAddress? ComparisonAddress,
    bool IsFirstParty,
    string Notes = "",
    bool TreatServerErrorAsNormal = false);

internal sealed record DiagnosticRunOptions(
    string OutputDirectory,
    string StoreName,
    string Carrier,
    bool OpenFolderWhenComplete,
    TimeSpan MonitorDuration,
    TimeSpan MonitorInterval);

internal sealed record DiagnosticProgress(
    string Message,
    int Completed,
    int Total);

internal sealed record DiagnosticRunResult(
    string LogPath,
    string? HtmlReportPath,
    string? TimelineCsvPath,
    IReadOnlyList<TargetResult> Targets,
    bool Cancelled);

internal sealed record NetworkInterfaceCounter(
    string Name,
    long BytesReceived,
    long BytesSent,
    long IncomingErrors,
    long OutgoingErrors,
    long IncomingDiscards,
    long OutgoingDiscards);

internal sealed record NetworkSnapshot(
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyDictionary<string, NetworkInterfaceCounter> InterfaceCounters);

internal sealed record GatewayBaseline(
    IPAddress Address,
    int Sent,
    int Received,
    double? AverageMilliseconds,
    double LossPercent);

internal sealed record LanBaselineResult(IReadOnlyList<GatewayBaseline> Gateways)
{
    public bool HasGateway => Gateways.Count > 0;
    public bool AnyGatewayReachable => Gateways.Any(item => item.Received > 0);
    public double MaximumLossPercent => Gateways.Count == 0 ? 0 : Gateways.Max(item => item.LossPercent);
}

internal sealed class TargetResult
{
    public required DiagnosticTarget Target { get; init; }
    public List<IPAddress> SystemAddresses { get; } = [];
    public bool SystemDnsSucceeded { get; set; }
    public bool AnyRawDnsSucceeded { get; set; }
    public List<IPAddress> RawDnsAddresses { get; } = [];
    public bool AnyTcpSucceeded { get; set; }
    public bool AnyTlsSucceeded { get; set; }
    public bool AnyHttpResponse { get; set; }
    public bool SystemHttpSucceeded { get; set; }
    public bool CurrentDirectHttpSucceeded { get; set; }
    public bool HasServerError { get; set; }
    public bool ComparisonSucceeded { get; set; }
    public bool ResolvedHistoricalBadEdge { get; set; }
    public string? FailureReason { get; set; }

    public string Conclusion
    {
        get
        {
            if (ResolvedHistoricalBadEdge)
            {
                return "危险：命中历史异常CDN节点";
            }

            if (!SystemDnsSucceeded)
            {
                return "失败：系统DNS解析失败";
            }

            if (!AnyTcpSucceeded)
            {
                return ComparisonSucceeded
                    ? "失败：当前节点不可达，对照节点正常"
                    : "失败：TCP 443不可达";
            }

            if (!AnyTlsSucceeded)
            {
                return "失败：TLS握手异常";
            }

            if (!AnyHttpResponse)
            {
                return "警告：HTTP请求未收到响应";
            }

            if (HasServerError)
            {
                return Target.TreatServerErrorAsNormal
                    ? "正常：网络可达（API根路径5xx按正常处理）"
                    : "警告：网络可达，但服务返回5xx";
            }

            return "正常：网络链路可达";
        }
    }
}

internal sealed record TcpTestResult(bool Success, TimeSpan Elapsed, string Detail);

internal sealed record TlsTestResult(
    bool Success,
    TimeSpan Elapsed,
    string Detail,
    string CertificateSummary);

internal sealed record HttpTestResult(
    bool ReceivedResponse,
    int? StatusCode,
    TimeSpan Elapsed,
    string Detail);

internal sealed record PingTestResult(
    int Sent,
    int Received,
    double? AverageMilliseconds,
    string Detail);

internal sealed record DnsAnswer(string Type, string Value, uint Ttl);

internal sealed record DnsQueryResult(
    IPAddress Server,
    bool Success,
    TimeSpan Elapsed,
    string Status,
    IReadOnlyList<DnsAnswer> Answers);

internal sealed record MonitorSample(
    DateTimeOffset Timestamp,
    string Kind,
    string Host,
    string Addresses,
    double DnsMilliseconds,
    bool DnsSucceeded,
    bool TcpSucceeded,
    double? TcpMilliseconds,
    bool TlsSucceeded,
    double? TlsMilliseconds,
    bool HttpResponded,
    int? HttpStatus,
    double? HttpMilliseconds,
    string State,
    string Detail);

internal sealed class MonitorAggregate
{
    public int Samples { get; set; }
    public int Failures { get; set; }
    public int DnsFailures { get; set; }
    public int TcpFailures { get; set; }
    public int TlsFailures { get; set; }
    public int HttpFailures { get; set; }
    public int IpChanges { get; set; }
    public int StateChanges { get; set; }
    public double TcpMillisecondsTotal { get; set; }
    public int TcpMillisecondsCount { get; set; }
    public double TcpMillisecondsMaximum { get; set; }
}

internal sealed record MonitorEvent(
    DateTimeOffset Timestamp,
    string Host,
    string EventType,
    string Before,
    string After);

internal sealed class MonitoringResult
{
    public string? CsvPath { get; set; }
    public int Rounds { get; set; }
    public bool Cancelled { get; set; }
    public Dictionary<string, MonitorAggregate> Aggregates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MonitorEvent> Events { get; } = [];
}
