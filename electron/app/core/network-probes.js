'use strict';

const https = require('https');
const net = require('net');
const tls = require('tls');
const { performance } = require('perf_hooks');
const { version: APP_VERSION } = require('../../package.json');
const { oneLine } = require('./utils');

function tcpProbe(address, port, timeoutMs, signal) {
  const started = performance.now();
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    const socket = net.createConnection({ host: address, port, family: net.isIPv4(address) ? 4 : 6 });
    let settled = false;
    const abort = () => finishReject(abortError());
    const timer = setTimeout(() => finish(false, '连接超时'), timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      if (signal) signal.removeEventListener('abort', abort);
      socket.removeAllListeners();
      socket.destroy();
    }
    function finish(success, detail) {
      if (settled) return;
      settled = true;
      const elapsedMs = performance.now() - started;
      cleanup();
      resolve({ success, elapsedMs, detail });
    }
    function finishReject(error) {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    }
    socket.once('connect', () => finish(true, `已连接 ${address}:${port}`));
    socket.once('error', (error) => finish(false, oneLine(error.message)));
    if (signal) signal.addEventListener('abort', abort, { once: true });
  });
}

function tlsProbe(host, address, timeoutMs, signal) {
  const started = performance.now();
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    const socket = tls.connect({
      host: address,
      port: 443,
      servername: host,
      rejectUnauthorized: true,
      minVersion: 'TLSv1.2'
    });
    let settled = false;
    const abort = () => finishReject(abortError());
    const timer = setTimeout(() => finish(false, 'TLS握手超时', ''), timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      if (signal) signal.removeEventListener('abort', abort);
      socket.removeAllListeners();
      socket.destroy();
    }
    function finish(success, detail, certificate) {
      if (settled) return;
      settled = true;
      const elapsedMs = performance.now() - started;
      cleanup();
      resolve({ success, elapsedMs, detail, certificate });
    }
    function finishReject(error) {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    }
    socket.once('secureConnect', () => {
      const certificate = socket.getPeerCertificate();
      const summary = certificate && certificate.subject
        ? `主题=${certificate.subject.CN || '-'}；颁发者=${certificate.issuer && certificate.issuer.CN || '-'}；有效期=${certificate.valid_from || '-'} 至 ${certificate.valid_to || '-'}`
        : '';
      finish(true, `${socket.getProtocol() || 'TLS'}；授权校验通过`, summary);
    });
    socket.once('error', (error) => finish(false, oneLine(error.message), ''));
    if (signal) signal.addEventListener('abort', abort, { once: true });
  });
}

function httpProbe(host, address, timeoutMs, signal) {
  const started = performance.now();
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    let settled = false;
    const request = https.request({
      protocol: 'https:',
      hostname: host,
      port: 443,
      path: '/',
      method: 'GET',
      servername: host,
      rejectUnauthorized: true,
      agent: false,
      headers: { 'User-Agent': `LYFZ-NetDiag-Electron/${APP_VERSION}`, Connection: 'close' },
      lookup: address ? (_hostname, options, callback) => {
        const family = net.isIPv4(address) ? 4 : 6;
        // Node 20+ asks custom lookup functions for all addresses, while the
        // Node 16 runtime in Electron 22 expects the classic scalar callback.
        if (options && options.all) callback(null, [{ address, family }]);
        else callback(null, address, family);
      } : undefined
    });
    const abort = () => finishReject(abortError());
    const timer = setTimeout(() => request.destroy(new Error('HTTP请求超时')), timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      if (signal) signal.removeEventListener('abort', abort);
    }
    function finish(result) {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(Object.assign({ elapsedMs: performance.now() - started }, result));
    }
    function finishReject(error) {
      if (settled) return;
      settled = true;
      cleanup();
      request.destroy();
      reject(error);
    }
    request.once('response', (response) => {
      const traceHeaders = pickTraceHeaders(response.headers);
      finish({ receivedResponse: true, statusCode: response.statusCode || null, detail: formatHttpDetail(response.statusCode, traceHeaders), traceHeaders });
      response.destroy();
    });
    request.once('error', (error) => {
      if (signal && signal.aborted) finishReject(abortError());
      else finish({ receivedResponse: false, statusCode: null, detail: oneLine(error.message), traceHeaders: {} });
    });
    if (signal) signal.addEventListener('abort', abort, { once: true });
    request.end();
  });
}

