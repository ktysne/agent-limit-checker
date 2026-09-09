'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  getSnapshot: () => ipcRenderer.invoke('get-snapshot'),
  refresh: () => ipcRenderer.invoke('refresh'),
  setInterval: (seconds) => ipcRenderer.invoke('set-interval', seconds),
  setAutoLaunch: (enabled) => ipcRenderer.invoke('set-auto-launch', enabled),
  setNtfySettings: (partial) => ipcRenderer.invoke('set-ntfy-settings', partial),
  // `label` is a Codex home directory name; the main process only accepts one it
  // discovered itself, so it is a settings key here, never a path.
  setCodexAccountName: (label, name) => ipcRenderer.invoke('set-codex-account-name', label, name),
  // `accountId` selects one Codex account; the main process matches it against
  // the accounts it discovered, so it is an opaque id here, never a path.
  openLogin: (target, accountId) => ipcRenderer.invoke('open-login', target, accountId),
  quit: () => ipcRenderer.invoke('quit'),
  // One-way: tell the main process how tall the rendered content actually is
  // (CSS px) so it can size the window to fit — no scrollbar, no empty padding.
  reportContentHeight: (height) => ipcRenderer.send('content-height', height),
  onSnapshot: (cb) => {
    const listener = (_evt, payload) => cb(payload);
    ipcRenderer.on('snapshot', listener);
    return () => ipcRenderer.removeListener('snapshot', listener);
  },
  // The main process cut our requested height down to the display: the content
  // does not fit, so the renderer may scroll and must stop growing the request.
  onContentClamped: (cb) => {
    const listener = (_evt, clamped) => cb(!!clamped);
    ipcRenderer.on('content-clamped', listener);
    return () => ipcRenderer.removeListener('content-clamped', listener);
  },
});
