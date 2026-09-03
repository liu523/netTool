'use strict';

const path = require('path');
const { buildAnalysisReasons, overallStatus, targetConclusion } = require('./analysis');
const { basenameLink, escapeHtml, formatDate, writeFileAtomic } = require('./utils');

function createHtmlReport(data) {
  const status = overallStatus(data.results, data.cancelled);
  const reasons = buildAnalysisReasons(data.results, data.gatewayBaselines, data.monitoring);
  const normal = data.results.filter((item) => targetConclusion(item).startsWith('正常')).length;
  const abnormal = data.results.length - normal;
  const duration = Math.max(0, data.endedAt.getTime() - data.startedAt.getTime());
  const rows = data.results.map(targetRow).join('');
  const gatewayRows = data.gatewayBaselines.length
    ? data.gatewayBaselines.map((item) => `<tr><td>${e(item.address)}</td><td>${item.received}/${item.sent}</td><td>${numberOrDash(item.averageMs)} ms</td><td>${item.lossPercent.toFixed(0)}%</td></tr>`).join('')
    : '<tr><td colspan="4">未取得默认网关，或原生探测组件不可用。</td></tr>';
  const monitoring = monitoringSection(data.monitoring);
  const rawLog = escapeHtml(data.rawLog);
  const txtName = path.basename(data.logPath);
  const csvLink = data.monitoring && data.monitoring.csvPath
    ? `<a class="artifact" href="${basenameLink(data.monitoring.csvPath)}">下载CSV时间序列</a>` : '';

  return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>海螺云网络诊断分析报告</title>
<style>
:root{color-scheme:light;--ink:#172033;--muted:#64748b;--line:#dce4ef;--bg:#f3f6fa;--card:#fff;--blue:#1769e0;--ok:#16835a;--warn:#b76700;--danger:#c72c41}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.65 -apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif}.page{max-width:1240px;margin:0 auto;padding:28px}.hero{color:white;border-radius:18px;padding:28px;background:linear-gradient(135deg,#102a56,#1769e0);box-shadow:0 12px 34px #163b7b2b}.hero h1{margin:0 0 6px;font-size:28px}.hero p{margin:4px 0;color:#e7efff}.status{display:inline-flex;margin-top:18px;padding:8px 14px;border-radius:999px;font-weight:700;background:#ffffff24;border:1px solid #ffffff52}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:18px 0}.metric,.card{background:var(--card);border:1px solid var(--line);border-radius:14px;box-shadow:0 5px 18px #152b5110}.metric{padding:17px}.metric span{display:block;color:var(--muted);font-size:12px}.metric strong{font-size:23px}.card{padding:20px;margin:14px 0}.card h2{margin:0 0 13px;font-size:18px}.analysis{border-left:5px solid var(--blue)}.analysis li{margin:7px 0}.table-wrap{overflow:auto;border:1px solid var(--line);border-radius:10px}table{border-collapse:collapse;width:100%;min-width:760px}th,td{text-align:left;padding:10px 11px;border-bottom:1px solid var(--line);vertical-align:top}th{background:#f7f9fc;color:#475569;white-space:nowrap}tr:last-child td{border-bottom:0}.pill{display:inline-block;border-radius:999px;padding:3px 9px;font-size:12px;font-weight:700}.pill.ok{color:var(--ok);background:#e8f8f1}.pill.warn{color:var(--warn);background:#fff3df}.pill.danger{color:var(--danger);background:#ffeaed}.muted{color:var(--muted)}.artifacts{display:flex;gap:10px;flex-wrap:wrap}.artifact,button{border:0;border-radius:9px;padding:9px 13px;background:#e9f1ff;color:#1257b6;text-decoration:none;font-weight:650;cursor:pointer}.raw{white-space:pre-wrap;word-break:break-word;background:#101827;color:#dce7f8;border-radius:10px;padding:16px;max-height:620px;overflow:auto;font:12px/1.55 Consolas,monospace}details summary{cursor:pointer;font-weight:700}.footer{padding:12px 0 30px;color:var(--muted);text-align:center}
@media(max-width:800px){.page{padding:12px}.grid{grid-template-columns:repeat(2,1fr)}.hero{padding:20px}.hero h1{font-size:22px}}@media print{body{background:#fff}.page{max-width:none;padding:0}.hero,.card,.metric{box-shadow:none}.no-print{display:none}.raw{max-height:none}.table-wrap{overflow:visible}}
.route-path{display:flex;flex-wrap:wrap;align-items:center;gap:6px;min-width:520px}.route-hop{display:inline-flex;gap:5px;align-items:center;padding:4px 7px;border:1px solid #dce4ef;border-radius:7px;background:#f8fafc;font:12px Consolas,monospace}.route-hop.missing{color:#8994a4;background:#f3f5f8}.route-hop.reached{color:var(--ok);border-color:#a9dbc8;background:#edf9f4}.route-arrow{color:#94a3b8}.route-note{margin:10px 0 0;color:var(--muted)}
</style></head><body><main class="page">
<section class="hero"><h1>利亚方舟海螺云网络诊断报告</h1><p>${e(data.options.storeName || '未填写门店')} · ${e(data.options.carrier || '未知运营商')}</p><p>${e(formatDate(data.startedAt))} — ${e(formatDate(data.endedAt))}</p><div class="status">${e(status.title)} · ${e(status.detail)}</div></section>
<section class="grid"><div class="metric"><span>已检测域名</span><strong>${data.results.length}</strong></div><div class="metric"><span>正常</span><strong>${normal}</strong></div><div class="metric"><span>异常/警告</span><strong>${abnormal}</strong></div><div class="metric"><span>检测耗时</span><strong>${formatDuration(duration)}</strong></div></section>
<section class="card analysis"><h2>自动故障归因</h2><ol>${reasons.map((reason) => `<li>${e(reason)}</li>`).join('')}</ol></section>
<section class="card"><h2>诊断信息</h2><div class="table-wrap"><table><tbody>
<tr><th>设备</th><td>${e(data.snapshot.hostname)}</td><th>系统</th><td>${e(data.snapshot.platform)}</td></tr>
<tr><th>架构</th><td>${e(data.snapshot.arch)}</td><th>工具版本</th><td>${e(data.version)}</td></tr>
<tr><th>应用运行时</th><td colspan="3">${e(data.snapshot.runtime || '-')}</td></tr>
<tr><th>检测模式</th><td>${e(data.options.monitorMinutes ? `连续检测${data.options.monitorMinutes}分钟` : '快速诊断一次')}</td><th>原生探测器</th><td>${data.snapshot.nativeAvailable ? '可用' : '不可用（ICMP项目跳过）'}</td></tr>
<tr><th>额外诊断域名</th><td colspan="3">${e(data.options.extraDomains && data.options.extraDomains.length ? data.options.extraDomains.join(', ') : '无')}</td></tr>
</tbody></table></div></section>
<section class="card"><h2>默认网关基线</h2><div class="table-wrap"><table><thead><tr><th>网关</th><th>收到/发送</th><th>平均延迟</th><th>丢包</th></tr></thead><tbody>${gatewayRows}</tbody></table></div></section>
<section class="card"><h2>域名诊断结果</h2><div class="table-wrap"><table><thead><tr><th>域名</th><th>结论</th><th>DNS/IP</th><th>TCP 443</th><th>TLS</th><th>HTTP</th><th>健康对照</th></tr></thead><tbody>${rows}</tbody></table></div><p class="muted">特别规则：api.lyfz.net 根路径返回5xx表示请求已经到达服务器，按网络连通正常处理。</p></section>
${routeSection(data.results)}
${monitoring}
<section class="card"><h2>报告文件</h2><div class="artifacts"><a class="artifact" href="${basenameLink(data.logPath)}">打开TXT原始日志</a>${csvLink}<button class="no-print" onclick="window.print()">打印或另存为PDF</button></div></section>
<section class="card"><details><summary>查看完整TXT原始证据</summary><pre class="raw">${rawLog}</pre></details></section>
<div class="footer">报告由利亚方舟海螺云网络诊断工具自动生成 · ${e(txtName)}</div>
</main></body></html>`;
}

function targetRow(item) {
  const conclusion = targetConclusion(item);
  const level = conclusion.startsWith('正常') ? 'ok' : conclusion.startsWith('警告') ? 'warn' : 'danger';
  const addresses = item.systemAddresses.length ? item.systemAddresses.join(', ') : '-';
  const http = item.httpStatuses.length ? item.httpStatuses.map((code) => `HTTP ${code}`).join(', ') : '-';
  return `<tr><td><strong>${e(item.target.host)}</strong><br><span class="muted">${e(item.target.category)}</span></td><td><span class="pill ${level}">${e(conclusion)}</span></td><td>${e(addresses)}</td><td>${okText(item.anyTcpSucceeded)}</td><td>${okText(item.anyTlsSucceeded)}</td><td>${e(http)}</td><td>${item.target.comparisonAddress ? okText(item.comparisonSucceeded) : '-'}</td></tr>`;
}

function routeSection(results) {
  const rows = results.map((item) => {
    const trace = item.routeTrace || { targetAddress: '', available: false, reached: false, hops: [], detail: '未执行' };
    const status = !trace.targetAddress ? '未执行' : !trace.available ? '探测器不可用' : trace.reached ? '已到达目标' : trace.hops.length ? '已记录，终点未响应ICMP' : '未获得跳点';
    const level = trace.reached ? 'ok' : trace.hops.length ? 'warn' : 'danger';
    const routePath = trace.hops.length ? trace.hops.map((hop) => {
      const address = hop.address || '*';
      const classes = `route-hop${hop.address ? '' : ' missing'}${hop.reached ? ' reached' : ''}`;
      const latency = hop.averageMs == null ? '*' : `${Number(hop.averageMs).toFixed(0)}ms`;
      return `<span class="${classes}"><b>${Number(hop.ttl)}</b><span>${e(address)}</span><span>${e(latency)}</span></span>`;
    }).join('<span class="route-arrow">→</span>') : `<span class="muted">${e(trace.detail || '无路由数据')}</span>`;
    return `<tr><td><strong>${e(item.target.host)}</strong><br><span class="muted">目标IP：${e(trace.targetAddress || '-')}</span></td><td><span class="pill ${level}">${e(status)}</span></td><td><div class="route-path">${routePath}</div></td></tr>`;
  }).join('');
  return `<section class="card"><h2>路由跟踪诊断</h2><div class="table-wrap"><table><thead><tr><th>诊断域名</th><th>路由状态</th><th>逐跳路径（TTL / IP / 平均延迟）</th></tr></thead><tbody>${rows}</tbody></table></div><p class="route-note">“*”只表示该跳路由器未响应ICMP；如果后续跳点、TCP 443、TLS或HTTP正常，不能据此判定网络中断。</p></section>`;
}

function monitoringSection(monitoring) {
  if (!monitoring) return '';
  const rows = Object.keys(monitoring.aggregates).sort().map((host) => {
    const value = monitoring.aggregates[host];
    return `<tr><td>${e(host)}</td><td>${value.samples}</td><td>${value.failures}</td><td>${value.dnsFailures}</td><td>${value.tcpFailures}</td><td>${value.tlsFailures}</td><td>${value.httpFailures}</td><td>${value.ipChanges}</td><td>${value.stateChanges}</td></tr>`;
  }).join('');
  const events = monitoring.events.length
    ? monitoring.events.slice(0, 150).map((item) => `<tr><td>${e(formatDate(item.timestamp))}</td><td>${e(item.host)}</td><td>${e(item.type)}</td><td>${e(item.before || '-')} → ${e(item.after || '-')}</td></tr>`).join('')
    : '<tr><td colspan="4">没有记录到状态或IP变化。</td></tr>';
  return `<section class="card"><h2>连续检测汇总</h2><p>轮数：${monitoring.rounds}；事件：${monitoring.events.length}</p><div class="table-wrap"><table><thead><tr><th>目标</th><th>样本</th><th>异常</th><th>DNS失败</th><th>TCP失败</th><th>TLS失败</th><th>HTTP异常</th><th>IP变化</th><th>状态变化</th></tr></thead><tbody>${rows}</tbody></table></div><h2 style="margin-top:22px">变化事件</h2><div class="table-wrap"><table><thead><tr><th>时间</th><th>目标</th><th>事件</th><th>变化</th></tr></thead><tbody>${events}</tbody></table></div></section>`;
}

function saveHtmlReport(filePath, data) {
  writeFileAtomic(filePath, createHtmlReport(data));
  return filePath;
}

function e(value) { return escapeHtml(value); }
function okText(value) { return value ? '<span class="pill ok">正常</span>' : '<span class="pill danger">失败</span>'; }
function numberOrDash(value) { return value == null ? '-' : Number(value).toFixed(1); }
function formatDuration(milliseconds) {
  const seconds = Math.round(milliseconds / 1000);
  if (seconds < 60) return `${seconds}秒`;
  return `${Math.floor(seconds / 60)}分${seconds % 60}秒`;
}

module.exports = { createHtmlReport, saveHtmlReport };
