'use strict';

function targetConclusion(result) {
  if (result.resolvedHistoricalBadEdge) return '危险：命中历史异常CDN节点';
  if (!result.systemDnsSucceeded) return '失败：系统DNS解析失败';
  if (!result.anyTcpSucceeded) return result.comparisonSucceeded ? '失败：当前节点不可达，对照节点正常' : '失败：TCP 443不可达';
  if (!result.anyTlsSucceeded) return '失败：TLS握手异常';
  if (!result.anyHttpResponse) return '警告：HTTP请求未收到响应';
  if (result.hasServerError) {
    return result.target.treatServerErrorAsNormal
      ? '正常：网络可达（API根路径5xx按正常处理）'
      : '警告：网络可达，但服务返回5xx';
  }
  return '正常：网络链路可达';
}

function buildAnalysisReasons(results, gatewayBaselines, monitoring) {
  const reasons = [];
  const hasGateway = gatewayBaselines.length > 0;
  const maximumLoss = hasGateway ? Math.max.apply(null, gatewayBaselines.map((item) => item.lossPercent)) : 0;
  const anyGatewayReachable = gatewayBaselines.some((item) => item.received > 0);
  const badGateway = hasGateway && (!anyGatewayReachable || maximumLoss >= 50);
  const externalLayerFailures = results.filter((item) => !item.systemDnsSucceeded || !item.anyTcpSucceeded || !item.anyTlsSucceeded).length;
  const historicalBad = results.filter((item) => item.resolvedHistoricalBadEdge);
  const comparisonWorked = results.filter((item) => item.systemDnsSucceeded && !item.anyTcpSucceeded && item.comparisonSucceeded);
  const localDnsIssue = results.filter((item) => !item.systemDnsSucceeded && item.anyRawDnsSucceeded);
  const proxyIssue = results.filter((item) => !item.systemHttpSucceeded && item.currentDirectHttpSucceeded);
  const kunlunFailures = results.filter((item) => item.target.category.indexOf('昆仑') >= 0 && (!item.anyTcpSucceeded || !item.anyTlsSucceeded));
  const directHealthy = results.some((item) => item.target.firstParty && item.target.category.indexOf('昆仑') < 0 && item.anyTcpSucceeded && item.anyTlsSucceeded);
  const routedTcpFailures = results.filter((item) => !item.anyTcpSucceeded && item.routeTrace && item.routeTrace.hops && item.routeTrace.hops.length);

  if (badGateway && externalLayerFailures >= 3) {
    reasons.push('高可信：默认网关出现严重丢包或不可达，优先检查门店Wi-Fi、网线、交换机和路由器。');
  } else if (badGateway) {
    reasons.push('提示：默认网关不响应或严重丢弃ICMP，但外部TCP/TLS大体正常；可能仅禁用了Ping，不能单独判定局域网故障。');
  }
  if (historicalBad.length) reasons.push(`高可信：命中历史异常CDN节点 113.215.230.101，域名：${hosts(historicalBad)}。`);
  if (comparisonWorked.length) reasons.push(`高可信：当前调度节点不可达但健康对照节点正常，倾向CDN节点或运营商到该节点的路由问题：${hosts(comparisonWorked)}。`);
  if (kunlunFailures.length && directHealthy) reasons.push(`较高可信：昆仑域名异常而直连业务域名正常，倾向昆仑CDN/WAF或其跨网路由问题：${hosts(kunlunFailures)}。`);
  if (localDnsIssue.length) reasons.push(`较高可信：系统DNS失败但指定DNS服务器查询成功，倾向电脑DNS缓存、本地DNS代理或安全软件问题：${hosts(localDnsIssue)}。`);
  if (proxyIssue.length) reasons.push(`较高可信：系统代理路径失败但指定节点直连正常，请检查代理、VPN或终端安全软件：${hosts(proxyIssue)}。`);
  if (routedTcpFailures.length) {
    const evidence = routedTcpFailures.map((item) => {
      const responded = item.routeTrace.hops.filter((hop) => hop.address);
      const last = responded.length ? responded[responded.length - 1] : null;
      return `${item.target.host}(最后响应${last ? `${last.ttl}跳/${last.address}` : '无'})`;
    });
    reasons.push(`路由证据：以下域名TCP 443失败，逐跳探测可用于交给运营商定位中断区间：${evidence.join(', ')}。`);
  }

  if (monitoring) {
    const failed = Object.keys(monitoring.aggregates)
      .filter((host) => monitoring.aggregates[host].failures > 0)
      .map((host) => `${host}(${monitoring.aggregates[host].failures}/${monitoring.aggregates[host].samples})`);
    if (failed.length) reasons.push(`连续监测捕获到异常采样：${failed.join(', ')}。`);
  }
  if (!reasons.length) reasons.push('本次没有捕获到可明确归因的网络异常；如果问题为间歇性，请选择连续检测并覆盖故障发生和恢复时段。');
  return reasons;
}

function overallStatus(results, cancelled) {
  if (cancelled) return { level: 'warn', title: '检测已停止', detail: '报告保留了停止前已经完成的证据。' };
  if (results.some((item) => targetConclusion(item).startsWith('危险'))) return { level: 'danger', title: '发现高风险网络异常', detail: '请优先查看自动故障归因和标红的域名。' };
  if (results.some((item) => targetConclusion(item).startsWith('失败'))) return { level: 'danger', title: '发现网络连接失败', detail: '已捕获DNS、TCP或TLS层面的失败证据。' };
  if (results.some((item) => targetConclusion(item).startsWith('警告'))) return { level: 'warn', title: '网络可达，但存在警告', detail: '请结合HTTP状态和自动分析判断。' };
  return { level: 'ok', title: '本次网络诊断正常', detail: '所有已完成的关键网络层检测均可达。' };
}

function hosts(items) {
  return items.map((item) => item.target.host).join(', ');
}

module.exports = { buildAnalysisReasons, overallStatus, targetConclusion };
