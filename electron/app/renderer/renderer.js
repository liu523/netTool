'use strict';

const elements = {
  storeName: document.getElementById('storeName'), carrier: document.getElementById('carrier'),
  extraDomains: document.getElementById('extraDomains'), mode: document.getElementById('mode'),
  start: document.getElementById('start'), cancel: document.getElementById('cancel'),
  status: document.getElementById('status'), counter: document.getElementById('counter'),
  progress: document.getElementById('progress'), output: document.getElementById('output'),
  stateDot: document.getElementById('stateDot'), openReport: document.getElementById('openReport'),
  openFolder: document.getElementById('openFolder'), version: document.getElementById('version')
};

const bridge = window.netdiag || {
  appInfo: async () => ({ version: '开发预览', platform: 'browser', arch: 'preview' }),
  onProgress: () => () => {},
  start: async () => { throw new Error('请从Electron桌面应用中运行网络诊断'); },
  cancel: async () => true,
  openReport: async () => '浏览器预览不能打开本地报告',
  openFolder: async () => false
};

bridge.appInfo().then((info) => { elements.version.textContent = `v${info.version} · ${info.platform}-${info.arch}`; });
bridge.onProgress((progress) => {
  const total = Math.max(1, Number(progress.total || 1));
  const completed = Math.max(0, Number(progress.completed || 0));
  elements.status.textContent = progress.message;
  elements.counter.textContent = `${completed} / ${total}`;
  elements.progress.style.width = `${Math.min(100, completed * 100 / total)}%`;
  addLine(`[${new Date().toLocaleTimeString()}] ${progress.message}`);
});

elements.start.addEventListener('click', async () => {
  setRunning(true);
  elements.output.textContent = '';
  addLine('正在采集当前网络现场，请保持故障状态……', 'muted');
  try {
    const result = await bridge.start({
      storeName: elements.storeName.value,
      carrier: elements.carrier.value,
      extraDomains: elements.extraDomains.value,
      monitorMinutes: Number(elements.mode.value)
    });
    elements.stateDot.className = 'state-dot done';
    elements.status.textContent = result.cancelled ? '检测已停止，已保存部分报告' : '检测完成，请发送HTML、TXT和CSV';
    addLine('', 'muted');
    addLine(`TXT日志：${result.logPath}`, 'success');
    addLine(`HTML报告：${result.htmlPath}`, 'success');
    if (result.csvPath) addLine(`CSV时间序列：${result.csvPath}`, 'success');
    elements.openReport.disabled = false;
    elements.openFolder.disabled = false;
  } catch (error) {
    elements.stateDot.className = 'state-dot failed';
    elements.status.textContent = '检测程序发生异常';
    addLine(`程序异常：${error.message}`, 'error');
  } finally {
    setRunning(false, true);
  }
});

elements.cancel.addEventListener('click', () => bridge.cancel());
elements.openReport.addEventListener('click', async () => {
  const error = await bridge.openReport();
  if (error) addLine(`打开报告失败：${error}`, 'error');
});
elements.openFolder.addEventListener('click', () => bridge.openFolder());

function setRunning(running, preserveState) {
  elements.start.disabled = running;
  elements.cancel.disabled = !running;
  elements.storeName.disabled = running;
  elements.carrier.disabled = running;
  elements.extraDomains.disabled = running;
  elements.mode.disabled = running;
  if (running) {
    elements.openReport.disabled = true;
    elements.openFolder.disabled = true;
    elements.stateDot.className = 'state-dot running';
    elements.status.textContent = '正在准备检测';
    elements.progress.style.width = '0%';
  } else if (!preserveState) {
    elements.stateDot.className = 'state-dot';
  }
}

function addLine(text, className) {
  const line = document.createElement('p');
  if (className) line.className = className;
  line.textContent = text || ' ';
  elements.output.appendChild(line);
  elements.output.scrollTop = elements.output.scrollHeight;
}
