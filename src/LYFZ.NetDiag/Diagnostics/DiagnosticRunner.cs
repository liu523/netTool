using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LYFZ.NetDiag.Diagnostics;

internal sealed partial class DiagnosticRunner
{
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RawDnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(9);

    public async Task<DiagnosticRunResult> RunAsync(
        DiagnosticRunOptions options,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var startedAt = DateTimeOffset.Now;
        var logPath = CreateLogPath(options.OutputDirectory, options.StoreName);
        var results = new ConcurrentDictionary<string, TargetResult>(StringComparer.OrdinalIgnoreCase);
        var cancelled = false;
        var networkSnapshot = new NetworkSnapshot([], [], new Dictionary<string, NetworkInterfaceCounter>());
        var lanBaseline = new LanBaselineResult([]);
        MonitoringResult? monitoringResult = null;
        string? htmlReportPath = null;
        var targets = DomainCatalog.BuildTargets(options.ExtraDomains);

        await using var logger = new DiagnosticLogger(logPath, progress);
        await logger.WriteAsync(BuildHeader(options, targets));

        try
        {
            networkSnapshot = await WriteNetworkSnapshotAsync(logger, cancellationToken);
            lanBaseline = await WriteLanBaselineAsync(logger, networkSnapshot.Gateways, cancellationToken);
            await WritePublicAddressAsync(logger, cancellationToken);

            var completed = 0;
            using var concurrency = new SemaphoreSlim(4, 4);
            var tasks = targets.Select(async target =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    logger.Report($"正在检测 {target.Host}", Volatile.Read(ref completed), targets.Count);
                    TargetDiagnosticOutput output;
                    try
                    {
                        output = await DiagnoseTargetAsync(target, networkSnapshot.DnsServers, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var failed = new TargetResult
                        {
                            Target = target,
                            FailureReason = OneLine(ex.Message)
                        };
                        output = new TargetDiagnosticOutput(
                            failed,
                            $"\r\n========== {target.Host} | {target.Category} ==========\r\n" +
                            $"诊断过程异常：{OneLine(ex.Message)}\r\n");
                    }

                    results[target.Host] = output.Result;
                    await logger.WriteAsync(output.Text, CancellationToken.None);
                    var value = Interlocked.Increment(ref completed);
                    logger.Report($"已完成 {target.Host}：{output.Result.Conclusion}", value, targets.Count);
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            if (options.MonitorDuration > TimeSpan.Zero)
            {
                monitoringResult = await RunContinuousMonitoringAsync(
                    logger,
                    networkSnapshot,
                    options,
                    targets,
                    cancellationToken);
                cancelled |= monitoringResult.Cancelled;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await logger.WriteAsync("\r\n检测已被用户停止，日志包含停止前已完成的项目。", CancellationToken.None);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync($"\r\n全局诊断异常：{OneLine(ex.ToString())}", CancellationToken.None);
        }

        try
        {
            await WriteNetworkCounterDeltaAsync(logger, networkSnapshot.InterfaceCounters, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync($"读取结束网卡统计失败：{OneLine(ex.Message)}", CancellationToken.None);
        }

        var orderedResults = targets
            .Where(target => results.ContainsKey(target.Host))
            .Select(target => results[target.Host])
            .ToList();
        await logger.WriteAsync(BuildSummary(orderedResults, cancelled, lanBaseline, monitoringResult), CancellationToken.None);
        var endedAt = DateTimeOffset.Now;
        try
        {
            var expectedHtmlPath = Path.ChangeExtension(logPath, ".html");
            await logger.WriteAsync($"HTML分析报告：{expectedHtmlPath}", CancellationToken.None);
            htmlReportPath = await WriteHtmlReportAsync(
                logPath,
                options,
                startedAt,
                endedAt,
                networkSnapshot,
                lanBaseline,
                orderedResults,
                monitoringResult,
                cancelled);
        }
        catch (Exception ex)
        {
            await logger.WriteAsync($"生成HTML分析报告失败：{OneLine(ex.Message)}", CancellationToken.None);
        }

        logger.Report(cancelled ? "检测已停止" : "检测完成", 1, 1);
        return new DiagnosticRunResult(logPath, htmlReportPath, monitoringResult?.CsvPath, orderedResults, cancelled);
    }

    private static string BuildHeader(
        DiagnosticRunOptions options,
        IReadOnlyList<DiagnosticTarget> targets)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName();
        var builder = new StringBuilder();
        builder.AppendLine("利亚方舟海螺云网络诊断日志");
        builder.AppendLine(new string('=', 72));
        builder.AppendLine($"开始时间（本地）：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"开始时间（UTC） ：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
        builder.AppendLine($"门店名称          ：{SafeUserValue(options.StoreName)}");
        builder.AppendLine($"宽带运营商        ：{SafeUserValue(options.Carrier)}");
        builder.AppendLine($"检测模式          ：{(options.MonitorDuration > TimeSpan.Zero ? $"连续监测 {FormatDuration(options.MonitorDuration)} / 间隔 {options.MonitorInterval.TotalSeconds:F0} 秒" : "快速诊断")}");
        builder.AppendLine($"诊断域名          ：共 {targets.Count} 个（默认 {DomainCatalog.All.Count} 个，额外 {targets.Count(target => target.IsExtra)} 个）");
        var extraHosts = targets.Where(target => target.IsExtra).Select(target => target.Host).ToArray();
        if (extraHosts.Length > 0)
        {
            builder.AppendLine($"额外诊断域名      ：{string.Join(", ", extraHosts)}");
        }
        builder.AppendLine($"工具版本          ：{assembly.Version}");
        builder.AppendLine($"操作系统          ：{RuntimeInformation.OSDescription}");
        builder.AppendLine($"系统架构/进程架构 ：{RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"设备名称          ：{Environment.MachineName}");
        builder.AppendLine($"时区              ：{TimeZoneInfo.Local.DisplayName}");
        builder.AppendLine();
        builder.AppendLine("说明：本工具不会提交账号、密码、Cookie或业务数据；HTTP仅请求各域名根路径并读取响应头。");
        builder.AppendLine("说明：Ping或路由星号可能只是设备不响应ICMP，必须结合TCP 443、TLS和HTTP结果判断。");
        return builder.ToString();
    }

    private static async Task<NetworkSnapshot> WriteNetworkSnapshotAsync(
        DiagnosticLogger logger,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\r\n========== 本机网络配置 ==========");
        var dnsServers = new List<IPAddress>();
        var gatewayAddresses = new List<IPAddress>();
        var counters = new Dictionary<string, NetworkInterfaceCounter>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (interfaces.Count == 0)
            {
                builder.AppendLine("未发现处于启用状态的物理/无线网络接口。");
            }

            foreach (var networkInterface in interfaces)
            {
                var properties = networkInterface.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Select(item => item.Address.ToString())
                    .ToArray();
                var interfaceGateways = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .Where(value => !value.Equals(IPAddress.Any) && !value.Equals(IPAddress.IPv6Any))
                    .ToArray();
                var servers = properties.DnsAddresses.ToArray();
                dnsServers.AddRange(servers);
                gatewayAddresses.AddRange(interfaceGateways);

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
                    // Some virtual adapters do not expose counters.
                }

                builder.AppendLine($"接口：{networkInterface.Name} | {networkInterface.Description}");
                builder.AppendLine($"  类型/速率：{networkInterface.NetworkInterfaceType} / {FormatSpeed(networkInterface.Speed)}");
                builder.AppendLine($"  地址：{JoinOrDash(addresses)}");
                builder.AppendLine($"  网关：{JoinOrDash(interfaceGateways.Select(item => item.ToString()))}");
                builder.AppendLine($"  DNS ：{JoinOrDash(servers.Select(item => item.ToString()))}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"读取网络接口失败：{OneLine(ex.Message)}");
        }

        try
        {
            var uri = new Uri("https://login.lyfz.net/");
            var proxy = HttpClient.DefaultProxy;
            var isBypassed = proxy.IsBypassed(uri);
            var proxyUri = proxy.GetProxy(uri);
            builder.AppendLine($"系统代理：{(isBypassed || proxyUri == uri ? "未使用" : SanitizeUri(proxyUri))}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"系统代理：读取失败（{OneLine(ex.Message)}）");
        }

        await logger.WriteAsync(builder.ToString(), cancellationToken);
        return new NetworkSnapshot(
            dnsServers.Distinct().ToArray(),
            gatewayAddresses.Distinct().ToArray(),
            counters);
    }

    private static async Task WritePublicAddressAsync(DiagnosticLogger logger, CancellationToken cancellationToken)
    {
        var services = new[]
        {
            new Uri("https://api64.ipify.org/"),
            new Uri("https://4.ipw.cn/")
        };
        var lines = new ConcurrentBag<string>();

        await Task.WhenAll(services.Select(async service =>
        {
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
                var text = (await client.GetStringAsync(service, cancellationToken)).Trim();
                stopwatch.Stop();
                lines.Add(IPAddress.TryParse(text, out _)
                    ? $"公网出口IP（{service.Host}）：{text}，{stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                    : $"公网出口IP（{service.Host}）：响应无法识别");
            }
            catch (Exception ex)
            {
                lines.Add($"公网出口IP（{service.Host}）：获取失败（{OneLine(ex.Message)}）");
            }
        }));

        var builder = new StringBuilder("\r\n========== 公网出口 ==========" + Environment.NewLine);
        foreach (var line in lines.OrderBy(value => value, StringComparer.Ordinal))
        {
            builder.AppendLine(line);
        }

        await logger.WriteAsync(builder.ToString(), cancellationToken);
    }

    private static async Task<TargetDiagnosticOutput> DiagnoseTargetAsync(
        DiagnosticTarget target,
        IReadOnlyList<IPAddress> configuredDnsServers,
        CancellationToken cancellationToken)
    {
        var result = new TargetResult { Target = target };
        var builder = new StringBuilder();
        builder.AppendLine($"\r\n========== {target.Host} | {target.Category} ==========");
        if (!string.IsNullOrWhiteSpace(target.Notes))
        {
            builder.AppendLine($"备注：{target.Notes}");
        }

        var dnsStopwatch = Stopwatch.StartNew();
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(DnsTimeout);
            var addresses = await Dns.GetHostAddressesAsync(target.Host, dnsCts.Token);
            dnsStopwatch.Stop();
            result.SystemDnsSucceeded = addresses.Length > 0;
            result.SystemAddresses.AddRange(addresses.Distinct());
            result.ResolvedHistoricalBadEdge = addresses.Contains(DomainCatalog.HistoricalBadEdge);
            builder.AppendLine(
                $"系统DNS：{dnsStopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
                (addresses.Length == 0 ? "无地址" : string.Join(", ", addresses.Select(item => item.ToString()))));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            dnsStopwatch.Stop();
            builder.AppendLine($"系统DNS：失败，{dnsStopwatch.Elapsed.TotalMilliseconds:F0} ms，{OneLine(ex.Message)}");
        }

        var dnsServers = configuredDnsServers
            .Concat([IPAddress.Parse("223.5.5.5"), IPAddress.Parse("119.29.29.29")])
            .Distinct()
            .Take(6)
            .ToArray();
        var rawDnsResults = await Task.WhenAll(dnsServers.Select(server =>
            DnsProtocolClient.QueryAAsync(target.Host, server, RawDnsTimeout, cancellationToken)));
        foreach (var rawDns in rawDnsResults)
        {
            result.AnyRawDnsSucceeded |= rawDns.Success;
            foreach (var answer in rawDns.Answers.Where(answer => answer.Type == "A"))
            {
                if (IPAddress.TryParse(answer.Value, out var parsedAddress) && !result.RawDnsAddresses.Contains(parsedAddress))
                {
                    result.RawDnsAddresses.Add(parsedAddress);
                }
            }

            var answerText = rawDns.Answers.Count == 0
                ? "无答案"
                : string.Join(", ", rawDns.Answers.Select(answer => $"{answer.Type}={answer.Value}(TTL={answer.Ttl})"));
            builder.AppendLine(
                $"DNS服务器 {rawDns.Server,-39}：{rawDns.Status}，{rawDns.Elapsed.TotalMilliseconds:F0} ms，{answerText}");
        }

        var systemHttp = await TestHttpWithSystemSettingsAsync(target.Host, cancellationToken);
        AppendHttp(builder, "HTTP（系统DNS/系统代理）", systemHttp);
        result.SystemHttpSucceeded = systemHttp.ReceivedResponse;
        ApplyHttpResult(result, systemHttp);

        var addressesToTest = result.SystemAddresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Take(target.IsFirstParty ? 3 : 2)
            .ToArray();
        foreach (var address in addressesToTest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine($"-- 当前解析节点 {address} --");
            var ping = await TestPingAsync(address, cancellationToken);
            builder.AppendLine($"Ping：{ping.Detail}");

            var tcp = await TestTcpAsync(address, 443, cancellationToken);
            builder.AppendLine($"TCP 443：{FormatSuccess(tcp.Success)}，{tcp.Elapsed.TotalMilliseconds:F0} ms，{tcp.Detail}");
            result.AnyTcpSucceeded |= tcp.Success;

            if (tcp.Success)
            {
                var tls = await TestTlsAsync(target.Host, address, cancellationToken);
                builder.AppendLine($"TLS：{FormatSuccess(tls.Success)}，{tls.Elapsed.TotalMilliseconds:F0} ms，{tls.Detail}");
                if (!string.IsNullOrWhiteSpace(tls.CertificateSummary))
                {
                    builder.AppendLine($"证书：{tls.CertificateSummary}");
                }

                result.AnyTlsSucceeded |= tls.Success;
                var http = await TestHttpByAddressAsync(target.Host, address, cancellationToken);
                AppendHttp(builder, $"HTTP（固定节点 {address}）", http);
                result.CurrentDirectHttpSucceeded |= http.ReceivedResponse;
                ApplyHttpResult(result, http);
            }
        }

        if (addressesToTest.Length == 0)
        {
            builder.AppendLine("没有可用于TCP/TLS测试的系统解析地址。");
        }

        var traceAddress = result.SystemAddresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault();
        var traceAddressSource = "系统DNS当前解析节点";
        if (traceAddress is null)
        {
            traceAddress = result.RawDnsAddresses
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
            traceAddressSource = "指定DNS解析节点";
        }
        if (traceAddress is null && target.ComparisonAddress is not null)
        {
            traceAddress = target.ComparisonAddress;
            traceAddressSource = "配置的健康对照IP（DNS无可用地址）";
        }

        if (traceAddress is not null)
        {
            builder.AppendLine($"路由跟踪（每个域名固定执行，目标 {traceAddress}，来源：{traceAddressSource}，最多15跳）：");
            result.TraceRoute = await TraceRouteAsync(traceAddress, traceAddressSource, cancellationToken);
            builder.Append(FormatTraceRoute(result.TraceRoute));
        }
        else
        {
            result.TraceRouteUnavailableReason = "系统DNS、指定DNS均未得到可用IP，无法执行路由跟踪。";
            builder.AppendLine($"路由跟踪：未执行，{result.TraceRouteUnavailableReason}");
        }

        if (target.ComparisonAddress is not null)
        {
            var comparisonAlreadyTested = addressesToTest.Contains(target.ComparisonAddress);
            builder.AppendLine($"-- 配置的健康对照IP {target.ComparisonAddress}" +
                               (comparisonAlreadyTested ? "（与当前解析相同） --" : " --"));
            if (comparisonAlreadyTested)
            {
                result.ComparisonSucceeded = result.AnyTcpSucceeded && result.AnyTlsSucceeded && result.AnyHttpResponse;
            }
            else
            {
                var tcp = await TestTcpAsync(target.ComparisonAddress, 443, cancellationToken);
                builder.AppendLine($"对照TCP 443：{FormatSuccess(tcp.Success)}，{tcp.Elapsed.TotalMilliseconds:F0} ms，{tcp.Detail}");
                if (tcp.Success)
                {
                    var tls = await TestTlsAsync(target.Host, target.ComparisonAddress, cancellationToken);
                    builder.AppendLine($"对照TLS：{FormatSuccess(tls.Success)}，{tls.Elapsed.TotalMilliseconds:F0} ms，{tls.Detail}");
                    var http = await TestHttpByAddressAsync(target.Host, target.ComparisonAddress, cancellationToken);
                    AppendHttp(builder, "对照HTTP", http);
                    result.ComparisonSucceeded = tls.Success && http.ReceivedResponse;
                }
            }
        }

        builder.AppendLine($"结论：{result.Conclusion}");
        return new TargetDiagnosticOutput(result, builder.ToString());
    }

    private static async Task<TcpTestResult> TestTcpAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var socket = await ConnectSocketAsync(address, port, ConnectTimeout, cancellationToken);
            stopwatch.Stop();
            return new TcpTestResult(true, stopwatch.Elapsed, $"已连接 {FormatEndpoint(address, port)}");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            stopwatch.Stop();
            return new TcpTestResult(false, stopwatch.Elapsed, OneLine(ex.Message));
        }
    }

    private static async Task<TlsTestResult> TestTlsAsync(
        string host,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SslPolicyErrors policyErrors = SslPolicyErrors.None;
        X509Certificate2? remoteCertificate = null;
        try
        {
            using var socket = await ConnectSocketAsync(address, 443, ConnectTimeout, cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var ssl = new SslStream(stream, false, (_, certificate, _, errors) =>
            {
                policyErrors = errors;
                if (certificate is not null)
                {
                    remoteCertificate = new X509Certificate2(certificate);
                }

                return true; // Continue only to collect diagnostic details; errors are still reported as failure.
            });
            using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tlsCts.CancelAfter(ConnectTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
            }, tlsCts.Token);
            stopwatch.Stop();

            var valid = policyErrors == SslPolicyErrors.None;
            var detail = $"{ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}, ALPN={ssl.NegotiatedApplicationProtocol}, " +
                         $"证书校验={(valid ? "通过" : policyErrors)}";
            var certificateSummary = remoteCertificate is null
                ? "未取得远端证书"
                : $"Subject={OneLine(remoteCertificate.Subject)}；Issuer={OneLine(remoteCertificate.Issuer)}；" +
                  $"有效期={remoteCertificate.NotBefore:yyyy-MM-dd}至{remoteCertificate.NotAfter:yyyy-MM-dd}";
            return new TlsTestResult(valid, stopwatch.Elapsed, detail, certificateSummary);
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or OperationCanceledException or IOException)
        {
            stopwatch.Stop();
            return new TlsTestResult(false, stopwatch.Elapsed, OneLine(ex.Message), string.Empty);
        }
        finally
        {
            remoteCertificate?.Dispose();
        }
    }

    private static async Task<HttpTestResult> TestHttpWithSystemSettingsAsync(
        string host,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = ConnectTimeout
        };
        return await SendHttpAsync(host, handler, cancellationToken);
    }

    private static async Task<HttpTestResult> TestHttpByAddressAsync(
        string host,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = async (context, token) =>
            {
                var socket = await ConnectSocketAsync(address, context.DnsEndPoint.Port, ConnectTimeout, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return await SendHttpAsync(host, handler, cancellationToken);
    }

    private static async Task<HttpTestResult> SendHttpAsync(
        string host,
        SocketsHttpHandler handler,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = HttpTimeout
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LYFZ-NetDiag/1.1");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            stopwatch.Stop();

            var details = new List<string>
            {
                $"HTTP/{response.Version}",
                $"状态={(int)response.StatusCode} {response.ReasonPhrase}"
            };
            AddHeader(details, "Server", response.Headers.Server.ToString());
            AddHeader(details, "Via", HeaderValue(response.Headers, "Via"));
            AddHeader(details, "X-Cache", HeaderValue(response.Headers, "X-Cache"));
            AddHeader(details, "EagleId", HeaderValue(response.Headers, "EagleId"));
            AddHeader(details, "Ali-Swift-Global-Savetime", HeaderValue(response.Headers, "Ali-Swift-Global-Savetime"));
            AddHeader(details, "X-Swift-SaveTime", HeaderValue(response.Headers, "X-Swift-SaveTime"));
            AddHeader(details, "X-Swift-CacheTime", HeaderValue(response.Headers, "X-Swift-CacheTime"));
            AddHeader(details, "X-Request-Id", HeaderValue(response.Headers, "X-Request-Id"));
            AddHeader(details, "Traceparent", HeaderValue(response.Headers, "Traceparent"));
            AddHeader(details, "Server-Timing", HeaderValue(response.Headers, "Server-Timing"));
            AddHeader(details, "Date", response.Headers.Date?.ToString("R"));
            AddHeader(details, "Location", response.Headers.Location?.ToString());
            if (response.Headers.Date is { } serverDate)
            {
                var clockDifference = (DateTimeOffset.UtcNow - serverDate).Duration();
                if (clockDifference > TimeSpan.FromMinutes(2))
                {
                    details.Add($"本机与服务器时间差约={clockDifference.TotalSeconds:F0}秒");
                }
            }
            return new HttpTestResult(true, (int)response.StatusCode, stopwatch.Elapsed, string.Join("；", details));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            stopwatch.Stop();
            return new HttpTestResult(false, null, stopwatch.Elapsed, OneLine(ex.Message));
        }
    }

    private static async Task<PingTestResult> TestPingAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SendPingAsync(address, null, cancellationToken)));
        var successful = replies.Where(reply => reply is { Status: IPStatus.Success }).ToArray();
        var average = successful.Length == 0 ? (double?)null : successful.Average(reply => reply!.RoundtripTime);
        var samples = replies.Select(reply => reply switch
        {
            null => "错误",
            { Status: IPStatus.Success } => $"{reply.RoundtripTime}ms",
            _ => reply.Status.ToString()
        });
        return new PingTestResult(3, successful.Length, average,
            $"收到 {successful.Length}/3，平均={(average is null ? "-" : $"{average:F1}ms")}，样本=[{string.Join(", ", samples)}]");
    }

