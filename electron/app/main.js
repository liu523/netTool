'use strict';

const path = require('path');
const fs = require('fs');
const { app, BrowserWindow, dialog, ipcMain, session, shell } = require('electron');
const { DiagnosticRunner } = require('./core/runner');
const { parseExtraDomains } = require('./core/catalog');
const { httpProbe, httpProbeViaProxy } = require('./core/network-probes');
const { oneLine } = require('./core/utils');

let mainWindow = null;
let activeController = null;
let lastResult = null;

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1080,
    height: 900,
    minWidth: 860,
    minHeight: 700,
    show: false,
    title: '利亚方舟海螺云网络诊断工具',
    backgroundColor: '#f3f6fa',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      devTools: !app.isPackaged
    }
  });
  mainWindow.removeMenu();
  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  mainWindow.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith('file://')) event.preventDefault();
  });
  mainWindow.on('close', (event) => {
    if (!activeController) return;
    const choice = dialog.showMessageBoxSync(mainWindow, {
      type: 'question', buttons: ['继续检测', '停止并退出'], defaultId: 0, cancelId: 0,
      title: '检测仍在进行', message: '检测仍在进行，确定停止并退出吗？', detail: '已经完成的结果会保留在日志中。'
    });
    if (choice === 0) event.preventDefault();
    else activeController.abort();
  });
}

function registerIpc() {
  ipcMain.handle('diag:start', async (_event, rawOptions) => {
    if (activeController) throw new Error('已有检测正在运行');
    const options = sanitizeOptions(rawOptions);
    activeController = new AbortController();
    lastResult = null;
    try {
      const runner = createRunner();
      const result = await runner.run(options, (progress) => {
        if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send('diag:progress', progress);
      }, activeController.signal);
      lastResult = result;
      return result;
    } finally {
      activeController = null;
    }
  });
  ipcMain.handle('diag:cancel', () => {
    if (activeController) activeController.abort();
    return true;
  });
  ipcMain.handle('diag:open-report', async () => {
    if (!lastResult || !lastResult.htmlPath || !fs.existsSync(lastResult.htmlPath)) return '报告文件不存在';
    return shell.openPath(lastResult.htmlPath);
  });
  ipcMain.handle('diag:open-folder', () => {
    if (!lastResult || !lastResult.logPath || !fs.existsSync(lastResult.logPath)) return false;
    shell.showItemInFolder(lastResult.logPath);
    return true;
  });
  ipcMain.handle('app:info', () => ({ version: app.getVersion(), platform: process.platform, arch: process.arch }));
}

function createRunner() {
  return new DiagnosticRunner({
    systemHttpProbe: systemHttpProbe,
    resolveSystemProxy: async (url) => {
      const value = await session.defaultSession.resolveProxy(url);
      return value === 'DIRECT' ? '未使用（DIRECT）' : value;
    }
  });
}

async function systemHttpProbe(host, signal) {
  const proxyRule = await session.defaultSession.resolveProxy(`https://${host}/`);
  const directives = String(proxyRule || 'DIRECT').split(';').map((item) => item.trim()).filter(Boolean);
  const selected = directives[0] || 'DIRECT';
  if (/^DIRECT$/i.test(selected)) return httpProbe(host, null, 9000, signal);
  const match = /^PROXY\s+(.+)$/i.exec(selected);
  if (!match) {
    return { receivedResponse: false, statusCode: null, elapsedMs: 0, detail: `检测到暂不支持的系统代理类型：${selected}`, traceHeaders: {} };
  }
  try {
    const proxyUrl = new URL(`http://${match[1]}`);
    return httpProbeViaProxy(host, proxyUrl.hostname, Number(proxyUrl.port || 80), 9000, signal);
  } catch (error) {
    return { receivedResponse: false, statusCode: null, elapsedMs: 0, detail: `系统代理地址无效：${oneLine(error.message)}`, traceHeaders: {} };
  }
}

function sanitizeOptions(value) {
  const raw = value && typeof value === 'object' ? value : {};
  const monitorMinutes = [0, 1, 5, 10].indexOf(Number(raw.monitorMinutes)) >= 0 ? Number(raw.monitorMinutes) : 0;
  return {
    outputDirectory: path.join(app.getPath('documents'), 'LYFZ-NetDiag-Electron', 'Logs'),
    storeName: String(raw.storeName || '').replace(/[\r\n\t]/g, ' ').slice(0, 80),
    carrier: String(raw.carrier || '未知').replace(/[\r\n\t]/g, ' ').slice(0, 40),
    extraDomains: parseExtraDomains(raw.extraDomains),
    monitorMinutes
  };
}

async function runAutomatic() {
  const outputArgument = process.argv.find((argument) => argument.startsWith('--output='));
  const monitorArgument = process.argv.find((argument) => argument.startsWith('--monitor-minutes='));
  const domainsArgument = process.argv.find((argument) => argument.startsWith('--domains='));
  const monitorMinutes = monitorArgument ? Number(monitorArgument.split('=')[1]) : 0;
  const outputDirectory = outputArgument ? path.resolve(outputArgument.slice('--output='.length)) : path.join(app.getPath('documents'), 'LYFZ-NetDiag-Electron', 'Logs');
  const runner = createRunner();
  try {
    const result = await runner.run({
      outputDirectory, storeName: '自动测试', carrier: '未知',
      extraDomains: domainsArgument ? domainsArgument.slice('--domains='.length) : '',
      monitorMinutes: [1, 5, 10].indexOf(monitorMinutes) >= 0 ? monitorMinutes : 0
    },
      () => {}, new AbortController().signal);
    fs.writeFileSync(path.join(outputDirectory, 'auto-result.json'), JSON.stringify(result, null, 2), 'utf8');
    app.exit(0);
  } catch (error) {
    fs.mkdirSync(outputDirectory, { recursive: true });
    fs.writeFileSync(path.join(outputDirectory, 'auto-error.txt'), String(error && error.stack || error), 'utf8');
    app.exit(1);
  }
}

function abortError() { const error = new Error('检测已取消'); error.name = 'AbortError'; return error; }

app.whenReady().then(async () => {
  if (process.argv.indexOf('--auto') >= 0) {
    await runAutomatic();
    return;
  }
  registerIpc();
  createWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
});

app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
