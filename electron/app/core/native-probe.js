'use strict';

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const { isIpV4, oneLine, withTimeout } = require('./utils');

let electronApp = null;
try {
  const electron = require('electron');
  electronApp = electron && electron.app || null;
} catch (_) {
  // Core unit tests can run under plain Node.js without loading Electron.
}

class NativeProbe {
  constructor() {
    this.executable = resolveNativeExecutable();
  }

  get available() {
    return Boolean(this.executable && fs.existsSync(this.executable));
  }

  async snapshot(signal) {
    if (!this.available) return { available: false, gateways: [], interfaces: [], detail: '原生探测组件未随当前开发包提供' };
    return this.invoke(['snapshot'], 5000, signal);
  }

  async ping(address, count, timeoutMs, ttl, signal) {
    if (!isIpV4(address)) {
      return { available: this.available, sent: count, received: 0, averageMs: null, detail: '当前原生组件仅探测IPv4 ICMP' };
    }
    if (!this.available) {
      return { available: false, sent: count, received: 0, averageMs: null, detail: '原生ICMP组件不可用；不调用系统ping命令' };
    }
    const args = ['ping', address, String(count), String(timeoutMs)];
    if (ttl) args.push(String(ttl));
    return this.invoke(args, Math.max(5000, count * (timeoutMs + 250)), signal);
  }

  async trace(address, maxHops, timeoutMs, signal) {
    if (!isIpV4(address)) return { available: this.available, hops: [], detail: '当前原生组件仅跟踪IPv4路由' };
    if (!this.available) return { available: false, hops: [], detail: '原生路由组件不可用；不调用系统tracert/traceroute命令' };
    return this.invoke(['trace', address, String(maxHops), String(timeoutMs)], maxHops * timeoutMs * 3 + 5000, signal);
  }

  invoke(args, timeoutMs, signal) {
    return new Promise((resolve, reject) => {
      if (signal && signal.aborted) {
        const error = new Error('检测已取消');
        error.name = 'AbortError';
        reject(error);
        return;
      }
      const child = spawn(this.executable, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      let stdout = '';
      let stderr = '';
      const abort = () => child.kill();
      if (signal) signal.addEventListener('abort', abort, { once: true });
      child.stdout.setEncoding('utf8');
      child.stderr.setEncoding('utf8');
      child.stdout.on('data', (chunk) => { stdout += chunk; });
      child.stderr.on('data', (chunk) => { stderr += chunk; });
      const completed = new Promise((innerResolve, innerReject) => {
        child.once('error', innerReject);
        child.once('close', (code) => {
          if (signal) signal.removeEventListener('abort', abort);
          if (signal && signal.aborted) {
            const error = new Error('检测已取消');
            error.name = 'AbortError';
            innerReject(error);
          } else if (code !== 0) {
            innerReject(new Error(oneLine(stderr) || `原生组件退出码 ${code}`));
          } else {
            try {
              innerResolve(JSON.parse(stdout));
            } catch (error) {
              innerReject(new Error(`原生组件返回格式无效：${oneLine(error.message)} ${oneLine(stdout)}`));
            }
          }
        });
      });
      withTimeout(completed, timeoutMs, '原生网络探测', () => child.kill()).then(resolve, reject);
    });
  }
}

function resolveNativeExecutable() {
  const name = process.platform === 'win32' ? 'netdiag-native.exe' : 'netdiag-native';
  const platformArch = nativePlatformDirectory(process.platform, process.arch);
  const roots = [];
  if (electronApp && electronApp.isPackaged) roots.push(path.join(process.resourcesPath, 'native', platformArch, name));
  roots.push(path.join(__dirname, '..', '..', 'native', 'bin', platformArch, name));
  roots.push(path.join(process.cwd(), 'native', 'bin', platformArch, name));
  return roots.find((candidate) => fs.existsSync(candidate)) || roots[0];
}

function nativePlatformDirectory(platform, arch) {
  return `${platform}-${platform === 'win32' && arch === 'ia32' ? 'x86' : arch}`;
}

module.exports = { NativeProbe, nativePlatformDirectory, resolveNativeExecutable };
