'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { performance } = require('perf_hooks');
const { version: APP_VERSION } = require('../../package.json');
const { buildTargets, HISTORICAL_BAD_EDGE, parseExtraDomains } = require('./catalog');
const { configuredDnsServers, queryServer, systemLookup } = require('./dns-probe');
const { NativeProbe } = require('./native-probe');
const { httpProbe, publicIpProbe, tcpProbe, tlsProbe } = require('./network-probes');
const { buildAnalysisReasons, targetConclusion } = require('./analysis');
const { saveHtmlReport } = require('./report');
const {
  csvCell, delay, ensureDirectory, formatDate, isAbortError, oneLine, safeName,
  timestampForFile, unique
} = require('./utils');

const DNS_TIMEOUT = 4000;
const RAW_DNS_TIMEOUT = 2500;
const CONNECT_TIMEOUT = 5000;
const HTTP_TIMEOUT = 9000;

class DiagnosticRunner {
  constructor(dependencies) {
    const deps = dependencies || {};
    this.nativeProbe = deps.nativeProbe || new NativeProbe();
    this.systemHttpProbe = deps.systemHttpProbe || ((host, signal) => httpProbe(host, null, HTTP_TIMEOUT, signal));
    this.resolveSystemProxy = deps.resolveSystemProxy || (async () => '未检测（Node直连模式）');
  }

  async run(options, onProgress, signal) {
    validateOptions(options);
    ensureDirectory(options.outputDirectory);
    const startedAt = new Date();
    const stem = `LYFZ-NetDiag-Electron-${safeName(options.storeName)}-${timestampForFile(startedAt)}`;
    const logPath = path.join(options.outputDirectory, `${stem}.txt`);
    const htmlPath = path.join(options.outputDirectory, `${stem}.html`);
    const logger = new Logger(logPath, onProgress);
    const targets = buildTargets(options.extraDomains);
    let cancelled = false;
    let monitoring = null;
    let snapshot = emptySnapshot();
    let gatewayBaselines = [];
    const results = [];

    logger.write(buildHeader(options, startedAt));
    try {
      snapshot = await this.captureSnapshot(logger, signal);
      gatewayBaselines = await this.captureGatewayBaseline(snapshot.gateways, logger, signal);
      await capturePublicAddresses(logger, signal);

      let completed = 0;
      const diagnosed = await mapLimit(targets, 4, async (target) => {
        logger.progress(`正在检测 ${target.host}`, completed, targets.length);
        const result = await this.diagnoseTarget(target, snapshot.dnsServers, logger, signal);
        completed += 1;
        logger.progress(`已完成 ${target.host}：${targetConclusion(result)}`, completed, targets.length);
        return result;
      }, signal);
      results.push.apply(results, diagnosed);

      if (options.monitorMinutes > 0) {
        monitoring = await this.monitor(options, snapshot, logger, signal, stem, targets);
      }
    } catch (error) {
      if (isAbortError(error)) {
        cancelled = true;
        logger.write('\n检测已被用户停止，报告包含停止前已经完成的项目。');
      } else {
        logger.write(`\n全局诊断异常：${oneLine(error && error.stack || error)}`);
      }
    }

    logger.write(buildSummary(results, gatewayBaselines, monitoring, cancelled));
    logger.write(`HTML分析报告：${htmlPath}`);
    const endedAt = new Date();
    const rawLog = fs.readFileSync(logPath, 'utf8');
    saveHtmlReport(htmlPath, {
      version: APP_VERSION, options, startedAt, endedAt, snapshot, gatewayBaselines,
      results, monitoring, cancelled, logPath, rawLog
    });
    logger.progress(cancelled ? '检测已停止' : '检测完成', 1, 1);
    return { logPath, htmlPath, csvPath: monitoring && monitoring.csvPath, results, cancelled };
  }

