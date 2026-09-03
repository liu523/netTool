'use strict';

const dns = require('dns');
const { performance } = require('perf_hooks');
const { unique, withTimeout } = require('./utils');

async function systemLookup(host, timeoutMs) {
  const started = performance.now();
  try {
    // dns.lookup() may block for the full Windows resolver retry cycle (often 30-45s).
    // resolve4/resolve6 use the DNS servers configured by the OS through asynchronous c-ares.
    const queries = await withTimeout(Promise.allSettled([
      dns.promises.resolve4(host),
      dns.promises.resolve6(host)
    ]), timeoutMs, '系统DNS解析');
    const addresses = [];
    queries.forEach((query) => {
      if (query.status === 'fulfilled') addresses.push.apply(addresses, query.value);
    });
    const failures = queries.filter((query) => query.status === 'rejected');
    if (!addresses.length && failures.length) throw failures[0].reason;
    return {
      success: addresses.length > 0,
      elapsedMs: performance.now() - started,
      addresses: unique(addresses),
      detail: addresses.length ? '正常' : '无地址'
    };
  } catch (error) {
    return { success: false, elapsedMs: performance.now() - started, addresses: [], detail: error.message };
  }
}

async function queryServer(host, server, timeoutMs) {
  const resolver = new dns.promises.Resolver();
  resolver.setServers([server]);
  const started = performance.now();
  try {
    const answers = await withTimeout(resolver.resolve4(host, { ttl: true }), timeoutMs, `DNS服务器 ${server}`);
    return {
      server,
      success: true,
      elapsedMs: performance.now() - started,
      status: '正常',
      answers: answers.map((answer) => ({ type: 'A', value: answer.address, ttl: answer.ttl }))
    };
  } catch (error) {
    return {
      server,
      success: false,
      elapsedMs: performance.now() - started,
      status: error && error.code === 'ETIMEDOUT' ? '超时' : `失败：${error.message}`,
      answers: []
    };
  }
}

function configuredDnsServers() {
  try {
    return unique(dns.getServers().filter(Boolean));
  } catch (_) {
    return [];
  }
}

module.exports = { configuredDnsServers, queryServer, systemLookup };
