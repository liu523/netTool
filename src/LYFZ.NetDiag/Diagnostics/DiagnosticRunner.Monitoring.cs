using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LYFZ.NetDiag.Diagnostics;

internal sealed partial class DiagnosticRunner
{
    private static async Task<LanBaselineResult> WriteLanBaselineAsync(
        DiagnosticLogger logger,
        IReadOnlyList<IPAddress> gateways,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\r\n========== 局域网/默认网关基线 ==========");
        var results = new List<GatewayBaseline>();

        foreach (var gateway in gateways.Take(4))
        {
            var ping = await TestPingManyAsync(gateway, 10, cancellationToken);
            var loss = ping.Sent == 0 ? 100 : (ping.Sent - ping.Received) * 100d / ping.Sent;
            results.Add(new GatewayBaseline(gateway, ping.Sent, ping.Received, ping.AverageMilliseconds, loss));
            builder.AppendLine(
                $"网关 {gateway}：收到 {ping.Received}/{ping.Sent}，丢包={loss:F0}%，" +
                $"平均={(ping.AverageMilliseconds is null ? "-" : $"{ping.AverageMilliseconds:F1} ms")}");
        }

        if (results.Count == 0)
        {
            builder.AppendLine("未发现默认网关，可能是接口配置、VPN或网络未连接。");
        }

        builder.AppendLine("说明：默认网关持续丢包通常指向门店Wi-Fi、网线、交换机或路由器；网关不响应ICMP时需结合其他结果判断。");
        await logger.WriteAsync(builder.ToString(), cancellationToken);
        return new LanBaselineResult(results);
    }