  async captureSnapshot(logger, signal) {
    throwIfAborted(signal);
    const dnsServers = configuredDnsServers();
    const interfaces = [];
    const systemInterfaces = os.networkInterfaces();
    Object.keys(systemInterfaces).sort().forEach((name) => {
      const addresses = (systemInterfaces[name] || []).filter((item) => !item.internal);
      if (addresses.length) interfaces.push({ name, addresses });
    });
    let native = { available: false, gateways: [], interfaces: [], detail: '不可用' };
    try {
      native = await this.nativeProbe.snapshot(signal);
    } catch (error) {
      native = { available: false, gateways: [], interfaces: [], detail: oneLine(error.message) };
    }
    let proxy = '读取失败';
    try { proxy = await this.resolveSystemProxy('https://login.lyfz.net/'); } catch (error) { proxy = `读取失败：${oneLine(error.message)}`; }
    const snapshot = {
      hostname: os.hostname(), platform: `${os.type()} ${os.release()}`, arch: `${os.arch()} / ${process.arch}`,
      runtime: runtimeDescription(),
      dnsServers, interfaces, gateways: unique((native.gateways || []).map((item) => typeof item === 'string' ? item : item.address)),
      nativeAvailable: Boolean(native.available), nativeDetail: native.detail || '', proxy
    };
    const lines = ['\n========== 本机网络配置 =========='];
    interfaces.forEach((item) => {
      lines.push(`接口：${item.name}`);
      item.addresses.forEach((address) => lines.push(`  ${address.family}：${address.address}；掩码=${address.netmask}；MAC=${address.mac || '-'}`));
    });
    lines.push(`默认网关：${snapshot.gateways.join(', ') || '未取得'}`);
    lines.push(`系统DNS：${dnsServers.join(', ') || '未取得'}`);
    lines.push(`系统代理：${proxy}`);
    lines.push(`应用运行时：${snapshot.runtime}`);
    lines.push(`原生探测组件：${snapshot.nativeAvailable ? '可用' : `不可用（${snapshot.nativeDetail || '未提供'}）`}`);
    logger.write(lines.join('\n'));
    return snapshot;
  }

  async captureGatewayBaseline(gateways, logger, signal) {
    const baselines = [];
    logger.write('\n========== 默认网关基线 ==========');
    if (!gateways.length) {
      logger.write('未取得默认网关，跳过局域网ICMP基线。');
      return baselines;
    }
    for (const gateway of gateways) {
      throwIfAborted(signal);
      const ping = await this.nativeProbe.ping(gateway, 10, 1200, null, signal);
      const received = Number(ping.received || 0);
      const sent = Number(ping.sent || 10);
      const lossPercent = sent ? (sent - received) * 100 / sent : 100;
      baselines.push({ address: gateway, sent, received, averageMs: ping.averageMs == null ? null : Number(ping.averageMs), lossPercent });
      logger.write(`网关 ${gateway}：收到 ${received}/${sent}，丢包=${lossPercent.toFixed(0)}%，平均=${ping.averageMs == null ? '-' : `${Number(ping.averageMs).toFixed(1)} ms`}；${ping.detail || ''}`);
    }
    return baselines;
  }