function httpProbeViaProxy(host, proxyHost, proxyPort, timeoutMs, signal) {
  const started = performance.now();
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    let settled = false;
    let socket = net.createConnection({ host: proxyHost, port: proxyPort });
    let tunnelBuffer = Buffer.alloc(0);
    const abort = () => finishReject(abortError());
    const timer = setTimeout(() => finish({ receivedResponse: false, statusCode: null, detail: '代理HTTPS请求超时', traceHeaders: {} }), timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      if (signal) signal.removeEventListener('abort', abort);
      if (socket) { socket.removeAllListeners(); socket.destroy(); }
    }
    function finish(result) {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(Object.assign({ elapsedMs: performance.now() - started }, result));
    }
    function finishReject(error) {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    }
    socket.once('connect', () => {
      socket.write(`CONNECT ${host}:443 HTTP/1.1\r\nHost: ${host}:443\r\nProxy-Connection: close\r\n\r\n`);
    });
    socket.on('data', onTunnelData);
    socket.once('error', (error) => finish({ receivedResponse: false, statusCode: null, detail: `代理连接失败：${oneLine(error.message)}`, traceHeaders: {} }));
    if (signal) signal.addEventListener('abort', abort, { once: true });

    function onTunnelData(chunk) {
      tunnelBuffer = Buffer.concat([tunnelBuffer, chunk]);
      const end = tunnelBuffer.indexOf('\r\n\r\n');
      if (end < 0) {
        if (tunnelBuffer.length > 32768) finish({ receivedResponse: false, statusCode: null, detail: '代理CONNECT响应头过大', traceHeaders: {} });
        return;
      }
      const head = tunnelBuffer.slice(0, end).toString('latin1');
      const match = /^HTTP\/\d(?:\.\d)?\s+(\d{3})/i.exec(head);
      if (!match || Number(match[1]) !== 200) {
        finish({ receivedResponse: false, statusCode: match ? Number(match[1]) : null, detail: `代理CONNECT失败：${oneLine(head.split('\r\n')[0])}`, traceHeaders: {} });
        return;
      }
      socket.removeListener('data', onTunnelData);
      socket.removeAllListeners('error');
      const plainSocket = socket;
      socket = tls.connect({ socket: plainSocket, servername: host, rejectUnauthorized: true, minVersion: 'TLSv1.2', ALPNProtocols: ['http/1.1'] });
      let response = Buffer.alloc(0);
      socket.once('secureConnect', () => socket.write(`GET / HTTP/1.1\r\nHost: ${host}\r\nUser-Agent: LYFZ-NetDiag-Electron/${APP_VERSION}\r\nConnection: close\r\n\r\n`));
      socket.on('data', (data) => {
        response = Buffer.concat([response, data]);
        const headerEnd = response.indexOf('\r\n\r\n');
        if (headerEnd < 0) {
          if (response.length > 65536) finish({ receivedResponse: false, statusCode: null, detail: 'HTTP响应头过大', traceHeaders: {} });
          return;
        }
        const text = response.slice(0, headerEnd).toString('latin1');
        const lines = text.split('\r\n');
        const status = /^HTTP\/\d(?:\.\d)?\s+(\d{3})/i.exec(lines[0]);
        const headers = parseTraceHeaders(lines.slice(1));
        finish({ receivedResponse: Boolean(status), statusCode: status ? Number(status[1]) : null, detail: status ? formatHttpDetail(Number(status[1]), headers) : 'HTTP状态行无效', traceHeaders: headers });
      });
      socket.once('error', (error) => finish({ receivedResponse: false, statusCode: null, detail: `代理TLS/HTTP失败：${oneLine(error.message)}`, traceHeaders: {} }));
    }
  });
}

function publicIpProbe(url, timeoutMs, signal) {
  const started = performance.now();
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    let body = '';
    const request = https.get(url, { headers: { 'User-Agent': `LYFZ-NetDiag-Electron/${APP_VERSION}` }, agent: false }, (response) => {
      response.setEncoding('utf8');
      response.on('data', (chunk) => { if (body.length < 200) body += chunk; });
      response.on('end', () => resolve({ success: net.isIP(body.trim()) > 0, address: body.trim(), elapsedMs: performance.now() - started }));
    });
    const abort = () => { request.destroy(); reject(abortError()); };
    const timer = setTimeout(() => request.destroy(new Error('公网IP请求超时')), timeoutMs);
    request.once('close', () => {
      clearTimeout(timer);
      if (signal) signal.removeEventListener('abort', abort);
    });
    request.once('error', (error) => {
      if (signal && signal.aborted) reject(abortError());
      else resolve({ success: false, address: '', elapsedMs: performance.now() - started, detail: oneLine(error.message) });
    });
    if (signal) signal.addEventListener('abort', abort, { once: true });
  });
}

function pickTraceHeaders(headers) {
  const wanted = ['server', 'via', 'x-cache', 'x-request-id', 'eagleid', 'ali-swift-global-savetime', 'x-swift-cachetime', 'x-swift-savetime'];
  const result = {};
  wanted.forEach((name) => {
    if (headers[name] != null) result[name] = Array.isArray(headers[name]) ? headers[name].join(', ') : String(headers[name]);
  });
  return result;
}

function parseTraceHeaders(lines) {
  const all = {};
  lines.forEach((line) => {
    const colon = line.indexOf(':');
    if (colon > 0) all[line.slice(0, colon).trim().toLowerCase()] = line.slice(colon + 1).trim();
  });
  return pickTraceHeaders(all);
}

function formatHttpDetail(statusCode, headers) {
  const trace = Object.keys(headers).map((name) => `${name}=${headers[name]}`).join('；');
  return `HTTP ${statusCode == null ? '-' : statusCode}${trace ? `；${trace}` : ''}`;
}

function abortError() {
  const error = new Error('检测已取消');
  error.name = 'AbortError';
  return error;
}

module.exports = { httpProbe, httpProbeViaProxy, publicIpProbe, tcpProbe, tlsProbe };
