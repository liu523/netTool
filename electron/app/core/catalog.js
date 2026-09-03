'use strict';

const { domainToASCII } = require('url');

const HISTORICAL_BAD_EDGE = '113.215.230.101';

const TARGETS = Object.freeze([
  { host: 'app.lyfz.net', category: '海螺云页面入口/昆仑CDN', comparisonAddress: '113.113.100.49', firstParty: true },
  { host: 'login.lyfz.net', category: '海螺云登录/昆仑CDN', comparisonAddress: '183.60.202.48', firstParty: true },
  {
    host: 'api.lyfz.net', category: '海螺云业务API', comparisonAddress: '47.99.141.216', firstParty: true,
    notes: '根路径5xx仅表示服务已收到请求，按网络连通正常处理', treatServerErrorAsNormal: true
  },
  {
    host: 'storage.lyfz.net', category: '海螺云存储/昆仑CDN', comparisonAddress: '116.253.29.46', firstParty: true,
    notes: 'HTTP 403可能是正常的鉴权响应'
  },
  {
    host: 'message.lyfz.net', category: '海螺云消息/WSS', comparisonAddress: '47.111.176.231', firstParty: true,
    notes: '检测DNS、TCP、TLS和HTTP入口；业务WebSocket路径由应用决定'
  },
  { host: 'erp-cdn.lyfz.net', category: '海螺云ERP静态资源/昆仑CDN', comparisonAddress: '113.113.100.48', firstParty: true },
  {
    host: 'napi.lyfz.net', category: '海螺云NAPI', comparisonAddress: '47.97.157.241', firstParty: true,
    notes: '根路径状态码仅用于连通性判断'
  },
  {
    host: 'system.lyfz.net', category: '海螺云系统服务/昆仑CDN', comparisonAddress: '113.113.100.43', firstParty: true,
    notes: 'HTTP 403可能是正常的鉴权响应'
  },
  { host: 'turing.captcha.gtimg.com', category: '腾讯验证码', comparisonAddress: null, firstParty: false },
  { host: 'lp.open.weixin.qq.com', category: '微信开放平台', comparisonAddress: null, firstParty: false },
  { host: 'open.weixin.qq.com', category: '微信开放平台', comparisonAddress: null, firstParty: false },
  { host: 'support.weixin.qq.com', category: '微信支持服务', comparisonAddress: null, firstParty: false },
  { host: 'res.wx.qq.com', category: '微信静态资源', comparisonAddress: null, firstParty: false }
]);

function parseExtraDomains(value) {
  const source = Array.isArray(value) ? value.join('\n') : String(value || '');
  const tokens = source.split(/[\s,，;；]+/).map((item) => item.trim()).filter(Boolean);
  const domains = [];
  const invalid = [];
  tokens.forEach((token) => {
    try {
      let input = token;
      if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(input)) input = `https://${input}`;
      const parsed = new URL(input);
      const ascii = domainToASCII(parsed.hostname.replace(/\.$/, '')).toLowerCase();
      const labels = ascii.split('.');
      const valid = ascii.length > 0 && ascii.length <= 253 && labels.every((label) =>
        label.length > 0 && label.length <= 63 && /^(?!-)[a-z0-9-]+(?<!-)$/.test(label));
      if (!valid || /^\d+\.\d+\.\d+\.\d+$/.test(ascii)) throw new Error('invalid domain');
      if (domains.indexOf(ascii) < 0) domains.push(ascii);
    } catch (_) {
      invalid.push(token);
    }
  });
  if (invalid.length) throw new Error(`额外诊断域名格式无效：${invalid.slice(0, 5).join('、')}`);
  if (domains.length > 20) throw new Error('额外诊断域名最多允许20个');
  return domains;
}

function buildTargets(extraDomains) {
  const defaults = TARGETS.slice();
  const defaultHosts = new Set(defaults.map((item) => item.host));
  parseExtraDomains(extraDomains).forEach((host) => {
    if (!defaultHosts.has(host)) {
      defaults.push({
        host,
        category: '用户追加诊断域名',
        comparisonAddress: null,
        firstParty: false,
        custom: true,
        notes: '用户临时追加；检测DNS、Ping、路由、TCP 443、TLS和HTTPS根路径'
      });
    }
  });
  return defaults;
}

module.exports = { buildTargets, HISTORICAL_BAD_EDGE, parseExtraDomains, TARGETS };