  async diagnoseTarget(target, dnsServers, logger, signal) {
    throwIfAborted(signal);
    const result = newTargetResult(target);
    const lines = [`\n========== ${target.host} | ${target.category} ==========`];
    if (target.notes) lines.push(`备注：${target.notes}`);
    logger.write(`[域名进度] ${target.host}：开始系统DNS`);
    const lookup = await systemLookup(target.host, DNS_TIMEOUT);
    logger.write(`[域名进度] ${target.host}：系统DNS完成，开始指定DNS查询`);
    result.systemDnsSucceeded = lookup.success;
    result.systemAddresses = lookup.addresses;
    result.resolvedHistoricalBadEdge = lookup.addresses.indexOf(HISTORICAL_BAD_EDGE) >= 0;
    lines.push(`系统DNS：${lookup.success ? lookup.addresses.join(', ') : `失败，${oneLine(lookup.detail)}`}；${lookup.elapsedMs.toFixed(0)} ms`);

    const servers = unique(dnsServers.concat(['223.5.5.5', '119.29.29.29'])).slice(0, 6);
    const rawResults = await Promise.all(servers.map((server) => queryServer(target.host, server, RAW_DNS_TIMEOUT)));
    logger.write(`[域名进度] ${target.host}：指定DNS完成，开始系统代理HTTP`);
    rawResults.forEach((raw) => {
      result.anyRawDnsSucceeded = result.anyRawDnsSucceeded || raw.success;
      raw.answers.forEach((answer) => { if (result.rawDnsAddresses.indexOf(answer.value) < 0) result.rawDnsAddresses.push(answer.value); });
      const answers = raw.answers.length ? raw.answers.map((answer) => `${answer.type}=${answer.value}(TTL=${answer.ttl})`).join(', ') : '无答案';
      lines.push(`DNS服务器 ${raw.server}：${raw.status}，${raw.elapsedMs.toFixed(0)} ms，${answers}`);
    });

    let systemHttp;
    try { systemHttp = await this.systemHttpProbe(target.host, signal); }
    catch (error) { if (isAbortError(error)) throw error; systemHttp = failedHttp(error); }
    logger.write(`[域名进度] ${target.host}：系统代理HTTP完成，开始节点深度检测`);
    applyHttp(result, systemHttp);
    result.systemHttpSucceeded = systemHttp.receivedResponse;
    lines.push(formatHttpLine('HTTP（系统DNS/系统代理）', systemHttp));

    const addresses = orderAddresses(lookup.addresses).slice(0, target.firstParty ? 3 : 2);
    for (const address of addresses) {
      throwIfAborted(signal);
      lines.push(`-- 当前解析节点 ${address} --`);
      const ping = await this.nativeProbe.ping(address, 3, 1200, null, signal);
      lines.push(`Ping：${ping.available === false ? '跳过' : `${ping.received || 0}/${ping.sent || 3}`}；${ping.detail || ''}`);
      const tcp = await tcpProbe(address, 443, CONNECT_TIMEOUT, signal);
      result.anyTcpSucceeded = result.anyTcpSucceeded || tcp.success;
      lines.push(`TCP 443：${tcp.success ? 'OK' : 'FAIL'}，${tcp.elapsedMs.toFixed(0)} ms，${tcp.detail}`);
      if (tcp.success) {
        const tlsResult = await tlsProbe(target.host, address, CONNECT_TIMEOUT, signal);
        result.anyTlsSucceeded = result.anyTlsSucceeded || tlsResult.success;
        lines.push(`TLS：${tlsResult.success ? 'OK' : 'FAIL'}，${tlsResult.elapsedMs.toFixed(0)} ms，${tlsResult.detail}`);
        if (tlsResult.certificate) lines.push(`证书：${tlsResult.certificate}`);
        const directHttp = await httpProbe(target.host, address, HTTP_TIMEOUT, signal);
        result.currentDirectHttpSucceeded = result.currentDirectHttpSucceeded || directHttp.receivedResponse;
        applyHttp(result, directHttp);
        lines.push(formatHttpLine(`HTTP（固定节点 ${address}）`, directHttp));
      }
    }
    if (!addresses.length) lines.push('没有可用于TCP/TLS测试的系统解析地址。');

    const traceAddress = addresses.find((address) => /^\d+\.\d+\.\d+\.\d+$/.test(address));
    if (traceAddress) {
      logger.write(`[域名进度] ${target.host}：开始路由跟踪 ${traceAddress}`);
      let trace;
      try {
        trace = await this.nativeProbe.trace(traceAddress, 15, 500, signal);
      } catch (error) {
        if (isAbortError(error)) throw error;
        trace = { available: false, hops: [], detail: oneLine(error.message) };
      }
      result.routeTrace = normalizeRouteTrace(traceAddress, trace);
      lines.push(`-- 路由跟踪 ${traceAddress}（最多15跳，每次等待500ms） --`);
      if (result.routeTrace.hops.length) {
        result.routeTrace.hops.forEach((hop) => lines.push(formatRouteHop(hop)));
      } else {
        lines.push(result.routeTrace.detail || '未获得路由跳点');
      }
      lines.push('提示：星号仅表示该跳未响应ICMP，不能单独判定链路故障。');
    } else {
      result.routeTrace = { targetAddress: '', available: false, reached: false, hops: [], detail: '没有可跟踪的IPv4解析地址' };
      lines.push('-- 路由跟踪：跳过（没有可跟踪的IPv4解析地址） --');
    }

    if (target.comparisonAddress) {
      lines.push(`-- 配置的健康对照IP ${target.comparisonAddress} --`);
      if (addresses.indexOf(target.comparisonAddress) >= 0) {
        result.comparisonSucceeded = result.anyTcpSucceeded && result.anyTlsSucceeded && result.anyHttpResponse;
        lines.push(`对照结果：与当前解析相同；${result.comparisonSucceeded ? 'OK' : 'FAIL'}`);
      } else {
        const tcp = await tcpProbe(target.comparisonAddress, 443, CONNECT_TIMEOUT, signal);
        lines.push(`对照TCP 443：${tcp.success ? 'OK' : 'FAIL'}，${tcp.elapsedMs.toFixed(0)} ms，${tcp.detail}`);
        if (tcp.success) {
          const tlsResult = await tlsProbe(target.host, target.comparisonAddress, CONNECT_TIMEOUT, signal);
          lines.push(`对照TLS：${tlsResult.success ? 'OK' : 'FAIL'}，${tlsResult.elapsedMs.toFixed(0)} ms，${tlsResult.detail}`);
          let http = { receivedResponse: false };
          if (tlsResult.success) {
            http = await httpProbe(target.host, target.comparisonAddress, HTTP_TIMEOUT, signal);
            lines.push(formatHttpLine('对照HTTP', http));
          }
          result.comparisonSucceeded = Boolean(tcp.success && tlsResult.success && http.receivedResponse);
        }
      }
    }
    result.conclusion = targetConclusion(result);
    lines.push(`结论：${result.conclusion}`);
    logger.write(lines.join('\n'));
    return result;
  }

