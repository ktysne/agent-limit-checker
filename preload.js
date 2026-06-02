'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  getSnapshot: () => ipcRenderer.invoke('get-snapshot'),
  refresh: () => ipcRenderer.invoke('refresh'),
  setInterval: (seconds) => ipcRenderer.invoke('set-interval', seconds),
  setAutoLaunch: (enabled) => ipcRenderer.invoke('set-auto-launch', enabled),
  openLogin: (target) => ipcRenderer.invoke('open-login', target),
  quit: () => ipcRenderer.invoke('quit'),
  // One-way: tell the main process how tall the rendered content actually is
  // (CSS px) so it can size the window to fit — no scrollbar, no empty padding.
  reportContentHeight: (height) => ipcRenderer.send('content-height', height),
  onSnapshot: (cb) => {
    const listener = (_evt, payload) => cb(payload);
    ipcRenderer.on('snapshot', listener);
    return () => ipcRenderer.removeListener('snapshot', listener);
  },
});
