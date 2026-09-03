'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { buildTargets, parseExtraDomains, TARGETS } = require('../app/core/catalog');
const { nativePlatformDirectory } = require('../app/core/native-probe');
const { targetConclusion, buildAnalysisReasons } = require('../app/core/analysis');
const { createHtmlReport } = require('../app/core/report');
const { normalizeRouteTrace, recordSample, validateOptions } = require('../app/core/runner');
const { safeName } = require('../app/core/utils');

let passed = 0;
test('域名目录完整', () => {
  equal(TARGETS.length, 13);
  truthy(TARGETS.some((item) => item.host === 'api.lyfz.net' && item.treatServerErrorAsNormal));
});
test('api.lyfz.net 根路径5xx按网络正常处理', () => {
  const target = TARGETS.find((item) => item.host === 'api.lyfz.net');
  equal(targetConclusion(healthyResult(target, 500)), '正常：网络可达（API根路径5xx按正常处理）');
});
test('其他域名5xx保留服务警告', () => {
  equal(targetConclusion(healthyResult(TARGETS[0], 503)), '警告：网络可达，但服务返回5xx');
});
test('模式仅允许快速、1分钟、5分钟、10分钟', () => {
  [0, 1, 5, 10].forEach((monitorMinutes) => validateOptions({ outputDirectory: path.resolve('.'), storeName: '', carrier: '', monitorMinutes }));
  throws(() => validateOptions({ outputDirectory: path.resolve('.'), storeName: '', carrier: '', monitorMinutes: 2 }));
});
test('额外诊断域名支持URL、中文域名、去重并保留13个默认项', () => {
  const parsed = parseExtraDomains('https://Example.com/path, example.com\n例子.公司');
  equal(parsed.length, 2);
  equal(parsed[0], 'example.com');
  truthy(parsed[1].startsWith('xn--'));
  const targets = buildTargets('example.com, login.lyfz.net');
  equal(targets.length, 14);
  equal(targets[13].category, '用户追加诊断域名');
});
test('额外诊断域名拒绝IP、无效格式和超过20个', () => {
  throws(() => parseExtraDomains('127.0.0.1'));
  throws(() => parseExtraDomains('bad_domain.example'));
  throws(() => parseExtraDomains(Array.from({ length: 21 }, (_, index) => `d${index}.example.com`)));
});
test('Windows 32位运行时映射到x86原生探测器目录', () => {
  equal(nativePlatformDirectory('win32', 'ia32'), 'win32-x86');
  equal(nativePlatformDirectory('win32', 'x64'), 'win32-x64');
  equal(nativePlatformDirectory('darwin', 'arm64'), 'darwin-arm64');
});
test('日志文件名清理危险字符', () => equal(safeName('三亚/门店:*?'), '三亚_门店___'));
test('HTML报告包含分析、域名表和原始证据，并执行转义', () => {
  const target = TARGETS[0];
  const result = healthyResult(target, 200);
  const html = createHtmlReport({
    version: '2.1.0', options: { storeName: '<script>alert(1)</script>', carrier: '电信', extraDomains: ['example.com'], monitorMinutes: 0 },
    startedAt: new Date('2026-01-01T00:00:00Z'), endedAt: new Date('2026-01-01T00:00:03Z'),
    snapshot: { hostname: 'test', platform: os.platform(), arch: os.arch(), nativeAvailable: true },
    gatewayBaselines: [], results: [result], monitoring: null, cancelled: false,
    logPath: path.join(os.tmpdir(), 'result.txt'), rawLog: 'raw <evidence>'
  });
  truthy(html.startsWith('<!doctype html>'));
  truthy(html.includes('自动故障归因'));
  truthy(html.includes('域名诊断结果'));
  truthy(html.includes('路由跟踪诊断'));
  truthy(html.includes('192.168.1.1'));
  truthy(html.includes('完整TXT原始证据'));
  truthy(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
  truthy(!html.includes('<script>alert(1)</script>'));
});
test('路由结果规范化保留逐跳、延迟和到达状态', () => {
  const trace = normalizeRouteTrace('1.1.1.1', { available: true, detail: 'ok', hops: [
    { ttl: 1, address: '192.168.1.1', averageMs: 2.4, reached: false },
    { ttl: 2, address: '1.1.1.1', averageMs: 8.2, reached: true }
  ] });
  equal(trace.hops.length, 2);
  truthy(trace.reached);
});
test('自动归因能够识别系统DNS失败但指定DNS成功', () => {
  const result = healthyResult(TARGETS[0], 200);
  result.systemDnsSucceeded = false;
  result.anyRawDnsSucceeded = true;
  const reasons = buildAnalysisReasons([result], [], null);
  truthy(reasons.some((item) => item.includes('本地DNS代理') || item.includes('系统DNS失败')));
});
test('连续监测记录IP变化、故障恢复和CSV证据', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'lyfz-netdiag-monitor-'));
  const csvPath = path.join(directory, 'timeline.csv');
  fs.writeFileSync(csvPath, 'header\n', 'utf8');
  const monitoring = { aggregates: {}, events: [], previous: {} };
  recordSample(monitoring, sample('1.1.1.1', 'TCP_FAIL'), csvPath);
  recordSample(monitoring, sample('2.2.2.2', 'OK'), csvPath);
  equal(monitoring.aggregates['login.lyfz.net'].samples, 2);
  equal(monitoring.aggregates['login.lyfz.net'].failures, 1);
  truthy(monitoring.events.some((item) => item.type === 'IP变化'));
  truthy(monitoring.events.some((item) => item.type === '故障恢复'));
  truthy(fs.readFileSync(csvPath, 'utf8').includes('TCP_FAIL'));
  fs.rmSync(directory, { recursive: true, force: true });
});

process.stdout.write(`\n${passed} tests passed.\n`);

function healthyResult(target, statusCode) {
  return {
    target, systemAddresses: ['127.0.0.1'], rawDnsAddresses: ['127.0.0.1'], httpStatuses: [statusCode],
    systemDnsSucceeded: true, anyRawDnsSucceeded: true, anyTcpSucceeded: true, anyTlsSucceeded: true,
    anyHttpResponse: true, systemHttpSucceeded: true, currentDirectHttpSucceeded: true,
    hasServerError: statusCode >= 500, comparisonSucceeded: true, resolvedHistoricalBadEdge: false,
    routeTrace: { targetAddress: '1.1.1.1', available: true, reached: false, detail: 'test', hops: [
      { ttl: 1, address: '192.168.1.1', averageMs: 1.2, reached: false }
    ] }
  };
}
function sample(addresses, state) {
  return {
    timestamp: new Date('2026-01-01T00:00:00Z'), kind: 'DOMAIN', host: 'login.lyfz.net', addresses,
    dnsMs: 1, dnsOk: true, tcpOk: state === 'OK', tcpMs: 2, tlsOk: state === 'OK', tlsMs: 3,
    httpOk: state === 'OK', httpStatus: state === 'OK' ? 200 : null, httpMs: 4, state, detail: 'test'
  };
}
function test(name, callback) {
  try { callback(); passed += 1; process.stdout.write(`PASS ${name}\n`); }
  catch (error) { process.stderr.write(`FAIL ${name}\n${error.stack}\n`); process.exitCode = 1; }
}
function equal(actual, expected) { if (actual !== expected) throw new Error(`Expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`); }
function truthy(value) { if (!value) throw new Error(`Expected truthy value, got ${JSON.stringify(value)}`); }
function throws(callback) { let didThrow = false; try { callback(); } catch (_) { didThrow = true; } if (!didThrow) throw new Error('Expected function to throw'); }