  async monitor(options, snapshot, logger, signal, stem, targets) {
    const durationMs = options.monitorMinutes * 60 * 1000;
    const intervalMs = 10000;
    const endAt = Date.now() + durationMs;
    const csvPath = path.join(options.outputDirectory, `${stem}-timeline.csv`);
    fs.writeFileSync(csvPath, 'timestamp,kind,host,addresses,dns_ms,dns_ok,tcp_ok,tcp_ms,tls_ok,tls_ms,http_ok,http_status,http_ms,state,detail\n', 'utf8');
    const monitoring = { csvPath, rounds: 0, events: [], aggregates: {}, previous: {} };
    logger.write(`\n========== 连续检测 ${options.monitorMinutes}分钟 ==========\n采样间隔：自有域名10秒，第三方域名60秒；CSV：${csvPath}`);
    while (Date.now() < endAt) {
      throwIfAborted(signal);
      const roundStarted = Date.now();
      monitoring.rounds += 1;
      const roundTargets = targets.filter((target) => target.firstParty || monitoring.rounds === 1 || monitoring.rounds % 6 === 0);
      logger.progress(`连续检测第 ${monitoring.rounds} 轮`, monitoring.rounds, Math.ceil(durationMs / intervalMs));
      const samples = await mapLimit(roundTargets, 4, (target) => sampleTarget(target, signal), signal);
      if (monitoring.rounds === 1 || monitoring.rounds % 6 === 0) {
        const publicIp = await publicIpProbe('https://api64.ipify.org/', HTTP_TIMEOUT, signal);
        samples.push({
          timestamp: new Date(), kind: 'PUBLIC_IP', host: 'public-ip', addresses: publicIp.address || '', dnsMs: 0, dnsOk: true,
          tcpOk: publicIp.success, tcpMs: publicIp.elapsedMs, tlsOk: publicIp.success, tlsMs: null, httpOk: publicIp.success,
          httpStatus: publicIp.success ? 200 : null, httpMs: publicIp.elapsedMs, state: publicIp.success ? 'OK' : 'PUBLIC_IP_FAIL', detail: publicIp.detail || `公网出口IP=${publicIp.address}`
        });
      }
      for (const gateway of snapshot.gateways) {
        const ping = await this.nativeProbe.ping(gateway, 1, 1200, null, signal);
        const success = Number(ping.received || 0) > 0;
        samples.push({
          timestamp: new Date(), kind: 'GATEWAY', host: gateway, addresses: gateway, dnsMs: 0, dnsOk: true,
          tcpOk: success, tcpMs: ping.averageMs, tlsOk: success, tlsMs: null, httpOk: success, httpStatus: null,
          httpMs: null, state: success ? 'OK' : 'GATEWAY_NO_REPLY', detail: ping.detail || ''
        });
      }
      samples.forEach((sample) => recordSample(monitoring, sample, csvPath));
      const remaining = Math.min(intervalMs - (Date.now() - roundStarted), endAt - Date.now());
      if (remaining > 0) await delay(remaining, signal);
    }
    delete monitoring.previous;
    logger.write(buildMonitoringSummary(monitoring));
    return monitoring;
  }
}