    private static async Task<MonitoringResult> RunContinuousMonitoringAsync(
        DiagnosticLogger logger,
        NetworkSnapshot networkSnapshot,
        DiagnosticRunOptions options,
        CancellationToken cancellationToken)
    {
        var result = new MonitoringResult
        {
            CsvPath = Path.ChangeExtension(logger.Path, ".csv")
        };
        var interval = options.MonitorInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.MonitorInterval;
        var start = DateTimeOffset.Now;
        var end = start + options.MonitorDuration;
        var previous = new Dictionary<string, MonitorPreviousState>(StringComparer.OrdinalIgnoreCase);

        await logger.WriteAsync(
            $"\r\n========== 连续监测开始 ==========\r\n" +
            $"开始：{start:yyyy-MM-dd HH:mm:ss zzz}\r\n" +
            $"计划结束：{end:yyyy-MM-dd HH:mm:ss zzz}\r\n" +
            $"采样间隔：自有域名 {interval.TotalSeconds:F0} 秒；第三方域名 60 秒\r\n" +
            $"CSV时间序列：{result.CsvPath}\r\n",
            CancellationToken.None);

        await using var csvStream = new FileStream(
            result.CsvPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await using var csv = new StreamWriter(csvStream, new UTF8Encoding(true)) { AutoFlush = true };
        await csv.WriteLineAsync(
            "timestamp,kind,host,addresses,dns_ms,dns_ok,tcp_ok,tcp_ms,tls_ok,tls_ms,http_ok,http_status,http_ms,state,detail");

        try
        {
            var round = 0;
            while (DateTimeOffset.Now < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                round++;
                result.Rounds = round;
                var roundStarted = DateTimeOffset.Now;
                var elapsed = roundStarted - start;
                var totalSeconds = Math.Max(1, (int)Math.Ceiling(options.MonitorDuration.TotalSeconds));
                var completedSeconds = Math.Clamp((int)elapsed.TotalSeconds, 0, totalSeconds);
                logger.Report(
                    $"连续监测第 {round} 轮，已运行 {FormatDuration(elapsed)}",
                    completedSeconds,
                    totalSeconds);

                var includeThirdParty = round == 1 || elapsed == TimeSpan.Zero ||
                                        elapsed.TotalSeconds / 60 >= Math.Floor((elapsed - interval).TotalSeconds / 60) + 1;
                var targets = DomainCatalog.All
                    .Where(target => target.IsFirstParty || includeThirdParty)
                    .ToArray();

                using var concurrency = new SemaphoreSlim(4, 4);
                var tasks = targets.Select(async target =>
                {
                    await concurrency.WaitAsync(cancellationToken);
                    try
                    {
                        return await SampleTargetAsync(target, cancellationToken);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }).ToArray();
                var samples = (await Task.WhenAll(tasks)).ToList();

                foreach (var gateway in networkSnapshot.Gateways.Take(4))
                {
                    samples.Add(await SampleGatewayAsync(gateway, cancellationToken));
                }

                if (includeThirdParty)
                {
                    samples.Add(await SamplePublicIpAsync(cancellationToken));
                }

                var deepChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var roundText = new StringBuilder();
                roundText.AppendLine(
                    $"\r\n---- 连续监测第 {round} 轮 | {roundStarted:yyyy-MM-dd HH:mm:ss zzz} ----");
                foreach (var sample in samples.OrderBy(item => item.Kind).ThenBy(item => item.Host, StringComparer.OrdinalIgnoreCase))
                {
                    await csv.WriteLineAsync(ToCsv(sample));
                    UpdateAggregate(result, sample);
                    var display =
                        $"[{sample.Kind}] {sample.Host} | IP={EmptyAsDash(sample.Addresses)} | " +
                        $"TCP={BoolText(sample.TcpSucceeded)}" +
                        (sample.TcpMilliseconds is null ? string.Empty : $"/{sample.TcpMilliseconds:F0}ms") +
                        $" | TLS={BoolText(sample.TlsSucceeded)} | HTTP={(sample.HttpStatus?.ToString() ?? "-")} | {sample.State}";
                    roundText.AppendLine(display);

                    var key = $"{sample.Kind}:{sample.Host}";
                    if (previous.TryGetValue(key, out var before))
                    {
                        if (!string.Equals(before.Addresses, sample.Addresses, StringComparison.OrdinalIgnoreCase))
                        {
                            AddMonitorEvent(result, sample.Timestamp, sample.Host, "DNS_IP_CHANGED", before.Addresses, sample.Addresses);
                            roundText.AppendLine($"  事件：DNS IP变化 {EmptyAsDash(before.Addresses)} -> {EmptyAsDash(sample.Addresses)}");
                            if (result.Aggregates.TryGetValue(sample.Host, out var aggregate))
                            {
                                aggregate.IpChanges++;
                            }
                        }

                        if (!string.Equals(before.State, sample.State, StringComparison.Ordinal))
                        {
                            AddMonitorEvent(result, sample.Timestamp, sample.Host, "STATE_CHANGED", before.State, sample.State);
                            roundText.AppendLine($"  事件：状态变化 {before.State} -> {sample.State}");
                            if (result.Aggregates.TryGetValue(sample.Host, out var aggregate))
                            {
                                aggregate.StateChanges++;
                            }

                            if (sample.Kind == "DOMAIN")
                            {
                                deepChecks.Add(sample.Host);
                            }
                        }
                    }

                    previous[key] = new MonitorPreviousState(sample.Addresses, sample.State);
                }

                await logger.WriteAsync(roundText.ToString(), CancellationToken.None);

                foreach (var host in deepChecks.Take(4))
                {
                    var target = DomainCatalog.All.First(item =>
                        string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase));
                    await logger.WriteAsync(
                        $"监测到 {host} 状态变化，自动执行完整深度复测。",
                        CancellationToken.None);
                    var output = await DiagnoseTargetAsync(target, networkSnapshot.DnsServers, cancellationToken);
                    await logger.WriteAsync(output.Text, CancellationToken.None);
                }

                var nextRound = DateTimeOffset.Now + interval;
                var remaining = end - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = nextRound - DateTimeOffset.Now;
                if (delay > remaining)
                {
                    delay = remaining;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            await logger.WriteAsync("连续监测被用户停止，CSV和TXT已保留停止前的全部采样。", CancellationToken.None);
        }

        await logger.WriteAsync(
            $"\r\n========== 连续监测结束 ==========\r\n" +
            $"完成轮数：{result.Rounds}\r\n" +
            $"状态/DNS变化事件：{result.Events.Count}\r\n" +
            $"时间序列：{result.CsvPath}\r\n",
            CancellationToken.None);
        return result;
    }

    private static async Task<MonitorSample> SampleTargetAsync(
        DiagnosticTarget target,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        var dnsStopwatch = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(DnsTimeout);
            addresses = await Dns.GetHostAddressesAsync(target.Host, dnsCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            addresses = [];
        }
        catch (SocketException)
        {
            addresses = [];
        }

        dnsStopwatch.Stop();
        var distinctAddresses = addresses.Distinct().ToArray();
        var addressText = string.Join(';', distinctAddresses.Select(item => item.ToString()));
        if (distinctAddresses.Length == 0)
        {
            return new MonitorSample(
                timestamp, "DOMAIN", target.Host, addressText, dnsStopwatch.Elapsed.TotalMilliseconds,
                false, false, null, false, null, false, null, null,
                "DNS_FAIL", "系统DNS未返回地址");
        }

        var address = distinctAddresses
            .OrderBy(item => item.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .First();
        var historicalBad = distinctAddresses.Contains(DomainCatalog.HistoricalBadEdge);
        var tcp = await TestTcpAsync(address, 443, cancellationToken);
        if (!tcp.Success)
        {
            return new MonitorSample(
                timestamp, "DOMAIN", target.Host, addressText, dnsStopwatch.Elapsed.TotalMilliseconds,
                true, false, tcp.Elapsed.TotalMilliseconds, false, null, false, null, null,
                historicalBad ? "HISTORICAL_BAD_EDGE" : "TCP_FAIL", tcp.Detail);
        }

        var tls = await TestTlsAsync(target.Host, address, cancellationToken);
        if (!tls.Success)
        {
            return new MonitorSample(
                timestamp, "DOMAIN", target.Host, addressText, dnsStopwatch.Elapsed.TotalMilliseconds,
                true, true, tcp.Elapsed.TotalMilliseconds, false, tls.Elapsed.TotalMilliseconds,
                false, null, null, historicalBad ? "HISTORICAL_BAD_EDGE" : "TLS_FAIL", tls.Detail);
        }

        var http = await TestHttpByAddressAsync(target.Host, address, cancellationToken);
        var state = historicalBad
            ? "HISTORICAL_BAD_EDGE"
            : !http.ReceivedResponse
                ? "HTTP_FAIL"
                : http.StatusCode is >= 500 && !target.TreatServerErrorAsNormal
                    ? "APP_5XX"
                    : "OK";
        return new MonitorSample(
            timestamp, "DOMAIN", target.Host, addressText, dnsStopwatch.Elapsed.TotalMilliseconds,
            true, true, tcp.Elapsed.TotalMilliseconds, true, tls.Elapsed.TotalMilliseconds,
            http.ReceivedResponse, http.StatusCode, http.Elapsed.TotalMilliseconds, state, http.Detail);
    }

    private static async Task<MonitorSample> SampleGatewayAsync(
        IPAddress gateway,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var reply = await SendPingAsync(gateway, null, cancellationToken);
        stopwatch.Stop();
        var success = reply?.Status == IPStatus.Success;
        return new MonitorSample(
            timestamp, "GATEWAY", gateway.ToString(), gateway.ToString(), 0, true,
            success, success ? reply!.RoundtripTime : stopwatch.Elapsed.TotalMilliseconds,
            success, null, success, null, null,
            success ? "OK" : "GATEWAY_NO_REPLY",
            reply is null ? "Ping执行失败" : reply.Status.ToString());
    }

    private static async Task<MonitorSample> SamplePublicIpAsync(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                AllowAutoRedirect = false,
                ConnectTimeout = ConnectTimeout
            };
            using var client = new HttpClient(handler) { Timeout = HttpTimeout };
            var text = (await client.GetStringAsync("https://api64.ipify.org/", cancellationToken)).Trim();
            stopwatch.Stop();
            var valid = IPAddress.TryParse(text, out _);
            return new MonitorSample(
                timestamp, "PUBLIC_IP", "public-ip", valid ? text : string.Empty,
                0, true, valid, stopwatch.Elapsed.TotalMilliseconds,
                valid, null, valid, valid ? 200 : null, stopwatch.Elapsed.TotalMilliseconds,
                valid ? "OK" : "PUBLIC_IP_FAIL",
                valid ? $"公网出口IP={text}" : "公网出口IP响应无法识别");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            stopwatch.Stop();
            return new MonitorSample(
                timestamp, "PUBLIC_IP", "public-ip", string.Empty,
                0, false, false, stopwatch.Elapsed.TotalMilliseconds,
                false, null, false, null, stopwatch.Elapsed.TotalMilliseconds,
                "PUBLIC_IP_FAIL", OneLine(ex.Message));
        }
    }

    private static async Task<PingTestResult> TestPingManyAsync(
        IPAddress address,
        int count,
        CancellationToken cancellationToken)
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, count)
            .Select(_ => SendPingAsync(address, null, cancellationToken)));
        var successful = replies.Where(reply => reply is { Status: IPStatus.Success }).ToArray();
        var average = successful.Length == 0 ? (double?)null : successful.Average(reply => reply!.RoundtripTime);
        return new PingTestResult(count, successful.Length, average, string.Empty);
    }

    private static void UpdateAggregate(MonitoringResult monitoring, MonitorSample sample)
    {
        if (!monitoring.Aggregates.TryGetValue(sample.Host, out var aggregate))
        {
            aggregate = new MonitorAggregate();
            monitoring.Aggregates[sample.Host] = aggregate;
        }

        aggregate.Samples++;
        if (sample.State != "OK")
        {
            aggregate.Failures++;
        }

        if (sample.State == "DNS_FAIL") aggregate.DnsFailures++;
        if (sample.State is "TCP_FAIL" or "HISTORICAL_BAD_EDGE" or "GATEWAY_NO_REPLY") aggregate.TcpFailures++;
        if (sample.State == "TLS_FAIL") aggregate.TlsFailures++;
        if (sample.State is "HTTP_FAIL" or "APP_5XX") aggregate.HttpFailures++;
        if (sample.TcpMilliseconds is { } tcpMilliseconds && sample.TcpSucceeded)
        {
            aggregate.TcpMillisecondsTotal += tcpMilliseconds;
            aggregate.TcpMillisecondsCount++;
            aggregate.TcpMillisecondsMaximum = Math.Max(aggregate.TcpMillisecondsMaximum, tcpMilliseconds);
        }
    }

    private static void AddMonitorEvent(
        MonitoringResult result,
        DateTimeOffset timestamp,
        string host,
        string eventType,
        string before,
        string after) =>
        result.Events.Add(new MonitorEvent(timestamp, host, eventType, before, after));

    private static async Task WriteNetworkCounterDeltaAsync(
        DiagnosticLogger logger,
        IReadOnlyDictionary<string, NetworkInterfaceCounter> initial,
        CancellationToken cancellationToken)
    {
        if (initial.Count == 0)
        {
            return;
        }

        var current = CaptureInterfaceCounters();
        var builder = new StringBuilder();
        builder.AppendLine("\r\n========== 网卡统计变化 ==========");
        foreach (var pair in initial)
        {
            if (!current.TryGetValue(pair.Key, out var after))
            {
                builder.AppendLine($"接口 {pair.Value.Name}：检测结束时已不可用");
                continue;
            }

            var before = pair.Value;
            builder.AppendLine(
                $"接口 {before.Name}：接收={FormatBytes(NonNegativeDelta(after.BytesReceived, before.BytesReceived))}，" +
                $"发送={FormatBytes(NonNegativeDelta(after.BytesSent, before.BytesSent))}，" +
                $"接收错误={NonNegativeDelta(after.IncomingErrors, before.IncomingErrors)}，" +
                $"发送错误={NonNegativeDelta(after.OutgoingErrors, before.OutgoingErrors)}，" +
                $"接收丢弃={NonNegativeDelta(after.IncomingDiscards, before.IncomingDiscards)}，" +
                $"发送丢弃={NonNegativeDelta(after.OutgoingDiscards, before.OutgoingDiscards)}");
        }

        await logger.WriteAsync(builder.ToString(), cancellationToken);
    }

    private static IReadOnlyDictionary<string, NetworkInterfaceCounter> CaptureInterfaceCounters()
    {
        var counters = new Dictionary<string, NetworkInterfaceCounter>(StringComparer.OrdinalIgnoreCase);
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var statistics = networkInterface.GetIPStatistics();
                counters[networkInterface.Id] = new NetworkInterfaceCounter(
                    networkInterface.Name,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    statistics.IncomingPacketsWithErrors,
                    statistics.OutgoingPacketsWithErrors,
                    statistics.IncomingPacketsDiscarded,
                    statistics.OutgoingPacketsDiscarded);
            }
            catch
            {
                // Ignore interfaces that cannot expose statistics.
            }
        }

        return counters;
    }

    private static void AppendAutomaticAnalysis(
        StringBuilder builder,
        IReadOnlyList<TargetResult> results,
        LanBaselineResult lanBaseline,
        MonitoringResult? monitoring)
    {
        builder.AppendLine("\r\n========== 自动故障归因 ==========");
        foreach (var reason in BuildAutomaticAnalysisReasons(results, lanBaseline, monitoring))
        {
            builder.AppendLine($"- {reason}");
        }
    }

    private static IReadOnlyList<string> BuildAutomaticAnalysisReasons(
        IReadOnlyList<TargetResult> results,
        LanBaselineResult lanBaseline,
        MonitoringResult? monitoring)
    {
        var reasons = new List<string>();
        var badGateway = lanBaseline.HasGateway &&
                         (!lanBaseline.AnyGatewayReachable || lanBaseline.MaximumLossPercent >= 50);
        var externalLayerFailures = results.Count(item =>
            !item.SystemDnsSucceeded || !item.AnyTcpSucceeded || !item.AnyTlsSucceeded);
        var historicalBad = results.Where(item => item.ResolvedHistoricalBadEdge).ToList();
        var currentNodeFailedButComparisonWorked = results
            .Where(item => item.SystemDnsSucceeded && !item.AnyTcpSucceeded && item.ComparisonSucceeded)
            .ToList();
        var localDnsIssue = results
            .Where(item => !item.SystemDnsSucceeded && item.AnyRawDnsSucceeded)
            .ToList();
        var proxyIssue = results
            .Where(item => !item.SystemHttpSucceeded && item.CurrentDirectHttpSucceeded)
            .ToList();
        var kunlunFailures = results
            .Where(item => item.Target.Category.Contains("昆仑", StringComparison.Ordinal))
            .Where(item => !item.AnyTcpSucceeded || !item.AnyTlsSucceeded)
            .ToList();
        var directHealthy = results
            .Where(item => item.Target.IsFirstParty && !item.Target.Category.Contains("昆仑", StringComparison.Ordinal))
            .Any(item => item.AnyTcpSucceeded && item.AnyTlsSucceeded);

        if (badGateway && externalLayerFailures >= 3)
        {
            reasons.Add("高可信：默认网关出现严重丢包或不可达，优先检查门店Wi-Fi、网线、交换机和路由器。");
        }
        else if (badGateway)
        {
            reasons.Add("提示：默认网关不响应或严重丢弃ICMP，但外部TCP/TLS大体正常；路由器可能仅禁用了Ping，不能单独据此判定局域网故障。");
        }

        if (historicalBad.Count > 0)
        {
            reasons.Add($"高可信：命中历史异常CDN节点 113.215.230.101，域名：{string.Join(", ", historicalBad.Select(item => item.Target.Host))}。");
        }

        if (currentNodeFailedButComparisonWorked.Count > 0)
        {
            reasons.Add($"高可信：当前调度节点不可达但健康对照节点正常，倾向CDN节点或运营商到该节点的路由问题：{string.Join(", ", currentNodeFailedButComparisonWorked.Select(item => item.Target.Host))}。");
        }

        if (kunlunFailures.Count > 0 && directHealthy)
        {
            reasons.Add($"较高可信：昆仑域名异常而直连业务域名正常，倾向昆仑CDN/WAF或其跨网路由问题：{string.Join(", ", kunlunFailures.Select(item => item.Target.Host))}。");
        }

        if (localDnsIssue.Count > 0)
        {
            reasons.Add($"较高可信：系统DNS失败但直接查询DNS服务器成功，倾向电脑DNS缓存、本地DNS代理或安全软件问题：{string.Join(", ", localDnsIssue.Select(item => item.Target.Host))}。");
        }

        if (proxyIssue.Count > 0)
        {
            reasons.Add($"较高可信：系统代理路径失败但指定节点直连正常，检查代理、VPN或终端安全软件：{string.Join(", ", proxyIssue.Select(item => item.Target.Host))}。");
        }

        if (monitoring is not null)
        {
            var gatewayFailures = monitoring.Aggregates
                .Where(item => IPAddress.TryParse(item.Key, out _) && item.Value.Failures > 0)
                .ToList();
            if (gatewayFailures.Count > 0)
            {
                reasons.Add("连续监测期间默认网关曾无响应；若同时伴随多个外部域名失败，优先定位门店局域网设备。");
            }

            var monitoredFailures = monitoring.Aggregates
                .Where(item => DomainCatalog.All.Any(target => string.Equals(target.Host, item.Key, StringComparison.OrdinalIgnoreCase)))
                .Where(item => item.Value.Failures > 0)
                .Select(item => $"{item.Key}({item.Value.Failures}/{item.Value.Samples})")
                .ToList();
            if (monitoredFailures.Count > 0)
            {
                reasons.Add($"连续监测捕获到异常采样：{string.Join(", ", monitoredFailures)}。");
            }
        }

        if (reasons.Count == 0)
        {
            reasons.Add("本次没有捕获到可明确归因的网络异常；若问题为间歇性，请使用连续监测并覆盖故障发生和恢复时段。");
        }

        return reasons;
    }

    private static void AppendMonitoringSummary(StringBuilder builder, MonitoringResult? monitoring)
    {
        if (monitoring is null)
        {
            return;
        }

        builder.AppendLine("\r\n========== 连续监测汇总 ==========");
        builder.AppendLine($"轮数：{monitoring.Rounds}；事件：{monitoring.Events.Count}；CSV：{monitoring.CsvPath}");
        foreach (var item in monitoring.Aggregates.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var aggregate = item.Value;
            var averageTcp = aggregate.TcpMillisecondsCount == 0
                ? "-"
                : $"{aggregate.TcpMillisecondsTotal / aggregate.TcpMillisecondsCount:F1} ms";
            builder.AppendLine(
                $"{item.Key,-29} 样本={aggregate.Samples}，异常={aggregate.Failures}，" +
                $"DNS失败={aggregate.DnsFailures}，TCP失败={aggregate.TcpFailures}，TLS失败={aggregate.TlsFailures}，" +
                $"HTTP异常={aggregate.HttpFailures}，IP变化={aggregate.IpChanges}，状态变化={aggregate.StateChanges}，" +
                $"连接/探测平均/最大={averageTcp}/{aggregate.TcpMillisecondsMaximum:F1} ms");
        }

        if (monitoring.Events.Count > 0)
        {
            builder.AppendLine("变化事件：");
            foreach (var item in monitoring.Events.Take(100))
            {
                builder.AppendLine(
                    $"  {item.Timestamp:yyyy-MM-dd HH:mm:ss zzz} {item.Host} {item.EventType}: " +
                    $"{EmptyAsDash(item.Before)} -> {EmptyAsDash(item.After)}");
            }
        }
    }

    private static string ToCsv(MonitorSample sample)
    {
        var values = new[]
        {
            sample.Timestamp.ToString("O"),
            sample.Kind,
            sample.Host,
            sample.Addresses,
            sample.DnsMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            sample.DnsSucceeded.ToString(),
            sample.TcpSucceeded.ToString(),
            sample.TcpMilliseconds?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
            sample.TlsSucceeded.ToString(),
            sample.TlsMilliseconds?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
            sample.HttpResponded.ToString(),
            sample.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.HttpMilliseconds?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
            sample.State,
            sample.Detail
        };
        return string.Join(',', values.Select(CsvEscape));
    }

    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static long NonNegativeDelta(long after, long before) => Math.Max(0, after - before);

    private static string FormatBytes(long value) => value >= 1024 * 1024
        ? $"{value / 1024d / 1024d:F1} MB"
        : value >= 1024
            ? $"{value / 1024d:F1} KB"
            : $"{value} B";

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}小时{value.Minutes}分{value.Seconds}秒"
            : $"{value.Minutes}分{value.Seconds}秒";

    private static string BoolText(bool value) => value ? "OK" : "FAIL";

    private static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record MonitorPreviousState(string Addresses, string State);
}