    private static async Task<TraceRouteResult> TraceRouteAsync(
        IPAddress address,
        string addressSource,
        CancellationToken cancellationToken)
    {
        var hops = new List<TraceRouteHopResult>();
        var reached = false;
        for (var ttl = 1; ttl <= 15; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replies = await Task.WhenAll(Enumerable.Range(0, 3)
                .Select(_ => SendPingAsync(address, ttl, cancellationToken, 500)));
            var displayAddress = replies
                .FirstOrDefault(reply => reply?.Address is not null && !reply.Address.Equals(IPAddress.Any))
                ?.Address?.ToString() ?? "*";
            var samples = replies.Select(reply => reply switch
            {
                { Status: IPStatus.Success } => $"{reply.RoundtripTime} ms",
                { Status: IPStatus.TtlExpired } => $"{reply.RoundtripTime} ms",
                _ => "*"
            }).ToArray();
            var hopReached = replies.Any(reply => reply?.Status == IPStatus.Success);
            hops.Add(new TraceRouteHopResult(ttl, displayAddress, samples, hopReached));
            if (hopReached)
            {
                reached = true;
                break;
            }
        }

        return new TraceRouteResult(address, addressSource, hops, reached);
    }

    private static string FormatTraceRoute(TraceRouteResult traceRoute)
    {
        var builder = new StringBuilder();
        foreach (var hop in traceRoute.Hops)
        {
            builder.AppendLine($"  {hop.Hop,2}  {string.Join("  ", hop.Samples),-26} {hop.Address}");
        }

        builder.AppendLine(traceRoute.Reached
            ? $"路由结果：已到达目标 {traceRoute.TargetAddress}。"
            : $"路由结果：15跳内未收到目标 {traceRoute.TargetAddress} 的ICMP响应；星号不等同于链路中断，需结合TCP/TLS/HTTP判断。");
        return builder.ToString();
    }