async function sampleTarget(target, signal) {
  const timestamp = new Date();
  const dnsResult = await systemLookup(target.host, DNS_TIMEOUT);
  const addresses = orderAddresses(dnsResult.addresses);
  const address = addresses[0];
  if (!dnsResult.success || !address) return sampleFailure(timestamp, target.host, '', dnsResult.elapsedMs, 'DNS_FAIL', dnsResult.detail);
  if (addresses.indexOf(HISTORICAL_BAD_EDGE) >= 0) return sampleFailure(timestamp, target.host, addresses.join('|'), dnsResult.elapsedMs, 'HISTORICAL_BAD_EDGE', '命中历史异常节点');
  const tcp = await tcpProbe(address, 443, CONNECT_TIMEOUT, signal);
  if (!tcp.success) return sampleFailure(timestamp, target.host, addresses.join('|'), dnsResult.elapsedMs, 'TCP_FAIL', tcp.detail, tcp);
  const tlsResult = await tlsProbe(target.host, address, CONNECT_TIMEOUT, signal);
  if (!tlsResult.success) return sampleFailure(timestamp, target.host, addresses.join('|'), dnsResult.elapsedMs, 'TLS_FAIL', tlsResult.detail, tcp, tlsResult);
  const http = await httpProbe(target.host, address, HTTP_TIMEOUT, signal);
  const state = !http.receivedResponse ? 'HTTP_FAIL' : http.statusCode >= 500 && !target.treatServerErrorAsNormal ? 'APP_5XX' : 'OK';
  return {
    timestamp, kind: 'DOMAIN', host: target.host, addresses: addresses.join('|'), dnsMs: dnsResult.elapsedMs, dnsOk: true,
    tcpOk: true, tcpMs: tcp.elapsedMs, tlsOk: true, tlsMs: tlsResult.elapsedMs, httpOk: http.receivedResponse,
    httpStatus: http.statusCode, httpMs: http.elapsedMs, state, detail: http.detail
  };
}

function sampleFailure(timestamp, host, addresses, dnsMs, state, detail, tcp, tlsResult) {
  return {
    timestamp, kind: 'DOMAIN', host, addresses, dnsMs, dnsOk: state !== 'DNS_FAIL', tcpOk: Boolean(tcp && tcp.success),
    tcpMs: tcp && tcp.elapsedMs, tlsOk: Boolean(tlsResult && tlsResult.success), tlsMs: tlsResult && tlsResult.elapsedMs,
    httpOk: false, httpStatus: null, httpMs: null, state, detail: oneLine(detail)
  };
}

function recordSample(monitoring, sample, csvPath) {
  const aggregate = monitoring.aggregates[sample.host] || (monitoring.aggregates[sample.host] = emptyAggregate());
  aggregate.samples += 1;
  if (sample.state !== 'OK') aggregate.failures += 1;
  if (sample.state === 'DNS_FAIL') aggregate.dnsFailures += 1;
  if (['TCP_FAIL', 'HISTORICAL_BAD_EDGE', 'GATEWAY_NO_REPLY'].indexOf(sample.state) >= 0) aggregate.tcpFailures += 1;
  if (sample.state === 'TLS_FAIL') aggregate.tlsFailures += 1;
  if (['HTTP_FAIL', 'APP_5XX'].indexOf(sample.state) >= 0) aggregate.httpFailures += 1;
  const previous = monitoring.previous[sample.host];
  if (previous) {
    if (previous.addresses !== sample.addresses) {
      aggregate.ipChanges += 1;
      monitoring.events.push({ timestamp: sample.timestamp, host: sample.host, type: 'IP变化', before: previous.addresses, after: sample.addresses });
    }
    if (previous.state !== sample.state) {
      aggregate.stateChanges += 1;
      monitoring.events.push({ timestamp: sample.timestamp, host: sample.host, type: sample.state === 'OK' ? '故障恢复' : '状态变化', before: previous.state, after: sample.state });
    }
  }
  monitoring.previous[sample.host] = { addresses: sample.addresses, state: sample.state };
  const columns = [sample.timestamp.toISOString(), sample.kind, sample.host, sample.addresses, fixed(sample.dnsMs), sample.dnsOk,
    sample.tcpOk, fixed(sample.tcpMs), sample.tlsOk, fixed(sample.tlsMs), sample.httpOk, sample.httpStatus == null ? '' : sample.httpStatus,
    fixed(sample.httpMs), sample.state, sample.detail];
  fs.appendFileSync(csvPath, `${columns.map(csvCell).join(',')}\n`, 'utf8');
}

