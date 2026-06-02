'use strict';

// Electron-process probe for the fit-to-content popover sizing. Run with:
//   npx electron test/popover-fit-probe.js
//
// Loads the REAL renderer + preload, feeds it the tallest realistic snapshot
// (Claude: plan + 5h + weekly + Sonnet weekly; Codex: 5h + weekly), and checks:
//   1. the renderer reports its measured content height over 'content-height';
//   2. the OLD fixed 560px height would have scrolled this content (the bug);
//   3. after sizing the window to the reported height, nothing scrolls (the fix).

const path = require('node:path');
const { app, BrowserWindow, ipcMain } = require('electron');

const POPOVER_WIDTH = 360;
const OLD_FIXED_HEIGHT = 560;

const now = Date.now();
const tallSnapshot = {
  claude: {
    ok: true,
    data: {
      plan: 'Max 20x',
      fiveHour: { utilization: 0.42, resetsAt: now + 3 * 3600 * 1000 },
      weekly: { utilization: 0.66, resetsAt: now + 5 * 86400 * 1000 },
      weeklySonnet: { utilization: 0.33, resetsAt: now + 5 * 86400 * 1000 },
    },
  },
  codex: {
    ok: true,
    data: {
      plan: 'Plus',
      fiveHour: { utilization: 0.5, resetsAt: now + 2 * 3600 * 1000 },
      weekly: { utilization: 0.7, resetsAt: now + 4 * 86400 * 1000 },
    },
  },
  fetchedAt: now,
  settings: { pollingIntervalSec: 300 },
  autoLaunchEnabled: false,
  isPolling: false,
  theme: 'dark',
  appVersion: '0.0.0-probe',
};

app.disableHardwareAcceleration();

let reportedHeight = null;
ipcMain.handle('get-snapshot', () => tallSnapshot);
ipcMain.on('content-height', (_evt, h) => { reportedHeight = h; });

async function measureScroll(win) {
  return win.webContents.executeJavaScript(`(() => {
    const c = document.querySelector('.container');
    return {
      container: c ? c.getBoundingClientRect().height : null,
      scrollHeight: document.documentElement.scrollHeight,
      innerHeight: window.innerHeight,
      scrolls: document.documentElement.scrollHeight > window.innerHeight,
    };
  })()`);
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: POPOVER_WIDTH,
    height: OLD_FIXED_HEIGHT,
    minWidth: POPOVER_WIDTH,
    maxWidth: POPOVER_WIDTH,
    useContentSize: true,
    show: false,
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  await win.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));
  // Let DOMContentLoaded -> getSnapshot -> render -> ResizeObserver settle.
  await wait(400);

  console.log(`# environment: contentSize=${JSON.stringify(win.getContentSize())}`);
  console.log(`# renderer reported content height: ${reportedHeight}px`);

  // (2) Old behaviour: window pinned at 560 — does this content scroll?
  win.setContentSize(POPOVER_WIDTH, OLD_FIXED_HEIGHT);
  await wait(60);
  const before = await measureScroll(win);
  console.log(`\n## OLD fixed ${OLD_FIXED_HEIGHT}px`);
  console.log(`   container=${before.container.toFixed(1)} innerHeight=${before.innerHeight} scrolls=${before.scrolls}`);

  // (3) New behaviour: size to the reported height — should not scroll.
  win.setContentSize(POPOVER_WIDTH, reportedHeight);
  await wait(60);
  const after = await measureScroll(win);
  console.log(`\n## NEW fit-to-content ${reportedHeight}px`);
  console.log(`   container=${after.container.toFixed(1)} innerHeight=${after.innerHeight} scrolls=${after.scrolls}`);

  const pass = Number.isFinite(reportedHeight) && !after.scrolls && after.container <= after.innerHeight;
  console.log(`\n${pass ? 'PASS' : 'FAIL'}: fit-to-content height ${pass ? 'removes' : 'does NOT remove'} the scrollbar`);
  win.destroy();
  app.exit(pass ? 0 : 1);
});