    private static async Task<PingReply?> SendPingAsync(
        IPAddress address,
        int? ttl,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 1200)
    {
        try
        {
            using var ping = new Ping();
            var options = ttl.HasValue ? new PingOptions(ttl.Value, true) : null;
            var task = ping.SendPingAsync(address, timeoutMilliseconds, new byte[32], options);
            return await task.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException or InvalidOperationException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(address, port), connectCts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void AppendHttp(StringBuilder builder, string label, HttpTestResult result)
    {
        builder.AppendLine(
            $"{label}：{(result.ReceivedResponse ? "收到响应" : "失败")}，" +
            $"{result.Elapsed.TotalMilliseconds:F0} ms，{result.Detail}");
    }

    private static void ApplyHttpResult(TargetResult targetResult, HttpTestResult httpResult)
    {
        targetResult.AnyHttpResponse |= httpResult.ReceivedResponse;
        targetResult.HasServerError |= httpResult.StatusCode is >= 500;
    }

    private static string BuildSummary(
        IReadOnlyList<TargetResult> results,
        bool cancelled,
        LanBaselineResult lanBaseline,
        MonitoringResult? monitoringResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\r\n========== 汇总结论 ==========");
        foreach (var result in results)
        {
            var addresses = result.SystemAddresses.Count == 0
                ? "-"
                : string.Join(',', result.SystemAddresses);
            builder.AppendLine($"{result.Target.Host,-29} {result.Conclusion} | DNS={addresses}");
        }

        var failures = results.Where(result =>
            !result.SystemDnsSucceeded ||
            !result.AnyTcpSucceeded ||
            !result.AnyTlsSucceeded ||
            result.ResolvedHistoricalBadEdge).ToList();
        builder.AppendLine();
        if (cancelled)
        {
            builder.AppendLine("本次检测被提前停止，汇总可能不完整。");
        }
        else if (failures.Count == 0)
        {
            builder.AppendLine("本次未发现DNS、TCP 443或TLS层面的网络不可达；5xx等应用层告警请结合服务端日志判断。");
        }
        else
        {
            builder.AppendLine($"发现 {failures.Count} 个存在DNS/TCP/TLS异常或命中历史坏节点的域名，请将完整日志交给利亚方舟技术人员。");
        }

        AppendAutomaticAnalysis(builder, results, lanBaseline, monitoringResult);
        AppendMonitoringSummary(builder, monitoringResult);

        builder.AppendLine($"结束时间（本地）：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return builder.ToString();
    }

    private static string CreateLogPath(string directory, string storeName)
    {
        var safeStore = OutputPaths.SanitizeFilePart(storeName);
        var baseName = $"利亚方舟网络诊断-{safeStore}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, baseName + ".txt");
        if (!File.Exists(path))
        {
            return path;
        }

        return Path.Combine(directory, baseName + $"-{Guid.NewGuid():N}" + ".txt");
    }

    private static string SafeUserValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "未填写" : OneLine(value.Trim());

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string FormatSuccess(bool success) => success ? "成功" : "失败";

    private static string FormatEndpoint(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static string JoinOrDash(IEnumerable<string> values)
    {
        var array = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return array.Length == 0 ? "-" : string.Join(", ", array);
    }

    private static string FormatSpeed(long bitsPerSecond) => bitsPerSecond <= 0
        ? "未知"
        : bitsPerSecond >= 1_000_000_000
            ? $"{bitsPerSecond / 1_000_000_000d:F1} Gbps"
            : $"{bitsPerSecond / 1_000_000d:F0} Mbps";

    private static string? HeaderValue(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? string.Join(',', values) : null;

    private static void AddHeader(List<string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add($"{name}={OneLine(value)}");
        }
    }

    private static string SanitizeUri(Uri? uri)
    {
        if (uri is null)
        {
            return "未知";
        }

        return string.IsNullOrEmpty(uri.UserInfo)
            ? uri.ToString()
            : new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
    }

    private sealed record TargetDiagnosticOutput(TargetResult Result, string Text);
}