function applyHttp(result, http) {
  result.anyHttpResponse = result.anyHttpResponse || Boolean(http.receivedResponse);
  if (http.statusCode != null && result.httpStatuses.indexOf(http.statusCode) < 0) result.httpStatuses.push(http.statusCode);
  if (http.statusCode >= 500) result.hasServerError = true;
}

function newTargetResult(target) {
  return {
    target, systemAddresses: [], rawDnsAddresses: [], httpStatuses: [], systemDnsSucceeded: false, anyRawDnsSucceeded: false,
    anyTcpSucceeded: false, anyTlsSucceeded: false, anyHttpResponse: false, systemHttpSucceeded: false,
    currentDirectHttpSucceeded: false, hasServerError: false, comparisonSucceeded: false, resolvedHistoricalBadEdge: false,
    routeTrace: { targetAddress: '', available: false, reached: false, hops: [], detail: '尚未执行' }
  };
}

function normalizeRouteTrace(targetAddress, trace) {
  const hops = Array.isArray(trace && trace.hops) ? trace.hops.map((hop) => ({
    ttl: Number(hop.ttl || 0),
    address: String(hop.address || ''),
    averageMs: hop.averageMs == null ? null : Number(hop.averageMs),
    reached: Boolean(hop.reached)
  })) : [];
  return {
    targetAddress,
    available: Boolean(trace && trace.available !== false),
    reached: hops.some((hop) => hop.reached),
    hops,
    detail: oneLine(trace && trace.detail || '')
  };
}

function formatRouteHop(hop) {
  return `${String(hop.ttl).padStart(2)}  ${(hop.address || '*').padEnd(15)}  ${hop.averageMs == null ? '*' : `${Number(hop.averageMs).toFixed(0)} ms`}${hop.reached ? '  已到达' : ''}`;
}

function failedHttp(error) { return { receivedResponse: false, statusCode: null, elapsedMs: 0, detail: oneLine(error.message), traceHeaders: {} }; }
function formatHttpLine(label, value) { return `${label}：${value.receivedResponse ? '收到响应' : '失败'}，${value.elapsedMs.toFixed(0)} ms，${value.detail}`; }
function orderAddresses(addresses) { return addresses.slice().sort((a, b) => (a.indexOf(':') < 0 ? 0 : 1) - (b.indexOf(':') < 0 ? 0 : 1)); }
function fixed(value) { return value == null ? '' : Number(value).toFixed(1); }
function throwIfAborted(signal) { if (signal && signal.aborted) { const error = new Error('检测已取消'); error.name = 'AbortError'; throw error; } }

async function capturePublicAddresses(logger, signal) {
  logger.write('\n========== 公网出口 ==========');
  const services = ['https://api64.ipify.org/', 'https://4.ipw.cn/'];
  const results = await Promise.all(services.map((url) => publicIpProbe(url, HTTP_TIMEOUT, signal)));
  results.forEach((result, index) => logger.write(result.success
    ? `公网出口IP（${new URL(services[index]).host}）：${result.address}，${result.elapsedMs.toFixed(0)} ms`
    : `公网出口IP（${new URL(services[index]).host}）：获取失败（${result.detail || '响应无效'}）`));
}

function buildHeader(options, startedAt) {
  return `利亚方舟海螺云跨平台网络诊断日志
========================================================================
开始时间          ：${formatDate(startedAt)}
门店名称          ：${options.storeName || '未填写'}
宽带运营商        ：${options.carrier || '未知'}
额外诊断域名      ：${options.extraDomains.length ? options.extraDomains.join(', ') : '无（使用13个默认域名）'}
检测模式          ：${options.monitorMinutes ? `连续检测 ${options.monitorMinutes} 分钟 / 间隔10秒` : '快速诊断一次'}
工具版本          ：Electron ${APP_VERSION}
应用运行时        ：${runtimeDescription()}
操作系统          ：${os.type()} ${os.release()}
系统架构/进程架构 ：${os.arch()}/${process.arch}
设备名称          ：${os.hostname()}
说明：工具不采集账号、密码、Cookie或业务内容；HTTP只请求固定域名根路径并读取响应头。
说明：不调用ping、tracert、traceroute、nslookup、curl或PowerShell等系统命令。
说明：Ping或路由星号可能只是设备不响应ICMP，必须结合TCP 443、TLS和HTTP结果判断。
`;
}

