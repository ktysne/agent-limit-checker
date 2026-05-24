'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  getSnapshot: () => ipcRenderer.invoke('get-snapshot'),
  refresh: () => ipcRenderer.invoke('refresh'),
  setInterval: (seconds) => ipcRenderer.invoke('set-interval', seconds),
  setAutoLaunch: (enabled) => ipcRenderer.invoke('set-auto-launch', enabled),
  openLogin: (target) => ipcRenderer.invoke('open-login', target),
  quit: () => ipcRenderer.invoke('quit'),
  onSnapshot: (cb) => {
    const listener = (_evt, payload) => cb(payload);
    ipcRenderer.on('snapshot', listener);
    return () => ipcRenderer.removeListener('snapshot', listener);
  },
});
