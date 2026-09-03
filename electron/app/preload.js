'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('netdiag', Object.freeze({
  start: (options) => ipcRenderer.invoke('diag:start', options),
  cancel: () => ipcRenderer.invoke('diag:cancel'),
  openReport: () => ipcRenderer.invoke('diag:open-report'),
  openFolder: () => ipcRenderer.invoke('diag:open-folder'),
  appInfo: () => ipcRenderer.invoke('app:info'),
  onProgress: (callback) => {
    const listener = (_event, progress) => callback(progress);
    ipcRenderer.on('diag:progress', listener);
    return () => ipcRenderer.removeListener('diag:progress', listener);
  }
}));