function buildSummary(results, gatewayBaselines, monitoring, cancelled) {
  const lines = ['\n========== 汇总 =========='];
  if (cancelled) lines.push('检测已停止，以下为已经完成的结果。');
  results.forEach((result) => lines.push(`${result.target.host.padEnd(29)} ${targetConclusion(result)}`));
  lines.push('\n========== 自动故障归因 ==========');
  buildAnalysisReasons(results, gatewayBaselines, monitoring).forEach((reason) => lines.push(`- ${reason}`));
  lines.push('\n特别规则：api.lyfz.net 根路径返回5xx表示服务已收到请求，按网络正常处理。');
  return lines.join('\n');
}

function buildMonitoringSummary(monitoring) {
  const lines = ['\n========== 连续检测汇总 ==========', `轮数：${monitoring.rounds}；事件：${monitoring.events.length}；CSV：${monitoring.csvPath}`];
  Object.keys(monitoring.aggregates).sort().forEach((host) => {
    const value = monitoring.aggregates[host];
    lines.push(`${host.padEnd(29)} 样本=${value.samples}，异常=${value.failures}，DNS失败=${value.dnsFailures}，TCP失败=${value.tcpFailures}，TLS失败=${value.tlsFailures}，HTTP异常=${value.httpFailures}，IP变化=${value.ipChanges}，状态变化=${value.stateChanges}`);
  });
  if (monitoring.events.length) {
    lines.push('变化事件：');
    monitoring.events.slice(0, 150).forEach((item) => lines.push(`  ${formatDate(item.timestamp)} ${item.host} ${item.type}: ${item.before || '-'} -> ${item.after || '-'}`));
  }
  return lines.join('\n');
}

function runtimeDescription() {
  return `Electron ${process.versions.electron || '开发测试'} / Node ${process.versions.node || '-'} / Chromium ${process.versions.chrome || '-'}`;
}

function emptySnapshot() { return { hostname: os.hostname(), platform: `${os.type()} ${os.release()}`, arch: os.arch(), runtime: runtimeDescription(), dnsServers: [], interfaces: [], gateways: [], nativeAvailable: false, nativeDetail: '', proxy: '' }; }
function emptyAggregate() { return { samples: 0, failures: 0, dnsFailures: 0, tcpFailures: 0, tlsFailures: 0, httpFailures: 0, ipChanges: 0, stateChanges: 0 }; }

async function mapLimit(items, concurrency, worker, signal) {
  const results = new Array(items.length);
  let next = 0;
  async function consume() {
    while (true) {
      throwIfAborted(signal);
      const index = next++;
      if (index >= items.length) return;
      results[index] = await worker(items[index], index);
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, consume));
  return results;
}

function validateOptions(options) {
  if (!options || typeof options.outputDirectory !== 'string' || !path.isAbsolute(options.outputDirectory)) throw new Error('输出目录无效');
  if ([0, 1, 5, 10].indexOf(Number(options.monitorMinutes)) < 0) throw new Error('检测时长只允许快速、1分钟、5分钟或10分钟');
  options.storeName = String(options.storeName || '').slice(0, 80);
  options.carrier = String(options.carrier || '未知').slice(0, 40);
  options.extraDomains = parseExtraDomains(options.extraDomains);
  options.monitorMinutes = Number(options.monitorMinutes);
}

class Logger {
  constructor(filePath, onProgress) {
    this.filePath = filePath;
    this.onProgress = typeof onProgress === 'function' ? onProgress : () => {};
    fs.writeFileSync(filePath, '', 'utf8');
  }
  write(text) { fs.appendFileSync(this.filePath, `${text.replace(/\r?\n/g, os.EOL)}${os.EOL}`, 'utf8'); }
  progress(message, completed, total) { this.onProgress({ message, completed, total }); }
}

module.exports = { DiagnosticRunner, formatRouteHop, mapLimit, normalizeRouteTrace, recordSample, validateOptions };
